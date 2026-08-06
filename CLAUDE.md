# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

RASTA (Radio Astronomy Slew • Track • Acquire) is a hobby-grade .NET 10 WPF/MVVM app for amateur
hydrogen-line (1420 MHz / 21cm) radio astronomy: it drives an ASCOM Alpaca telescope mount and an
RTL-SDR receiver through a five-stage workflow — **Prepare → Plan → Observe → Process → Visualise** —
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
`CurrentViewModel`. The five workflow stages map 1:1 to `PrepareViewModel` / `PlanViewModel` /
`ObserveViewModel` / `ProcessViewModel` / `VisualiseViewModel`. `NavigatePlan`/`NavigateObserve` are
gated on `StatusBarViewModel.SdrConnected` (an SDR must be enumerated before you can move past
Prepare).

### The HI reduction pipeline (the scientific core)

`RASTA.Processing/HiPipeline/HiPipelineProcessor.cs` (documented in detail in
`HiPipelineDescription.md`) is the FFT-size-agnostic, actively-used pipeline:

- `HiStreamingAccumulator` — sums arbitrary numbers of baseline/capture power frames, exposes
  `GetAveragedSpectra()` (needs both) or `GetBaselineAverage()` (baseline-only, used during
  calibration before any capture frames exist).
- `HiStreamingPipeline` — fftshift → frequency axis → velocity axis (radio convention, centered on
  1420.40575177 MHz) → baseline division (bandpass flattening) → linear continuum fit from two
  small edge windows (channel-index based, RFI-outlier-rejected — *not* a velocity-magnitude mask
  over most of the spectrum, despite what the fraction names might suggest) → continuum subtraction
  → optional Savitzky–Golay smoothing.
- `HiStreamingProcessor` — convenience wrapper combining the two.

`HiPipeline/SkaoPipelineProcessor.cs` is a separate, fixed-256-bin port of the SKAO TTRT reference
pipeline, kept for cross-checking against `HiStreamingPipeline`'s FFT-size-agnostic version.
`VisualiseViewModel` exposes it plus four other modes as `SpectrumMode`: `IF`, `HiFrequency`,
`HiVelocity`, `TTRT`, `Ratio` (the bandpass-flattened capture/baseline ratio *before* continuum
subtraction — strictly positive, unlike `HiSpectrum`, so it's the one mode besides `IF` that can
validly be shown in dB; see `VisualiseViewModel.UseDbScale`/`ToDb`).

`HiStreamingPipeline.Process` (and `HiStreamingProcessor.Compute`) take an optional
`lsrCorrectionKmPerSec` parameter (default 0), added as a flat offset to every channel's velocity —
`VisualiseViewModel.ProcessHiCore` computes it via `AstronomyUtils.ComputeLsrCorrectionKmPerSec` from
the **capture** file's recorded pointing/time/site (never the baseline — that's just a terminator
reading with no meaningful pointing), falling back to 0 for files that predate that metadata.

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

`PrepareViewModel` → `CalibrationService` (loads a saved `CalibrationProfile` via
`CalibrationRepository`, or triggers a new run and persists the result) → `Calibrator.RunFullCalibrationAsync`,
run against a terminator on the SAWbird H1+ LNA input. Two jobs, in order:

1. **Gain sweep** — for each SDR-supported gain, capture raw IQ, hard-reject any gain where raw
   I/Q bytes show real ADC saturation (`ComputeSaturationFraction` — a fraction-of-samples-at-the-
   rail check on the raw bytes, not a spectral-domain proxy; threshold `SaturationFractionThreshold`
   = 0.05%), then score survivors on a full-buffer-averaged power spectrum's flatness/spur-count/slope
   (each metric min-max normalized across candidates before weighting, so the weights are
   meaningful). A short settle period (`GainSettleTimeSec`) is discarded at the start of each trial
   so a gain-switching transient doesn't bias that gain's averaged spectrum.
2. **Baseline capture** — a *separately configurable* dwell (`BaselineDwellSeconds` in
   `SettingsViewModel`/`PrepareViewModel`, decoupled from the per-gain sweep dwell — the sweep only
   needs a couple of seconds per gain now that it averages the whole buffer, but the baseline is
   reused for the rest of the session and deserves much better averaging) raw IQ capture at the
   chosen gain, written to FITS as-is, and also reduced to an averaged linear-power baseline via
   `HiStreamingAccumulator` + `ComputeSkAoPower` (matching exactly how the observation capture side
   will later be averaged, so the two are directly comparable when `HiStreamingPipeline.Process`
   divides one by the other).

### Capture and FITS conventions

`ObserveViewModel` drives a `CapturePlan`/`TargetPoint` sweep against `ITelescopeMount` and
`ISdrDevice`, writing raw IQ to FITS via `FitsFileIo`. Its live spectrum display (updated as chunks
stream in during a capture) runs on `HiStreamingAccumulator`/`HiStreamingPipeline` against the fixed
calibration baseline, reassembling arbitrary-sized USB streaming chunks into fftSize-aligned frames
first (`ProcessChunk`'s leftover-byte buffer) — raw async buffer chunks are *not* aligned to fftSize.

`FitsFileMetaData` (`RASTA.Core/Storage/FitsFileMetaData.cs`) is the header schema written/read on
every file: origin, data format, center freq, sample rate, FFT size, gain, dwell, observation date,
site lat/lon/elevation, and pointing in **both** RA/Dec and Az/Alt (whichever the active
`CoordinateMode` didn't produce directly is left null — reconstructing it later needs the stored
site+time via `AstronomyUtils`). `FitsPathBuilder` lays files out under the user's configured
capture folder as `{freqMHz}MHz/{yyyy-MM-dd}/{prefix}_....fits`; multi-file dwell points
(`CapturePlan.FilesPerPoint > 1`) get an `_{index}of{total}.fits` suffix via
`FitsPathBuilder.BuildSweepFilePath`.

`VisualiseViewModel` auto-combines these multi-file dwell points: selecting *any one* file matching
`..._{n}of{total}.fits` (`ResolveRelatedCaptureFiles`/`ReadCombinedCaptureRawIq`) pulls in every
sibling that exists alongside it, validates FFT size/sample rate/center frequency agree, and
concatenates their raw IQ — each file's contribution is first trimmed to a whole number of its own
native FFT frames, so a chunk extracted later never straddles the boundary between two physically
discontinuous captures. `CombinedFileCount` (shown in the view) reports how many files went in;
non-matching filenames (baseline files, single-file dwells) are unaffected.

### Progress reporting convention

`Calibrator`, `ObserveViewModel`, and `VisualiseViewModel` all report progress the same way: real,
measured progress from actual work completed (bytes captured, chunks processed, files read/gain
trials finished) — never a simulated/time-based animation. `VisualiseViewModel` has the canonical
small implementation of the pattern: `BeginProgress(status)` resets `StatusBarViewModel.CaptureProgress`
to 0 and shows the bar, `ReportProgress(fraction)` updates it, `EndProgress()` hides it again, and
`ForEachChunk` drives `ReportProgress` from a chunks-processed/total-chunks ratio. Each logical phase
(reading a file, processing a baseline, processing a capture) gets its own fresh `BeginProgress` — a
new 0→1 run with its own status message — rather than one continuous bar across unrelated phases.
`StatusBarViewModel`'s properties are safe to set from a background thread (WPF's data-binding
machinery marshals `INotifyPropertyChanged` notifications to the UI thread automatically); several of
these call sites intentionally run inside `Task.Run(...)` so the UI thread stays free to actually
repaint between updates — a synchronous CPU-bound loop on the UI thread will never show intermediate
progress no matter how often you set the bound property. `ObserveViewModel.StartProgressTimer` (a
`DispatcherTimer`-based *simulated* progress bar, since removed) is the anti-pattern to avoid: it
estimated elapsed time against a nominal duration instead of measuring real progress, and could hit
100%/hide itself while the real work was still running.

### Known incomplete / placeholder areas

- `RASTA.Processing/Gridding/GridBuilder.cs` and `RASTA.Processing/VisualisationData/HeatmapBuilder.cs`
  are early placeholders for combining many single-pointing observations into a sky-mosaic (RA/Dec
  intensity map). They are registered in DI but **not called from any View/ViewModel** — dead code
  for now — and they still consume the old `ObservationRecord.AveragedSpectrum.Max()` shape rather
  than `HiStreamingPipeline`'s baseline-divided, continuum-subtracted `HiSpectrum`. Expect these to
  be reworked rather than extended as-is.
- `RASTA.Simulators` and `RASTA.Tests` are stub projects only (see Commands section above).
