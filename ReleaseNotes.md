# RASTA Release Notes

A record of what each installed release (`Releases\RASTA-Setup-<version>.exe`, built via
`scripts\Build-Release.ps1`) actually contains. One section per version, newest first. The
version number matches `<Version>` in `Directory.Build.props` and the corresponding `vX.Y.Z`
git tag.

## v0.4.0 — 2026-09-01

### Features

- **Plan view: a Radio Sky map.** Plan is now built around a zenith-centered Alt/Az hemisphere
  map instead of a plain numeric form, so a plan can be seen and validated before it's ever run
  for real: the background is rendered from real HI4PI all-sky neutral-hydrogen survey data
  (not an analytic approximation), capture points are previewed all-at-once or as an animation
  by running through the real sweep-ordering/horizon-validation pipeline, there's a toggle
  between the dome's Alt/Az reference frame and a live RA/Dec grid, freeform region drawing for
  Equatorial plans, and a right-click "Slew & Capture Here" straight from the map. Capture-point
  dots are now sized to the antenna's actual beamwidth instead of a fixed pixel size. Plan needs
  neither an SDR nor a mount connected any more, so it can be used fully offline.
- **Mosaic: Zenith Dome and 3D Dome views.** A new "Zenith Dome" tab shows every processed
  position's live Az/El at a chosen moment as a zenith-centered dome, the way a naked-eye sky
  chart is drawn. The old spherical-globe "3D Surface" tab is replaced by a "3D Dome" extrusion
  view built on that same shared dome geometry, with zero-anchored height/colour and an optional
  Delaunay-triangulated surface fitted through the points, plus click-to-select linking a point
  back to its row in the Positions grid.
- **Mosaic: LAB Survey test data.** A folder of LAB Survey (Leiden/Argentine/Bonn Galactic HI
  Survey) profile files is now detected automatically and processed the same way as a real RASTA
  session, letting the Sky Mosaic/3D Dome/Zenith Dome views be exercised against real,
  richly-varying sky data without needing actual observing time. New PowerShell scripts fetch and
  generate test grids for this.
- **Site settings usable without a mount.** Latitude/longitude/elevation - plus new dish
  diameter/focal length antenna fields, feeding an estimated beamwidth - can be set and persist
  before any mount is ever connected; connecting a mount now only prompts to reconcile if its own
  reported site actually disagrees with what's already set.
- **Sweep points ordered by urgency.** A sweep now visits whichever remaining point is closest to
  setting below the horizon limit first, rather than simply whichever is highest in the sky right
  now - so a target that's about to be lost isn't stranded behind higher-but-not-urgent points. A
  point that can't clear the horizon limit for its own dwell is skipped individually (with a
  warning) instead of cancelling the whole plan.

### Fixes

- Fixed a real bug in `AstronomyUtils.HorizontalToEquatorial` (a scale mismatch in its hour-angle
  recovery, off by several to tens of degrees away from the meridian/equator) and a related
  azimuth-quadrant ambiguity in `EquatorialToHorizontal` (a bare Acos can't tell an object rising
  from one setting). Both fed the Plan/Mosaic sky maps, the HI4PI background sampling, cold-sky
  candidate selection, and AltAz FITS metadata's RA/Dec reconstruction, so the fix reaches well
  beyond where each was first noticed.
- Region-mode sweep grids are now built in a pole-safe tangent-plane projection instead of raw
  RA/Dec, fixing badly distorted grids for any region drawn near due-North from a mid/high-latitude
  site.
- Fixed a data-binding crash ("Cannot find governing FrameworkElement") on the Plan sky map's
  capture-point dots.
- Default centre frequency reverted to 1420.4058 MHz (the HI rest frequency itself).

### UI

- Plan sky map dots get a two-tone (white/black) halo so they stay visible against the HI4PI
  background regardless of its colour underneath.
- Mosaic's colour ramp switched to a Radio Eyes-style visible-spectrum scale (deep blue through
  to red), shared by the 2D heatmap and 3D Dome.
- New Range / New Region buttons replace a single ambiguous "New Plan" button; the Plan editor's
  Range/Region toggle is now display-only, since geometry mode is fixed by whichever button
  created the plan.

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
