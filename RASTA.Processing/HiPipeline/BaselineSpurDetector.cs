using System;
using System.Collections.Generic;
using System.Linq;

namespace RASTA.Processing.HiPipeline
{
    /// <summary>One narrow spur found in a baseline spectrum (bin indices in fftshifted order).</summary>
    public sealed record BaselineSpur(int PeakBin, int LoBin, int HiBin, double OffsetHz, double Sigma, double ExcessFraction);

    /// <summary>
    /// A regular comb fitted to a set of spurs: one or more teeth per period, each tooth
    /// family ("phase") repeating every <see cref="PeriodBins"/>. Positions are fractional
    /// bin indices in fftshifted order.
    /// </summary>
    public sealed record CombFit(
        double PeriodBins,
        double PeriodHz,
        IReadOnlyList<double> PhasesBins,
        int TeethExplained,
        IReadOnlyList<double> PredictedToothBins,
        int ToothHalfWidthBins,
        double MaskedFraction);

    /// <summary>Result of <see cref="BaselineSpurDetector.Detect"/>.</summary>
    public sealed class BaselineSpurReport
    {
        public BaselineSpurReport(bool[] flagged, IReadOnlyList<BaselineSpur> spurs, CombFit? comb, bool[] combMask)
        {
            Flagged = flagged;
            Spurs = spurs;
            Comb = comb;
            CombMask = combMask;
        }

        /// <summary>Bins (fftshifted order) belonging to a detected spur, skirts included.</summary>
        public bool[] Flagged { get; }

        public IReadOnlyList<BaselineSpur> Spurs { get; }

        /// <summary>The fitted comb, or null when the spurs don't form a convincing one.</summary>
        public CombFit? Comb { get; }

        /// <summary>Every predicted tooth position of <see cref="Comb"/> (all false when there is none).</summary>
        public bool[] CombMask { get; }

        public int FlaggedBinCount => Flagged.Count(f => f);
    }

    /// <summary>
    /// Finds narrow spurs in a baseline (bandpass reference) spectrum, from that spectrum
    /// alone, and optionally fits a regular comb to them (issue #23, items 1 and 2).
    ///
    /// A receiver bandpass has no genuinely narrow features, so anything a few bins wide
    /// standing above the local bandpass shape is contamination - RFI picked up while the
    /// baseline was recorded. Because the decision never looks at a capture spectrum, using
    /// it to excise bins from both spectra cannot remove capture-only sky signal, which is
    /// what lets <see cref="HiStreamingPipeline.Process"/> apply it unconditionally.
    ///
    /// Detection fits a quadratic to a reference window either side of each bin (with a guard
    /// gap so the spur itself can't bias the fit, and one round of outlier clipping), and
    /// compares the bin with that prediction in units of the fit's own robust residual
    /// scatter. Fitting the local shape - rather than taking a flat local median, as
    /// <see cref="HiStreamingPipeline.Despike"/> does - is what catches spurs on the sloping
    /// part of the bandpass: a flat median/MAD window on a slope measures the slope, not the
    /// noise. On the 2026-11-08 baseline the four comb teeth Despike missed (-857, -790,
    /// +901, +969 kHz) measured only 2.5-7 sigma against a flat window but 29-39 sigma
    /// against a detrended one, with the bandpass falling ~0.1-0.2% per bin there.
    ///
    /// The DC/LO spike at the centre bin is deliberately left to RemoveDcSpike, which owns
    /// it; this detector never reports a spur within a few bins of the centre.
    /// </summary>
    public static class BaselineSpurDetector
    {
        /// <summary>Default detection threshold, in robust sigmas above the local fit.</summary>
        public const double DefaultThresholdSigma = 6.0;

        // Same physical widths HiStreamingPipeline.Despike uses (see SpikeMaxHalfWidthHz's
        // remarks there): how far a spur's skirt can extend either side of its peak.
        private const double MaxHalfWidthHz = 7000.0;
        private const int MaxHalfWidthMinBins = 3;

        // Reference window either side of the guard, as a multiple of the max half-width.
        private const int ReferenceWindowMultiple = 2;

        // Skirt growth threshold (hysteresis) - see HiStreamingPipeline.SpikeGrowSigmaCap.
        private const double GrowSigma = 2.5;

        // RemoveDcSpike's own window; the detector leaves these bins to it.
        private const int DcExclusionHalfWidthBins = 4;

        private const double ClipSigma = 3.0;

        // Comb fitting.
        private const int MinTeethPerPhase = 4;

        // Look-elsewhere-corrected false-alarm probability a comb fit must beat: the chance of
        // the explained spurs lining up this well at random, times the number of periods tried.
        private const double MaxChanceProbability = 1e-3;
        private const int MinTeethExplained = 6;
        private const double MaxCombMaskFraction = 0.05;
        private const double MinCombPeriodBins = 8.0;

        // The period search is O(spurs^2) per trial period; a baseline with more spurs than
        // this is too contaminated for a comb fit to mean much anyway.
        private const int MaxSpursForCombFit = 150;

        /// <summary>
        /// <paramref name="shiftedBaseline"/> is an averaged linear-power spectrum in
        /// fftshifted (monotonic frequency) order. Nothing is modified.
        /// </summary>
        public static BaselineSpurReport Detect(
            double[] shiftedBaseline,
            double sampleRateHz,
            bool fitComb = true,
            double thresholdSigma = DefaultThresholdSigma)
        {
            if (shiftedBaseline == null) throw new ArgumentNullException(nameof(shiftedBaseline));

            int n = shiftedBaseline.Length;
            var flagged = new bool[n];
            var spurs = new List<BaselineSpur>();
            double binHz = sampleRateHz > 0 && n > 0 ? sampleRateHz / n : 0;

            int halfWidth = MaxHalfWidthMinBins;
            if (binHz > 0)
                halfWidth = Math.Max(MaxHalfWidthMinBins, (int)Math.Round(MaxHalfWidthHz / binHz));
            int reference = ReferenceWindowMultiple * halfWidth;
            int reach = halfWidth + reference;

            if (n < 2 * reach + 1)
                return new BaselineSpurReport(flagged, spurs, null, new bool[n]);

            // Per-bin prediction and noise from the detrended local fit.
            var predicted = new double[n];
            var sigma = new double[n];
            var z = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (i < reach || i >= n - reach)
                    continue; // not enough reference either side - left undetected

                var (pred, sig) = LocalQuadraticFit(shiftedBaseline, i, halfWidth, reference);
                predicted[i] = pred;
                sigma[i] = sig;
                z[i] = sig > 0 ? (shiftedBaseline[i] - pred) / sig : 0;
            }

            int centre = n / 2;
            double growSigma = Math.Min(thresholdSigma, GrowSigma);

            int k = reach;
            while (k < n - reach)
            {
                if (z[k] <= thresholdSigma || Math.Abs(k - centre) <= DcExclusionHalfWidthBins)
                {
                    k++;
                    continue;
                }

                // Climb to the local maximum of this run of elevated bins.
                int peak = k;
                int j = k + 1;
                while (j < n - reach && z[j] > growSigma)
                {
                    if (z[j] > z[peak]) peak = j;
                    j++;
                }

                int lo = peak, hi = peak;
                while (lo - 1 >= reach && peak - (lo - 1) <= halfWidth && z[lo - 1] > growSigma) lo--;
                while (hi + 1 < n - reach && (hi + 1) - peak <= halfWidth && z[hi + 1] > growSigma) hi++;

                // Never reach into the DC window; RemoveDcSpike owns it.
                if (lo <= centre + DcExclusionHalfWidthBins && hi >= centre - DcExclusionHalfWidthBins)
                {
                    if (peak < centre) hi = Math.Min(hi, centre - DcExclusionHalfWidthBins - 1);
                    else lo = Math.Max(lo, centre + DcExclusionHalfWidthBins + 1);
                }

                for (int b = lo; b <= hi; b++)
                    flagged[b] = true;

                double excess = predicted[peak] > 0 ? shiftedBaseline[peak] / predicted[peak] - 1.0 : 0;
                spurs.Add(new BaselineSpur(peak, lo, hi, (peak - centre) * binHz, z[peak], excess));

                k = Math.Max(hi, j) + 1;
            }

            CombFit? comb = fitComb && spurs.Count <= MaxSpursForCombFit ? FitComb(spurs, n, binHz, reach) : null;
            var combMask = new bool[n];
            if (comb != null)
            {
                foreach (double t in comb.PredictedToothBins)
                {
                    int c = (int)Math.Round(t);
                    for (int b = c - comb.ToothHalfWidthBins; b <= c + comb.ToothHalfWidthBins; b++)
                        if (b >= 0 && b < n && Math.Abs(b - centre) > DcExclusionHalfWidthBins)
                            combMask[b] = true;
                }
            }

            return new BaselineSpurReport(flagged, spurs, comb, combMask);
        }

        /// <summary>
        /// Quadratic least-squares fit to data[i-reach .. i-halfWidth-1] and
        /// data[i+halfWidth+1 .. i+reach] (x centred on i), refitted once after dropping points
        /// more than ClipSigma robust sigmas from the first fit. Returns the fit evaluated at i
        /// and the robust (MAD-based) scatter of the surviving residuals.
        /// </summary>
        private static (double predicted, double sigma) LocalQuadraticFit(double[] data, int i, int halfWidth, int reference)
        {
            int count = 2 * reference;
            var xs = new double[count];
            var ys = new double[count];
            int m = 0;
            for (int x = -halfWidth - reference; x < -halfWidth; x++) { xs[m] = x; ys[m] = data[i + x]; m++; }
            for (int x = halfWidth + 1; x <= halfWidth + reference; x++) { xs[m] = x; ys[m] = data[i + x]; m++; }

            var use = new bool[count];
            Array.Fill(use, true);

            double[] coeffs = FitQuadratic(xs, ys, use);
            double sig = RobustResidualSigma(xs, ys, use, coeffs);

            bool clipped = false;
            for (int p = 0; p < count; p++)
            {
                if (Math.Abs(ys[p] - Eval(coeffs, xs[p])) > ClipSigma * sig)
                {
                    use[p] = false;
                    clipped = true;
                }
            }

            if (clipped && use.Count(u => u) >= 6)
            {
                coeffs = FitQuadratic(xs, ys, use);
                sig = RobustResidualSigma(xs, ys, use, coeffs);
            }

            double pred = coeffs[0];
            const double MinRelativeSigma = 1e-6;
            sig = Math.Max(sig, Math.Abs(pred) * MinRelativeSigma);
            return (pred, sig);
        }

        private static double Eval(double[] c, double x) => c[0] + c[1] * x + c[2] * x * x;

        private static double RobustResidualSigma(double[] xs, double[] ys, bool[] use, double[] coeffs)
        {
            var res = new List<double>(xs.Length);
            for (int p = 0; p < xs.Length; p++)
                if (use[p]) res.Add(Math.Abs(ys[p] - Eval(coeffs, xs[p])));
            if (res.Count == 0) return 0;
            res.Sort();
            return res[res.Count / 2] * 1.4826;
        }

        private static double[] FitQuadratic(double[] xs, double[] ys, bool[] use)
        {
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
            for (int p = 0; p < xs.Length; p++)
            {
                if (!use[p]) continue;
                double x = xs[p], y = ys[p], x2 = x * x;
                s0 += 1; s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
                t0 += y; t1 += x * y; t2 += x2 * y;
            }

            // Solve [[s0 s1 s2][s1 s2 s3][s2 s3 s4]] c = [t0 t1 t2] by Cramer's rule.
            double det = Det3(s0, s1, s2, s1, s2, s3, s2, s3, s4);
            if (Math.Abs(det) < 1e-300)
                return new[] { s0 > 0 ? t0 / s0 : 0, 0.0, 0.0 };

            double c0 = Det3(t0, s1, s2, t1, s2, s3, t2, s3, s4) / det;
            double c1 = Det3(s0, t0, s2, s1, t1, s3, s2, t2, s4) / det;
            double c2 = Det3(s0, s1, t0, s1, s2, t1, s2, s3, t2) / det;
            return new[] { c0, c1, c2 };
        }

        private static double Det3(double a, double b, double c, double d, double e, double f, double g, double h, double k) =>
            a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);

        /// <summary>
        /// Looks for a period P (bins) and a set of tooth phases such that the spurs fall on
        /// phase + k*P. Every spur family must contribute at least MinTeethPerPhase distinct
        /// teeth, at least MinTeethExplained spurs (and at least half of all spurs) must be
        /// explained, the alignment must be unlikely to be chance (binomial tail per family,
        /// multiplied across families, corrected for the number of periods tried - see
        /// MaxChanceProbability), and the predicted teeth may cover at most MaxCombMaskFraction
        /// of the band.
        /// Among passing candidates: most spurs explained, then fewest predicted teeth (so a
        /// sub-multiple of the true period, which predicts teeth that aren't there, loses),
        /// then fewest phases. The period is then refined by least squares over every explained
        /// spur. Never hardcodes a period - the source is external and may differ by session.
        /// </summary>
        private static CombFit? FitComb(List<BaselineSpur> spurs, int n, double binHz, int reach)
        {
            if (spurs.Count < MinTeethExplained)
                return null;

            double[] pos = spurs.Select(s => (double)s.PeakBin).ToArray();
            int m = pos.Length;
            const double tol = 1.0; // bins

            int toothHalfWidth = Math.Max(1, (int)Math.Ceiling(Median(spurs.Select(s => (s.HiBin - s.LoBin) / 2.0).ToList())));
            int validLo = reach, validHi = n - reach - 1;
            double validSpan = validHi - validLo + 1;

            double maxPeriod = validSpan / (MinTeethPerPhase - 1);
            (int explained, int predicted, int phases)? best = null;
            double bestPeriod = 0;
            List<List<int>>? bestClusters = null;

            // Number of trial periods, for the look-elsewhere correction below.
            int trials = 0;
            for (double q = MinCombPeriodBins; q <= maxPeriod; q += PeriodStep(q, tol, n))
                trials++;
            double maxLog10Chance = Math.Log10(MaxChanceProbability) - Math.Log10(Math.Max(1, trials));

            double p = MinCombPeriodBins;
            while (p <= maxPeriod)
            {
                var clusters = ClusterPhases(pos, p, tol);
                int explained = 0, predicted = 0;
                double log10Chance = 0;
                var accepted = new List<List<int>>();
                foreach (var cl in clusters)
                {
                    int distinctTeeth = cl.Select(idx => (int)Math.Floor(pos[idx] / p)).Distinct().Count();
                    if (distinctTeeth < MinTeethPerPhase) continue;
                    accepted.Add(cl);
                    explained += cl.Count;
                    double phase = CircularMeanPhase(cl.Select(idx => pos[idx]), p);
                    int familyTeeth = CountTeeth(phase, p, validLo, validHi);
                    predicted += familyTeeth;

                    // Chance that, given one spur fixes the phase, at least the remaining
                    // members of this family land within +-tol of a predicted tooth if the
                    // other spurs were scattered at random across the band.
                    double hitProbability = Math.Min(1.0, familyTeeth * (2 * tol + 1) / validSpan);
                    log10Chance += Log10BinomialTail(m - 1, cl.Count - 1, hitProbability);
                }

                if (accepted.Count > 0 &&
                    explained >= MinTeethExplained &&
                    explained * 2 >= m &&
                    log10Chance <= maxLog10Chance &&
                    (double)predicted * (2 * toothHalfWidth + 1) / n <= MaxCombMaskFraction)
                {
                    var score = (explained, predicted, accepted.Count);
                    if (best is null || IsBetter(score, best.Value))
                    {
                        best = score;
                        bestPeriod = p;
                        bestClusters = accepted;
                    }
                }

                p += PeriodStep(p, tol, n);
            }

            if (best is null || bestClusters is null)
                return null;

            // Refine: shared period, one offset per phase, least squares over members.
            var members = new List<(int phaseIdx, int k, double pos)>();
            for (int c = 0; c < bestClusters.Count; c++)
            {
                double anchor = pos[bestClusters[c][0]];
                foreach (int idx in bestClusters[c])
                    members.Add((c, (int)Math.Round((pos[idx] - anchor) / bestPeriod), pos[idx]));
            }

            double period = RefinePeriod(members, bestClusters.Count, bestPeriod);
            var phasesOut = new List<double>();
            var teeth = new List<double>();
            for (int c = 0; c < bestClusters.Count; c++)
            {
                var mine = members.Where(mm => mm.phaseIdx == c).ToList();
                double offset = mine.Average(mm => mm.pos - mm.k * period);
                double phase = ((offset % period) + period) % period;
                phasesOut.Add(phase);
                for (double t = phase; t <= validHi; t += period)
                    if (t >= validLo) teeth.Add(t);
            }

            teeth.Sort();
            double masked = (double)teeth.Count * (2 * toothHalfWidth + 1) / n;
            return new CombFit(period, period * binHz, phasesOut, best.Value.explained, teeth, toothHalfWidth, masked);
        }

        // Step so the accumulated drift across the band (n/P teeth) stays well inside tol.
        private static double PeriodStep(double period, double tol, int n) => Math.Max(1e-3, 0.5 * tol * period / n);

        /// <summary>log10 P(X >= k) for X ~ Binomial(trials, q).</summary>
        private static double Log10BinomialTail(int trials, int k, double q)
        {
            if (k <= 0) return 0;
            if (q <= 0) return double.NegativeInfinity;
            if (q >= 1) return 0;

            double logQ = Math.Log(q), log1mQ = Math.Log(1 - q);
            double maxTerm = double.NegativeInfinity;
            var terms = new List<double>();
            for (int j = k; j <= trials; j++)
            {
                double t = LogChoose(trials, j) + j * logQ + (trials - j) * log1mQ;
                terms.Add(t);
                maxTerm = Math.Max(maxTerm, t);
            }
            if (terms.Count == 0) return double.NegativeInfinity;
            double sum = terms.Sum(t => Math.Exp(t - maxTerm));
            return (maxTerm + Math.Log(sum)) / Math.Log(10);
        }

        private static double LogChoose(int a, int b)
        {
            double r = 0;
            for (int i = 1; i <= b; i++)
                r += Math.Log(a - b + i) - Math.Log(i);
            return r;
        }

        private static bool IsBetter((int explained, int predicted, int phases) a, (int explained, int predicted, int phases) b)
        {
            if (a.explained != b.explained) return a.explained > b.explained;
            if (a.predicted != b.predicted) return a.predicted < b.predicted;
            return a.phases < b.phases;
        }

        /// <summary>Greedy circular clustering of pos mod period: biggest group within ±tol first.</summary>
        private static List<List<int>> ClusterPhases(double[] pos, double period, double tol)
        {
            int m = pos.Length;
            var phase = new double[m];
            for (int i = 0; i < m; i++)
                phase[i] = pos[i] % period;

            var taken = new bool[m];
            var clusters = new List<List<int>>();
            while (true)
            {
                int bestAnchor = -1, bestCount = 0;
                for (int a = 0; a < m; a++)
                {
                    if (taken[a]) continue;
                    int count = 0;
                    for (int b = 0; b < m; b++)
                    {
                        if (taken[b]) continue;
                        double d = Math.Abs(phase[a] - phase[b]);
                        if (Math.Min(d, period - d) <= tol) count++;
                    }
                    if (count > bestCount) { bestCount = count; bestAnchor = a; }
                }

                if (bestAnchor < 0 || bestCount < MinTeethPerPhase)
                    break;

                var group = new List<int>(bestCount);
                for (int b = 0; b < m; b++)
                {
                    if (taken[b]) continue;
                    double d = Math.Abs(phase[bestAnchor] - phase[b]);
                    if (Math.Min(d, period - d) <= tol) { group.Add(b); taken[b] = true; }
                }
                clusters.Add(group);
            }
            return clusters;
        }

        private static double CircularMeanPhase(IEnumerable<double> positions, double period)
        {
            double sx = 0, sy = 0;
            foreach (double q in positions)
            {
                double ang = 2 * Math.PI * (q % period) / period;
                sx += Math.Cos(ang);
                sy += Math.Sin(ang);
            }
            double mean = Math.Atan2(sy, sx) / (2 * Math.PI) * period;
            return mean < 0 ? mean + period : mean;
        }

        private static int CountTeeth(double phase, double period, int validLo, int validHi)
        {
            int count = 0;
            for (double t = phase; t <= validHi; t += period)
                if (t >= validLo) count++;
            return count;
        }

        /// <summary>
        /// pos = offset_c + k*P: centring k and pos within each phase removes the offsets, so
        /// P is the pooled within-phase regression slope.
        /// </summary>
        private static double RefinePeriod(List<(int phaseIdx, int k, double pos)> members, int phases, double fallback)
        {
            double num = 0, den = 0;
            for (int c = 0; c < phases; c++)
            {
                var mine = members.Where(mm => mm.phaseIdx == c).ToList();
                if (mine.Count < 2) continue;
                double kMean = mine.Average(mm => mm.k);
                double pMean = mine.Average(mm => mm.pos);
                foreach (var mm in mine)
                {
                    num += (mm.k - kMean) * (mm.pos - pMean);
                    den += (mm.k - kMean) * (mm.k - kMean);
                }
            }
            return den > 0 ? num / den : fallback;
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            return values[values.Count / 2];
        }
    }
}
