# Classical Cutout Spike

Issue: #53

A disposable standalone experiment for testing whether a fully local, non-ML, human-in-the-loop cutout workflow is good enough before Phase 5 is designed.

## Intended use

This spike is for **human figure / body-part cutout for short moving-picture clips**. Typical targets are a whole person, face/head, eye area for blinking, arm, hand, leg, or chest/shoulder area for breathing motion.

It is deliberately not trying to separate every static item in an illustration. Backgrounds, chairs, guitars, props, and other objects that will not move do not need semantic isolation here.

## Quality target

The target is not pixel-perfect segmentation. Evaluate this experiment by:

> **perceptual sufficiency × editing speed**

For an approximately eight-second moving-picture clip, small boundary errors that do not show at normal playback, individual hair strands, and artifacts visible only at extreme zoom are acceptable. Spend brush cleanup time only where an artifact is visible in the intended clip.

## What it does

- opens ordinary PNG/JPEG/WebP/BMP images with no transparency requirement
- initializes GrabCut from a user-drawn **Box** or a rough **Polygon Lasso**
- lets the user paint definite foreground/background hints
- recomputes GrabCut from those hints
- provides small deterministic cleanup operations: fill holes, remove islands, expand, shrink, smooth
- exports either the binary mask or a transparent PNG cutout
- keeps the original decoded image immutable while only the mask changes
- can create an experimental **Base** layer (source minus mask) and **Cutout** layer (masked source) for blink-oriented close-up tests
- previews either layer independently over a checkerboard; optional **Fill Base Hole** uses OpenCV `INPAINT_TELEA` from the original source and current mask

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
2. Choose **Box** and drag a loose rectangle, or choose **Polygon Lasso** and click a loose polygon around the moving person/body part.
   - Press **Enter** or double-click to finish a Polygon Lasso.
   - Press **Esc** to cancel it.
3. Inspect the dimmed-background preview after automatic GrabCut.
4. Use **FG Brush** / **BG Brush** only where visible artifacts matter.
5. Press **Refine GrabCut**.
6. Optionally try Fill Holes / Remove Islands / Expand / Shrink / Smooth.
7. Press **Create Layers** to inspect Base / Cutout independently.
8. For a blink test, hide Cutout; optionally use **Fill Base Hole** to see whether the hole reads as plausible surrounding skin.
9. Export the cutout or mask for comparison.

The Polygon Lasso is not a precision tracing tool. Leave a little margin around the target; its exterior becomes definite background and its interior is a foreground candidate for GrabCut.

## Recommended test sequence

1. Full human figure with simple background.
2. Human figure against complex or similar-color background.
3. Face, eye, arm, hand, and leg regions.
4. Compare **Box** against **Polygon Lasso** for the same target.
5. Use FG/BG Brush only where visible artifacts matter.

## Build an experimental EXE

Packaging is optional for the first spike, but the current prototype can be bundled for local testing:

```powershell
pip install -r requirements-build.txt
pyinstaller --noconfirm --clean --windowed --name FlamorisCutoutSpike app.py
```

The executable will be under `dist/FlamorisCutoutSpike/` or `dist/` depending on the local PyInstaller version/configuration. This packaging path is experimental and not a production distribution decision.

## Important limitations

- GrabCut is not semantic object recognition. Similar-color or touching objects, such as a person, chair, and guitar, can still become one foreground region.
- Hair, fur, translucency, and anti-aliased edges are not true alpha matting in this spike.
- The cleanup buttons currently convert the active mask to probable foreground/background labels, so they are coarse refinement tools rather than a production matte model.
- No background generation, GPT Image, SAM-family model, Project persistence, or FLAMORIS Scene integration is included.

## Decision gate

After real artwork testing, record one recommendation in Issue #53:

- **A**: classical pipeline is sufficient
- **B**: classical refinement is useful but semantic initial selection is needed
- **C**: classical selection is not worth productizing
