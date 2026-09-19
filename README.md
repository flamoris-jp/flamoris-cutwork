# FLAMORIS Cutwork

FLAMORIS Cutwork is a standalone Windows-oriented image decomposition and repair tool for preparing illustration parts used by short 2D moving-picture animation workflows.

Cutwork is intentionally separate from `flamoris-jp/flamoris-2D`.

Its job is narrow:

- open a normal illustration/image;
- quickly isolate only the parts that need to move;
- repair or expose hidden regions where necessary;
- manage the resulting layers/parts; and
- export the prepared result for downstream animation work.

The practical product target is:

> **perceptual sufficiency × editing speed**

If a defect is invisible at normal playback scale, Cutwork should not force the author into needless cleanup.

## Current status

The primary implementation is now the **C# / .NET 10 / WPF production application** under `src/`.

The earlier Python/Tkinter implementation is preserved under [`experiments/python-tkinter/`](experiments/python-tkinter/) as an executable behavioral/reference experiment. Production code must not import or execute it.

Current milestone:

- Phase 0: Python prototype performance baseline complete;
- Phase 1: Windows foundation and first real-image canvas complete;
- Phase 2: Document / Layer Stack / Compositor / Undo-Redo complete;
- Phase 3: unified Part Tool and Guriguri complete;
- Phase 4: unified Mask Brush and Patch workflow complete;
- Phase 5: point-source Clone Repair complete;
- Phase 6: Blur and Smudge repair finishing complete;
- Phase 7: `.flimg` persistence and export complete;
- current: Phase 8, self-contained Windows packaging.

## Live MCP / AI

The running editor can grant a local client bounded artwork inspection and ordinary
Part/Mask/Clone/Patch editing through the same Undo/Redo. Access starts disabled.
See [connection and operation guide](docs/live-mcp.md) and
[ADR 0002](docs/decisions/0002-live-mcp.md). No filesystem tools are exposed.

## Design authority

- [`docs/decisions/0001-production-stack.md`](docs/decisions/0001-production-stack.md) — production stack decision and evaluated alternatives
- [`docs/production-architecture.md`](docs/production-architecture.md) — application, document, tools, rendering, persistence, i18n, and migration architecture
- [`docs/roadmap.md`](docs/roadmap.md) — phased implementation and hands-on acceptance roadmap
- [`docs/python-prototype-benchmark.md`](docs/python-prototype-benchmark.md) — Phase 0 performance baseline

## Production application

The production UI follows a conventional Windows image-editor layout:

```text
+---------------------------------------------------------+
| File  Edit  View  Layer  Export  Help                 |
+----+--------------------------------------+-------------+
|    |                                      | Layers      |
| T  |                                      |-------------|
| o  |              Canvas                  | ...         |
| o  |                                      |             |
| l  |                                      | Properties  |
| s  |                                      |-------------|
|    |                                      | contextual  |
+----+--------------------------------------+-------------+
```

Current Phase 1 behavior includes:

- PNG/JPEG import;
- immutable Original asset;
- `WriteableBitmap` presentation foundation;
- Fit and Actual Size;
- wheel zoom;
- middle-button pan;
- Space+drag pan;
- DPI-aware document/viewport coordinates;
- independent overlay presentation;
- Japanese-first `.resx` resources with English switching; and
- Original / Composite command boundary.

### Run on Windows

Requires Windows and the .NET 10 SDK.

```powershell
dotnet run --project src/Cutwork.App/Cutwork.App.csproj
```

Build and test:

```powershell
dotnet build Cutwork.sln --configuration Release
dotnet test Cutwork.sln --configuration Release --no-build
```

For normal use, download the `FLAMORIS-Cutwork-v0.1.0-win-x64` CI artifact,
extract its ZIP completely, and start `Cutwork.exe`. The portable build is
self-contained and does not require the .NET SDK/runtime or Python. See the
[Windows release guide](docs/phase8-windows-packaging.md).

### Phase 2 developer checkpoint

Phase 2 adds the authored layer stack, Layers/Properties panels, partial composition,
and transactional Undo/Redo. End-user creation tools begin in Phase 3.

```powershell
dotnet run --project src/Cutwork.App/Cutwork.App.csproj -- --developer
```

Open an image, then use **Help → Developer: Add Fixture Layers** (Japanese:
**ヘルプ → 開発用：確認レイヤーを追加**). This adds a central rectangular Part and
two colored underpaint layers in one transaction. Hide the Part to inspect the
hole and repairs; add the fixture twice to test foreground reordering. Rename,
toggle visibility, reorder within a band, delete, Undo (Ctrl+Z), and Redo (Ctrl+Y).
Base cannot move or be deleted. The unsaved marker reflects authored changes only;
viewport and tool-preview changes do not trigger it.

See [Phase 2 editing spine](docs/phase2-editing-spine.md) for pixel, dirty-region,
history, and memory-budget contracts.

Phase 3–6 hands-on behavior is documented in
[the Part Tool guide](docs/phase3-part-tool.md) and
[the Mask/Patch guide](docs/phase4-mask-patch.md),
[the Clone Repair guide](docs/phase5-clone-repair.md), and
[the repair finishing guide](docs/phase6-repair-finishing.md). Phase 7 project and
export contracts are defined by [the current `.flimg` v2 schema](docs/flimg-schema-v2.md)
and [the persistence/export guide](docs/phase7-persistence-export.md).

## Repository layout

```text
src/                         C# / WPF production code
tests/                       production deterministic tests
experiments/python-tkinter/  preserved Python/Tkinter experiments
docs/                        architecture, decisions, roadmap, benchmarks
```

The repository root is production-oriented. Experimental Python launchers, modules, dependencies, configuration, and tests live under `experiments/python-tkinter/`.

## Experimental Python/Tkinter reference

The preserved experiment contains the behavior discoveries that informed the production design, including:

- Guriguri selection behavior;
- Clone Repair semantics;
- immutable-original sampling;
- Hole Only behavior;
- Patch/Repair composition findings; and
- deterministic regression tests for image operations.

It does **not** define the production UI architecture.

See [`experiments/python-tkinter/README.md`](experiments/python-tkinter/README.md) for setup, launchers, tests, and benchmark commands.

The canonical late-stage prototype launcher remains:

```powershell
python experiments/python-tkinter/app_clone_guriguri.py
```

## Production direction

The planned production tool model remains intentionally small:

- Part Tool: rough polygon/fence + Guriguri refinement;
- Mask Brush: add/erase through one brush workflow;
- Patch Tool: source selection + placement/transform;
- Clone Tool: point-source repair using immutable Original;
- Blur / Smudge: local repair finishing;
- Hand/Pan: viewport-only navigation.

The useful conceptual layer stack discovered by the experiments is:

1. Part layers;
2. Base;
3. Patch / Repair layers.

The accepted production architecture, not the old Tkinter window structure, is authoritative for implementation.

## Repository workflow

`main` is the reviewed current baseline.

Use small purpose-driven branches and commits. For meaningful changes:

1. define the problem and acceptance criteria;
2. branch from `main`;
3. implement the smallest understandable change;
4. run the smallest relevant deterministic tests;
5. perform hands-on verification when the risk is visual/interactive;
6. open a PR;
7. review before merging to `main`.

Repository-wide AI/development rules are defined in [`AGENTS.md`](AGENTS.md).

## Relationship to FLAMORIS 2D

Cutwork and FLAMORIS 2D are separate applications and repositories:

```text
flamoris-jp/flamoris-cutwork  -> image decomposition / repair
flamoris-jp/flamoris-2D       -> 2D animation / rigging / clip authoring
```

Cutwork may export or hand off prepared assets to FLAMORIS 2D, but it must not become an embedded editor panel or depend on FLAMORIS 2D runtime internals.


## License, support, and philosophy

The software source code in this repository is licensed under the [Apache License 2.0](LICENSE), unless otherwise noted.

Commercial use is welcome and does not require permission. If you'd like, we'd be happy to hear what you used FLAMORIS for. This is completely optional.

FLAMORIS software is provided as-is. We do not provide guaranteed individual support. If you run into trouble, we encourage you to let your AI assistant read the repository, documentation, Issues, tests, logs, and source code and help you solve it.

If FLAMORIS helps you or you find it interesting, your support helps fund development and keeps the project growing. 🌱  
<sub>Mostly GPU bills.</sub>

FLAMORIS characters, character designs, artwork, illustrations, PSD source assets, music, audio, logos, trademarks, branding, video, and other creative assets are **not automatically licensed under Apache-2.0**. Their applicable licenses or rights statements must be provided separately.

### 方針

このリポジトリのソフトウェアソースコードは、特に明記がない限り [Apache License 2.0](LICENSE) で提供されます。

勝手に使ってください。改造しても、組み込んでも、商用利用してもOKです。

商用作品や製品で使う場合も、許可は不要です。もしよければ「こんなのに使ったよ」と教えてもらえるとうれしいです。もちろん強制ではありません。

FLAMORISのソフトウェアは現状のまま提供されます。個別サポートや動作保証はありません。困ったときは、README、ドキュメント、Issue、テスト、ログ、ソースコードをあなたのAIに読ませて、自己サポートしてもらってください。

もし、あなたのお役に立てたり、面白いと思っていただけたなら、開発費用をご支援いただけるとうれしいです。  
FLAMORISは元気になって育ちます。🌱  
<sub>主にGPU代とか。</sub>

FLAMORISのキャラクター、キャラクターデザイン、イラスト、PSD素材、音楽・音声、ロゴ、商標・ブランド、映像その他のクリエイティブ素材は、**Apache-2.0によって自動的にライセンスされるものではありません**。各素材に適用されるライセンスや権利表示を別途確認してください。
