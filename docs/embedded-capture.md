# Embedded window observation / 画面共有

Implemented candidate, 2026-10-24. Real Windows acceptance is pending.

The editor embeds `Flamoris.Capture.Wpf` over Capture Core/Windows/Mcp.
The editor remains authoritative for its document, commands and Undo/Redo.
Capture only observes this editor's owner window; no input recording or remote
window selection is installed. The normal editor MCP connection is reused.

## Use

1. Start the editor and open artwork/a project.
2. Enable **画面共有** in the bottom bar and acknowledge the safety notice.
3. Connect/reconnect using the existing **MCP / AI** menu (read-only or edit).
4. Request `capture.screenshot.get` with `{"input":{}}` through that editor's
   connection. `capture.session.get` reports consent and bounded image limits.
   In Hub these names are prefixed with the editor namespace.
5. Turn sharing OFF to revoke pending/new images. Changing sharing invalidates
   the MCP connection, so reconnect to keep using ordinary editor tools.

The checkbox displays **画面共有中** in yellow while enabled and always starts OFF.
Right-click it to restore the safety notice. Notice suppression is per application;
sharing consent is never saved. Lock/disconnect/suspend/window loss/exit stops
sharing. Unlock/restart never resumes it. Minimized/hidden windows fail capture;
there is no desktop fallback. Separate modal HWNDs are outside the image.
Screenshots use Capture's default bounded JPEG (max edge 1280, <=1 MiB); callers
may request ROI, PNG, or max edge up to 1920. No images are written to disk.

## Build ownership

`dependencies/flamoris-capture` is a pinned public git submodule, temporarily used
until Capture NuGet distribution exists. Its Wpf/Core/Windows/Mcp projects are the
only Capture production build inputs; no standalone EXE, tests or reference assets
are runtime dependencies. Initialize once before direct `dotnet build`:

```powershell
git submodule update --init --recursive
```

The normal publish script does this automatically, and Windows CI checks it out
recursively. MCP Core/Wpf and the packaged bridge use 1.3.1 for single-image MCP
results. Windows build 19041 is the API floor; WGC/D3D11 support is required.

## Acceptance

- Build/portable package must include Capture DLLs and MCP Core 1.3.1.
- Existing document/edit/Undo tests must still pass.
- Windows: request screenshots with sharing OFF/ON in read-only and edit modes;
  verify canvas, selections and panels reflect the current window.
- Change sharing during a pending shot; no old pixels may be disclosed.
- Lock/unlock, suspend/resume, minimize, replace a document and close/reopen;
  verify revoked grants fail and sharing never resumes after OS lock or restart.
- Existing normal MCP edits and ordinary manual editing remain usable.

Cross-platform builds cannot certify real WGC/GPU/DPI behavior.

## Existing Hub catalog

Append the two tool entries from
`dependencies/flamoris-capture/config/hub-embedded-capture.tools.json` to the
existing editor catalog's `tools` list. Preserve all existing tools, transport,
product identity, namespace, routes and credentials. The fragment is an array,
not a replacement catalog. Do not create a new Capture upstream for this mode.
Reload the Hub catalog using the deployment's normal procedure, then refresh the
external client's tools. This source change does not modify a running Hub.
