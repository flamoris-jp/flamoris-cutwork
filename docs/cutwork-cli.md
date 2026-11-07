# Cutwork Linux / LIME service

The CLI is the headless Linux production host. Windows users keep the existing
Cutwork GUI. LIME runs independently of Mango, with the same Core document,
Guriguri/Part tools, revision guards, Undo/Redo and flimg formats.

## Build and local commands

Requires .NET 10 and Linux x64 (LIME). No desktop, display server or GPU is needed.
PNG/JPEG use SkiaSharp 4.154.0; persistence uses canonical RGBA8/Gray8 PNG assets.
The Windows GUI retains its reviewed WPF codec. Both implementations share the
existing archive reader/writer and handoff exporter.

```bash
dotnet build src/Cutwork.Cli -c Release -p:TargetFrameworks=net10.0
dotnet src/Cutwork.Cli/bin/Release/net10.0/cutwork-cli.dll import spy.png --out spy.flimg
dotnet src/Cutwork.Cli/bin/Release/net10.0/cutwork-cli.dll part-add spy.flimg --polygon "100,100;800,100;800,1000;100,1000" --name Akino --out spy-parts.flimg
dotnet src/Cutwork.Cli/bin/Release/net10.0/cutwork-cli.dll composite spy-parts.flimg --out preview.png
dotnet src/Cutwork.Cli/bin/Release/net10.0/cutwork-cli.dll handoff spy-parts.flimg --out layers.zip
```

One-shot outputs must be new files. A polygon cutout is not automatic semantic
segmentation or a repaired background. The Base remains unchanged. Review masks
at intended playback size; a successful export is not visual acceptance.

## Permanent service

```bash
cutwork-cli serve --workspace /srv/flamoris/apps/flamoris-cutwork-cli/state --permission edit --hub "$HUB_ENDPOINT"
```

Set CUTWORK_LIME_CONNECTOR_TOKEN through the service environment, never through
arguments/source. The endpoint is the dedicated Hub reverse-WSS upstream URL.
The service starts without an image. It recovers the last active **saved** project
on restart. Unsaved edits are not automatically saved. No window or Mango is used.
Omitting --hub runs stdio for a local MCP client; permission defaults to readOnly.
SIGTERM/Ctrl+C revokes the grant, drains pending checkpoints and exits. Long Hub
outages trigger a fresh connection cycle without replaying a mutation.

Generate the separate Hub upstream descriptor from the actual service registry:

```bash
cutwork-cli mcp-catalog > cutwork-lime.json
```

Its id/namespace is cutwork-lime and token environment name is
CUTWORK_LIME_CONNECTOR_TOKEN. It must be installed as a separate upstream from
Windows cutwork. product_id remains flamoris.cutwork. It excludes desktop capture.
A started process is not proof of Hub registration: verify mcp.context through Hub.

## Remote production workflow

All tools except mcp.context use the usual input envelope; mutations require guard
with runtimeId/documentToken/expectedRevision from the latest mcp.context. An empty
service uses its runtime token, allowing guarded uploads without inventing artwork.
After document_import, query mcp.context again: the target token has changed.

| Tool | Behavior |
|---|---|
| workspace_status | Active document, dirty/saving state and pending upload |
| upload_begin | UUID uploadId, fileName, decimal sizeBytes, whole-file sha256 |
| upload_write | Sequential chunks up to 128 KiB; offset and chunk sha256; identical retry accepted |
| upload_cancel | Discard the unfinished upload |
| document_import | Verify upload, open as a new flimg working project; explicit discardUnsaved controls replacement |
| context / layers / image | Current artwork and bounded pixel previews |
| part_candidates / part_polygon_create / edit | Existing Cutwork Part and repair operations |
| undo / redo | Existing shared document history |
| project_context / project_save / project_save_status | Revisioned checkpoints and working-copy save |
| artifact_export | Immutable project, composite or handoff export, UUID requestId |
| artifact_read | Bounded chunks; verify chunk and complete artifact SHA-256 |

File paths and shell execution are never accepted through MCP. PNG/JPEG/flimg
uploads are bounded to 64 MiB, one unfinished upload, 30-minute expiry. A workspace
is private local storage, locked to one host, bounded to 8 GiB and 10,000 files.
Operators manage retention. Import rejects replacing dirty artwork unless
explicitly requested. Each import gets a new working project; previous files stay.

Call project_context for the exact save target hash and parent; supply a fresh
requestId/label to project_save and poll project_save_status. Confirm
receipt.workingCopySaved, not only checkpointSaved. Use the same receipt lookup
after an ambiguous disconnect. Never automatically replay an edit. Exporting a
project creates a download copy and does not mark the working project saved.

For an existing image/file at process startup, mcp INPUT [--permission edit]
[--save-to NEW.flimg] remains available. This mode has the ordinary editing tools;
use serve for transfer, document switching and permanent service operation.

## Install on LIME

The Linux CI artifact is a self-contained linux-x64 tarball including executable,
Hub descriptor, systemd template and Skia license/notices. Stage its contents at
/srv/flamoris/apps/flamoris-cutwork-cli/bin after review. Create the service account
flamoris-cutwork and an owned private state directory alongside bin. Install the
unit from deploy/linux/flamoris-cutwork-cli.service under /etc/systemd/system.
Create a root-owned mode-0600 /etc/flamoris/cutwork-lime.env with HUB_ENDPOINT and
CUTWORK_LIME_CONNECTOR_TOKEN. Then enable/start flamoris-cutwork-cli.service.
Use a local filesystem for state, not a symlink or NAS mount.

The descriptor/token on DECO PON must be provisioned separately. This repository
change does not install the service or modify the live Hub. Server Manager remains
the authority for live service status; stdout is reserved for MCP and diagnostics
use stderr/journal. No additional network listener is opened on LIME.

## Acceptance

```bash
dotnet test tests/Cutwork.Tests -c Release -p:TargetFrameworks=net10.0
dotnet run --project tests/Cutwork.CliSmoke -c Release -p:TargetFrameworks=net10.0 -- src/Cutwork.Cli/bin/Release/net10.0/cutwork-cli.dll
```

Linux CI checks an actual CLI process through the official MCP client: read-only
discovery/direct denial, image, Part, stale guards, Undo/Redo, checkpoint save,
cold service/upload/import/export/download, saved restart, EOF and descriptor.
Portable regressions cover alpha, mask, archive round trips and existing editing
behavior. Windows-only sharing/ACL tests remain Windows-specific. Live Hub/LIME
installation and real-artwork perceptual acceptance are distinct from CI.
