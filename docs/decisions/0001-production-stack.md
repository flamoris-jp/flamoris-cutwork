# ADR 0001: Production technology stack

- Status: Accepted for v0.1 design
- Date: 2026-09-12
- Issue: [#3](https://github.com/flamoris-jp/flamoris-cutwork/issues/3)

## Context

FLAMORIS Cutwork is a small, Windows-oriented editor for turning one illustration into only the parts needed by a short moving-picture workflow. Its success criterion is **perceptual sufficiency × editing speed**, not general-purpose image editing or pixel-perfect semantic segmentation.

The preserved Python/Tkinter application is an executable experiment and behavior reference. It already demonstrates useful deterministic algorithms and workflows, but Issue #2 shows that the current full-frame PIL transform and `ImageTk.PhotoImage` presentation path does not provide acceptable Clone Paint responsiveness on real artwork.

The production stack must provide:

- a conventional Windows desktop shell;
- low-latency pointer and brush interaction;
- document-space pixel operations with dirty-region presentation;
- practical migration of NumPy/OpenCV algorithms;
- deterministic save/open and export;
- Japanese-first localization;
- straightforward Windows packaging; and
- a development surface that remains understandable to AI-assisted and human contributors.

Cross-platform support is not a v0.1 requirement. GPU acceleration must remain possible, but is not assumed to be necessary.

## Decision

Use the following production stack:

- **Language/runtime:** C# on **.NET 10 LTS**.
- **Application shell:** **WPF** with XAML for the main window, menus, toolbar, panels, dialogs, commands, and accessibility.
- **Canvas presentation:** a custom WPF canvas control backed initially by a **premultiplied BGRA `WriteableBitmap`** plus separate lightweight overlay visuals for the brush cursor, polygon, handles, and clone-source marker.
- **Image core:** UI-independent C# libraries using explicit pixel-buffer, mask, rectangle, and affine-transform types.
- **Classical image operations:** managed C# kernels where simple and hot; **OpenCvSharp** behind the imaging boundary for the initial port of boundary maps, blur, remap, affine warp, and related proven OpenCV operations.
- **Persistence:** `System.IO.Compression.ZipArchive`, `System.Text.Json`, a canonical normalized Original PNG, and lossless layer PNG assets in a versioned `.flimg` container. Exact imported bytes may also be preserved as provenance.
- **Input/output codecs:** Windows Imaging Component/WPF codecs for the initial PNG/JPEG boundary. Add a dedicated codec dependency only when an accepted format requirement justifies it.
- **Testing:** the standard .NET test runner with one small deterministic test suite per stable boundary. The scaffold phase may select MSTest as the default to avoid an unnecessary test-framework layer.
- **Packaging:** self-contained `win-x64` publish first. Evaluate MSIX when install/update/file-association requirements stabilize; do not make installer machinery a prerequisite for the first usable build.

Use a light MVVM boundary for shell state and commands, implemented with plain .NET types initially. Do not add a broad MVVM, dependency-injection, mediator, docking, or plugin framework until a concrete requirement appears.

## Why this stack

### Windows fit

WPF directly supplies the desktop concepts Cutwork needs: an application window, menus, keyboard commands, panels, dialogs, data binding, focus/accessibility behavior, and mature Windows deployment support. Cutwork does not currently benefit enough from cross-platform abstraction to pay for one.

### Responsive CPU-first canvas

`WriteableBitmap` exposes a mutable back buffer and dirty rectangles. Cutwork can therefore update only the document region changed by a brush stroke while WPF handles presentation and viewport scaling. The cursor and other guides remain separate overlays, so pointer feedback does not wait for image recomposition.

This addresses the observed prototype bottleneck directly: local pixel mutation, layer compositing, viewport transform, and UI presentation become separate measurable stages. It does not promise that every WPF path is fast; it creates a narrow path that can be profiled and replaced without changing the document or tools.

### Migration from the experiment

C# maps the current deterministic structures cleanly: arrays become typed pixel/mask buffers, dataclasses become explicit document records/classes, and Python UI callbacks become testable tool state machines. OpenCvSharp reduces the first-port cost for operations already expressed through OpenCV while leaving application state and interaction logic independent from native OpenCV objects.

### Maintainability and packaging

One primary language covers the shell, tools, document, algorithms, persistence, and tests. A self-contained Windows publish is conventional and does not require a browser runtime, a Rust/JavaScript bridge, or a C++ ABI owned by the project. .NET 10 is an LTS release, so v0.1 starts on a supported long-lived baseline rather than a short-term runtime.

## Performance expectations and gates

The choice is conditional on measured acceptance, not faith in a framework.

On the agreed representative Windows machine and a representative 4K-class illustration:

- cursor overlay should track input within one display frame;
- a normal Clone or Mask stroke should present continuously at **at least 30 fps**, with 60 fps preferred;
- local stroke processing should target **p95 <= 8 ms per processed input batch**;
- event coalescing must not create gaps because document-space interpolation connects samples;
- a full-quality settle after pointer-up should target **<= 200 ms**; and
- profiling must separately report sampling/kernel, dirty composite, bitmap transfer, and presentation time.

These are v0.1 engineering targets, not claims about the current prototype. The representative fixture sizes and machine must be recorded with benchmark results.

Start with dense document buffers and dirty rectangles because they are simpler and likely sufficient for the target artwork. Keep pixel access behind buffer interfaces so sparse/tiled storage can be introduced only if measured memory or very-large-image behavior requires it.

## Alternatives considered

| Candidate | Strengths | Costs/risks for Cutwork | Decision |
|---|---|---|---|
| TypeScript + Electron + Canvas/WebGL/WebGPU | Familiar web UI, strong canvas ecosystem, easy cross-platform shell | Bundles Chromium/Node, multi-process/runtime weight, native algorithm and pixel-transfer boundaries, cross-platform value is currently unused | Rejected for v0.1 |
| TypeScript + Tauri + Canvas/WebGL/WebGPU | Smaller shell than Electron, Rust escape hatch, web UI productivity | Two-language bridge from the start, WebView/runtime variance, pixel data crossing and packaging/debug complexity | Secondary only if a web UI becomes a product requirement |
| C# + WinUI 3 + Win2D | Modern Windows UI and a direct GPU-oriented rendering path | More packaging/runtime ceremony and a larger first-step surface than the product currently needs | Preferred renderer/shell escalation candidate if WPF measurements fail |
| C# + Avalonia/Skia | Cross-platform, retained UI plus Skia drawing | Cross-platform is a non-goal and adds an abstraction layer; Windows-native behavior/packaging is less direct | Rejected for v0.1 |
| Rust + native UI/windowing + wgpu | Excellent control, safety, and a strong GPU path | High UI/tooling implementation cost; basic Windows-editor behavior becomes project-owned; Python algorithm migration is slower | Rejected as primary stack |
| Rust core + C#/web frontend | Fast isolated kernels and future portability | FFI, memory ownership, build, debug, and packaging complexity before profiling proves a need | Reserved escalation for proven hot kernels only |
| C++ + Win32/Direct2D | Maximum rendering and Windows control | Highest implementation and maintenance cost; too much custom infrastructure for this focused editor | Rejected |
| Python + PySide/Qt + native acceleration | Highest code reuse and a stronger UI than Tkinter | Retains Python/native ownership and packaging complexity; does not directly remove the full-frame presentation risk; performance tuning remains split | Keep experiment/reference only |

## Rejected early commitments

The decision explicitly does **not** commit v0.1 to:

- a GPU renderer;
- SkiaSharp, Direct2D, Win2D, WebGL, WebGPU, or wgpu;
- a tiled image engine;
- PSD compatibility;
- a plugin architecture;
- cloud services or generative AI;
- cross-platform UI; or
- persisted Undo/Redo history.

Each can be reconsidered through a focused decision when a measured need appears.

## Escalation path

The rendering and algorithm contracts must keep three escape hatches open:

1. Replace `WriteableBitmap` presentation with Win2D/Direct2D while preserving document, tool, history, and persistence layers.
2. Move one proven hot kernel to Rust or C++ behind a narrow buffer-based interface while keeping C# as the application language.
3. Introduce tiled/sparse surfaces behind the pixel-buffer abstraction when memory profiling proves dense buffers insufficient.

None of these paths should be implemented speculatively.

## Consequences

Positive:

- one primary production language;
- familiar Windows UI and shortcuts;
- direct dirty-region update support;
- simple separation between interactive overlays and document pixels;
- low migration risk for OpenCV-based algorithms;
- deterministic core tests can run without creating a WPF window; and
- realistic self-contained packaging.

Negative:

- Windows-only UI code is intentional;
- OpenCvSharp adds a native runtime dependency during migration;
- WPF's default image controls are not enough by themselves, so the canvas must follow the restricted back-buffer architecture;
- GPU acceleration, if later required, needs a renderer adapter implementation; and
- the Python tests specify behavior but cannot be reused as the production test runner.

## Migration cost

Migration is **moderate**, not a line-for-line rewrite:

- UI/application orchestration is rewritten;
- document and history are redesigned for production;
- deterministic algorithm contracts and test vectors are ported;
- simple image kernels are ported directly;
- OpenCV-based operations can initially retain equivalent native operations through OpenCvSharp; and
- the Python application remains available as a comparison oracle until the corresponding production behavior is accepted.

## References

- [.NET 10 is an LTS release](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)
- [`WriteableBitmap.AddDirtyRect` marks changed bitmap regions](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.writeablebitmap.adddirtyrect?view=windowsdesktop-10.0)
- [WPF globalization and localization overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-globalization-and-localization-overview)
- [MSIX packaging for desktop applications](https://learn.microsoft.com/en-us/windows/msix/desktop/vs-package-overview)
- [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model)
- [Tauri 2 documentation](https://v2.tauri.app/)
