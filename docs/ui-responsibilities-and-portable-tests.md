# UI responsibilities and portable tests

Issue #42 reviews the current WPF boundary rather than moving code solely to
reduce file size. The document/session, history, and tool controllers remain the
editing authority. This audit introduces no new ViewModel or editing model.

## Responsibility audit

| Area | Owner | Decision |
| --- | --- | --- |
| Pointer/key routing, capture, focus, DPI and dispatcher scheduling | `DocumentCanvas.xaml.cs`, delegating domain input to `CanvasInputRouter` | Keep WPF event glue in the view; portable routing tests cover tool semantics. |
| Part/Mask/Clone/Patch/Blur/Smudge interaction and history | Core tool controllers and Imaging kernels | Already outside WPF; reuse these owners. |
| Composite/Original presentation, bitmap lifetime, dirty mask ROI, viewport transforms and overlays | `DocumentCanvas.xaml.cs`, `WriteableBitmapSurface`, `CompositeCache` | Keep WPF bitmap/shape/cache lifetime in the view; composition remains in Imaging. |
| Mask tint bytes and straight-alpha display premultiplication | `DisplayPixels` in Imaging | Extract these WPF-independent loops. Keep tint choices and bitmap creation in WPF. Preserve existing integer rounding and source immutability. |
| Connection settings/status and grant lifecycle | `McpDesktopUi`, `McpBoundary`, `CutworkMcpHost` | Already delegated; do not create another connection manager. |
| Dispatcher admission, human-busy checks, file coordination and disabling WPF controls during remote edits | `MainWindow.Mcp.cs` | Keep application/UI coordination beside the controls and shared session. A generic service would still need these WPF/controller references and would not improve ownership. |
| Wire tools, bounded preparation, revision checks and shared editing operations | `Cutwork.Mcp` and Core/Imaging | Preserve the existing boundary and ordinary Undo/Redo. |

`DocumentCanvas.xaml.cs` is the largest view file. Most of its remaining code
creates, positions, or updates WPF visuals; splitting it mechanically would
scatter bitmap/cache lifecycle without making domain behavior more portable.
The concrete extraction above removes duplicate pixel loops from the canvas and
bitmap presenter and makes their behavior testable without WPF.

`MainWindow.Mcp.cs` is already a small application adapter after the shared MCP
UI migration. It contains no protocol parser, image kernel, document authority,
or separate history implementation. Keep it as the place where the running
window coordinates the existing MCP/session owners.

## Portable regression boundary

Core targets `net10.0`. Imaging already has a `net10.0` target containing
composition and kernels; its Windows target adds WPF image decoding,
persistence, and export. Portable tests reuse the same source files as Windows
tests, with an explicit compile allowlist in `Cutwork.Tests.csproj`.

The portable lane covers document/layer identity and restore, history,
coordinate/input routing, Part fitting, Mask/Clone/Patch/repair kernels,
compositing, synthetic fixture behavior, and display-byte conversion.
New test files remain Windows-only until explicitly reviewed for the portable
allowlist. This prevents WPF/App/Bridge dependencies from entering Linux tests.

The full Windows test target and published editor/bridge smoke remain the
authority for WPF, image import, `.flimg` persistence, export, MCP transport and
packaging. Linux results do not establish visual, DPI, focus or painting-latency
acceptance.

Run only the portable target (the global property also limits Imaging restore
to its portable target):

```sh
dotnet test tests/Cutwork.Tests/Cutwork.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

On Windows, the normal full build compiles both targets. Run the Windows suite
once, without repeating portable tests already exercised by Linux CI:

```powershell
dotnet build Cutwork.sln -c Release
dotnet test Cutwork.sln -c Release --no-build --framework net10.0-windows
```

Linux CI has no packaging or artifact upload. Windows keeps the existing
portable-package verification, external official MCP-client proof, and
three-day artifact retention.
