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
| 1 | LSR correction fixes (#21) | done, see "Outcome" under Phase 1 |
| 2 | Overlapped frame averaging (#22) | not started |
| 3 | Frame and bin rejection before averaging (#23) | items 1–2 done 2026-11-20; frame rejection and spectral kurtosis not started |
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

### External reference: H1-HUNTER SDR (reviewed 2026-11-23)

H1-HUNTER SDR (Jarkko Suominen, Python, GPL v3) does a similar job to RASTA with an RTL-SDR.
Phil's fork is cloned at `C:\Source\repos\H1-HUNTER-SDR`. Its processing code and methods document
were read on 2026-11-23 and compared with this plan; nothing from it was run. Phases below point
at it where it has a worked implementation ("H1-HUNTER:" notes), and items it has that this plan
lacked are under "Candidate additions from the H1-HUNTER review" near the end.

- Processing lives in `h1h/physics.py` (spectra, RFI, calibration model, LSR), `h1h/receiver.py`
  (frame flagging, spectral kurtosis, simulator) and `h1h/maps.py` (velocity cube, moment maps,
  longitude–velocity, rotation curve, top view).
- `docs/methods/build_methods.py` is the source of its methods PDF and states every formula,
  threshold and default in one place. Read that before the code.
- Its figures were measured on a different receiver chain (0.7–0.9 m dish, default 512-point FFT).
  Treat thresholds as starting points and confirm them on RASTA data.
- Licence: GPL v3 can be combined into RASTA's AGPL v3 with attribution. Reimplementing from the
  methods document avoids the question; say which was done in the phase's outcome notes.

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
5. **Phase 5:** calibrate to Kelvin by hot/cold (Y-factor) measurement, by fitting the LAB survey,
   or both? See the hot/cold item under "Candidate additions from the H1-HUNTER review". Settle
   this before starting Phase 5.
6. **Candidate additions (#32–#36):** issues exist; in what order, relative to the numbered
   phases, should they be done?

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
  apex to J2000 (expect roughly RA 18h 03.8m, Dec +30.0°; compute it rather than trusting that
  figure). Worth up to about 0.35 km/s. An earlier version of this plan said 18h 07.8m, which was
  wrong: H1-HUNTER uses RA 270.95934°, Dec +30.00467° (J2000) with 20 km/s (`h1h/common.py`,
  `APEX_RA_DEG`/`APEX_DEC_DEG`), and 270.959° is 18h 03.8m.
- **Earth's orbit is treated as circular.** Add the first-order eccentricity term to the orbital
  velocity (e ≈ 0.0167). Worth up to about 0.5 km/s.

**Verification.** Compare against astropy for a spread of at least ten cases (different RA/Dec,
dates across the year, hour angles either side of the meridian). Python is at `C:\Python314`
and is not on PATH. Use the kinematic LSR explicitly (20 km/s toward the standard apex); astropy's
default `LSR` frame uses a different solar motion and will disagree by several km/s if left at
defaults. Target agreement: within 0.1 km/s.

**Knock-on.** `LsrCorrectionKmPerSec` is stored in `SpectrumCacheEntry`, so bump `PipelineVersion`.

**Related.** The 0.1 km/s target also needs the receiver's frequency error dealt with (1 ppm is
about 0.3 km/s). See the frequency-correction item under "Candidate additions from the H1-HUNTER
review".

**Outcome (2026-11-23).** Each finding was re-derived and then measured against JPL Horizons
(DE441) instead of astropy: Horizons gives the velocity of a surface point relative to the Solar
System barycentre in ICRF axes, so the reference is `v_observer · u_target + 20 km/s · u_apex ·
u_target` with none of RASTA's own astronomy in it. `scripts/Get-LsrReferenceVectors.ps1` fetches
the vectors; 14 cases are in `AstronomyUtilsTests`.

- Diurnal sign: confirmed wrong, fixed.
- Circular orbit: confirmed, replaced with the first-order elliptical velocity.
- Apex: **the finding was wrong.** The constant (270.9592°, +30.0047°) already was the B1900
  18h/+30° apex precessed to J2000; only its comment said B1900. A test now recomputes the
  precession (270.95932°, +30.00466°).
- Found instead: the orbital velocity was built from the Sun's longitude at the equinox of date
  but dotted with a J2000 target, a 0.37° frame mismatch in 2026 (up to 0.2 km/s). The longitudes
  are now precessed back to J2000.
- Result: worst error 1.23 km/s before, 0.020 km/s after. `PipelineVersion` is 3.
- Not done: the function assumes J2000 RA/Dec, and nothing checks which epoch the mount reports
  (ASCOM `EquatorialSystem`). Of-date coordinates would cost up to ~0.3 km/s. With a ~10° beam this
  is academic for pointing, but it is the same size as the errors fixed here.

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

**Outcome so far (2026-11-20).**
- Baseline cleaning and comb masking are in `HiPipeline/BaselineSpurDetector.cs`, wired into
  `HiStreamingPipeline.Process` (cleaning always on, `combMask` opt-in, exposed as Visualise's
  Comb Mask checkbox and mirrored into Mosaic). Tests: `HiPipeline/BaselineSpurDetectorTests.cs`.
- The four missed band-edge teeth: `Despike`'s flat median/MAD reference window measures the
  bandpass slope there instead of the noise. A local quadratic fit fixes it. See `CLAUDE.md`.
- The comb has **four** teeth per 292.96 kHz period, not three (the fourth, near −42 kHz, is weak).
- Snapshot `phase3-clean` (defaults): no comb dip above 6σ at any of the 40 baseline spur positions
  in any capture. `phase3-combmask` is the same with comb masking on; it changes little on this
  session because the baseline's own teeth are already all detected.
- `ReferenceSnapshotTests` now also writes `{label}_baseline_spurs.csv`, and honours
  `RASTA_SNAPSHOT_COMBMASK=1`.
- Still to do: frame rejection, spectral kurtosis (both need `PipelineVersion` bumps). The northern
  positions (Dec 70–90) remain noisy and are the obvious test case for them.

**H1-HUNTER: design notes for the two remaining items (2026-11-23).**
- **Frame rejection** (`h1h/receiver.py`, `Receiver._process`). Each frame is tested on 17 values,
  not one: mean power of the trusted band plus of 16 equal sub-bands. Each is compared with the
  median and MAD of the last 2048 frames, and the frame is flagged if any exceeds 5 robust sigma.
  Only increases are flagged. The sub-bands catch a narrowband burst that barely moves total
  power, which the total-power-only test described above would miss. Adopt the sub-band test.
  RASTA has the whole dwell in memory, so a whole-dwell median/MAD can replace the running one,
  unless power drifts over the dwell.
- **Spectral kurtosis** (`h1h/receiver.py`, `Averager._sk_feed`/`_sk_eval`; constants in
  `h1h/common.py`). Two differences from the work item above:
  - It tests each channel in blocks of M = 1024 frames and removes only the flagged blocks from
    that channel (it keeps ΣP and the count removed per channel, so the cleaned average is exact).
    One SK value over a whole dwell can only keep or drop the channel outright. A channel that
    loses more than half its blocks is dropped whole.
  - **Correction to the work item above:** SK does not respond to a weak steady carrier. Their
    simulation shows a pulsed carrier removed completely and a weak steady one not detected. So
    SK is for pulsed and switching interference; steady spurs stay with `BaselineSpurDetector`
    and `Despike`.
  - Their bounds for Hann frames at 50% overlap, M = 1024, are 0.836 and 1.214 (0.135% false
    alarms each side, checked by simulation through their own FFT). Use them as the starting
    point after Phase 2 and confirm on clean RASTA data. Frames already rejected in time are left
    out of the block; a block with under half its frames left is not tested.
  - Their simulation also shows the hydrogen line itself is not flagged (it is Gaussian noise).
    Repeat that check with `SyntheticSpectrum`.
- **Line protection for `Despike`.** Their narrow-channel search (`h1h/physics.py`,
  `rfi_channels`) adds a test `Despike` does not have: a candidate's excess over a 7-channel
  median must be at least 0.7 of its excess over a 31-channel median, so a channel riding on a
  broad feature (the line) is not a candidate. `Despike` already caps the excised width
  (`MaxHalfWidthHz`). Consider adding the narrow-share test if `Despike` is ever seen clipping a
  real line.

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

**H1-HUNTER (2026-11-23).** It defines the line window in frequency (±0.8 MHz around the rest
frequency, narrowed automatically so baseline channels remain, plus a 0.2 MHz guard) and trusts
only the central 80% of the band (`h1h/physics.py`, `resolve_half_mhz`, `trusted_mask`,
`baseline_fit`). Its baseline is a straight line, and its methods document lists the leftover tilt
as a known limitation, which supports the polynomial here. Its optional gain-drift correction
divides the ratio by its median over the line-free channels (`line_free_level`), the zero-order
case of the normalised output above.

**Verification.** Synthetic: with a curved bandpass mismatch and an injected line, the polynomial
mode recovers the line amplitude within noise where the linear mode leaves a visible slope or
bow. Scaling the capture power by 1.2× leaves the normalised output unchanged.

---

## Phase 5 — Kelvin calibration against the LAB survey

**Goal.** Report brightness temperature in Kelvin, and show a survey profile for comparison.

**Depends on.** Phase 4's normalised output.

**Open decision (5).** H1-HUNTER gets to Kelvin without a survey, by a hot/cold measurement. See
the hot/cold item under "Candidate additions from the H1-HUNTER review". The two approaches can
coexist: hot/cold gives the scale offline, and the beam-averaged LAB profile below becomes the
check on it. Its author reports a ratio of 0.98–1.08 against LAB at a 20° beam with beam
efficiency taken as 1.0 (Galactic hydrogen fills the beam and sidelobes), so do not apply a
main-beam efficiency below 1 to the line here either.

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

**H1-HUNTER (2026-11-23).** `h1h/maps.py` `velocity_arrays` computes channel, integrated-intensity
and mean-velocity maps from a velocity cube (`VelCube`: spectra resampled onto one fixed LSR axis
per sky cell, so positions with different LSR corrections line up). Its mean velocity uses only
channels above 2σ, with σ from the MAD of the cube. Its line integral fills interference-removed
channels by interpolation rather than leaving them out (`h1h/physics.py`, `line_integral`), which
otherwise lowers the integral. `smooth_sphere` smooths a sky grid with a width that grows as
1/cos(latitude) along RA, the same correction the 2D heatmap needs if it is ever smoothed.

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

**H1-HUNTER (2026-11-23): a simpler measurement that removes the Phase 4 dependency.**
- Measure the noise from differences of neighbouring channels instead of an rms over line-free
  channels (`h1h/physics.py`, `measure_noise`): `d = (b − a) / mean(a, b)` for adjacent channels,
  σ = 1.4826 · MAD(d) / √(2·(1 − ρ)), where ρ is the correlation between adjacent channels caused
  by the window. A slope, a curved baseline and the line itself barely affect it, so it needs no
  line-free mask and works on a single raw spectrum (what issue #17 needs for a baseline).
- `noise_model(win, hop)` computes, from the window and frame step, the variance factor v₁ for
  overlapped frames (relative variance of K averaged frames is v₁/K; 1.056 for Hann at 50%
  overlap) and ρ. Port it rather than using the "1.5 bins" approximation above; it stays correct
  if the window or overlap changes. `SpectrumBinner` changes ρ and must be allowed for.
- Predicted relative noise is √(v₁/K + v₁/K_baseline).
- Their "radiometer check" plots measured and predicted noise against K. A curve that follows
  1/√K shows the average is noise-limited. Consider it as the display.
- On a raw spectrum before division, stable fine bandpass structure reads as noise (their example:
  0.326% raw against 0.100% expected, 0.141% against 0.142% after division). Label a
  raw-spectrum figure as a guide only.

**Verification.** On synthetic noise the ratio is close to 1. With the neighbour-difference
measurement this phase is independent of the others (with Phase 2 done first, or not, v₁ follows
from the frame step actually used).

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

**H1-HUNTER (2026-11-23).** `h1h/maps.py` `rotation_curve` is the same tangent-point method with
three differences worth taking:
- It also uses the 4th quadrant (270° < l < 360°), where the terminal velocity is the most
  negative one. From the UK little of that is visible, but the code costs nothing.
- It restricts longitudes to 10°–85° from the centre direction, where the method is reliable.
- Its defaults are R₀ = 8.15 kpc and V₀ = 236 km/s (Reid et al. 2019) rather than the older IAU
  8.5 / 220. Use the newer values as defaults, and expect the curve near 236, not 220.

It also reports enclosed mass, M(<R) = V²R/G. A face-on "top view" built from the same
longitude–velocity data is listed under "Candidate additions from the H1-HUNTER review".

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

## Candidate additions from the H1-HUNTER review

Found on 2026-11-23 by reading H1-HUNTER SDR (see "External reference" near the top). Each has a
GitHub issue (created 2026-11-23) except the Dicke load. None is a numbered phase or scheduled
yet. Ordered roughly by value for effort.

- **Receiver frequency correction (#32).** `RtlSdrDevice.SetPpmCorrection` is an empty stub, so the
  crystal error goes straight into every velocity: 1 ppm is 1.42 kHz, about 0.3 km/s, larger than
  Phase 1's 0.1 km/s target. H1-HUNTER sets the integer ppm in the tuner, applies the fractional
  part to the frequency axis, and stores the value in every file. It also has a test that counts
  delivered samples against the PC clock to estimate the error (`h1h/receiver.py`,
  `_run_ppm_test`; only as good as the PC clock). Work here: implement the stub, add a ppm
  setting, record it in `FitsFileMetaData`, apply it to the frequency axis. A capture and its
  baseline must use the same value. Small and independent.

  **Outcome (2026-11-23).** Reimplemented from H1-HUNTER's description, no code copied.
  `RASTA.Core/Sdr/FrequencyCorrection` splits a ppm value into the whole part for the tuner
  (rounded half away from zero) and a residual that scales both the centre frequency and the
  sample rate (they share the crystal), which gives the true axis exactly. A persisted PPM
  Correction setting on Prepare feeds Calibrate Device Gain and Capture Baseline; the profile
  records it, and sweeps/Quick Capture use the profile's value like its gain, so a capture
  always matches its baseline. Prepare flags a setting that differs from the loaded
  calibration. FITS gets `PPM` and `PPM_HW`; Visualise and Mosaic apply the residual to the
  axis and refuse to combine a baseline and capture with different values (no card = 0, so
  old files reduce as before). No `PipelineVersion` bump (old cache entries read the new
  fields as null, which is correct). Not done: the PC-clock measurement aid.
- **Clipping check on every capture (#33).** RASTA checks ADC saturation only during the gain sweep
  (`Calibrator.ComputeSaturationFraction`). H1-HUNTER checks every block (raw bytes ≤ 3 or ≥ 252)
  and rejects a calibration capture with more than 0.1% clipped samples. Work here: compute the
  fraction for baseline and sweep/Quick captures, warn or reject, and record it in the FITS
  header. Small and independent.
  **Done (2026-11-24):** `RASTA.Core/Sdr/AdcClipping` (rail bytes 0/255 only, limit 0.05% — kept
  stricter than H1-HUNTER's ≤3/≥252 and 0.1%) is shared by the gain sweep, the baseline (refused
  over the limit, nothing written: `BaselineClippedException`), and sweep/Quick captures (kept,
  logged, one warning per sweep, FITS `CLIPFRAC`). `CachedSpectrumLoader` measures it from the
  raw IQ itself, so Visualise (Clipping field) and Mosaic (Clipping column, status line) show it
  for old files too once re-read; entries cached before #33 show "not measured".
- **Hot/cold (Y-factor) calibration to Kelvin (#34).** Capture cold sky, then a hot load (the ground, or
  an absorber over the feed) at air temperature:
  `Y = median(P_hot / P_cold)` over line-free channels,
  `T_sys = (T_hot − Y·T_cold) / (Y − 1)`,
  `T_A = (T_sys + T_cold) · P / P_cold − T_sys`.
  T_cold is computed, not assumed (`h1h/physics.py`, `cold_reference`, `spillover_model`): CMB
  2.725 K, Galactic continuum 0.7 + 4.0·exp(−|b|/8°) K, atmosphere from air temperature and
  altitude, and ground spillover from altitude and dish diameter. Needs no network. RASTA already
  captures the cold half; the new parts are a hot-capture step on Prepare, an air-temperature
  setting, the sky model, and the two results stored on `CalibrationProfile`. Their stated
  accuracy is 10–20% absolute (a 10 K error in T_hot is about 3.7%). Hot and cold must share
  receiver settings, and hot is rejected if Y ≤ 1.01. Check the hot load does not saturate at the
  gain chosen by the gain sweep. This is decision 5.
- **Milky Way top view (#35).** `h1h/maps.py` `topview_grid` places each longitude–velocity cell at its
  kinematic distance for a flat rotation curve, seen from the Galactic north pole. Emission with
  two possible distances (near and far) is drawn in a separate layer from emission with one, and
  directions within 10° of l = 0° and 180° are left out. A follow-on to Phase 8, using its data.
- **Hardware simulator (#36).** `h1h/receiver.py` `SimSource` is a 75-line fake RTL-SDR: receiver noise,
  bandpass ripple and roll-off, a two-component line, cold/hot scenes, crystal error, DC spike, a
  spur, interference bursts, clipping at high gain and slow gain drift. A model for an
  `ISdrDevice` in the empty `RASTA.Simulators` project, which would let Capture and Prepare be
  exercised without hardware.
- **Dicke reference load.** A USB relay switches the LNA input to a resistor every N averages to
  track gain drift. Needs hardware Phil does not have; noted for completeness only.

---

## Considered and left out

- **Smoothing the baseline alone before dividing.** This is standard practice and would reduce
  the baseline's noise contribution. `CLAUDE.md` records that smoothing both spectra before
  division was tried and made things worse; the likely cause is fine-scale structure common to
  both that only cancels unsmoothed. Revisit only after Phase 3, when that structure can be
  flagged instead. H1-HUNTER offers exactly this as an option (`smooth_reference`: spikes removed,
  25 kHz median, 60 kHz cubic Savitzky–Golay) and its author measured it as worse on an RTL-SDR
  for the same reason: 0.219% noise with the smoothed reference against 0.141% with the measured
  one (0.142% expected). That supports leaving it out.
- **IQ imbalance correction.** With the centre at 1420.7 MHz the line's mirror image falls in
  band, but at a level far below the noise for current integration times.
