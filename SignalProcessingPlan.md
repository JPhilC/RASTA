# Signal Processing Improvement Plan

A staged plan for improving how RASTA detects the hydrogen line and what it produces from it.
It comes from a read-through review of the HI pipeline on 2026-11-18. Nothing in the review was
run or measured, so every claim below is from reading the code and should be confirmed as part
of the phase that acts on it.

## How to use this plan

- Work one phase per session, in order unless a phase says it is independent.
- Read `CLAUDE.md` first. It is the authoritative description of the current pipeline, and it
  records several experiments that were tried and reverted. Do not repeat them without a new reason.
- Each phase ends with: a clean `dotnet build RASTA.slnx`, the verification listed for that
  phase, an update to the relevant section of `CLAUDE.md` (and `HiPipelineDescription.md` where
  the pipeline steps change), and a tick in the status table below.
- Commit only when Phil asks.

### Rules that apply to every phase

- **Leave `SkaoPipelineProcessor.cs` alone.** It exists as an unmodified cross-check against the
  SKAO reference algorithm.
- **Bump `SpectrumCacheService.PipelineVersion`** whenever a change affects anything upstream of,
  or stored in, `SpectrumCacheEntry` (the averaged spectrum, the frame counts, the cached LSR
  correction). Phases 1, 2 and 3 all do.
- **Keep the three averaging paths consistent.** Frames are FFT'd and averaged in
  `CachedSpectrumLoader.Load` (Visualise and Mosaic), `Calibrator` (the baseline stored on
  `CalibrationProfile`), and `CaptureViewModel` (the live spectrum). A change to how frames are
  averaged must either reach all three or state why one is exempt.
- **New behaviour that changes existing numbers goes behind a setting first**, defaulting to the
  old behaviour until Phil has compared before and after on real data.
- There is no automated test suite. See Phase 0.

### Status

Tracking issue: #31. Each phase has its own GitHub issue, shown in the table.

Tracking issue: #31. Each phase has its own GitHub issue, shown in the table.

| Phase | Title | Status |
|---|---|---|
| 0 | Verification groundwork | done 2026-11-18 |
| 1 | LSR correction fixes (#21) | not started |
| 2 | Overlapped frame averaging (#22) | not started |
| 3 | Frame and bin rejection before averaging (#23) | not started |
| 4 | Continuum fit and gain-independent line scale (#24) | not started |
| 5 | Kelvin calibration against the LAB survey (#25) | not started |
| 6 | Moment maps for the mosaic (#26) | not started |
| 7 | Noise readout (#27) | not started |
| 8 | Longitude–velocity diagram and rotation curve (#28) | not started |
| 9 | Gaussian component fitting (#29) | not started |
| 10 | Frequency-switched baseline (#30) | not started |

### Related GitHub issues (checked 2026-11-18)

| Issue | Relation to this plan |
|---|---|
| #17 Baseline quality rating | Consumes Phase 3's baseline spur/comb detection and Phase 7's radiometer noise prediction. Build those as shared functions; #17 owns the UI. |
| #16 Cold-sky picker favours higher elevation | Independent. Reduces how often Phase 3's baseline cleaning has work to do. Phase 10 would reduce reliance on the picker altogether. |
| #20 Batch charts and animation | Independent of the pipeline, but its per-folder spectrum stack is the same data Phase 8's longitude–velocity diagram needs. Build the stack once and share it. |
| #18 Capture sky-map window, #3 Calibration/plan mismatch warning | Unrelated to signal processing. |

### Decisions needed from Phil

1. ~~Phase 0: enable a real test project?~~ Decided: yes. `RASTA.Tests` is an NUnit project.
2. ~~Reference data.~~ Decided: `D:\Raw\RASTA\Captures\1420.7000MHz\2026-11-08` is the only real
   session available (one cold-sky baseline, 27 single-file sweep positions at RA 21h and 22h,
   Dec 0° to 90°). Its baseline is the one found contaminated by the spur comb, so treat it as a
   regression reference, not as clean data.
3. **Phase 4:** once compared, should the gain-independent scale replace the `× 300` display
   scale outright, or stay as a selectable mode?
4. **Phase 10:** is frequency switching worth the capture-side changes, given the 8 November
   finding that the cold-sky baseline was contaminated by the pointing-dependent spur comb?

---

## Phase 0 — Verification groundwork

**Goal.** Have a repeatable way to check numeric changes before touching the pipeline.

**Work.**
- If Phil agrees: add a test framework package to `RASTA.Tests`, reference `RASTA.Core` and
  `RASTA.Processing`, delete the placeholder `Class1.cs`, and re-enable the project's `Build`
  flag in `RASTA.slnx`. Update the "no automated test suite" note in `CLAUDE.md`.
- Add a synthetic-spectrum helper: a known bandpass shape, Gaussian noise of known variance, and
  one or more Gaussian emission lines of known amplitude, centre and width. Later phases test
  against it.
- Record "before" outputs for the reference data set (decision 2): the HI Velocity chart for the
  single pair, and the Positions table for the mosaic session.

**Done when.** A test (or scratch harness) runs `HiStreamingPipeline.Process` on a synthetic
spectrum and recovers the injected line's centre velocity.

**Outcome.**
- `dotnet test RASTA.Tests/RASTA.Tests.csproj` runs the suite. See the Commands section of
  `CLAUDE.md`.
- `RASTA.Tests/Synthetic/SyntheticSpectrum.cs` is the synthetic-spectrum helper. Later phases
  should add their tests next to `HiPipeline/HiStreamingPipelineTests.cs`.
- `ReferenceData/ReferenceSnapshotTests.cs` writes a real-data snapshot when
  `RASTA_SNAPSHOT_LABEL` is set. The pre-change snapshot is
  `RASTA.Tests/ReferenceSnapshots/phase0-before.csv` (+ `_spectra.csv`). After each phase, write
  a new one (`phaseN-after`) and compare.
- The snapshot's shared velocity axis carries only the first position's LSR correction (a
  property of `MosaicResult`), so compare per-position `PeakVelocityKmPerSec` for velocities.

---

## Phase 1 — LSR correction fixes

**Goal.** Make `AstronomyUtils.ComputeLsrCorrectionKmPerSec` accurate to about 0.1 km/s. A native
channel is about 0.12 km/s wide (586 Hz at 2.4 Msps / 4096), so today's errors span several channels.

**Findings.**
- **Diurnal term has the wrong sign** (`RASTA.Core/Astro/AstronomyUtils.cs`, the `vRotation`
  line). The observer's velocity is eastward. Projected onto the target direction it is
  `V·cos(lat)·cos(dec)·sin(RA − LST)`, which is `−V·cos(lat)·cos(dec)·sin(HA)`. The code uses
  `+sin(HA)`. Error is up to about 0.6 km/s at a mid-northern latitude. Re-derive this
  independently before changing it.
- **Solar apex is in B1900 coordinates** but is dotted with a J2000 target vector. Precess the
  apex to J2000 (expect roughly RA 18h 07.8m, Dec +30.0°; compute it rather than trusting that
  figure). Worth up to about 0.35 km/s.
- **Earth's orbit is treated as circular.** Add the first-order eccentricity term to the orbital
  velocity (e ≈ 0.0167). Worth up to about 0.5 km/s.

**Verification.** Compare against astropy for a spread of at least ten cases (different RA/Dec,
dates across the year, hour angles either side of the meridian). Python is at `C:\Python314`
and is not on PATH. Use the kinematic LSR explicitly (20 km/s toward the standard apex); astropy's
default `LSR` frame uses a different solar motion and will disagree by several km/s if left at
defaults. Target agreement: within 0.1 km/s.

**Knock-on.** `LsrCorrectionKmPerSec` is stored in `SpectrumCacheEntry`, so bump `PipelineVersion`.

---

## Phase 2 — Overlapped frame averaging

**Goal.** About 1.37× better signal-to-noise from the same captures, by not discarding data.

**Finding.** `CachedSpectrumLoader.Load` steps Hann-windowed frames end to end. A Hann window
gives little weight to the samples near each frame's edges, so nearly half the data contributes
almost nothing. Stepping by half a frame (Welch's method, 50% overlap) roughly halves the
variance of the averaged spectrum.

**Work.**
- Step frames by `fftSize / 2` in `CachedSpectrumLoader.Load`, `Calibrator`'s baseline averaging,
  and `CaptureViewModel`'s live chunk processing.
- **Do not let a frame straddle a file boundary.** `FitsFileIo.ReadCombinedRawIq` concatenates
  multi-file dwell points after trimming each to whole frames; a half-step frame at a join would
  mix two physically discontinuous captures. Either carry the per-file byte offsets through, or
  restart the half-step sequence at each file's start.
- `AveragedFrames` now counts overlapped frames. Check every reader of it (Phase 7 will need the
  number of *independent* frames, about half as many).
- Leave the SKAO path (`AccumulateBinnedPower`) unoverlapped; it is unwindowed and must match the
  reference.
- Bump `PipelineVersion`.

**Verification.** On synthetic noise, the per-bin scatter of the averaged spectrum drops by
about 1.37× for the same sample count, and the mean level is unchanged. On the reference pair,
the HI Velocity chart is visibly less noisy with the line at the same place and height.

---

## Phase 3 — Frame and bin rejection before averaging

**Goal.** Stop short interference bursts and non-noise-like bins contaminating a whole dwell.
Today every frame is summed unconditionally and RFI is only addressed afterwards, on the
already-averaged spectrum (`Despike`).

**Evidence from the reference session (2026-11-18).** The comb appears as *dips* in every
position's spectrum, so it was picked up by the cold-sky baseline and is being divided into
every capture. With the existing opt-in `Despike` on (snapshot `phase0-despike`), 19 of the 23
comb dips disappear and line-free rms roughly halves (about 4–8 down to 2–3). Four teeth near
the band edges survive (−857, −790, +901, +969 kHz from centre; the last two still 25 sigma);
why they are missed has not been diagnosed. Positions toward Dec 60–90 then show the same
channel (+36.6 km/s on the shared axis) as their peak, consistent with the comb also being
present, positively, in captures toward the north.

**Work.**
- **Clean the baseline first (do this before the rest of the phase).** A bandpass has no real
  narrow features, so anything narrow in the baseline is contamination. Generalise what
  `RemoveDcSpike` already does at the centre bin to the whole band: detect spurs from the
  baseline alone (robust sigma test, plus the comb fit below), and interpolate those bins in the
  baseline and identically in the capture. Because detection never looks at the capture it
  cannot remove capture-only signal, so this can be always-on rather than opt-in. Capture-side
  detection stays the separate opt-in `Despike`. Diagnose the four missed band-edge teeth as
  part of this.
- **Warn at calibration time: tracked as GitHub issue #17** (baseline quality rating before
  accepting). This phase supplies the detection #17 needs (baseline spur/comb detection as a
  reusable function returning flagged bins, and the fitted comb period if any); the dialog,
  rating and retake flow belong to #17, not here.
- **Frame rejection.** In `CachedSpectrumLoader.Load` the raw IQ is already in memory, so make two
  passes: compute each frame's total power, take the median and MAD, and exclude frames more than
  a configurable number of robust sigmas above the median. Record how many were dropped in
  `SpectrumCacheEntry` and show it next to `ChartSource`.
- **Spectral kurtosis per bin.** Accumulate ΣP² alongside ΣP. For M frames the estimator
  `SK = ((M+1)/(M−1)) · (M·ΣP² / (ΣP)² − 1)` is about 1 for noise-like bins and departs from 1 for
  both steady carriers and intermittent RFI. Store the per-bin SK array in `SpectrumCacheEntry`.
  Downstream, offer flagged bins to the same union-and-interpolate excision `Despike` already uses.
  Overlapped frames (Phase 2) are correlated, which widens the estimator's spread; set the
  threshold empirically from clean data rather than from the textbook `2/√M`.
- **Comb-aware masking.** The 2026-11-08 session's spurs are a regular comb: three teeth per
  292.97 kHz period (exactly 500 native bins at 4096 / 2.4 Msps), the strongest at −90.8 kHz from
  centre and the other two 113.1 and 225.6 kHz above it, each 1–2 bins wide (measured from
  `ReferenceSnapshots/phase0-before_spectra.csv`). Once `Despike` or the kurtosis test has
  flagged enough teeth, fit the period and phase from the flagged bins and mask every predicted
  tooth position, including ones too weak to be detected individually. Fit per session rather
  than hardcoding these numbers: the source is external and may differ another day. Require
  several teeth to agree before trusting a fit, and never mask more than a few percent of the band.
- Keep all of these opt-in at first, consistent with how `Despike` was introduced.
- The live Capture spectrum can skip this; say so in `CLAUDE.md`.
- Bump `PipelineVersion`.

**Verification.** Synthetic: inject a burst into 1% of frames and a weak steady carrier in one
bin; both are flagged, and clean noise produces close to the expected false-flag rate. Real: the
8 November spur-comb captures should show the comb bins flagged.

---

## Phase 4 — Continuum fit and gain-independent line scale

**Goal.** A flatter baseline under the line, and a line height that does not move with receiver
gain drift.

**Findings.**
- `FitContinuumFromEdgeWindows` fits a straight line through two windows of about 6% of the band
  each, at fixed channel positions. That is a noisy fit, and a straight line cannot absorb the
  curvature left when the baseline was captured at a different time or temperature.
- `HiSpectrum = ratio × 300 − fit` is in arbitrary units that scale with the ratio of the two
  captures' gains.

**Work.**
- Add a continuum mode that fits a polynomial (order configurable, default 2) over every
  line-free channel: outside a configurable velocity window (default |v_LSR| > 150 km/s) and
  inside the usable passband (excluding the filter roll-off at each edge). Keep the existing
  outlier rejection. Keep the edge-window mode available.
- Add a normalised output: `ratio / continuumFit − 1`. This is the line temperature as a fraction
  of system temperature and is independent of gain. `RatioDisplayScale` cancels out of it.
- Expose both as Visualise settings and mirror them into `MosaicViewModel` the same way
  `SmoothingKind` and `DespikeEnabled` are mirrored.
- Check that the line-free window still leaves enough channels when the centre frequency is the
  default 1420.7 MHz, where the line sits off-centre.

**Verification.** Synthetic: with a curved bandpass mismatch and an injected line, the polynomial
mode recovers the line amplitude within noise where the linear mode leaves a visible slope or
bow. Scaling the capture power by 1.2× leaves the normalised output unchanged.

---

## Phase 5 — Kelvin calibration against the LAB survey

**Goal.** Report brightness temperature in Kelvin, and show a survey profile for comparison.

**Depends on.** Phase 4's normalised output.

**Work.**
- Add a system-temperature setting (persisted via `UserOptionsService`). Kelvin = normalised
  output × system temperature.
- Add a calibration action: for a capture at a standard position (S7 or S8), fetch the LAB
  profile and solve for the system temperature that best matches it over the line.
  `LabSurveyProfileParser` and `scripts/Fetch-LabSurveyGrid.ps1` already cover parsing and the
  service's URL parameters.
- **Account for the beam.** The LAB survey beam is about 0.6°; RASTA's 1.4 m dish has a beam of
  about 10.5°. A single LAB profile is not comparable. Average LAB profiles over a grid covering
  the beam, Gaussian-weighted by `AntennaUtils.ComputeBeamwidthDeg`. The result still folds in
  beam efficiency, so describe the outcome as an effective system temperature.
- Optionally overlay the beam-averaged LAB profile on the HI Velocity chart for any capture.
- Fetching is a network call. Make it an explicit user action with results cached on disk, so
  nothing else in the app depends on being online.

**Verification.** After calibrating on one standard position, a capture of a second standard
position agrees with its beam-averaged LAB profile in peak temperature to within about 20%.

---

## Phase 6 — Moment maps for the mosaic

**Goal.** Map hydrogen rather than total received power.

**Finding.** `MosaicProcessor.FindLinePeak` reports the single strongest ratio channel. A maximum
over noisy channels is biased upward, and because it is referenced to the baseline rather than
to each position's own continuum it mostly tracks gain and ground-spillover differences.

**Work.**
- Compute per position, from the continuum-subtracted spectrum over the velocity window:
  - integrated intensity, ∫T dv (K km/s once Phase 5 is in; normalised units before that);
  - column density, 1.823×10¹⁸ × ∫T_B dv cm⁻², valid where the line is optically thin;
  - intensity-weighted mean velocity, using only channels above about 3σ so noise does not drag
    it toward the window centre.
- Add these to `MosaicPosition` next to the existing fields and extend `MosaicSurfaceMetric` and
  the 2D heatmap to select among them. Keep peak strength available.
- Update `StrengthUnitLabel` handling so each metric carries its own unit.
- `LabSurveyMosaicProcessor` should produce the same fields so both sources stay comparable.
- Stretch: show the embedded HI4PI column density (`Hi4PiSkyMap`) at each position alongside the
  measured value.

**Verification.** On a LAB-sourced folder, integrated intensity along a Galactic-plane strip
peaks on the plane and falls off with latitude. On the reference mosaic session, the integrated
map is less sensitive to which baseline file is used than the peak map is.

---

## Phase 7 — Noise readout

**Goal.** Tell at a glance whether a spectrum is limited by integration time or by systematics.

**Work.**
- Measure the rms of the normalised spectrum over the line-free channels.
- Predict it from the radiometer equation: `1 / √(Δν · τ)` per spectrum, combined in quadrature
  for capture and baseline, where Δν is the channel's effective noise bandwidth (1.5 bins for a
  Hann window, scaled by any `SpectrumBinner` factor) and τ is the integration time actually
  used after any Phase 3 rejection.
- Show measured, predicted and their ratio on the Visualise tab.
- Put the prediction in `RASTA.Processing` as a shared function: issue #17 needs the same
  figure for a single baseline spectrum.

**Verification.** On synthetic noise the ratio is close to 1. Independent of other phases once
Phase 4's line-free mask exists.

---

## Phase 8 — Longitude–velocity diagram and rotation curve

**Goal.** The classic amateur HI result: spiral arms as tracks in longitude against velocity.

**Work.**
- New Mosaic tab. For positions within a configurable latitude band of the plane, convert
  pointing to Galactic longitude (`AstronomyUtils.EquatorialToGalactic`) and render each
  position's spectrum as a column of an image: longitude across, LSR velocity up, intensity as
  colour (`HeatmapImageBuilder.Ramp`).
- Rotation curve: for longitudes 0°–90°, find each spectrum's terminal velocity (the most
  positive velocity where emission exceeds a threshold), then `R = R₀·sin(l)` and
  `V = v_term + V₀·sin(l)` with R₀ and V₀ as settings (defaults 8.5 kpc, 220 km/s).
- Issue #20 (batch charts and animation) needs the same per-folder stack of reduced spectra.
  Whichever is built first should produce it in a form the other can reuse.
- Validate against a LAB-sourced Galactic strip (`scripts/New-GalacticStripGridPoints.ps1`)
  before real data.

**Verification.** The LAB strip shows the expected sinusoidal envelope in velocity against
longitude, and the derived rotation curve is roughly flat near 220 km/s.

---

## Phase 9 — Gaussian component fitting

**Goal.** Decompose a profile into its velocity components, typically one per spiral arm.

**Work.**
- Least-squares fit of a sum of Gaussians to the continuum-subtracted spectrum, seeded from
  peaks in a smoothed copy. `MathNet.Numerics` is already a dependency of
  `RASTA.Infrastructure`; check whether `RASTA.Processing` can use its optimisers directly.
- Report each component's centre, width and amplitude; overlay the components and their sum on
  the chart.
- Cap the component count and reject components below a signal-to-noise threshold (uses Phase 7).

**Verification.** Synthetic spectra with two and three overlapping components are recovered
within the fit's own uncertainties.

---

## Phase 10 — Frequency-switched baseline

**Goal.** A bandpass reference with no hydrogen in it, taken at the same pointing as the capture.

**Why.** Cold sky at high Galactic latitude still carries a few Kelvin of local hydrogen near
0 km/s, so dividing by it removes a little of every target's line. A separate baseline pointing
also sees a different interference environment from the target (see the spur-comb finding of
2026-11-08).

**This is the largest phase and changes capture, not just processing. Design it with Phil before
writing code.** Points to settle:
- Shift size (about 1 MHz moves the line out of the central part of the band) and whether to
  alternate shifted and unshifted files within one dwell point.
- How the shifted file is recorded and paired: a new `FitsFileMetaData` field and a
  `FitsPathBuilder` naming convention, and how `ResolveRelatedCaptureFiles` and
  `MosaicProcessor` group them.
- The receiver's bandpass is fixed in baseband, so the shifted spectrum is divided bin for bin;
  the sky frequency axis is what moves.
- Interference fixed in sky frequency will appear at two different bins and will not cancel.
- Whether the cold-sky baseline stays as the default with frequency switching as an alternative.

**Verification.** At a high-latitude position, a frequency-switched reduction shows the weak
local line that the cold-sky-baselined reduction of the same data suppresses.

---

## Considered and left out

- **Smoothing the baseline alone before dividing.** This is standard practice and would reduce
  the baseline's noise contribution. `CLAUDE.md` records that smoothing both spectra before
  division was tried and made things worse; the likely cause is fine-scale structure common to
  both that only cancels unsmoothed. Revisit only after Phase 3, when that structure can be
  flagged instead.
- **IQ imbalance correction.** With the centre at 1420.7 MHz the line's mirror image falls in
  band, but at a level far below the noise for current integration times.
