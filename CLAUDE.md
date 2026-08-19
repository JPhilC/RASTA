# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

RASTA (Radio Astronomy Slew • Track • Acquire) is a hobby-grade .NET 10 WPF/MVVM app for amateur
hydrogen-line (1420 MHz / 21cm) radio astronomy: it drives an ASCOM Alpaca telescope mount and an
RTL-SDR receiver through a four-stage workflow — **Prepare → Plan → Capture → Visualise** —
to capture raw IQ data and reduce it into HI spectra. It's exploratory/experimental; large parts of
the pipeline are still being reworked (see "Known incomplete / placeholder areas" below) — don't
assume a component is finished or wired up just because it exists.

## Commands

The solution file is **`RASTA.slnx`** (the newer XML solution format), not a `.sln` — glob/search
for `*.sln` will silently miss it.

```
dotnet build RASTA.slnx                          # build everything
dotnet build RASTA.App/RASTA.App.csproj           # build just the WPF app (fastest inner loop)
dotnet run --project RASTA.App/RASTA.App.csproj   # run the app (Windows only; needs real or absent hardware handled gracefully)
```

- **Platform is effectively x64-only.** `RASTA.App`, `RASTA.Core`, and `RASTA.Infrastructure` all
  hardcode `<PlatformTarget>x64</PlatformTarget>` regardless of the `AnyCPU;x64` platforms list —
  `RASTA.Infrastructure` ships native `librtlsdr.dll` / `libusb-1.0.dll` / `libwinpthread-1.dll`
  alongside itself, and those are x64. Building `AnyCPU` can produce a binary that fails to load the
  SDR driver at runtime.
- **There is no automated test suite yet.** `RASTA.Tests` and `RASTA.Simulators` are both placeholder
  projects (a single unused `Class1.cs` each, no test framework package referenced), and both are
  explicitly excluded from the default build in `RASTA.slnx`
  (`<Build Solution="*|x64" Project="false" />` / `Debug|Any CPU` likewise). Don't invent
  `dotnet test` instructions or assume NUnit is available — the README's mention of "NUnit tests" is
  aspirational, not current state. If you add real tests, you'll need to wire up the test framework
  and re-enable the project's `Build` flag in `RASTA.slnx` yourself.
- No `.editorconfig`, linter, or formatter is configured in the repo.
- The app needs a Windows desktop session to run (WPF) and talks to real or absent hardware — an
  RTL-SDR device (must be plugged in before the Plan view becomes usable) and an ASCOM Alpaca Remote
  Server for the mount. It degrades gracefully (hot-plug watched, connect/disconnect handled) rather
  than requiring hardware to be present to start.

## Architecture

### Project layering

Dependencies flow one way: `RASTA.Core` ← `RASTA.Infrastructure` ← `RASTA.Processing` ← `RASTA.App`.

- **RASTA.Core** — domain models and interfaces only, no concrete hardware/IO deps beyond `FITS.Lib`:
  `ITelescopeMount`, `ISdrDevice`, `IFftEngine`, `IPlanRepository`, coordinate/target types
  (`TargetPoint`, `CoordinateMode`, `CapturePlan`), `FitsFileMetaData` (the FITS header schema — see
  below), `AstronomyUtils` (LST/Julian-date/RA-Dec↔AltAz math, its inverse `HorizontalToEquatorial`,
  and `ComputeLsrCorrectionKmPerSec` — a low-precision analytic LSR Doppler correction, good to a few
  tenths of a km/s, not JPL-ephemeris precision).
- **RASTA.Infrastructure** — concrete implementations: `RtlSdrDevice` (wraps the `RtlSdrManager`
  NuGet package + native RTL-SDR driver), `AscomAlpacaClient`/`AscomTelescopeMount` (ASCOM **Alpaca**
  Remote Server, not direct COM), `FftEngine` (FFT via `MathNet.Numerics`), JSON-backed repositories
  (`JsonPlanRepository`, `CalibrationRepository`, `JsonObservationStorage`).
- **RASTA.Processing** — pure algorithms, no UI/hardware: the HI reduction pipeline
  (`HiPipeline/`), a small shared DSP helper (`Dsp/SavitzkyGolay.cs`), calibration (`Calibrator`),
  sweep planning (`SweepPlanner`), and visualisation data builders (`Gridding/`, `VisualisationData/`).
- **RASTA.App** — WPF MVVM shell. `App.xaml.cs` is the single composition root: one
  `ServiceCollection` is built once at startup (no scopes are ever created afterward, so
  `AddScoped` view models in practice behave like singletons for the app's lifetime — don't assume a
  fresh instance is created each time `NavigateTo<T>()` runs).

### Navigation

There's no router/framework — `NavigationService.NavigateTo<TViewModel>()` just resolves the view
model from the root DI container and `NavigationViewModel` (bound to `MainWindow`) swaps
`CurrentViewModel`. The four workflow stages map 1:1 to `PrepareViewModel` / `PlanViewModel` /
`CaptureViewModel` / `VisualiseViewModel`. `NavigatePlan` is gated on `StatusBarViewModel.SdrConnected`
only (an SDR must be enumerated before you can move past Prepare) — Plan itself doesn't need a mount,
since `PlanType`/`CoordinateMode` is a free choice on the Plan screen, not detected from a connected
mount. `NavigateCapture` additionally requires `StatusBarViewModel.TelescopeConnected`, since it
drives an actual mount slew and `CaptureViewModel.LoadAvailablePlans` filters plans by the mount's
detected `CoordinateMode` — neither means anything without a mount attached. `CaptureViewModel` no
longer takes its plan from `PlanViewModel.SelectedPlan` on
navigation — it builds its own `AvailablePlans` list (`LoadAvailablePlans`, using the same
`IPlanRepository.ListPlans(sdrDeviceId)` call `PlanViewModel.LoadSavedPlans` uses) filtered to
whichever `PlanType` matches the connected mount's current `TelescopeState.Mode` (`PlanMatchesMountMode`
— Equatorial/AltAz plans need the mount actually in that mode to slew correctly; Drift plans, being a
declination-based drift scan, are offered under Equatorial). The list refreshes reactively on
`SdrState.SelectedDevice`/`TelescopeState.Mode`/`IsConnected` changes, and `NavigateCapture` also
calls it explicitly so edits made on the Plan screen show up immediately. Because `ListPlans`
deserializes fresh `CapturePlan` instances on every call, `LoadAvailablePlans` re-resolves the current
selection by `FriendlyName` against the newly loaded instances rather than relying on reference
equality, dropping it only if no plan with that name is still offered.

### Tooltips

Every plain-string `ToolTip="..."` in the app wraps into a sensibly-sized box via an implicit
`Style TargetType="ToolTip"` in `RASTA.App/Styles/Styles.xaml` (`MaxWidth="480"` plus a
`ContentTemplate` that puts `{Binding}` in a `TextBlock TextWrapping="Wrap"`) — WPF's default
`ToolTip` template doesn't wrap on its own, it just sizes to content, which is what made the longer
Visualise tooltips span the whole window unwrapped before this. Literal `&#x0a;` newlines inside a
tooltip string still render as paragraph breaks even with wrapping on, since WPF's `TextBlock` honors
them directly - no need to avoid them for this reason.

### The HI reduction pipeline (the scientific core)

`RASTA.Processing/HiPipeline/HiPipelineProcessor.cs` (documented in detail in
`HiPipelineDescription.md`) is the FFT-size-agnostic, actively-used pipeline:

- `HiStreamingAccumulator` — sums arbitrary numbers of baseline/capture power frames, exposes
  `GetAveragedSpectra()` (needs both) or `GetBaselineAverage()` (baseline-only, used during
  calibration before any capture frames exist).
- `HiStreamingPipeline` — fftshift → DC/LO-spike excision (see below) → frequency axis → velocity
  axis (radio convention, centered on 1420.40575177 MHz) → baseline division (bandpass flattening)
  → linear continuum fit from two small edge windows (channel-index based, RFI-outlier-rejected —
  *not* a velocity-magnitude mask over most of the spectrum, despite what the fraction names might
  suggest) → continuum subtraction → optional Savitzky–Golay smoothing.
- `HiStreamingProcessor` — convenience wrapper combining the two.

`HiPipeline/SpectrumBinner.cs` is what implements the FFT-size-agnostic part for a **Target FFT
Size** smaller than a file's native size: `BinAverage` averages groups of physically-adjacent bins
of the *already-FFT'd, already frame-averaged* power spectrum together — it never touches the raw
IQ. It supersedes a since-removed `IqDownscaler`, which shrank the raw IQ *before* the FFT by
block-averaging groups of consecutive time-domain samples. That looked plausible but was a real bug:
block-averaging D consecutive time samples and then FFT-ing the shorter result is decimation, and
decimating in time *aliases* the spectrum — each output bin ended up combining a native bin with
another one a whole output-length away (e.g. a 4096→2048 downscale folded the middle of the band together with
its far edge) rather than averaging nearby frequencies. That's exactly why lowering Target FFT Size
used to make a spectrum *noisier* and erase its bandpass shape instead of smoothing it. It was mostly
invisible in the combined baseline/capture ratio view only because the same aliasing pattern hit both
spectra identically and roughly canceled in the division — not because the math was actually correct
there. `VisualiseViewModel.ProcessBaseline`/`ProcessCapture`/`ProcessHiCore` and
`MosaicProcessor.ProcessFolderAsync` all now FFT/accumulate at a file's **native** FFT size
unconditionally, then call `SpectrumBinner.BinAverage` on the averaged result only if `TargetFftSize`
is smaller. `ProcessBaseline`/`ProcessCapture` didn't respect `TargetFftSize` at all before this fix
(always native) — it's now usable on the standalone single-file charts too, not just the combined
HI/Ratio modes. `ProcessSkaoTtrt` (the SKAO reference cross-check) no longer downscales at all — it
passes native-rate raw IQ straight to `SkaoHiObservation.ProcessIq`, whose own fixed 256-point FFT is
meant to run against a native-rate capture in the first place; the old pre-shrink step there was never
part of the actual reference algorithm, so removing it makes that mode *more* faithful, not less.

`HiStreamingPipeline.Process` unconditionally runs `RemoveDcSpike` right after the fftshift: every
zero-IF SDR (including the RTL-SDR this app targets) leaks a fixed LO/DC-offset spike at exactly the
tuned center frequency, which after fftshift always lands on the array's center bin regardless of
pointing — a receiver artifact, not sky signal (this is what produced the "spike always at ~1420.41
MHz no matter where the scope pointed" symptom when the center frequency happened to be tuned close
to the HI rest frequency itself). Rather than hardcoding a fixed bin window to blank — which could
just as easily discard genuine HI emission if the tuned center ever coincided with a target's actual
line frequency — the window is *detected from the baseline spectrum alone* (`LocalMedianExcluding` +
a threshold-ratio scan outward from the center bin, capped at `DcSpikeMaxHalfWidthBins`). The
baseline file is always a cold-sky capture (see "Calibration flow" below — a terminator is used only
for the separate gain-sweep step, never for the baseline FITS file itself), deliberately chosen off
the HI-rich Galactic plane, so it carries no strong, narrow line of its own; any bin that spikes there
against the surrounding continuum is unambiguously instrumental, not sky signal. The decision never
inspects the capture spectrum, so it structurally cannot excise real, capture-only signal. Only bins
actually elevated in the baseline get linearly interpolated
away, identically, in both baseline and capture, before anything downstream (ratio, continuum fit)
sees them. `SkaoPipelineProcessor` deliberately does *not* get this fix — it exists specifically as
an unmodified cross-check against the SKAO reference algorithm.

`RemoveDcSpike`'s baseline-only trust also means it structurally *won't* catch a second, distinct
artifact discovered after moving the default center frequency to 1420.7 MHz: a narrow, ~3-4x-elevated
spur at a fixed **relative** offset from center (~+100 kHz — a rational fraction of the 2.4 MHz sample
rate, consistent with a typical RTL-SDR self-generated "birdie"), confirmed pointing-invariant across
an entire night's sweep (same bin regardless of RA/Dec) but *absent from that same session's baseline*
— it only appears once a real antenna/LNA is on the front end, not with a terminator (this finding
predates the cold-sky baseline migration described in "Calibration flow" below — worth re-confirming
against a cold-sky baseline, which also has a real antenna/LNA on the front end, rather than assuming
it still holds unchanged). Since it isn't
baseline-verified the way the DC spike is, blanking it in `HiStreamingPipeline` itself would reintroduce
exactly the "might discard genuine signal" risk `RemoveDcSpike` was designed to avoid, so it's left in
the data. Instead `SpectrumViewModel.ApplyRobustYAxisRange` (used by both `UpdateSpectrum` overloads)
sets the Y-axis from the 1st/99th percentile of the plotted spectrum plus a margin rather than raw
`Min()`/`Max()`, so a handful of outlier bins like this can no longer squash the genuine spectral shape
into a flat line — a display-only fix that never touches the underlying data.

`HiStreamingPipeline` also exposes an **opt-in** narrowband-RFI despike (`Despike` — a single-spectrum
overload and a baseline+capture-pair overload used by `Process` via its `despike`/
`despikeThresholdSigma` parameters), aimed at things like a USB3/mount-controller comb spur — a
different problem from the receiver's own DC/LO spike (`RemoveDcSpike`, above, stays always-on and
separate). Unlike `RemoveDcSpike`'s baseline-only trust, this runs on whichever spectrum it's given
and is opt-in for exactly that reason. Detection is a robust (MAD-based) local sigma test swept across
every bin (`MarkSpikeCandidates`/`LocalRobustStatsExcluding`) rather than a fixed dB-ratio threshold —
a real spur is often only a couple of dB above the local continuum, but once a spectrum is heavily
averaged the residual noise floor shrinks enough that even a small spur reads as many sigmas out.
Detection uses a stricter threshold (`despikeThresholdSigma`, default
`HiConstants.DefaultDespikeThresholdSigma` = 5) than the growth/hysteresis pass that widens each flagged
bin outward (capped at the more permissive `SpikeGrowSigmaCap` = 2.5), so a spike's shoulder bins —
individually more modest than its peak — still get excised instead of leaving a "flattened top, sloped
sides" artifact; the growth/reference window widths are specified in Hz and converted to bins from the
actual `sampleRateHz`/FFT size, since the underlying feature (measured wider than a bare Hann main lobe
alone would produce, consistent with real modulation bandwidth) is physically fixed in Hz, not bin
count. The pair overload flags each spectrum independently (their averaging depth — and therefore noise
floor — commonly differs) but excises the *union* of flagged bins identically in both, since
interpolating only one would hand the baseline-division step a spike/smooth mismatch it didn't have
before. Exposed as `VisualiseViewModel.DespikeEnabled`/`DespikeThresholdSigma` (mirrored into
`MosaicViewModel` so Mosaic processing follows the same toggle), `VisualiseViewModel.
ExportDespikeDebugCsvCommand` (a raw-vs-despiked CSV dump written next to the source FITS file), and
`CapturePlan.DespikeEnabled` (applies it to `CaptureViewModel`'s *live* spectrum while a sweep runs, off
by default like the pipeline itself).

`SpectrumViewModel` also renders a fixed vertical dashed reference line (`Sections`, a zero-width
`RectangularSection`) at the unshifted HI rest position — 0 km/s for `HiVelocity` (that axis is
LSR-corrected, so 0 means "at rest relative to the LSR"; real emission still typically shows up offset
from it due to the source's own galactic kinematics) or the static HI rest frequency
(`HiConstants.HiFreqHz`) for the frequency-axis modes (`HiFrequency`/`TTRT`/`Ratio`, never
LSR-corrected) — repositioned on every `Mode` change (`OnModeChanged` → `UpdateHiReferenceLine`).

`HiPipeline/SkaoPipelineProcessor.cs` is a separate, fixed-256-bin port of the SKAO TTRT reference
pipeline, kept for cross-checking against `HiStreamingPipeline`'s FFT-size-agnostic version.
`VisualiseViewModel` exposes it plus three other modes as `SpectrumMode`: `HiFrequency`, `HiVelocity`,
`TTRT`, `Ratio` (the bandpass-flattened capture/baseline ratio *before* continuum subtraction —
strictly positive, unlike `HiSpectrum`, so it's the one mode that can validly be shown in dB; see
`VisualiseViewModel.UseDbScale`/`ToDb`).

`HiStreamingPipeline.Process` (and `HiStreamingProcessor.Compute`) take an optional
`lsrCorrectionKmPerSec` parameter (default 0), added as a flat offset to every channel's velocity —
`VisualiseViewModel.ProcessHiCore` computes it via `AstronomyUtils.ComputeLsrCorrectionKmPerSec` from
the **capture** file's recorded pointing/time/site (never the baseline — even though the baseline file
now has its own real, meaningful cold-sky pointing, it's a calibration reference position, not the
target actually being observed, so LSR correction is about the capture's target, not the baseline's),
falling back to 0 for files that predate that metadata.

`HiStreamingPipeline.Process` also takes optional `smoothing`/`smoothingWindow`/`smoothingPolyOrder`
parameters (`RASTA.Processing.Dsp.SmoothingKind`: `None`/`SavitzkyGolay`/`MovingAverage`, default
`None`) applying an optional final smoothing pass to `HiSpectrum` only — never `RatioSpectrum` or the
continuum fit, both already computed beforehand. `VisualiseViewModel` exposes this as `SmoothingKind`/
`SmoothingWindow` (default window 21 bins). This is unrelated to the fixed 5-point kernel in
`RASTA.Processing/Dsp/SavitzkyGolay.cs`, which stays reserved for `SkaoPipelineProcessor`'s
unconditional, unmodified reference smoothing (`savgol_filter(sp, 5, 2)`, matching the SKAO algorithm
exactly) — `HiStreamingPipeline`'s `SavitzkyGolay` option instead reuses the general, arbitrary-
window/order SG implementation (`SavitzkyGolaySmooth`, scipy `mode='interp'`-equivalent edge handling)
already present for RFI-outlier detection in the continuum fit. `MovingAverage` (`RASTA.Processing/
Dsp/MovingAverage.cs`) is a plain centered boxcar average offered as a deliberately blunter
alternative: it smooths harder for a given window but flattens/broadens real features more than SG's
local-polynomial fit, since a real HI line (tens to hundreds of kHz wide) is far broader than a single
FFT bin (~586 Hz at 2.4 Msps/4096 FFT) — the fixed 5-bin kernel barely perturbs a spectrum at that
scale, which is why a wide, user-tunable window (not a different algorithm) is what actually reveals
a line.

`ApplySmoothing` (the dispatch `Process` uses internally) is `public static`, reused directly by
`VisualiseViewModel.ProcessBaseline`/`ProcessCapture` to smooth their own averaged spectrum (post-
`SpectrumBinner`, post-Despike) so a single file's own noise can be judged before it's ever combined
with its counterpart — those two views ignored `SmoothingKind` entirely before this session. `Process`
itself deliberately still smooths only the *final* `HiSpectrum`, not `baselinePower`/`capturePower`
individually before dividing them, even though that's what the single-file views do and even though it
was tried: `RatioSpectrum` is multiplied by `HiConstants.RatioDisplayScale` (300) before continuum
subtraction, so even a small (few-percent) residual per-bin scatter left in each *independently*
smoothed spectrum — invisible on that file's own dB-scale plot — combines in quadrature through the
division and then gets amplified ~300×, making the combined chart noisier than smoothing the
already-divided result directly. Smoothing the final `HiSpectrum` only has to reduce noise in the one
already-amplified quantity actually being displayed, which is structurally more effective per unit of
window size than requiring both inputs smoothed enough that their combined residual is separately
invisible.

Frame-level power spectra are computed by `IFftEngine.ComputeSkAoPower` (Hann-windowed, SKAO-style
`|FFT/N·2|²` normalization) — this is the one to use for anything that will be averaged and fed
into `HiStreamingAccumulator`, as opposed to `ComputeSpectrum` (unwindowed, single-frame; the last
remaining caller is `RtlSdrDevice`'s own internal use, not anything in the HI pipeline) or
`PowerSpectrum` (raw, takes `Complex[]` directly). Note `ComputeSkAoPower` deliberately does *not*
fftshift its output (DC stays at index 0) — callers that display an averaged spectrum without running
it through `HiStreamingPipeline.Process` (e.g. `VisualiseViewModel.ProcessBaseline`/`ProcessCapture`)
must call the now-public `HiStreamingPipeline.FftShift` themselves before plotting, or a receiver
DC/LO-leakage spike ends up misplaced at the edge of the frequency axis instead of the center.

**`RASTA.Processing/IfAverage/*` has been removed.** It was a signal-averaging chain (median filter
→ RFI detector → intermediate/long-term *moving-average* → background subtract/divide → dB
conversion → Savitzky-Golay) ported from Daniel M. Kamiński's SDR AVE plugin for SDR#. Comparing it
against the original plugin surfaced why it never picked up the HI line when tried: both the port and
the original average in a fixed-size sliding ring buffer (a live-display design, meant to keep
refreshing a real-time view), not a cumulative average — run once over a whole recorded FITS capture,
it only ever reflects the last `Intermediate.Window × LongTerm.Window` raw FFT frames (a couple of
seconds), discarding the rest of the dwell regardless of how long it ran. It also averaged in
`sqrt(power)` space rather than power, compressing the faint HI excess further (Jensen's inequality).
The plugin's real design was to export short block-averages to text files and let the user co-add
those externally (e.g. in Excel/MATLAB) — RASTA never implemented that export/co-add step, so the
port was never doing full-dwell integration either way. `SpectrumMode.IF` and its combined-file "IF
Spectrum" chart mode (which drove it) were removed entirely, including from `SpectrumModeValues.All`;
`ProcessBaseline`/`ProcessCapture` (the standalone baseline/capture charts) now set
`SpectrumVm.Mode = SpectrumMode.HiFrequency` instead. The one real dependency the chain had -
`SavitzkyGolay` (also used by `HiStreamingPipeline`/`SkaoPipelineProcessor` for their own optional
smoothing pass) - was promoted to `RASTA.Processing/Dsp/SavitzkyGolay.cs` rather than deleted.

### Calibration flow

`PrepareViewModel` exposes calibration as **three independent, button-triggered steps** on
`PrepareView` rather than one monolithic run, so an interrupted session (app closed, SDR unplugged,
etc.) can be resumed at whichever step it got to instead of starting over:

1. **Load Last Calibration** (`LoadLastCalibrationCommand`) — loads whatever `CalibrationProfile`
   `CalibrationRepository` has on disk via `CalibrationService.TryLoadSavedCalibrationAsync`, gain-only
   or complete, and sets it as `Calibration`. Always available (`CanLoadLastCalibration`, gated only on
   nothing else currently running) since it touches no hardware.
2. **Calibrate Device Gain** (`CalibrateGainCommand`, gated on `IsConnectedSdr` — no mount needed) —
   runs `Calibrator.RunGainSweepAsync`: for each SDR-supported gain, capture raw IQ against a terminator
   on the SAWbird H1+ LNA input, hard-reject any gain where raw I/Q bytes show real ADC saturation
   (`ComputeSaturationFraction` — a fraction-of-samples-at-the-rail check on the raw bytes, not a
   spectral-domain proxy; threshold `SaturationFractionThreshold` = 0.05%), then score survivors on a
   full-buffer-averaged power spectrum's flatness/spur-count/slope (each metric min-max normalized
   across candidates before weighting, so the weights are meaningful). A short settle period
   (`GainSettleTimeSec`) is discarded at the start of each trial so a gain-switching transient doesn't
   bias that gain's averaged spectrum. The chosen gain is immediately wrapped into a *gain-only*
   `CalibrationProfile` (`CalibrationService.SaveGainOnlyCalibrationAsync` — empty `BaselineSpectrum`)
   and persisted to disk right away, so this step alone survives an app restart and Load Last
   Calibration can pick it back up ready for step 3.
3. **Capture Baseline** (`CaptureBaselineCommand`, gated on `Calibration != null` — a profile must
   already be started or loaded — **and** the mount connected, since it needs to slew) — prompts to
   reconnect the antenna, then computes 4 candidate "cold sky" positions via `ColdSkyLocator.FindCandidates`
   (a static, pure algorithm in `RASTA.Processing/Calibration/` — scans an Az/El grid above the horizon
   limit, converts each point to Galactic l/b via `AstronomyUtils.EquatorialToGalactic`, keeps only
   points clear of the HI-rich Galactic plane at the coldest `|b|` threshold that still yields enough
   candidates, then greedily picks `count` maximizing minimum azimuth separation — spread widely so
   there's a decent chance one is conveniently unobstructed from wherever the mount sits). These show
   in `ColdSkySelectionWindow` (a modal `Window`, not a `MessageBox` — the one other place this app
   needed a genuinely custom dialog); the mount then slews there (`SlewToRaDecAsync`/`SlewToAzAltAsync`,
   chosen from the connected mount's live `_mount.Mode`, not a plan). `CaptureBaselineAsync` then loops:
   ask the user to confirm the *actual, physically slewed-to* position is unobstructed (e.g. no
   building/tree in the way); "No" excludes that azimuth (`ColdSkyLocator`'s `excludeAzimuthsDeg`, a
   ~20° exclusion radius since a real obstruction blocks a range of azimuths, not one exact bearing) and
   returns to the picker rather than capturing against whatever's in the way. The picker itself also
   offers **Recalculate** (asks for a fresh set, excluding whatever's currently shown, without closing
   the dialog) and **Cancel Calibration** (aborts the whole step), so the user can escape the loop and
   reposition the mount by hand if nothing offered works. Once a position is confirmed,
   `Calibrator.CaptureColdSkyBaselineAsync` runs a *separately configurable* dwell
   (`BaselineDwellSeconds` in `SettingsViewModel`/`PrepareViewModel`, decoupled from the gain sweep's own
   dwell) raw IQ capture at the gain/frequency/sample-rate/FFT-size already recorded on `Calibration`,
   written to FITS with full site+pointing metadata (see "Capture and FITS conventions" below), and
   also reduced to an averaged linear-power baseline via `HiStreamingAccumulator` + `ComputeSkAoPower`
   (matching exactly how the observation capture side will later be averaged, so the two are directly
   comparable when `HiStreamingPipeline.Process` divides one by the other) — producing the completed
   profile, persisted over the gain-only one. All of this exists because a terminator baseline leaves a
   spurious edge hump in `HiSpectrum` — baselining against real, line-free sky fixes it, chosen
   automatically rather than by hand.

`PrepareViewModel.IsCalibrated` is derived straight from `Calibration` (`Calibration.BaselineSpectrum.Length
> 0`) rather than tracked as a separate flag, so it can't drift out of sync with what's actually loaded;
`CalibrationService.IsCalibrationAvailable` mirrors the same rule and is what `CaptureViewModel`'s
`CanCaptureSweep`/`CanDriftCapture`/`CanQuickCapture` gate on, so a gain-only profile (no baseline yet)
can't be picked up for an actual capture. `CalibrationProfile` carries the chosen baseline's `Baseline*`
pointing (Az/Alt, RA/Dec, Galactic latitude) so a reload can show where a saved profile's baseline
actually pointed. `CaptureBaselineAsync`'s `finally` returns the mount home (tracking off first, then
`FindHomeAsync` — mirroring `CaptureViewModel.CaptureSweepAsync`'s end-of-sweep handling, and needed
because this mount specifically refuses a slew while tracking is active) regardless of how the step
ended; the status bar only reads "Baseline capture complete." once the mount is confirmed home after an
actual success, leaving a prior "Cancelled."/"Failed." status alone otherwise. `CaptureViewModel` (see
"Capture and FITS conventions" below) already reads `CalibrationService.CurrentCalibration.BaselineSpectrum`
directly for its live spectrum rather than re-reading any FITS file, for both a sweep and Quick Capture.

### Sweep planning

`SweepPlanner.BuildSweep` turns a `CapturePlan`'s `TargetRange` (RA/Dec or Az/El start, end, and step
— whichever pair applies is picked from `plan.PlanType`, which in turn follows the connected mount's
own coordinate mode, not a user toggle) into an ordered `List<TargetPoint>`. Two things worth knowing:

- **Range direction is start/end-agnostic.** `StepRange` steps from start to end using the *absolute*
  step magnitude, automatically counting downward if `end < start` — so e.g. `RAStartHours=20,
  RAEndHours=4` sweeps downward through 20h→4h just as validly as the reverse. It steps by an integer
  count rather than repeated float addition, so accumulated rounding error can't drop the final point.
- **Points are ordered greedily by elevation, not raster order.** After the raw RA/Dec or Az/El grid
  is generated, `BuildSweep` repeatedly picks whichever *remaining* point would be highest in the sky
  at its estimated arrival time (accounting for slew time from the current position) as the next
  target — not simply the next one in scan order. For AltAz plans elevation is time-invariant, so this
  reduces to visiting highest-elevation points first; for Equatorial plans it also accounts for
  targets rising/setting as the sweep runs long enough for LST to move. This deliberately prioritises
  staying high over minimising total slew distance: if a plan only gets partway through before hitting
  the horizon limit or running out of time, the best-positioned targets are the ones already captured.
  The horizon-limit failure check is evaluated against the *best* remaining candidate each step, so a
  plan only ever fails once every remaining point is below the limit, not just the next one in scan
  order (which the old raster-order implementation could fail on prematurely).

### Capture and FITS conventions

`CaptureViewModel` drives a `CapturePlan`/`TargetPoint` sweep against `ITelescopeMount` and
`ISdrDevice`, writing raw IQ to FITS via `FitsFileIo`. Its live spectrum display (updated as chunks
stream in during a capture) runs on `HiStreamingAccumulator`/`HiStreamingPipeline` against the fixed
calibration baseline, reassembling arbitrary-sized USB streaming chunks into fftSize-aligned frames
first (`ProcessChunk`'s leftover-byte buffer) — raw async buffer chunks are *not* aligned to fftSize.

`CaptureSweepAsync` always switches mount tracking on before a slew, regardless of the plan's own
`TrackingEnabled` checkbox — this mount's ASCOM driver rejects a slew outright while tracking is off,
which previously surfaced as a hard error the instant a sweep began if tracking hadn't already been
switched on by hand. It snapshots the mount's original tracking/at-home state before touching either:
if `TrackingEnabled` is ticked, tracking is switched on once and left on for the whole sweep; if not,
it's switched on immediately before each target's slew and dropped back to whatever it originally was
as soon as that slew completes, so the mount only tracks while actually slewing. The `finally` block
always restores tracking to its original state, and sends the mount home if `GoToHomeAfterCapture` is
set *or* the mount was already at home when the sweep started (dropping tracking first, since this
mount also refuses a `FindHome` while tracking is active) — restoring the starting "at home" condition
even when the plan itself doesn't explicitly ask for it. This `finally` block also swallows failures
from its own post-sweep tracking/find-home calls, so a mount that dies mid-sweep (see "Capture
cancellation and mount-disconnect recovery" below) doesn't leave `IsBusy`/`IsSweepCaptureRunning` stuck
`true` and the Cancel button stuck visible after the sweep has genuinely stopped.

`FitsFileMetaData` (`RASTA.Core/Storage/FitsFileMetaData.cs`) is the header schema written/read on
every file: origin, data format, center freq, sample rate, FFT size, gain, dwell, observation date,
site lat/lon/elevation, and pointing in **both** RA/Dec and Az/Alt (whichever the active
`CoordinateMode` didn't produce directly is left null — reconstructing it later needs the stored
site+time via `AstronomyUtils`). `FitsPathBuilder` lays files out under the user's configured
capture folder as `{freqMHz}MHz/{yyyy-MM-dd}/{prefix}_....fits`; multi-file dwell points
(`CapturePlan.FilesPerPoint > 1`) get an `_{index}of{total}.fits` suffix via
`FitsPathBuilder.BuildSweepFilePath`.

`CaptureViewModel.QuickCaptureAsync` is a single-shot alternative to the sweep: it captures one raw
IQ file at wherever the mount is *currently* pointed, for when the mount was positioned by hand or by
a third-party ASCOM tool rather than by a `CapturePlan` sweep. It mirrors what `CaptureSweepAsync`
does for one dwell point — same `FitsFileMetaData` shape, same `FitsPathBuilder.BuildSweepFilePath`
naming (prefix `"quick"`, always `1of1`), same RA/Dec-vs-Az/Alt convention keyed off
`TelescopeState.Mode` (built via `TargetPoint.FromRaDec`/`FromAzEl` from the mount's live polled
position rather than a planned `TargetPoint`) — but skips slewing and plan/sweep building entirely.
Its capture parameters (center frequency, sample rate, gain, FFT size) come from the active
`CalibrationProfile`, not from a `CapturePlan`, so `CanQuickCapture` requires only a loaded
calibration plus a connected mount/SDR — no plan needs to be selected. Dwell period is its own
`QuickCaptureDwellSeconds` (default 30s), independent of any plan's `DwellTime`.

`VisualiseViewModel` auto-combines these multi-file dwell points: selecting *any one* file matching
`..._{n}of{total}.fits` (`ResolveRelatedCaptureFiles`/`ReadCombinedCaptureRawIq`) pulls in every
sibling that exists alongside it, validates FFT size/sample rate/center frequency agree, and
concatenates their raw IQ — each file's contribution is first trimmed to a whole number of its own
native FFT frames, so a chunk extracted later never straddles the boundary between two physically
discontinuous captures. `CombinedFileCount` (shown in the view) reports how many files went in;
non-matching filenames (baseline files, single-file dwells) are unaffected.

### Capture cancellation and mount-disconnect recovery

`CaptureViewModel` exposes `CancelSweepCommand`/`CancelQuickCaptureCommand`, each cancelling its own
`CancellationTokenSource` (`_sweepCts`/`_quickCaptureCts` — Quick Capture used to run on a local
`using var cts` nothing outside the method could reach). Cancellation unwinds before
`FitsFileIo.WriteRawIq` is reached in both paths, so a cancelled capture's FITS file is never written
to disk — only files from already-completed sweep points remain. The slew-wait loop's timeout handling
deliberately uses a plain `catch (OperationCanceledException)` + conditional rethrow rather than an
exception filter (`when (!ct.IsCancellationRequested)`): a filtered catch anywhere in an async method's
compiled state machine can cause the debugger to mis-flag an unrelated, genuinely-handled throw
elsewhere in the *same* method as user-unhandled during first-chance dispatch.

Mount disconnects are handled separately from a user-initiated cancel, since `ITelescopeMount.
IsConnected` is just a cached flag set on `ConnectAsync`/`DisconnectAsync` and never re-derived from a
live check — the only way this app can actually tell the mount has gone away (network drop, mount
powered off, Alpaca server gone) is a live poll call throwing. `TelescopeService`'s poll loop now stops
itself and raises `ConnectionLost` (an `Action<Exception>`, fired on the poll loop's own background
thread) the first time that happens, instead of retrying forever behind an "Error: ..." status string.
`App.xaml.cs` subscribes once in `OnStartup` and tidies up in `OnTelescopeConnectionLost` exactly as if
the user had clicked Disconnect: `CaptureViewModel.CancelAnyRunningCapture()` cancels any in-flight
sweep/Quick Capture (same FITS-not-written guarantee as above), `SettingsViewModel.
ForceDisconnectTelescope()` resets local connection state via `ITelescopeMount.MarkDisconnected()`
*without* a live round-trip (the link is already known down, so a graceful `DisconnectAsync()` would
just hang or fail again), `TelescopeService.Stop()` halts polling, and `NavigationViewModel` navigates
back to Prepare — since Plan/Capture both require a connected mount to mean anything — before a
`MessageBox` explains why. There's deliberately no auto-reconnect: once a live poll has failed there's
no way to know what physical state the mount was actually left in (mid-slew? tracking? parked?), so
reconnecting is left as a deliberate, informed action for the user.

Because SDR/mount state changes can now fire from background threads at points that used to only ever
run on the UI thread (`SdrDeviceService.EnumerateDevicesAsync` and `TelescopeService`'s poll loop both
run inside their own `Task.Run`), `CaptureViewModel.LoadAvailablePlans`/`PlanViewModel.LoadSavedPlans`
wrap their `ObservableCollection` mutations in `UiThread.SafeInvoke` (a raw, unmarshaled
`ObservableCollection.Clear()`/`Add()` from a non-UI thread throws), and `SpectrumViewModel.
UpdateSpectrum` — including its axis-limit mutations, previously unmarshaled entirely, called from
`CaptureViewModel.ChunkWorker`'s background thread on every live-spectrum update — does the same in
place of a raw, shutdown-unsafe `App.Current.Dispatcher.Invoke(...)` call.

### Mosaic sky-map view

`MosaicViewModel` (a tab in `VisualiseView`) points at a session folder containing one baseline and
several multi-file dwell-point captures across different pointings, and turns them into a sky-mosaic.
`MosaicProcessor` runs each position's capture through the same `HiStreamingPipeline`
`VisualiseViewModel.ProcessHiCore` uses for a single file, then `FindLinePeak` reduces each position's
spectrum to two numbers from the same search: the strongest `RatioSpectrum` channel within a
configurable window of the LSR-corrected line center (0 km/s), reported as `LineStrengthDb` (dB
*relative to the cold-sky baseline itself*, `10*log10(peakRatio / HiConstants.RatioDisplayScale)` —
deliberately single-differenced against the baseline rather than each position's own local continuum,
so a position with no HI signal at all reads close to 0 dB rather than a fraction of a dB, and broad
continuum brightness differences across the sky (e.g. toward the Galactic plane) show up too, not just
narrow HI-line strength) and `PeakVelocityKmPerSec` (that channel's own velocity — signed,
toward/away from the LSR). Both come back `NaN` together when no channel falls in the window.
`GridBuilder.BuildGrid` bins whichever of a `MosaicPosition`'s fields its `valueSelector` picks (default
`LineStrengthDb`) onto a uniform RA/Dec-or-Az/El grid covering the *full* sky at a fixed cell size (not
just the captured area's own bounding box — the intent is one full-sky canvas that fills in across many
sessions over time, not a differently-scaled image every run); cells no position landed in stay `NaN`
and should be skipped, not treated as 0. `MosaicViewModel.BuildGrids` bins both metrics into
`_lastStrengthGrid`/`_lastVelocityGrid` every time a session is processed, and `MosaicSurfaceMetric`
(`Strength`/`Velocity`, radio-button toggle in `MosaicView.xaml`) picks which one `RenderSurface` feeds
the 3D tab, independently of the 2D heatmap — which always shows `LineStrengthDb`, since a
position-velocity map only means "toward/away from LSR" and doesn't read as a brightness scale the way
dB does. The grid renders as a 2D heatmap via `RASTA.App/Helpers/HeatmapImageBuilder` (a hand-rolled
`BitmapSource` — LiveChartsCore's `HeatSeries` produced a blank chart against real session data) —
`Build` for one flat colour per measured cell (the default, since each cell is a real independent
measurement) or `BuildBlended` for a bilinear-interpolated, continuous-looking gradient
(`MosaicViewModel.UseSmoothBlend`, re-renders the already-cached grid instantly rather than reprocessing
the session) — and as a 3D height-field surface (`MosaicSurfaceView`, built on `HelixToolkit.Wpf`) from
whichever grid `SurfaceMetric` selects. `UseSmoothBlend` also drives the 3D surface's own smoothing
(`MosaicSurfaceView.Smooth`) — one control smooths both representations together.

This supersedes the old `RASTA.Processing/VisualisationData/HeatmapBuilder.cs`/`SpectrumImageBuilder.cs`
placeholders (removed entirely), which consumed the old `ObservationRecord.AveragedSpectrum.Max()` shape
and were never wired to any View/ViewModel.

#### 3D surface mesh: getting HelixToolkit to actually render on this machine

`MosaicSurfaceView`'s mesh is built by hand from plain WPF 3D types (`Point3DCollection`/
`Int32Collection`), not `HelixToolkit.Geometry.MeshBuilder` (works in `System.Numerics.Vector3` in this
Helix version, its own conversion step for no real benefit here). Getting from "nothing renders" to a
working surface took several rounds, each worth knowing about since they're generic WPF-3D traps, not
Mosaic-specific:

- **Every quad gets drawn, even where a corner is `NaN`.** The mesh originally skipped any quad
  touching a no-data corner — reasonable-sounding, but on a sparse mosaic (a handful of scattered
  positions on the full-sky grid, or a single-row/column sweep, where no two adjacent grid cells in
  *both* axes ever both have data) that produced literally zero triangles: a mesh with real `Positions`
  but nothing visible, so the viewport showed only its fixed corner overlays (coordinate triad, view
  cube) with an empty main scene — which also makes mouse-wheel zoom look broken, since there's nothing
  to zoom into. Every quad now draws, with NaN corners defaulting to zero height (and a neutral 0.5
  texture coordinate) — matching what issue #13 asked for directly ("positions without data default to
  zero").
- **`MeshGeometry3D.Normals` needs setting explicitly.** WPF's automatic normal generation for a mesh
  that only sets `Positions`/`TriangleIndices` turned out unreliable on this specific machine's WPF 3D
  render tier — HelixToolkit's own `MeshBuilder`-based visuals always compute normals explicitly, which
  is why a `GridLinesVisual3D`/`SphereVisual3D` diagnostic added mid-investigation rendered fine while
  the hand-built mesh stayed invisible. Fixed by averaging face normals into an explicit
  `Vector3DCollection` (smooth per-vertex shading, matching a continuous height-field).
- **The real fix: raster `ImageBrush` materials don't render as 3D materials here, full stop.** The
  height→colour gradient was originally an `ImageBrush` wrapping a `BitmapSource` from
  `HeatmapImageBuilder.BuildLegendStrip` — swapping in a plain `Brushes.Orange` `SolidColorBrush`
  (with the exact same mesh/normals) rendered immediately, proving the geometry was never the problem.
  Neither a 1px- nor 2px-tall bitmap fixed it, so it isn't specifically an extreme-aspect-ratio/mipmap
  issue — it's that *any* raster-backed `ImageBrush` used as a 3D `DiffuseMaterial` silently fails to
  render on this machine's WPF 3D tier, while *vector* brushes (`LinearGradientBrush`,
  `SolidColorBrush`) render fine. `BuildGradientBrush` now builds the same diverging blue-gray-red ramp
  (`HeatmapImageBuilder.DivergingStops`) as a `LinearGradientBrush` instead. This also ruled out
  HelixToolkit's own `TextVisual3D`/`BillboardTextVisual3D` for axis labels (both render text via
  `RenderTargetBitmap` → `ImageBrush` internally — the same failing pattern) in favour of
  `HelixToolkit.Wpf.TextCreator.CreateTextLabelModel3D`, which renders its `DiffuseMaterial` via a
  `VisualBrush` wrapping a live `TextBlock` — vector-brush family, proven to render here.
- **`ZoomExtents(0)`** is called every rebuild so the camera reframes on new data; `IsVisibleChanged` +
  a `Dispatcher.InvokeAsync(..., DispatcherPriority.Loaded)` re-triggers the whole rebuild once the "3D
  Surface" tab is actually shown, since a `HelixViewport3D` inside a never-selected `TabItem` has no
  layout to frame a camera against yet.

`MosaicSurfaceView.Smooth` (bound to `UseSmoothBlend`) bilinearly subdivides the grid (`
UpsampleBilinear`, `SmoothSubdivisionFactor` = 4×) before meshing — the same NaN-dropping/renormalizing
technique `HeatmapImageBuilder.BuildBlended` uses for the 2D heatmap's own smoothing, applied to the
mesh's geometry instead of pixel colour. Without it, a genuinely sparse mosaic reads as sharp "pointy"
spikes wherever an isolated measured cell sits surrounded by the zero-height NaN fallback described
above; subdividing tapers each real measurement smoothly toward that fallback instead. Real cell centers
always land exactly on the subdivided grid, so smoothing never moves an actual measurement.

The mesh material is deliberately translucent (`TranslucentGradientBrush`, alpha baked into each
gradient stop since the brush is `Frozen`), because the reference axis grid/labels below sit at `Y=0` —
the plot's own zero reference (a real, physically meaningful plane in Velocity mode: 0 km/s, the
LSR-corrected line center), not pinned below the data's own minimum — which for data straddling zero
routinely sits *through* rather than under the surface. An opaque material would hide whatever part of
the grid/labels falls behind it from the current view angle.

#### Axis ticks and gridlines (Sky Mosaic 2D + 3D Surface)

`RASTA.App/Helpers/AxisTicks.ComputeNiceTicks` (Heckbert's "nice numbers for graph labels") is shared
by both views, so tick values read naturally (whole/5/10-step RA hours or Dec/Az/El degrees) instead of
raw grid-cell-center fractions like "13.333h". `RASTA.App/Helpers/AxisGridLine`/`AxisTick` are the two
small shared record types — deliberately `Helpers`, not `MosaicViewModel`, so `MosaicSurfaceView` (a
View) can consume tick data without depending on a ViewModel type, consistent with its other bound
properties (`IntensityGrid`/`XValues`/`YValues`) all being plain primitives.

On the 2D heatmap, `MosaicViewModel.BuildPixelAxisOverlay` computes tick pixel positions once (using
`AxisXCenters`/`AxisYCenters`' own cell spacing to recover the plotted range's true outer edges, not
just the cell-center range) into `MosaicHeatmapDisplay.GridLines`/`XTickLabels`/`YTickLabels`, all in
the same fixed pixel-coordinate space as `PixelWidth`/`PixelHeight` — so `MosaicView.xaml`'s overlay
`ItemsControl`s can position everything with simple one-to-one bindings, no runtime
`ActualWidth`/`ActualHeight` dependency. One genuine WPF trap surfaced building this: **`Canvas.Left`/
`Canvas.Top`/`Canvas.Right` set inside an `ItemsControl`'s `DataTemplate` silently do nothing** — those
attached properties only affect *direct* children of a `Canvas`, and an `ItemsControl`'s actual direct
children (when `Canvas` is the `ItemsPanel`) are the auto-generated item containers
(`ContentPresenter`), not the element inside the `DataTemplate`. Every tick label collapsed to the
canvas's own (0,0) origin until the position was moved onto an `ItemContainerStyle` targeting
`ContentPresenter` instead. The gridlines (`Line` elements) never had this problem, since `Line`
positions itself via its own `X1`/`Y1`/`X2`/`Y2` coordinates rather than `Canvas.Left`/`Top`.

On the 3D surface, `MosaicSurfaceView.BuildAxes` maps `XTicks`/`YTicks`' real RA/Dec(/Az/El) values into
the mesh's own normalized model space via the same `NormX`/`NormZ` closures the mesh uses, so a
gridline/label always lines up with the data beside it regardless of the session's real coordinate
range. Gridlines are one `LinesVisual3D` (screen-space-constant-width, `SolidColorBrush`-backed —
proven-safe per the section above) rather than HelixToolkit's `GridLinesVisual3D`, since
`GridLinesVisual3D`'s own `Center`/`MinorDistance` parameterization has no easy way to force gridlines
onto already-computed nice-tick positions. Both X and Y tick labels share one fixed
`textDirection`/`updirection` (`(1,0,0)`/`(0,0,1)`) — matching HelixToolkit's own `SurfacePlotVisual3D`
reference example's convention of one consistent direction pair for every axis, rather than a different
one per axis (which is what originally produced upside-down labels). The label lies flat on the floor
plane rather than billboarding to face the camera (this text technique has no such behaviour), so it
reads best close to a top-down view — `HelixViewport3D`'s `ViewCube` "Top" corner gets there in one
click, called out directly in `MosaicView.xaml`'s UI.

### Progress reporting convention

`Calibrator`, `CaptureViewModel`, `VisualiseViewModel`, and `MosaicViewModel` all report progress the
same way: real, measured progress from actual work completed (bytes captured, chunks processed, files
read/gain trials finished, positions processed) — never a simulated/time-based animation. The pattern
is the same small shape everywhere: `BeginProgress(status)` resets the progress value to 0 and sets a
status message, `ReportProgress(fraction)` updates it, `EndProgress()` resets it again, and
`ForEachChunk`/the per-position loop drives `ReportProgress` from a chunks-processed/total-chunks (or
positions-processed/total-positions) ratio. Each logical phase (reading a file, processing a baseline,
processing a capture, processing one mosaic position) gets its own fresh `BeginProgress` — a new 0→1
run with its own status message — rather than one continuous bar across unrelated phases.

`CaptureViewModel` and `PrepareViewModel`/`Calibrator` still drive the *shared* `StatusBarViewModel.
CaptureProgress`/`IsCaptureInProgress`/`CaptureStatus` (the status bar's own progress bar, visible
regardless of which view is on screen). `VisualiseViewModel` and `MosaicViewModel` deliberately do
**not** — `GenerateChartAsync`/mosaic processing used to write to that same shared state, which meant
generating a chart while a capture sweep was running elsewhere fought the sweep for ownership of the
same bar and status text. Each now has its own `IsGenerating`/`GenerationProgress`/`GenerationStatus`
instead, bound to a "Generate Chart"/"Generate Mosaic" button that turns into a Cancel button (the
button itself doubles as the progress indicator — a `ProgressBar` templated behind the caption, rather
than a separate bar next to it) while a run is in flight; `GenerationStatus` surfaces as that button's
tooltip. Cancelling sets a `CancellationTokenSource` checked per-chunk in `VisualiseViewModel.
ForEachChunk` / per-position in `MosaicProcessor.ProcessFolderAsync`'s existing token parameter, so a
long chart/mosaic generation can be aborted promptly rather than only between whole-file phases.

All of these properties are safe to set from a background thread (WPF's data-binding machinery
marshals `INotifyPropertyChanged` notifications to the UI thread automatically); several of these call
sites intentionally run inside `Task.Run(...)` so the UI thread stays free to actually repaint between
updates — a synchronous CPU-bound loop on the UI thread will never show intermediate progress no matter
how often you set the bound property. `CaptureViewModel.StartProgressTimer` (a `DispatcherTimer`-based
*simulated* progress bar, since removed) is the anti-pattern to avoid: it estimated elapsed time
against a nominal duration instead of measuring real progress, and could hit 100%/hide itself while the
real work was still running.

`CaptureViewModel.EstimatedCompletionTime` applies the same philosophy to session ETA: it starts from
`SweepPlanResult.EstimatedCompletionUtc` (a nominal estimate from planned dwell/slew figures, computed
once in `SweepPlanner.BuildSweep`), then after every completed target point it's overwritten using the
*real* average time-per-point measured so far, extrapolated across the remaining points — not the
original nominal figure held fixed for the whole run.

### App-wide exception handling, graceful shutdown, and SDR device staleness

`App.xaml.cs`'s `OnStartup` wires up three global handlers before anything else runs —
`DispatcherUnhandledException` (UI thread; logs via `RastaLogger` and shows a `MessageBox`, then sets
`e.Handled = true` so the app keeps running), `AppDomain.CurrentDomain.UnhandledException` (any other
thread — can't stop the process terminating by that point, but at least logs and best-effort tells the
user before it goes), and `TaskScheduler.UnobservedTaskException` (a faulted fire-and-forget `Task`
nobody awaited). None of this existed before; any unhandled exception anywhere used to take the whole
process down silently with no log entry and no message. `App.OnExit` complements this on the way out:
it stops `TelescopeService`'s mount-polling `Task.Run` loop and disposes
`UsbWatcherService`/`SdrDeviceService` before shutdown finishes — neither used to be stopped on close,
and left running, either could still flip `SdrState`/`TelescopeState.IsConnected` from a background
thread after `Application.Current` had already gone null, which crashed
`NavigationViewModel`/`PrepareViewModel`'s direct `Application.Current.Dispatcher.Invoke(...)` calls
with a `NullReferenceException`. `RASTA.App/Helpers/UiThread.SafeInvoke` is the matching defense-in-
depth fix at every one of those call sites (checks `Application.Current`/`Dispatcher.HasShutdownStarted`
first and no-ops instead of throwing), in case a similar race turns up somewhere else.

`SdrDeviceService.EnumerateDevicesAsync` also stopped unconditionally disposing and reopening the
persistent SDR device on every hot-plug event: `UsbWatcherService`'s debounce `Timer` reacts to *any*
`WM_DEVICECHANGE` system-wide, not just the RTL-SDR's own, so this used to fire (tearing the device down
and rebuilding it) for completely unrelated USB activity — harmless while a calibration run was quick,
but a real risk once one can run for minutes (see "Calibration flow" above): if it raced with an
in-progress capture, the forced reopen could fail and get misreported as the SDR having been unplugged.
It now skips the dispose/reopen when the already-open device (matched by serial, stable across
re-enumeration unlike `Index`) is still present in the freshly enumerated list.
`PrepareViewModel.CalibrateGainAsync`/`CaptureBaselineAsync` also re-fetch the device from
`SdrDeviceService` immediately before each SDR-touching step rather than holding one reference captured
at the start of the run, so a device that does get swapped mid-flow is picked up rather than used stale.

### Versioning, logging paths, and the installer (RASTA.Setup / RASTA.Bundle)

`Directory.Build.props` at the repo root sets a single `<Version>` picked up by every project.
`MainWindow.xaml.cs` appends it to the title bar (`R.A.S.T.A. v0.1.0`) by reading
`Assembly.GetExecutingAssembly().GetName().Version` (the plain `AssemblyVersion`) rather than
`AssemblyInformationalVersion` — the SDK auto-appends a `+<git-sha>` to the latter whenever building
inside a git repo, which would make the title bar noisy.

Per-user state (logs, options) must go under `%LOCALAPPDATA%\RASTA\...`
(`Environment.SpecialFolder.LocalApplicationData`, matching `UserOptionsService`'s existing
convention), never a path relative to the working directory. `App.xaml.cs`'s `OnStartup` constructs
`RastaLogger` before the global exception handlers (`DispatcherUnhandledException` etc.) are wired
up, since a startup-time failure is exactly what those handlers exist to catch — but that also means
if `RastaLogger`'s own constructor throws, that exception has nothing to catch it and the app dies
before any window appears, with no dialog and no log entry. This bit exactly once: the original
relative `"Logs/rasta.log"` path worked fine from a dev `bin/Debug` folder (owned by the developer's
own account) and only broke once actually installed — `Directory.CreateDirectory("Logs")` resolves
against the CWD, which for an installed Start Menu shortcut is `C:\Program Files\RASTA\`, not
writable by a standard user.

`RASTA.Setup` (MSI, WiX Toolset SDK) and `RASTA.Bundle` (Burn bootstrapper, chains the .NET 10
Desktop Runtime ahead of the MSI) build the release installer:

- Pinned to **WiX 6.0.2**, not 7.x — WiX 7 requires accepting the Open Source Maintenance Fee (OSMF)
  EULA before it'll build (`WIX7015`); revisit that pin if/when OSMF is dealt with.
- `RASTA.Setup.wixproj` publishes `RASTA.App` (framework-dependent, `win-x64`,
  `--self-contained false` — the Bundle supplies the runtime separately) via a target hooked to
  `PrepareForBuild`, not `Build`/`Rebuild` — WiX's own harvest/compile step runs as one of `Build`'s
  `DependsOnTargets`, which resolve *before* a plain `BeforeTargets="Build"` hook would fire.
  `Package.wxs` then harvests that publish output wholesale via the wildcard `<Files Include="**" />`
  (WiX v5+'s built-in harvesting) — no `heat.exe`, no file list to keep in sync by hand.
- `RASTA.Bundle.wixproj` needs `<OutputType>Bundle</OutputType>` set explicitly — without it,
  WixToolset.Sdk silently emits an MSI instead of the bootstrapper `.exe`, regardless of `Bundle.wxs`'s
  `<Bundle>` root element. `Bundle.wxs` uses `netfx:DotNetCoreSearch` (WixToolset.Netfx.wixext) to set
  a bundle variable from the installed `Microsoft.WindowsDesktop.App` version, and an `ExePackage`'s
  `DetectCondition` against it decides whether to download/run the runtime installer first. The
  download URL/SHA-512 hash/size are pinned to a specific `windowsdesktop-runtime-10.0.x-win-x64.exe`
  build (from Microsoft's own release-metadata feed) and need bumping by hand for a newer patch;
  `DetectCondition` only requires `>= v10.0.0`, so it won't force a reinstall over a newer patch
  already present — it only triggers when nothing satisfying 10.0.x exists at all.
- Building the Bundle can fail local MSI validation (`WIX0350`, requires a newer Windows Installer
  engine than the build machine has registered) — `-p:SuppressValidation=true` works around that; try
  a plain build first, it's been a machine-specific quirk, not an authoring issue.
- Neither `Bundle/@Id` nor `Package/@Id` (ProductCode) is pinned, so every rebuild mints fresh GUIDs.
  Combined with `Version` not changing between test rebuilds, Burn's related-bundle upgrade detection
  (keyed on `UpgradeCode` + `Version` comparison) can't always tell "newer replacement" from
  "unrelated," which can leave two Add/Remove Programs entries behind after an uninstall/reinstall
  cycle during iteration — bump `Version` between install-testing rebuilds to avoid it. The fixed
  `UpgradeCode` GUIDs (`RASTA.Setup`'s and `RASTA.Bundle`'s are different from each other) must never
  change once a real release has shipped — that's what ties future upgrades to this install.

`scripts/Build-Release.ps1` automates the above into one step: builds `RASTA.Bundle` in the given
configuration (default `Release`, retrying once with `-p:SuppressValidation=true` on the known
`WIX0350` machine-specific quirk), then copies the resulting `RASTA-Setup.exe` into the repo-root
`Releases\` folder (already covered by `.gitignore`'s `[Rr]eleases/` rule, so built installers never
get committed) as `RASTA-Setup-<version>.exe`, reading `<version>` from `Directory.Build.props` so the
installer and its filename can never disagree. Refuses to overwrite an existing versioned build unless
run with `-Force` (bump `<Version>` in `Directory.Build.props` for a new build instead — see the GUID
note above on why that matters during install-testing anyway); `-SkipBuild` just re-copies whatever's
already built.

### Known incomplete / placeholder areas

- The **Process** workflow stage (`ProcessViewModel`/`ProcessView`) has been removed outright rather
  than reworked — it operated on the old `ObservationRecord`/`SpectrumMath` shape with no caller ever
  supplying it data, and everything it nominally did already happens directly in Visualise. Its one
  real dependency, `RASTA.Processing/Spectral/SpectrumMath.cs`, was removed with it (fully superseded
  by `HiStreamingPipeline`'s proper baseline division/continuum fit and `RASTA.Processing/Dsp/
  SavitzkyGolay.cs`).
- `RASTA.Simulators` and `RASTA.Tests` are stub projects only (see Commands section above).
