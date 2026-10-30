# Embedded window observation / 画面共有

Application-lifetime MCP and independent sharing are implemented. See
[startup policy](mcp-startup.md). Historical catalog/image observations and the
remaining physical platform checks are separate from source/CI acceptance.

The editor embeds `Flamoris.Capture.Wpf` over Capture Core/Windows/Mcp.
The editor remains authoritative for its document, commands and Undo/Redo.
Capture only observes this editor's owner window; no input recording or remote
window selection is installed. The normal editor MCP connection is reused.

## Use

1. Start the editor. MCP can connect before artwork/a project is opened. New
   preferences auto-connect locally read-only; saved OFF remains OFF.
2. Use **MCP / AI** for connection/settings (read-only or edit). Connection only
   enables the independent **画面共有 OFF** checkbox in the bottom bar.
3. Enable that checkbox and confirm the notice. Only then may
   `capture.screenshot.get` with `{"input":{}}` return this owner window.
   `capture.session.get` reports safe status even with sharing OFF.
   In Hub the existing editor namespace prefixes these tool names.
4. Turn sharing OFF to cancel new/pending image disclosure without disconnecting
   MCP. Disconnect/failure stops that attachment's sharing; a stale connection
   cannot stop a newer one.

The checkbox displays **画面共有 ON** in yellow only while sharing. Screen consent
is not persisted or restored by connection/reconnection. Lock, suspend, window
loss and document replacement stop sharing while preserving the application MCP
connection when its runtime remains available. Explicit MCP Stop and app shutdown
revoke the connection. Minimized/hidden windows fail capture; there is no desktop
fallback. Separate modal HWNDs are outside the image.
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
recursively. MCP Core/Wpf and the packaged bridge use 1.4.0 for single-image MCP
results. Windows build 19041 is the API floor; WGC/D3D11 support is required.

## Acceptance

- Build/portable package must include Capture DLLs and MCP Core 1.4.0.
- Existing document/edit/Undo tests must still pass.
- Windows: connect with no document and sharing OFF; status works, pixels are denied.
- Explicitly enable sharing and verify canvas, selections and panels in both modes.
- Turn sharing OFF during a pending shot; no old pixels may be disclosed and the
  MCP connection remains usable.
- Lock/unlock, suspend/resume, minimize and replace/reopen a document; sharing
  stops and requires fresh consent. Reject old target guards without reconnecting.
- Explicit MCP Stop/shutdown revokes the connection and stops sharing.
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

