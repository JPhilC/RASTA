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
  frame accumulation → bandpass flattening (capture ÷ baseline) → RFI-rejected linear continuum
  fit → continuum subtraction → optional Savitzky–Golay smoothing → velocity axis with an
  analytic **LSR Doppler correction**. A separate fixed-256-bin port of the SKAO TTRT reference
  pipeline is kept alongside it for cross-checking.
- Telescope control via **ASCOM Alpaca** — connect/disconnect, park/unpark, slew, track.
- SDR capture via **RTL‑SDR**, with hot-plug detection (tested with a Nooelec NESDR SMArtee V5
  and RTL-SDR.COM V3).
- A real calibration routine: a gain sweep that hard-rejects any gain showing genuine ADC
  saturation (checked on the raw I/Q bytes, not inferred from the spectrum) and scores the
  survivors on flatness/spur-count/slope, followed by a long baseline capture against a
  terminator on a SAWbird H1+ LNA.
- Real, measured progress reporting throughout (captured bytes, chunks processed, files read) —
  not a simulated animation — for calibration, sweep capture, and chart generation alike.
- A capture sweep that drives the mount through a plan, saves raw IQ to FITS with full pointing
  (RA/Dec **and** Az/Alt) and site metadata baked into the header, and shows a live,
  continuously-averaging HI spectrum as each dwell point is captured.
- A Visualise view with five spectrum modes (IF, HI vs. Frequency, HI vs. Velocity, SKAO TTRT,
  and a bandpass Ratio view for sanity-checking calibration before continuum subtraction), an
  optional dB scale, and automatic combining of multi-file dwell points (`..._1of2.fits`,
  `..._2of2.fits`, …) selected from a single file.

RASTA is not a polished product — it's a growing, working system with some still-placeholder edges (see below).

---

## 📡 Application Overview

RASTA is structured around five workflow stages:

### 1. Prepare
Connect to the telescope and SDR, configure site/frequency/gain/FFT parameters, and run
calibration (gain sweep + baseline capture against a terminator).

### 2. Plan
Create observation plans — equatorial or Az/Alt sweeps, drift scans — with configurable dwell
time, files per dwell point, and settle time. Plans can be saved, loaded, and reused.

### 3. Observe
Execute the plan: slew and track, capture raw IQ per dwell point (optionally as multiple files),
show a live-updating HI spectrum built against the calibration baseline as each point is
captured, and write everything to FITS with real progress feedback.

### 4. Process
Currently a placeholder — not wired into the rest of the app yet. The actual spectral reduction
happens directly in Visualise; this stage is reserved for future work (e.g. batch reduction
across many saved captures).

### 5. Visualise
Load a baseline and/or capture FITS file (or a whole multi-file dwell point at once) and render
it through one of five DSP modes, with dB and LSR-correction options.

---

## 🧱 Architecture

- **RASTA.Core** — domain models, interfaces, telescope/SDR abstractions, astronomy math
  (LST, RA/Dec ↔ Az/Alt, LSR Doppler correction)
- **RASTA.Infrastructure** — ASCOM Alpaca telescope client, RTL‑SDR capture, FFT engine, JSON storage providers
- **RASTA.Processing** — the HI reduction pipeline, calibration, sweep planning; also some
  early-stage/placeholder sky-mosaic code (`Gridding/`, `VisualisationData/HeatmapBuilder.cs`)
  not yet wired into any view
- **RASTA.App** — the WPF MVVM application
- **RASTA.Tests** — placeholder project only; no tests yet, excluded from the default build
- **RASTA.Simulators** — placeholder project only; no simulated hardware yet, excluded from the default build

---

## 🛠 Hardware

- **Telescope:** ASCOM Alpaca compatible mounts, via the ASCOM Remote Server (not direct COM)
- **SDR:** RTL‑SDR (tested with a Nooelec NESDR SMArtee V5)
- **LNA:** SAWbird H1+ (used as the calibration front end, terminated for baseline capture)
- **Antenna:** DIY 1.4m dish + hydrogen‑line feed

---

## 📈 Current Status

RASTA is in active, exploratory development. The Prepare → Plan → Observe → Visualise path is a
real, working loop end to end; Process is not yet wired up.

- The app connects/disconnects to an ASCOM telescope mount via the ASCOM Remote Server, offers to
  unpark a parked mount on connect (and to re-park on disconnect).
- It responds to plugging/unplugging an RTL-SDR device. An SDR must be enumerated to unlock the
  Plan and Observe views.
- **Prepare** runs calibration (gain sweep + baseline capture) with independently configurable
  gain-sweep and baseline dwell times, and can reuse a previously saved calibration profile.
- **Plan** builds and saves equatorial/Az-Alt sweep or drift-scan plans.
- **Observe** runs a sweep plan, capturing raw IQ (optionally several files per dwell point) and
  showing a live HI spectrum as it goes.
- **Visualise** loads baseline/capture FITS (auto-combining multi-file dwell points) and renders
  IF, HI Frequency, HI Velocity, SKAO TTRT, or Bandpass Ratio charts, with dB scaling and an LSR
  velocity correction applied from each file's recorded pointing, time, and site.

---

## 🚀 Roadmap (Aspirational)

These are hopes, not promises:

- Wire up (or replace) the Process stage
- Sky-mosaic / heatmap view combining many pointings — the early `Gridding`/`HeatmapBuilder` code
  needs reworking to consume the current HI pipeline's output rather than the old data shape it
  was written against
- Real-time waterfall view
- Automated multi-target calibration sequences
- Multi-night drift-scan accumulation
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
  (terminator + gain sweep + baseline calibration) / Observe (sky spectrum, HI velocity plot)
  workflow follows the same shape as the SKAO tabletop telescope's. Licensed BSD-3-Clause,
  © 2023 SKA Observatory.
- **[Daniel M. Kamiński](https://github.com/DanielKami/SDR_AVE_new)** — the
  `RASTA.Processing/IfAverage` signal-averaging chain (median filter, RFI detector,
  intermediate/long-term averaging, background subtraction, Savitzky–Golay smoothing) is adapted
  from his "SDR AVE" Advanced Signal Averaging Plugin for SDR# (SDRSharp), licensed GNU AGPL-3.0.

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

GNU GPL 3.0

---

## 🤝 Contributing

This is a personal project, but contributions may be welcomed once the core stabilises.

---

## 💬 Author

**Phil Crompton**
Coalville, UK
Software developer & astronomy enthusiast
