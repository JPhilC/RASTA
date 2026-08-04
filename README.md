# RASTA — Radio Astronomy Slew • Track • Acquire

RASTA is an experimental .NET 10 WPF MVVM application for amateur radio astronomy. It is a personal, exploratory project aimed at building a complete workflow for hydrogen‑line observation using affordable hardware, custom tools, and a lot of engineering curiosity.

There is no guarantee this project will ever be “finished” — and that’s part of the fun. RASTA is a space to learn, experiment, and gradually assemble a system that might one day produce meaningful 1420 MHz data… or simply serve as a playground for telescope control, SDR capture, and spectral processing ideas.

---

## 🌌 Project Vision

RASTA exists because radio astronomy is fascinating — and because building your own tools to explore the universe is even more fascinating.

**Long‑term aspiration:**

> To create a unified, hobby‑grade radio astronomy application that can plan observations, control a telescope, capture SDR data, process hydrogen‑line spectra, and visualise results — all in one place.

Whether it reaches that goal or not, RASTA is designed to be a rewarding engineering journey.

---

## ✨ What RASTA Hopes to Achieve

RASTA is intentionally ambitious. The project aims to explore:

- A full hydrogen‑line workflow  
  **Prepare → Plan → Observe → Process → Visualise**

- Telescope control via **ASCOM Alpaca**  
  Slew, track, park, unpark, and manage mount state safely.

- SDR‑based capture  
  Focused on **RTL‑SDR** devices.  
  *(Airspy is not planned.)*

- Spectral processing experiments  
  - FFT stacking  
  - Cold‑sky normalisation  
  - Drift‑scan accumulation  
  - mx+b calibration  
  - Background correction  
  - Hydrogen‑line extraction

- Custom visualisation tools  
  2D spectra, 3D surfaces, drift‑scan heatmaps, hydrogen‑line features.

- A clean, modular architecture  
  - .NET 10  
  - WPF + MVVM Toolkit  
  - Dependency injection  
  - NUnit tests  
  - Separation of Core, Infrastructure, Processing, App, and Simulators

RASTA is not a polished product — it’s a growing ecosystem of ideas.

---

## 📡 Application Overview

RASTA is structured around five workflow stages:

### 1. Prepare
Configure telescope, SDR, frequency, gain, FFT parameters, and capture settings.

### 2. Plan
Create observation plans:
- Declination sweeps  
- RA sweeps  
- Drift scans  
- Custom geometries  

Plans can be saved, loaded, and reused.

### 3. Observe
Execute the plan:
- Telescope slews and tracks  
- SDR captures raw IQ or FFT data  
- Real‑time status monitoring  
- Safety logic for mount state

### 4. Process
Apply experimental calibration and correction algorithms.

### 5. Visualise
Render spectra, surfaces, drift‑scan maps, and hydrogen‑line features.

---

## 🧱 Architecture

- **RASTA.Core** — domain models, interfaces, telescope/SDR abstractions  
- **RASTA.Infrastructure** — Alpaca telescope implementation, RTL‑SDR capture, FFT engines, storage providers  
- **RASTA.Processing** — calibration, spectral math, heatmap generation  
- **RASTA.App** — WPF MVVM application  
- **RASTA.Tests** — NUnit test suite  
- **RASTA.Simulators** — simulated telescope and SDR for offline development

---

## 🛠 Hardware

- **Telescopes:** ASCOM Alpaca compatible mounts  
- **SDR:** RTL‑SDR  
- **Antennas:** Hydrogen‑line LNA + feedhorns, DIY dishes (e.g., 1.4m build)

---

## 📈 Current Status

RASTA is in active, exploratory development.  
Major components already exist, but many are incomplete or experimental. 
The application will connect and disconnect to an ASCOM Telescope Mount via the ASCOM Remote Server. If the telescope is Parked you are given the option of unparking (and also the option to park again when you disconnect.
It will as respond to plugging and unplugging an RTL-SDR device (tested with a Nooelec NESDR SMArtee V5). An SDR device must be plugged in to enable the Plan View (which is the most complete view at the moment).
The Calibrate button on the Plan View will run a calibration sequence and return a calibration profile.
Plan view allows sweep plans to be added.
Observe view will run a sweep plan and save rawIQ data files.
Visualise View will display a chart given a baseline data file and a capture data file (created by the Observe View) using one of 4 experimental DSP pipelines, IF Average, Hi Frequency, Hi Velocity or SKAO TTRT

---

## 🚀 Roadmap (Aspirational)

These are hopes, not promises:

- More robust hydrogen‑line processing  
- Real‑time waterfall view  
- Automated calibration sequences  
- Multi‑night drift‑scan accumulation  
- Additional visualisation modes  
- Plugin system for custom processing modules

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
