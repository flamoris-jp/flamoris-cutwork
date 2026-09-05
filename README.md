# Classical Cutout Spike

Issue: #53

A disposable standalone experiment for testing whether a fully local, non-ML, human-in-the-loop cutout workflow is good enough before Phase 5 is designed.

## What it does

- opens ordinary PNG/JPEG/WebP/BMP images with no transparency requirement
- initializes object selection with OpenCV GrabCut from a user-drawn rectangle
- lets the user paint definite foreground/background hints
- recomputes GrabCut from those hints
- provides small deterministic cleanup operations: fill holes, remove islands, expand, shrink, smooth
- exports either the binary mask or a transparent PNG cutout
- keeps the original decoded image immutable while only the mask changes

This is deliberately not FLAMORIS production architecture and is not a general image editor.

## Run on Windows

```powershell
cd experiments/classical-cutout
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python app.py
```

## Basic experiment flow

1. Open an ordinary source image.
2. Choose **Box** and drag a rectangle that encloses the target object.
3. Inspect the dimmed-background preview.
4. If needed, choose **FG Brush** and paint areas that must remain foreground.
5. Choose **BG Brush** and paint areas that must be excluded.
6. Press **Refine GrabCut**.
7. Optionally try Fill Holes / Remove Islands / Expand / Shrink / Smooth.
8. Export the cutout or mask for comparison.

Test both people and non-person objects. The goal is not perfect one-shot segmentation. The decision question is whether correction effort is meaningfully lower than manual tracing.

## Build an experimental EXE

Packaging is optional for the first spike, but the current prototype can be bundled for local testing:

```powershell
pip install -r requirements-build.txt
pyinstaller --noconfirm --clean --windowed --name FlamorisCutoutSpike app.py
```

The executable will be under `dist/FlamorisCutoutSpike/` or `dist/` depending on the local PyInstaller version/configuration. This packaging path is experimental and not a production distribution decision.

## Important limitations

- GrabCut is not semantic object recognition. A one-click iPhone/Photoshop-like result is not expected from arbitrary images.
- Hair, fur, translucency, and anti-aliased edges are not true alpha matting in this spike.
- The cleanup buttons currently convert the active mask to probable foreground/background labels, so they are coarse refinement tools rather than a production matte model.
- No background generation, GPT Image, SAM-family model, Project persistence, or FLAMORIS Scene integration is included.

## Decision gate

After real artwork testing, record one recommendation in Issue #53:

- **A**: classical pipeline is sufficient
- **B**: classical refinement is useful but semantic initial selection is needed
- **C**: classical selection is not worth productizing
