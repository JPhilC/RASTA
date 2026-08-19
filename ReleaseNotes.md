# RASTA Release Notes

A record of what each installed release (`Releases\RASTA-Setup-<version>.exe`, built via
`scripts\Build-Release.ps1`) actually contains. One section per version, newest first. The
version number matches `<Version>` in `Directory.Build.props` and the corresponding `vX.Y.Z`
git tag.

## v0.3.0 — 2026-08-19

### Fixes

- **Target FFT Size no longer aliases the spectrum.** Downscaling used to shrink the raw IQ
  *before* the FFT (`IqDownscaler`), which was a real bug: block-averaging time-domain samples
  and then FFT-ing the shorter result is decimation, and decimating in time aliases the
  spectrum — each output bin ended up combining a native bin with one a whole output-length
  away (e.g. a 4096→2048 downscale folded the middle of the band together with its far edge)
  instead of averaging nearby frequencies. A lower Target FFT Size used to make a spectrum
  *noisier* and erase its bandpass shape rather than smoothing it. Replaced with
  `SpectrumBinner`, which averages adjacent bins of the already-computed, native-resolution
  spectrum instead — correct, and it fixes Mosaic's own Target FFT Size too.
- Target FFT Size and Smooth now also work on the standalone Baseline-only/Capture-only
  charts (previously ignored, always native/unsmoothed) — lets a single file's own noise be
  judged before it's ever combined with its counterpart.
- The SKAO TTRT cross-check no longer pre-shrinks IQ data to 256 samples/frame; it now feeds
  native-rate IQ straight to the reference algorithm, which is what it actually expects.
- Corrected stale documentation/comments describing the calibration baseline as a terminator
  reading — baseline capture has used an automated cold-sky pointing for some time now; a
  terminator is only ever used for the separate gain-calibration step.

### UI

- Added explanatory tooltips to the Single Capture tab's Spectrum Mode, Target FFT Size, Show
  dB, Smooth, Window, and Despike controls, aimed at people new to radio astronomy.
- Long tooltips now wrap into a sensibly-sized box instead of spanning the whole window.
- Added an app icon, a transparent splash screen, and a desktop shortcut to the installer.

## v0.2.0 — 2026-08-14

First installer release (WiX MSI + Burn bootstrapper chaining the .NET 10 Desktop Runtime).
Baseline feature set at this point:

- Four-stage workflow — **Prepare → Plan → Capture → Visualise** — driving an ASCOM Alpaca
  telescope mount and an RTL-SDR receiver.
- Three-step calibration flow (Load Last Calibration / Calibrate Device Gain / Capture
  Baseline), baselining against an automatically-selected cold-sky pointing rather than a
  terminator, to avoid the spurious edge-of-band hump a terminator baseline leaves behind.
- FFT-size-agnostic HI reduction pipeline (`HiStreamingPipeline`): baseline division,
  continuum fit/subtraction, optional Savitzky–Golay/Moving-Average smoothing, optional
  narrowband-RFI despike, always-on receiver DC/LO-spike excision.
- Sweep planning with elevation-optimised target ordering; Quick Capture for one-off dwells at
  the mount's current pointing.
- Visualise: HI (Frequency)/HI (Velocity)/SKAO TTRT/Bandpass Ratio spectrum modes, LSR Doppler
  correction, automatic multi-file dwell combination.
- Mosaic sky-map view: 2D heatmap and 3D height-field surface, RA/Dec or Az/El grid,
  line-strength and velocity metrics.
- App-wide unhandled-exception handling and graceful shutdown; hot-plug-aware SDR device
  handling; mount-disconnect recovery.

*(Development before this point predates formal release tagging — see `git log` for the full
history.)*
