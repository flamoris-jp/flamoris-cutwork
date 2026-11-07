# Cutwork CLI v0 (Windows)

The first headless Cutwork CLI uses the **production** document, Part tool, Guriguri fitter,
.flimg codec and export services. It does not invoke the GUI, drive mouse clicks,
or use the experimental Python editor.

## Build and run

Requires Windows and the .NET 10 SDK. Run from the repository root:

```powershell
dotnet build src/Cutwork.Cli/Cutwork.Cli.csproj -c Release
dotnet run --project src/Cutwork.Cli -c Release -- import spy.png --out spy.flimg
dotnet run --project src/Cutwork.Cli -c Release -- inspect spy.flimg
dotnet run --project src/Cutwork.Cli -c Release -- part-add spy.flimg --polygon "100,100;800,100;800,1000;100,1000" --name Akino --semantic character --out spy-parts.flimg
dotnet run --project src/Cutwork.Cli -c Release -- handoff spy-parts.flimg --out spy-handoff.zip
dotnet run --project src/Cutwork.Cli -c Release -- composite spy-parts.flimg --out spy-preview.png
```

Commands emit JSON on stdout and errors on stderr. A failure returns a nonzero exit code.
All coordinates are **image-domain pixels**, not screen/view coordinates.
`part-add` can optionally take `--step 2` (0..20) to use the same Guriguri
fitting adjustment as the GUI. Each `part-add` writes a **new project**;
chain the newly created output into the next command to build more Parts.

No command overwrites an existing output. `inspect` is read-only. The CLI
writes and loads through the canonical `.flimg` store, and uses the existing
handoff v2 exporter.

## Meaning of the output

A manually supplied polygon is **not automatic character segmentation**.
Guriguri can refine it, but author review remains necessary for hair strands,
transparent edges and overlapping characters. A Part is a cutout of source
pixels with alpha; the original Base is unchanged. Creating a Part does **not**
fill the background behind it. `handoff` emits the existing Cutwork layer
ZIP, not a fully rigged FLAMORIS 2D animation.

For the spy MV, start with one character/upper-body Part and inspect the output
visually. Add eyes, mouth, hair, arms and hidden-region repair only after the
basic Cutwork -> FLAMORIS 2D handoff is accepted.

## Platform boundary

The current `Cutwork.Imaging` implementation compiles its .flimg persistence,
PNG encoder/decoder and image import only on the Windows/WPF target.
Thus **this v0 is Windows-only**, despite the UI-free invocation.
LIME/Linux support requires a reviewed portable image-codec boundary and
cross-platform flimg round-trip tests, tracked in Issue #68. Do not silently
substitute a new image format or an independent Python editor.

## Smoke test

After building the solution on Windows, run:

```powershell
./tests/cli/cli-smoke.ps1
```

The smoke uses generated synthetic artwork and checks:
import -> Part creation -> inspect -> composite PNG -> handoff ZIP. It does
not replace perceptual/real-artwork acceptance of masking quality.

## Headless MCP mode (Windows)

`mcp` keeps one production document/session alive, with no editor window. It
uses the existing MCP Core transport and Cutwork typed tools, not shell access.
Default permission is read-only. Launch with a PNG/JPEG supported by the importer
or an existing `.flimg`:

```powershell
dotnet run --project src/Cutwork.Cli -c Release --no-build -- mcp spy.png
dotnet run --project src/Cutwork.Cli -c Release --no-build -- mcp spy.png --permission edit --save-to spy-working.flimg
```

For a stdio MCP client, use the built executable directly; build output must not
enter MCP stdout. Example (replace paths for your workstation):

```json
{
  "mcpServers": {
    "cutwork-cli": {
      "command": "dotnet",
      "args": ["C:/Cutwork/src/Cutwork.Cli/bin/Release/net10.0-windows10.0.19041.0/cutwork-cli.dll", "mcp", "C:/Artwork/spy.png", "--permission", "edit", "--save-to", "C:/Artwork/spy-working.flimg"]
    }
  }
}
```

The new working path must not exist. Restarting should use the previous working
file as INPUT and a **new** --save-to path. Without --save-to, MCP edits stay in
memory and saving is not authorized. --save-to requires edit permission and
authorizes only the new copy, using the existing checkpoint/receipt machinery.
There is no autosave when the process exits. The source file is never rewritten.

Call `mcp.context` first; use its runtimeId, documentToken and revision as the
guard for mutations. Tools include context/layers/image, Guriguri candidates,
exact polygon preview/create, typed edits, repairs, work log and Undo/Redo.
For saving, query `project_context`, call `project_save` with its current target
hash/parent and a new requestId, then poll `project_save_status`. Confirm
`receipt.workingCopySaved`, not just checkpoint creation. After completed save,
the ordinary CLI `composite` and `handoff` commands export the working file.

Stdio EOF or Ctrl+C revokes MCP and stops the host. It waits for pending save
completion; disconnect does not imply a save failed. Check the durable receipts
after an ambiguous result; never automatically replay an edit.

### ChatGPT through the Hub

Stdio alone does not connect a cloud ChatGPT session. For Work/Chat use a
dedicated `cutwork-cli` Hub upstream, separate from desktop `cutwork`:

```powershell
# Export the descriptor generated from the actual registry; contains no secret.
dotnet run --project src/Cutwork.Cli -c Release --no-build -- mcp-catalog > cutwork-cli.json
# Set CUTWORK_CLI_CONNECTOR_TOKEN in the process environment through your normal
# secret provisioning. Do not put its value in source, command arguments or logs.
dotnet run --project src/Cutwork.Cli -c Release --no-build -- mcp spy.png --permission edit --save-to spy-working.flimg --hub wss://YOUR-HUB/UPSTREAM-PATH
```

The descriptor's `id` and namespace are `cutwork-cli`; product_id intentionally
remains `flamoris.cutwork`. Install it using the Hub's normal upstream procedure,
provision its dedicated token and supply that upstream's WSS URL. The generated
catalog excludes desktop screenshot/session capture. Hub registration is
asynchronous: provider startup is not evidence of a connected client. Confirm
through the Hub and `mcp.context`. Ctrl+C stops Hub mode. No reconnect replays
mutations. This change does not deploy or reconfigure the live Hub.

### MCP acceptance

```powershell
dotnet run --project tests/Cutwork.CliSmoke -c Release -- src/Cutwork.Cli/bin/Release/net10.0-windows10.0.19041.0/cutwork-cli.dll
```

The official MCP client exercises the actual CLI process: read-only discovery
and denial, original image, polygon Part, stale guards, Undo/Redo, checkpoint save
and reload, source preservation, target overwrite denial, EOF and catalog output.
Hub deployment, real-artwork visual quality and Linux portability remain separate.

