# Embedded window observation / 画面共有

Implemented candidate, 2026-10-24. Real Windows acceptance is pending.

The editor embeds `Flamoris.Capture.Wpf` over Capture Core/Windows/Mcp.
The editor remains authoritative for its document, commands and Undo/Redo.
Capture only observes this editor's owner window; no input recording or remote
window selection is installed. The normal editor MCP connection is reused.

## Use

1. Start the editor and open artwork/a project.
2. Connect using the existing **MCP / AI** menu (read-only or edit). The connection
   also authorizes screenshots of this editor window; no additional checkbox or
   safety dialog is required.
3. Request `capture.screenshot.get` with `{"input":{}}` through that editor's
   connection. `capture.session.get` reports the session and bounded image limits.
   In Hub these names are prefixed with the editor namespace.
4. Disconnect MCP to stop sharing. Failed/cancelled connection attempts also stop
   their capture session. A stale connection cannot stop a newer connection.

The bottom bar displays **MCP画面共有中** in yellow during the MCP attachment.
There is no independent saved sharing preference. Lock/disconnect/suspend/window
loss/exit revokes the current capture session; subsequent connections follow the
existing MCP provider/reconnect settings. Minimized/hidden windows fail capture;
there is no desktop fallback. Separate modal HWNDs are outside the image.
Screenshots use Capture's default bounded JPEG (max edge 1280, <=1 MiB); callers
may request ROI, PNG, or max edge up to 1920. No images are written to disk.
The standalone Capture EXE retains its separate consent controls and notice.

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
- Windows: connect MCP and immediately request screenshots in read-only/edit modes;
  verify canvas, selections and panels reflect the current window.
- Disconnect during a pending shot; no old pixels may be disclosed.
- Lock/unlock, suspend/resume, minimize, replace a document and close/reopen;
  verify revoked grants fail and only a new valid MCP attachment permits images.
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
