# FLAMORIS Cutwork

FLAMORIS Cutwork is a standalone Windows-oriented image decomposition and repair tool for preparing illustration parts used by short 2D moving-picture animation workflows.

Cutwork is intentionally separate from `flamoris-jp/flamoris-2D`.

Its job is narrower:

- open a normal illustration/image
- quickly isolate only the parts that need to move
- repair or expose hidden regions where necessary
- manage the resulting layers/parts
- export the prepared result for downstream animation work

The product goal is not pixel-perfect semantic segmentation. The practical target is:

> **perceptual sufficiency × editing speed**

If a defect is invisible at normal playback scale, Cutwork should not force the author into needless cleanup.

## Current status

The repository currently contains the preserved Python/Tkinter experimental implementation that established the core interaction ideas and classical image-processing behavior.

That code is now treated as an **executable reference / specification**, not as the final production UI architecture.

Current active work is tracked in GitHub Issues:

- `#1` hands-on Guriguri / Clone Repair findings
- `#2` Clone Paint performance profiling and responsiveness
- `#3` Cutwork v0.1 standalone UI / architecture / i18n design

## Production direction

Cutwork should feel like a small conventional Windows graphics application rather than a collection of prototype controls.

Planned layout:

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

- left: compact vertical tool icons
- center: image/canvas viewport
- right: Layers + contextual Properties
- top: conventional application menus
- Japanese-first UI through i18n resources

The production toolbar should consolidate prototype modes into a smaller set of understandable tools.

### Part Tool

`Part Polygon` and `Polygon Guriguri` converge into one Part Tool.

Expected interaction:

1. draw a rough polygon/fence around the intended part
2. Guriguri refinement is the normal behavior
3. exact/manual correction remains available as fallback

### Mask Brush

`Mask Add` and `Mask Erase` converge into one brush.

Preferred direction:

- one circular brush cursor
- temporary add/erase polarity switch with `Alt`

### Patch Tool

`Patch Source` and `Move Layer` are treated as phases of one Patch workflow rather than unrelated tools.

### Clone Tool

Preferred interaction direction:

- circular brush cursor visible at all times
- `Alt+click` selects clone source center
- releasing `Alt` returns immediately to painting
- wheel changes Clone brush diameter
- `Ctrl+wheel` preserves viewport zoom

The older rectangular Clone Source fence remains experimental reference behavior only unless later testing proves it useful.

## Experimental reference implementation

The current Python implementation includes several comparison entry points:

- `app.py`
- `app_polygon_guriguri.py`
- `app_boundary_guriguri.py`
- `app_guriguri.py`
- `app_clone_guriguri.py`

For current Guriguri + Clone Repair hands-on testing, the canonical experimental launcher is:

```powershell
python app_clone_guriguri.py
```

The other entry points are retained for comparison/reference and should not define the final application structure.

## Experimental algorithms and findings

### Polygon Guriguri

A loose polygon acts as a hard search fence. The algorithm progressively removes pixels from the inside boundary toward likely visual boundaries using deterministic cost-based search.

The useful interaction discovery is that the author can draw a rough 4–10 point fence, then refine it rather than manually placing dozens of exact polygon points.

### Clone Repair

Clone Repair copies pixels from the immutable original image into a repair layer.

The current experimental implementation established:

- source/destination relative offset semantics
- optional Hole Only restriction
- whole-stroke undo
- local ROI clone computation
- repair layer composition below Base

Interactive performance is still under active investigation. The current Python/Tk/PIL display path should be profiled before choosing a production rendering stack.

### Patch Repair

Manual Patch Fill remains a useful non-generative fallback for hidden-region repair:

- sample plausible source texture from the immutable original
- translate / scale / rotate it behind a cutout hole
- optionally apply light Blur / Smudge cleanup

This proved more controllable than classical inpainting for eyelid/skin repair in the experiments.

## Layer model discovered by the experiments

The useful conceptual stack is:

1. Part layers
2. Base
3. Patch / Repair layers

Drawing order is effectively:

```text
Patch / Repair -> Base -> Part
```

This lets hidden-area repair appear through holes in Base while the extracted moving part remains above it.

The production document model may evolve, but should preserve the user-visible semantics unless an accepted design explicitly replaces them.

## Run the current experiment on Windows

```powershell
cd C:\FLAMORIS\flamoris-cutwork
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python app_clone_guriguri.py
```

## Lightweight checks

```powershell
python -m py_compile app_clone_guriguri.py clone_brush.py
python -m unittest -v test_clone_brush.py
```

For broader experimental regression coverage:

```powershell
python -m unittest -v \
  test_image_ops.py \
  test_guriguri.py \
  test_boundary_guriguri.py \
  test_polygon_guriguri.py \
  test_clone_brush.py
```

On PowerShell, run the files on one line or invoke the tests individually if preferred.

## Current experimental limitations

- Python/Tkinter/PIL redraw is not yet responsive enough for production Clone Paint.
- multiple prototype launchers exist
- polygon/mask edges are binary rather than full alpha matting
- Smudge is deliberately simple
- Patch transform is affine only
- project persistence is not yet a production document format
- undo/redo is local and prototype-scoped
- no production Windows packaging yet
- no production i18n layer yet
- no generative AI requirement

These are prototype limitations, not promises about the final product architecture.

## Repository workflow

`main` is the reviewed current baseline.

Use small purpose-driven branches and commits. For meaningful changes:

1. define the problem / acceptance criteria
2. branch from `main`
3. implement the smallest understandable change
4. run the smallest relevant deterministic tests
5. open a PR
6. review before squash-merging to `main`

Repository-wide AI/development rules are defined in [`AGENTS.md`](AGENTS.md).

## Relationship to FLAMORIS 2D

Cutwork and FLAMORIS 2D are separate applications and repositories:

```text
flamoris-jp/flamoris-cutwork  -> image decomposition / repair
flamoris-jp/flamoris-2D       -> 2D animation / rigging / clip authoring
```

Cutwork may export or hand off prepared assets to FLAMORIS 2D, but it must not become an embedded editor panel or depend on FLAMORIS 2D runtime internals.
