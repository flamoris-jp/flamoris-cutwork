# Parts Guriguri experiment

This spike adds deliberately human-guided selection modes to the existing classical-cutout experiment.

## Goal

Do not ask AI or an automatic segmenter to decide semantic meaning.

The click only means: **include something around here**. The user decides what the part means. The software makes the connected selection larger or smaller with the mouse wheel.

The original source pixels are never regenerated.

## Run the original colour-continuity version

From `experiments/classical-cutout`:

```bash
python app_guriguri.py
```

This version uses floating-range Lab flood fill. It is kept as a comparison because it can drift through gradual colour ramps: neighbour A resembles B, B resembles C, and the selection may eventually reach pixels far from the original click colour.

## Run the Boundary Guriguri comparison

```bash
python app_boundary_guriguri.py
```

Boundary Guriguri changes the question from **how similar is the next colour?** to **how strong a visual boundary may this connected selection cross?**

### Interaction

1. Open an image.
2. Choose **Boundary Guriguri**.
3. Click anywhere inside the part you intend to select. The click has no semantic label such as white-of-eye, iris, skin, or hair.
4. Turn the mouse wheel up to allow the connected selection to cross stronger boundaries.
5. Turn the mouse wheel down to return inward through the same nested boundary levels.
6. Use `Ctrl+wheel` for normal viewport zoom.
7. Use **Create Part Layer** when the desired visual boundary is reached.
8. Click somewhere else to restart from another location.
9. Use Mask Add / Mask Erase or Part Polygon when exact manual correction is faster.
10. Press `Esc` to restore the previous pending selection.

The green cross shows the literal click. Boundary Guriguri may move the working seed only a few pixels toward a nearby low-boundary basin; the amber circle shows that effective seed. This is meant to make clicks directly on an eyelash, iris edge, hair line, or other strong contour usable without giving the click semantic meaning.

## Boundary algorithm

`boundary_guriguri.py`:

- converts the immutable original to Lab;
- lightly smooths pixel noise;
- combines luminance and chroma Scharr gradients into a visual-boundary strength map;
- robustly normalizes that map to `0..100`;
- stretches weak boundaries across more of the wheel range so small wheel movements remain useful;
- selects only the connected basin reachable from the seed without crossing a boundary stronger than the current wheel level.

Because increasing the threshold only adds passable pixels, repeated wheel growth creates nested connected selections. It does not use semantic segmentation, GrabCut, generative redraw, or a physical front/back model.

## Comparison question

The colour-continuity version asks whether neighbouring pixels look locally similar. The Boundary version asks whether the user is willing to cross the next visual contour.

The Boundary experiment is successful if regions such as face, eye, hair blocks, clothing, and small overlapping strands can be reached with fewer manual polygon points and with less uncontrolled background drift.

## Not included yet

- semantic recognition or AI segmentation
- automatic front/back decisions
- multi-seed add/subtract
- automatic hole/island inclusion
- selection-scoped blur / blend / feather
- a claim that every anime contour represents the desired animation part boundary
