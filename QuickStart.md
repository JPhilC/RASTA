# RASTA Quick Start

This is a practical, step-by-step guide to going from a fresh install to your first captured and
visualised hydrogen-line spectrum. It assumes the hardware described in the README (an ASCOM Alpaca
mount, an RTL-SDR receiver, an LNA, and a dish) — adjust as needed for your own setup.

RASTA is exploratory hobby software; if something on screen doesn't match this guide exactly, the
in-app tooltips and the `CLAUDE.md`/README are the more detailed references.

---

## 1. What you need in place first

### Hardware
- An **ASCOM Alpaca**-compatible mount (or a mount driven via the **ASCOM Remote Server**, which
  exposes any classic ASCOM/COM driver as Alpaca) — connected and powered up, with the Remote
  Server running and reachable on your network before you launch RASTA.
- An **RTL-SDR** receiver (tested with a Nooelec NESDR SMArtee V5 and RTL-SDR.COM V3), plugged in
  via USB.
- An **LNA** in front of the SDR for calibration and observing (a SAWbird H1+ is what's been tested).
- A dish/antenna for the hydrogen line — RASTA's own defaults assume a 1.4 m dish at f/0.4, but any
  dish works once you set its real diameter/focal length in Settings.
- A 50 Ω terminator (or dummy load) you can quickly swap onto the LNA input — used only during the
  device gain sweep, not for anything else.

### Software
- Windows, with the app installed (via the WiX-based installer, which chains in the required .NET
  10 Desktop Runtime automatically) or built from source (`dotnet build RASTA.slnx`, then
  `RASTA.App`).
- Your ASCOM Alpaca Remote Server already running, if your mount needs it.

None of the four workflow stages *require* hardware just to look around — **Plan** in particular
works fully offline — but Capture needs both a connected mount and SDR.

---

## 2. First launch: Prepare screen

Everything starts on **Prepare**.

### 2.1 Site Settings
Before anything else, set:
- **Site latitude / longitude / elevation** — your observing location. These persist across
  restarts and are used everywhere (LST, LSR correction, cold-sky candidate search, sky maps) even
  before a mount is ever connected.
- **Dish diameter** and **focal length** — used to estimate your antenna's beamwidth, which in turn
  suggests a sensible default point spacing for new plans.

If you later connect a mount that reports different site coordinates, RASTA will ask whether to
push your values to the mount or pull the mount's values into RASTA — it won't silently overwrite
what you've entered.

### 2.2 Connect the SDR and telescope
- Plug in the RTL-SDR — it's detected automatically (hot-plug aware), and Prepare's SDR panel lets
  you select it and set center frequency, sample rate, gain, and FFT size. The default center
  frequency is tuned near the hydrogen line (1420.4 MHz).
- Connect the telescope mount. If it's parked, RASTA offers to unpark it.

### 2.3 Calibrate
Calibration is three independent, resumable button-presses — do them in order the first time; on
later sessions you can often just reload a saved calibration.

1. **Load Last Calibration** — loads whatever calibration profile was last saved. Always available;
   touches no hardware. Try this first if you calibrated in a previous session and nothing about
   your setup (gain, frequency, SDR) has changed.
2. **Calibrate Device Gain** — needs the SDR connected (no mount needed yet). **Attach the
   terminator to the LNA input** when prompted. RASTA sweeps every supported gain, rejects any that
   saturate the ADC, and picks the flattest/cleanest survivor. This is saved to disk immediately as
   a gain-only profile, so even if you stop here you can resume later with Load Last Calibration.
3. **Capture Baseline** — needs the mount connected too, since it slews. **Reconnect your real
   antenna** when prompted (remove the terminator). RASTA computes several candidate "cold sky"
   positions (away from the Galactic plane), slews to one, and asks you to confirm nothing is
   physically blocking that view (a building, a tree, the mount's own head). If it's obstructed,
   say no and it'll pick another, or you can **Recalculate** for a fresh set. Once confirmed, it
   captures a dwell at that position and saves it as your baseline — completing the calibration
   profile.

You're calibrated once **Capture** and **Visualise** show as unlocked/usable — Prepare's own
`IsCalibrated` indicator reflects whether a real baseline (not just a gain-only profile) is loaded.

---

## 3. Plan an observation

Move to **Plan** — this works even without any hardware connected, so it's worth exploring before
you ever calibrate.

1. **New Range** or **New Region**:
   - *Range* — type explicit RA/Dec (or Az/El) start and end limits and an angular separation; RASTA
     builds a row-by-row grid, properly spaced for real angular separation (not just a flat
     coordinate step).
   - *Region* — freeform: click **Draw Region** and left-click points directly on the sky map to
     trace an area, then **Finish Region**. RASTA fills it with a grid at your chosen spacing.
     Equatorial plans only.
2. Set capture parameters in the **Plan Editor** window (opened via the toolbar) — dwell time, files
   per point, settle time, and (for Equatorial/AltAz) which coordinate mode the plan targets.
3. Watch the **Radio Sky** map update — it shows your plan's actual capture points (colour-ordered
   start→end) against a real Milky Way backdrop, already run through the same horizon-limit
   validation and elevation-urgency ordering a real sweep uses. If some points dip below your
   horizon limit, they're shown but flagged, rather than failing the whole plan.
4. Give it a **Friendly Name** and click **Save Plan**.

Tip: right-clicking any point on the map offers **Slew & Capture Here** — a quick one-off capture at
that exact spot, if a mount and SDR are already connected.

---

## 4. Capture

Move to **Capture** — this requires both the SDR and the telescope mount connected, since it drives
a real slew.

1. Pick a saved plan from the list — only plans matching the mount's current coordinate mode
   (Equatorial/AltAz) are offered, since a plan built for one mode can't be slewed correctly in the
   other.
2. Click **Start Sweep**. RASTA switches tracking on, slews to each dwell point in turn, captures
   raw IQ to FITS, and shows a live-updating HI spectrum (built against your calibration baseline)
   as each point streams in. Progress and an estimated completion time (refined from real
   measured per-point timing) are shown throughout.
3. You can **Cancel** at any point — nothing partial is written to disk; only fully-completed dwell
   points remain as files.
4. When it's done (or if `Go To Home After Capture` is set), the mount returns home.

**Quick Capture** is the alternative for a single, one-off file at wherever the mount is *currently*
pointed (e.g. positioned by hand or another ASCOM tool, or via Plan's right-click hand-off) — no
plan needed, just a loaded calibration and connected hardware.

Captured files land under your configured capture folder as
`{frequency}MHz/{yyyy-MM-dd}/..._{n}of{total}.fits`.

---

## 5. Visualise

Move to **Visualise** to turn captured FITS files into spectra.

1. **Single file / single position**: load a baseline and/or capture FITS file (selecting one file
   of a multi-file dwell point automatically pulls in and combines its siblings). Choose a spectrum
   mode:
   - **HI Frequency** / **HI Velocity** — the main reduced spectrum (baseline-divided, continuum-
     subtracted), velocity axis LSR-corrected.
   - **SKAO TTRT** — a fixed reference-pipeline cross-check.
   - **Ratio** — the bandpass-flattened capture/baseline ratio before continuum subtraction, useful
     for sanity-checking calibration; the one mode safe to view in dB.
2. Optional: enable smoothing (Savitzky–Golay or moving average), a dB scale (Ratio mode only), or
   the narrowband-RFI despike pass if you see a fixed comb-like spur.
3. **Mosaic tab**: point at a whole session folder (one baseline + several dwell-point captures
   across different pointings — or a folder of downloaded LAB Survey profiles for testing without
   real observing time) and click **Generate Mosaic**. This reduces every position through the same
   HI pipeline and renders:
   - a 2D sky heatmap (line strength or peak velocity, sinusoidal equal-area projection),
   - a **Zenith Dome** — a naked-eye-style Alt/Az view of every position as it looks from your site
     right now (or at any chosen time),
   - a **3D Dome** — the same view extruded into height-coded stems (or an optional fitted surface).

Any of the three views can be clicked to select the matching row in the positions table, and vice
versa.

---

## 6. A typical first-session checklist

1. Set Site Settings (lat/lon/elevation, dish diameter/focal length) — once, persists.
2. Connect SDR + mount.
3. Terminator on → **Calibrate Device Gain**.
4. Antenna back on → **Capture Baseline** (confirm the cold-sky position is unobstructed).
5. Go to **Plan**, build a small test Range or Region plan aimed at a bright HI region (e.g. near
   the Galactic plane), save it.
6. Go to **Capture**, run the sweep, watch the live spectrum.
7. Go to **Visualise**, load the capture (+ baseline), check **HI Velocity** mode for a bump near
   the expected LSR velocity for your target.
8. If you captured a whole session folder of dwell points, try the **Mosaic** tab to see it as a
   sky map.

---

## Troubleshooting notes

- **Capture and Visualise stay locked**: check the status bar — both need a connected SDR *and*
  mount; Plan does not.
- **No plans offered on the Capture screen**: `CaptureViewModel` only lists plans matching the
  mount's *current* coordinate mode (Equatorial vs. AltAz) — switch the mount's mode, or build a
  plan for the mode it's already in.
- **Mount refuses to slew**: this app always switches tracking on before slewing (some ASCOM
  drivers reject a slew while tracking is off) — if a slew still fails, check the mount is actually
  connected and not already mid-motion.
- **Mount connection drops mid-session**: RASTA detects this from a failed live poll (there's no
  other reliable signal), cancels any in-flight capture, resets connection state, and returns you to
  Prepare with an explanation. There's no auto-reconnect by design — reconnect manually once you've
  confirmed the mount's actual physical state.
- **A spectrum looks noisier after lowering Target FFT Size**: make sure you're on a recent build —
  older FFT-size downscaling had a real aliasing bug, since fixed.
- **A narrow, fixed-position spike in every spectrum regardless of pointing**: likely the receiver's
  own DC/LO leakage, which RASTA already excises automatically. A *different*, off-center spur at a
  fixed relative offset (a birdie) isn't auto-removed — try the opt-in despike pass in Visualise.

For deeper detail on any of this, see `README.md` (feature overview), `HiPipelineDescription.md`
(the reduction pipeline internals), and `CLAUDE.md` (full architecture notes).
