# HiPipeline Module Overview



The **HiPipeline** module provides a complete, SDR‑agnostic processing chain for extracting clean, scientifically meaningful hydrogen‑line (HI) spectra from raw FFT power data. It is designed for use with long integrations, arbitrary FFT sizes, and both tracked and drift‑scan observations.



The module is built around three core components:



- **HiStreamingAccumulator**

&#x20; A frame‑averaging engine that collects and averages baseline and capture spectra over time.



- **HiStreamingPipeline**

&#x20; A full HI‑reduction pipeline that flattens the bandpass, converts to velocity space, subtracts the continuum, and produces a clean HI spectrum.



- **HiStreamingProcessor**

&#x20; A high‑level wrapper that ties the accumulator and pipeline together into a simple, streaming‑friendly interface.



---



## 1. Purpose



Radio astronomy signals are extremely weak, and raw FFT power spectra from an SDR contain strong bandpass shapes, noise, and continuum background. The HiPipeline module removes these effects and produces a stable, calibrated HI spectrum suitable for:



- hydrogen‑line detection

- drift‑scan analysis

- tracked observations

- RA/Dec sky‑mapping

- long‑term averaging

- scientific comparison between pointings



It is the “clean‑up” stage between raw SDR data and usable radio astronomy output.



---



## 2. HiStreamingAccumulator



The accumulator collects FFT power frames and averages them over time.



### Key features



- Accepts **any FFT size**

- Supports **arbitrary numbers of frames**

- Maintains separate sums for **baseline** and **capture**

- Produces averaged spectra for stable downstream processing



### Why it matters



Averaging reduces noise and stabilises the spectrum, which is essential for detecting the HI line and for producing consistent results across multiple sky positions.



---



## 3. HiStreamingPipeline



This is the core HI‑reduction engine. It transforms averaged FFT spectra into a clean hydrogen‑line profile.



### Processing steps



1. **FFT shift**

&#x20;  Re‑orders the spectrum so frequency increases left‑to‑right.



2. **Frequency axis generation**

&#x20;  Computes the true frequency of each FFT bin.



3. **Velocity conversion**

&#x20;  Converts frequency offsets around 1420.40575177 MHz into radial velocity (km/s).



4. **Baseline division**

&#x20;  Divides capture by baseline to flatten the SDR’s bandpass response.



5. **Continuum masking**

&#x20;  Excludes the central HI region and far wings to isolate the smooth background.



6. **Linear continuum fitting**

&#x20;  Fits a straight line to the masked regions.



7. **Continuum subtraction**

&#x20;  Removes the fitted background, leaving only the HI signal.



8. **Savitzky–Golay smoothing (optional)**

&#x20;  Reduces noise while preserving the shape of the HI line.



### Output



- **FrequencyHz** — frequency axis

- **VelocityKmPerSec** — velocity axis

- **RatioSpectrum** — bandpass‑flattened spectrum

- **HiSpectrum** — continuum‑subtracted, smoothed HI line



---



## 4. HiStreamingProcessor



A convenience wrapper that:



- accepts streaming baseline/capture frames

- performs accumulation

- runs the full pipeline

- exposes the final HI spectrum and axes



This is the class you use directly in applications.



---



## 5. What the module achieves



After processing, the output HI spectrum:



- is stable over long integrations

- is independent of SDR bandpass shape

- is expressed in physical velocity units

- has the continuum removed

- is ready for scientific use or sky‑mapping



This makes the HiPipeline module suitable for:



- hydrogen‑line detection

- drift‑scan surveys

- tracked observations

- RA/Dec intensity mapping

- velocity‑resolved studies

- educational demonstrations

- amateur radio astronomy research



---



## 6. Summary



The **HiPipeline** module is a full hydrogen‑line reduction pipeline designed for real‑world SDR‑based radio astronomy. It takes raw FFT power data and produces clean, calibrated HI spectra that can be used for mapping, analysis, and long‑term studies.



It is flexible, FFT‑size‑agnostic, and suitable for both hobbyist and research‑grade workflows.


                          ┌──────────────────────────────┐
                          │     Raw FFT Power Frames      │
                          │  (baseline + capture streams) │
                          └───────────────┬───────────────┘
                                          │
                                          ▼
                     ┌──────────────────────────────────────────┐
                     │        HiStreamingAccumulator             │
                     │  - collects baseline frames               │
                     │  - collects capture frames                │
                     │  - averages each independently            │
                     └───────────────┬──────────────────────────┘
                                     │
                                     ▼
                    ┌───────────────────────────────────────────┐
                    │         Averaged Spectra (baseline, capture)│
                    └───────────────────┬─────────────────────────┘
                                        │
                                        ▼
                     ┌──────────────────────────────────────────┐
                     │           HiStreamingPipeline             │
                     ├──────────────────────────────────────────┤
                     │ 1. FFT Shift                              │
                     │    (centre spectrum around HI frequency)  │
                     │                                            │
                     │ 2. Frequency Axis                         │
                     │    (true Hz for each FFT bin)             │
                     │                                            │
                     │ 3. Velocity Axis                          │
                     │    (convert frequency → km/s)             │
                     │                                            │
                     │ 4. Baseline Division                      │
                     │    (capture ÷ baseline → flatten bandpass)│
                     │                                            │
                     │ 5. Continuum Masking                      │
                     │    (exclude HI region + far wings)        │
                     │                                            │
                     │ 6. Linear Continuum Fit                   │
                     │    (fit straight line to masked regions)  │
                     │                                            │
                     │ 7. Continuum Subtraction                  │
                     │    (remove background → isolate HI line)  │
                     │                                            │
                     │ 8. Savitzky–Golay Smoothing (optional)    │
                     │    (reduce noise, preserve line shape)    │
                     └───────────────────┬────────────────────────┘
                                         │
                                         ▼
                     ┌──────────────────────────────────────────┐
                     │         Final HI Spectrum Outputs         │
                     ├──────────────────────────────────────────┤
                     │ - FrequencyHz (Hz)                        │
                     │ - VelocityKmPerSec (km/s)                 │
                     │ - RatioSpectrum (bandpass‑flattened)      │
                     │ - HiSpectrum (continuum‑subtracted HI)    │
                     └──────────────────────────────────────────┘


