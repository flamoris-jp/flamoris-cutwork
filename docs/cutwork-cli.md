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
