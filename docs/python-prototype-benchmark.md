# Python prototype performance baseline

Issue #6 records a short Phase 0 baseline before the production migration. The goal is to locate time in the existing Clone / repair-paint path, not to optimize or redesign the Python prototype.

The preserved Python/Tkinter implementation now lives under `experiments/python-tkinter/`. Commands below are written from the repository root unless otherwise stated.

## Environment

Measurements were taken on 2026-09-12 in the available headless development environment.

| Item | Value |
| --- | --- |
| OS | Linux 6.18.35, x86-64, KVM virtual machine |
| CPU allocation | 9 vCPU, AMD EPYC 7763 host |
| Python | 3.12.14 |
| OpenCV | 4.14.0 |
| NumPy | 2.5.3 |
| Pillow | 12.3.0 |
| Display | Headless; Tk `PhotoImage` and Canvas update unavailable |

These values are a repository baseline from one virtual environment, not a Windows hardware target or a cross-machine score.

## Measured path

The interactive application has opt-in instrumentation around its existing path:

1. Tk pointer samples and their arrival interval.
2. Clone-stroke setup, including the current undo snapshot and optional Hole Only mask union.
3. The existing `paint_aligned_clone` local-ROI kernel.
4. Repair-layer cache / composite invalidation and refresh scheduling.
5. Full preview composite, overlay, PIL viewport transform, `ImageTk.PhotoImage`, and Tk Canvas update as separate stages.
6. Stroke wall time and the sum of recorded processing stages as separate values.

Stroke wall time deliberately includes time between human pointer events. It must not be interpreted as CPU processing time. Instrumentation is disabled by default. When disabled it skips timing clock reads and the additional ROI metadata calculation.

Because the benchmark host has no display, the checked-in headless runner exercises the same `paint_aligned_clone` kernel and `CutoutSpikeApp._composite_preview` method directly. It also performs the same PIL affine viewport transform. It does not provide a replacement Clone implementation.

Run the repeatable headless measurement with:

```text
python experiments/python-tkinter/benchmark_python_prototype.py
```

For a manual Windows/Tk run, either `cd experiments\python-tkinter` first or invoke the launcher by path. Example:

```powershell
cd experiments\python-tkinter
$env:CUTWORK_BENCHMARK = "1"
$env:CUTWORK_BENCHMARK_OUTPUT = "clone-benchmark.jsonl"
python app_clone_guriguri.py
```

Each completed Clone Paint stroke prints one JSON summary and, when the output variable is set, appends it to the selected JSON Lines file.

## Headless method

The runner uses deterministic synthetic RGB images at three sizes. Each case performs one warm-up stroke followed by four measured strokes. A stroke contains 24 segments, uses a 14 px radius, keeps Hole Only active over a central mask, and prepares a visible refresh after every four segments. The viewport is fixed at 1280×720.

The 3840×2160 case is an additional large-image boundary. There is no repository artwork suitable for a reproducible public benchmark, so no private or content-dependent image is used.

## Results

Times are milliseconds. Values are average with observed minimum–maximum in parentheses. Sample/kernel values contain 96 segments per image size; display values contain 24 refreshes per size.

| Document | ROI pixels | Interpolated points | Kernel | Image update | Sample total | Full composite | Viewport transform | Display preparation | Stroke processing |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1280×720 | 1,441 (1,419–1,452) | 11.7 (11–12) | 0.52 (0.30–3.41) | 0.001 (0.0005–0.003) | 0.53 (0.31–3.41) | 34.67 (31.02–63.38) | 21.79 (20.45–33.84) | 56.46 (51.52–92.59) | 351.53 (326.22–374.97) |
| 1920×1080 | 1,584 | 16 | 0.52 (0.33–2.41) | 0.001 (0.0005–0.003) | 0.53 (0.34–2.42) | 73.70 (70.88–93.57) | 21.85 (21.32–22.98) | 95.55 (92.48–116.56) | 586.14 (578.85–602.03) |
| 3840×2160 | 1,584 | 16 | 0.55 (0.33–2.55) | 0.110 (0.0004–1.44) | 0.66 (0.33–3.96) | 329.61 (297.11–557.82) | 26.53 (25.60–29.81) | 356.16 (322.72–585.47) | 2,152.98 (2,047.88–2,251.27) |

`Display preparation` is full composite plus the PIL viewport transform in this headless run. It excludes overlay work, `ImageTk.PhotoImage`, Tk Canvas replacement, and event-loop scheduling. `Stroke processing` is runner CPU work for the configured 24 segments and six refresh preparations; it is not the instrumented human-gesture wall time.

## Observations

- The local Clone kernel remains near 0.5 ms on average because its ROI stays around 1.4–1.6 thousand pixels. Its cost does not materially scale with the full document dimensions in these cases.
- Cache assignment / invalidation is negligible in the first two cases. The 4K variation is small relative to compositing and should not be treated as an optimization target from this sample alone.
- The existing full-frame composite scales with document size: about 35 ms at 720p, 74 ms at 1080p, and 330 ms at 4K.
- Transforming the preview into a 1280×720 viewport adds about 22–27 ms. This stays comparatively flat because the output viewport size is fixed.
- Even before Tk image creation and Canvas replacement, every measured display preparation exceeds a 30 fps frame budget of 33.3 ms. Full-frame preview work is therefore the likely primary bottleneck, while the local Clone kernel is not.
- The result supports the accepted production direction, ROI editing, dirty-region composition, and separate overlays, but does not by itself justify making GPU rendering a Phase 1 requirement.

## Limitations

- The host is Linux, virtualized, and headless. `ImageTk.PhotoImage`, Tk Canvas update, actual pointer cadence, event coalescing, and perceived latency were not measured here.
- The input is synthetic and deterministic. Different layer counts, masks, source textures, brush radii, or active overlays can change costs.
- Four measured strokes are enough for a directional baseline, not statistical performance qualification.
- The benchmark retains the prototype's current rectangle source and Hole Only behavior. It makes no production UX recommendation.
- Stage timings add small observer overhead when explicitly enabled. Normal operation remains opt-in and avoids clock reads and benchmark metadata collection.
- Results describe the preserved Python/Tk/PIL experiment only. They are not acceptance thresholds for the WPF implementation.

## Re-measure after the WPF foundation

Use the same conceptual boundaries on representative Windows hardware:

- OS pointer arrival interval, coalesced sample count, and document-space stroke interpolation.
- Clone ROI dimensions, touched pixels, and CPU kernel time for the ported behavior.
- Dirty-region composite/update cost independently from the kernel.
- `WriteableBitmap` buffer copy / lock / dirty-rectangle notification cost.
- Overlay cursor rendering and present cadence independently from document pixels.
- Input-to-visible latency under sustained strokes, including p50 and a simple slow-frame range.
- Full-quality settle refresh after the stroke ends.
- Memory use and responsiveness at 720p, 1080p, and 4K with realistic layer counts.

Phase 1 was not blocked on obtaining a Windows number for the legacy Tk display. The important Phase 0 finding remains clear: keep local editing local, and do not reproduce the prototype's per-refresh full-document composite path in the production canvas.
