# Windows portable packaging

## Build and distribute

From the repository root in PowerShell 7:

```powershell
git pull --ff-only
./product/packaging/publish-windows.ps1
```

The ordinary distribution candidate is the portable ZIP. Extract the whole ZIP
into a new directory and start `Cutwork.exe`; keep its runtime files and `mcp/` together.
No .NET SDK/runtime is required on the destination PC.

Outputs:

- `artifacts/windows/FLAMORIS-Cutwork-win-x64/` — verified portable directory
- `artifacts/windows/FLAMORIS-Cutwork-win-x64.zip` — the same files at ZIP root
- `artifacts/windows/FLAMORIS-Cutwork-win-x64.inventory.json` — JSON array of `path`, `size`, `sha256`

`-OutputDirectory <path>` changes the parent output directory; relative paths are
resolved from the repository root, absolute paths are supported. Scripts locate
inputs from `$PSScriptRoot`, so invocation does not depend on the current directory.
A repeat run replaces only this product's directory, ZIP and inventory. Package
names have no version suffix across the three products; `BUILD-INFO.txt` records
source commit and platform, and the executable carries its product version.

## Integrity and package boundary

`SHA256SUMS.txt` contains lowercase SHA-256, two spaces, and a package-relative
forward-slash path for each file except itself. The external inventory also hashes
`SHA256SUMS.txt`. ZIP names, sizes and actual decompressed contents must match the
inventory; missing, unexpected, duplicate or modified entries fail assembly.
Checksums establish file consistency, not publisher identity or code signing.

Self-contained publish inputs remain product-specific. Source, tests, intermediate
build directories, PDBs, repository metadata and retired Node/Electron/Product Host
content are rejected. Kachinco permits exactly the root `recipe-worker.py` runtime
file. 2D retains FFmpeg binaries, shared libraries and distribution notices/docs,
while excluding development headers/import libraries. No new shared runtime is
introduced; `portable-package.ps1` is repository-local build tooling only.

The final output is validated before CI executes the packaged editor/bridge and
uploads the ZIP plus inventory. All three workflows retain candidates for 3 days;
they do not publish GitHub Releases or merge PRs automatically.

## Family comparison

All package commands are `./product/packaging/publish-windows.ps1`; all directories
are `artifacts/windows/<Name>/`, ZIPs `<Name>.zip`, inventories `<Name>.inventory.json`.
All bridges live at `mcp/Flamoris.Mcp.Bridge.exe`.

| Item | Cutwork | 2D | Kachinco |
|---|---|---|---|
| Name | FLAMORIS-Cutwork-win-x64 | FLAMORIS-2D-win-x64 | FLAMORIS-Kachinco-win-x64 |
| Main executable | Cutwork.exe | Flamoris2D.exe | Kachinco.App.exe |
| Bundled runtime | Self-contained .NET/WPF, app resources, MCP | Self-contained .NET/WPF, C++ core/renderer/source codecs, ICU, pinned LGPL shared FFmpeg, MCP | Self-contained .NET/WPF, C++ runtime, restricted Recipe worker, MCP |
| Build prerequisites | PowerShell 7, .NET 10 SDK, Git | PowerShell 7, .NET 10 SDK, Git, CMake, Visual Studio C++ Build Tools, network for pinned ICU/FFmpeg | PowerShell 7, .NET 10 SDK, Git, CMake, Visual Studio C++ Build Tools |
| User prerequisites | Windows x64 | Windows x64 | Windows x64; FFmpeg/ffprobe on PATH for media, Python 3 for Recipes |
| Integrity/provenance | SHA256SUMS.txt, inventory, BUILD-INFO.txt | SHA256SUMS.txt, inventory, BUILD-INFO.txt | SHA256SUMS.txt, inventory, BUILD-INFO.txt with native ABI/capabilities/timebase |
| Package Action | Windows Portable Package (`windows-package.yml`) | Windows Portable Package (`windows-package.yml`) | Windows Portable Package (`windows-package.yml`) |
| CI execution | Same package workflow on PR/manual | Same package workflow on PR/manual | Product (`foundation.yml`) on non-main push; package Action delegates to it |
| Packaged automated smoke | Official MCP client, UI projection/edit/history, save/reopen, lifecycle, System32-only PATH | Native desktop/MCP client, packaged WPF production smoke, PATH without developer runtimes | Official MCP client, UI edit/history/lifecycle, System32-only PATH; five real media formats with declared FFmpeg dependency |
| Human acceptance remaining | Clean Windows, actual artwork/brush latency, DPI/navigation | Clean Windows, actual PSD/flimg, DPI/rig/preview/export | Clean Windows, pointer/DPI, A/V synchronization, Recipe generation/export |

## Local packaging contracts

```powershell
./tests/packaging/portable-package.Tests.ps1
```

This small synthetic test runs without WPF or downloads. It checks Unicode file
paths, complete inventory/checksum coverage, repeat assembly, allowed runtime source,
forbidden content, and corrupt/missing/extra ZIP entries. It does not claim Windows
UI, DPI, rendering or physical playback acceptance.
