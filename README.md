# RASTA — Radio Astronomy Slew • Track • Acquire

RASTA is an experimental .NET 10 WPF MVVM application for amateur radio astronomy. It is a personal, exploratory project building a real working hydrogen‑line (1420 MHz / 21cm) observation workflow — telescope control, SDR capture, and spectral reduction — around a DIY 1.4m dish, an ASCOM Alpaca-driven mount, and an RTL-SDR receiver.

There is no guarantee this project will ever be "finished" — and that's part of the fun. RASTA is a space to learn, experiment, and gradually assemble a system that produces meaningful 1420 MHz data.

---

## 🌌 Project Vision

RASTA exists because radio astronomy is fascinating — and because building your own tools to explore the universe is even more fascinating.

**Long‑term aspiration:**

> A unified, hobby‑grade radio astronomy application that can plan observations, control a telescope, capture SDR data, reduce hydrogen‑line spectra, and visualise results — all in one place.

Whether it reaches that goal or not, RASTA is designed to be a rewarding engineering journey.

---

## ✨ What Works Today

- A working hydrogen‑line reduction pipeline (`HiStreamingAccumulator` / `HiStreamingPipeline`):
  frame accumulation → DC/LO-spike excision → bandpass flattening (capture ÷ baseline) →
  RFI-rejected linear continuum fit → continuum subtraction → optional Savitzky–Golay or
  moving-average smoothing → velocity axis with an analytic **LSR Doppler correction**. An
  opt-in narrowband-RFI despike pass (robust, MAD-based detection with hysteresis growth) can
  excise things like a USB3/mount-controller comb spur from a spectrum before it's used. A
  separate fixed-256-bin port of the SKAO TTRT reference pipeline is kept alongside it for
  cross-checking.
- Telescope control via **ASCOM Alpaca** — connect/disconnect, park/unpark, slew, track — with
  automatic recovery if the mount connection drops mid-session (any in-flight capture is
  cancelled, connection state is reset, and the app returns to Prepare with an explanation).
- SDR capture via **RTL‑SDR**, with hot-plug detection (tested with a Nooelec NESDR SMArtee V5
  and RTL-SDR.COM V3).
- A real calibration routine: a gain sweep that hard-rejects any gain showing genuine ADC
  saturation (checked on the raw I/Q bytes, not inferred from the spectrum) and scores the
  survivors on flatness/spur-count/slope, followed by a baseline capture against automatically
  located, obstruction-checked cold-sky positions (falling back to a terminator only as an
  earlier, coarser step).
- Real, measured progress reporting throughout (captured bytes, chunks processed, files read,
  positions processed) — not a simulated animation — for calibration, sweep capture, and chart/
  mosaic generation alike, each with its own Cancel button.
- A capture sweep that drives the mount through a plan (keeping tracking on for every slew, even
  if the plan itself doesn't ask for continuous tracking), saves raw IQ to FITS with full
  pointing (RA/Dec **and** Az/Alt) and site metadata baked into the header, shows a live,
  continuously-averaging HI spectrum as each dwell point is captured, and can be cancelled
  mid-run without leaving a partial FITS file behind. A Quick Capture mode grabs a single file at
  wherever the mount is currently pointed, for hand- or third-party-tool-positioned observing.
- A Visualise view with four spectrum modes (HI vs. Frequency, HI vs. Velocity, SKAO TTRT, and a
  bandpass Ratio view for sanity-checking calibration before continuum subtraction), an optional dB
  scale, and automatic combining of multi-file dwell points (`..._1of2.fits`, `..._2of2.fits`, …)
  selected from a single file.
- A Mosaic tab that points at a whole session folder (one baseline + several multi-file dwell-point
  captures across different pointings), reduces each position through the same HI pipeline, and
  renders the result as both a 2D sky heatmap and a 3D height-field surface (line strength or
  peak velocity), with nice-number axis ticks/gridlines and an optional smoothed/blended render.
- A Windows installer (WiX-based MSI + Burn bootstrapper) that chains in the .NET 10 Desktop
  Runtime automatically, plus a one-command release build script.

RASTA is not a polished product — it's a growing, working system with some still-placeholder edges (see below).

---

## 📡 Application Overview

RASTA is structured around four workflow stages:

### 1. Prepare
Connect to the telescope and SDR, configure site/frequency/gain/FFT parameters, and run
calibration as three independent, resumable steps: load a saved calibration, run a device gain
sweep, and capture a baseline against an automatically located cold-sky position (with a manual
obstruction check and re-pick loop).

### 2. Plan
Create observation plans — equatorial or Az/Alt sweeps, drift scans — with configurable dwell
time, files per dwell point, and settle time. Plans can be saved, loaded, and reused.

### 3. Capture
Execute the plan: slew and track, capture raw IQ per dwell point (optionally as multiple files),
show a live-updating HI spectrum built against the calibration baseline as each point is
captured, and write everything to FITS with real progress feedback. Both a full sweep and a
single-shot Quick Capture can be cancelled in flight.

### 4. Visualise
Load a baseline and/or capture FITS file (or a whole multi-file dwell point at once) and render
it through one of four DSP modes, with dB scaling, optional smoothing/despiking, and an LSR
velocity correction — or switch to the Mosaic tab to turn a whole session folder into a sky map.

---

## 🧱 Architecture

- **RASTA.Core** — domain models, interfaces, telescope/SDR abstractions, astronomy math
  (LST, RA/Dec ↔ Az/Alt ↔ Galactic, LSR Doppler correction)
- **RASTA.Infrastructure** — ASCOM Alpaca telescope client, RTL‑SDR capture, FFT engine, JSON storage providers
- **RASTA.Processing** — the HI reduction pipeline, calibration (including cold-sky site
  selection), sweep planning, and the Mosaic sky-map's gridding/visualisation-data builders
- **RASTA.App** — the WPF MVVM application
- **RASTA.Tests** — placeholder project only; no tests yet, excluded from the default build
- **RASTA.Simulators** — placeholder project only; no simulated hardware yet, excluded from the default build
- **RASTA.Setup / RASTA.Bundle** — the WiX-based MSI installer and Burn bootstrapper (chains in
  the .NET 10 Desktop Runtime) used to build a distributable installer

---

## 🛠 Hardware

- **Telescope:** ASCOM Alpaca compatible mounts, via the ASCOM Remote Server (not direct COM)
- **SDR:** RTL‑SDR (tested with a Nooelec NESDR SMArtee V5 and RTL-SDR.COM V3)
- **LNA:** SAWbird H1+ (used as the calibration front end)
- **Antenna:** DIY 1.4m dish + hydrogen‑line feed

---

## 📈 Current Status

RASTA is in active, exploratory development. The Prepare → Plan → Capture → Visualise path is a
real, working loop end to end.

- The app connects/disconnects to an ASCOM telescope mount via the ASCOM Remote Server, offers to
  unpark a parked mount on connect (and to re-park on disconnect), and recovers gracefully if the
  live connection to the mount is lost mid-session.
- It responds to plugging/unplugging an RTL-SDR device. An SDR must be enumerated to unlock the
  Plan and Capture views; a mount must also be connected to unlock Capture.
- **Prepare** runs calibration (gain sweep + cold-sky baseline capture) as three independent,
  resumable steps with their own dwell-time settings, and can reuse a previously saved
  calibration profile.
- **Plan** builds and saves equatorial/Az-Alt sweep or drift-scan plans.
- **Capture** runs a sweep plan, capturing raw IQ (optionally several files per dwell point) and
  showing a live HI spectrum as it goes, or a single Quick Capture at the mount's current
  position; either can be cancelled without leaving a partial file behind.
- **Visualise** loads baseline/capture FITS (auto-combining multi-file dwell points) and renders
  HI Frequency, HI Velocity, SKAO TTRT, or Bandpass Ratio charts (plus a standalone
  frequency/power view when only a baseline or only a capture file is selected), with dB scaling,
  optional smoothing/despiking, and an LSR velocity correction applied from each file's recorded
  pointing, time, and site; its Mosaic tab turns a whole session folder into a 2D/3D sky map.
- A release installer can be built in one step via `scripts/Build-Release.ps1`.

---

## 🚀 Roadmap (Aspirational)

These are hopes, not promises:

- Real-time waterfall view
- Automated multi-target calibration sequences
- Multi-night drift-scan accumulation (partial support exists in the Mosaic view's full-sky grid,
  which is designed to fill in across many sessions over time)
- A real automated test suite and hardware simulators (currently both stub projects)
- Plugin system for custom processing modules

---

## 🙏 Acknowledgments

RASTA builds on work generously shared by others in the amateur/educational radio astronomy
community:

- **[SKA Observatory](https://gitlab.com/ska-telescope/ska-tabletop-radiotelescope)** — the
  `SkaoPipelineProcessor`/SKAO TTRT mode is a C# port of the reduction pipeline from the *Ska
  Tabletop Radiotelescope* project (built out of a SKAO Design Thinking Workshop), kept in RASTA
  specifically to cross-check the main HI pipeline's output. RASTA's own Prepare
  (gain sweep + cold-sky baseline calibration) / Capture (sky spectrum, HI velocity plot)
  workflow follows the same shape as the SKAO tabletop telescope's. Licensed BSD-3-Clause,
  © 2023 SKA Observatory.
- **[Daniel M. Kamiński](https://github.com/DanielKami/SDR_AVE_new)** — an early signal-averaging
  chain (median filter, RFI detector, intermediate/long-term averaging, background subtraction,
  Savitzky–Golay smoothing) was adapted from his "SDR AVE" Advanced Signal Averaging Plugin for SDR#
  (SDRSharp), licensed GNU AGPL-3.0. It's since been removed from RASTA — comparing it against the
  original plugin showed it was designed as a live, continuously-refreshing display (a sliding
  window), not a full-dwell integrator for a fixed recorded file, so it was never suited to reducing
  a whole capture into one spectrum the way `HiStreamingAccumulator` now does. Its one still-useful
  piece, the Savitzky–Golay smoothing kernel, lives on in `RASTA.Processing/Dsp` and is also used
  by `HiStreamingPipeline`'s own optional smoothing pass.

Thank you both — RASTA wouldn't have gotten this far without having real reference implementations
to learn from and check against.

---

## 📚 Why This Project Exists

Because building your own radio astronomy tools is fun.
Because learning is fun.
Because seeing a hydrogen‑line bump in data you captured yourself is magical.

RASTA is a hobby project — a place to explore ideas without deadlines, pressure, or expectations.

---

## 📄 License

GNU AGPL 3.0

---

## 🤝 Contributing

This is a personal project, but contributions may be welcomed once the core stabilises.

---

## 💬 Author

**Phil Crompton**
Coalville, UK
Software developer & astronomy enthusiast
