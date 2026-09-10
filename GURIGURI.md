# Parts Guriguri experiment

This spike explores a human-guided selector for the classical-cutout experiment. The human chooses only the starting location. The software expands or contracts a connected mask with the mouse wheel. Original artwork pixels are never regenerated.

## Run

From `experiments/classical-cutout`:

```bash
python app_boundary_guriguri.py
```

The earlier colour-continuity prototype remains available as:

```bash
python app_guriguri.py
```

## Current interaction

1. Open an image.
2. Choose **Boundary Guriguri**.
3. Click anywhere inside the thing you want to explore. The click has no semantic label such as iris, sclera, hair, or skin.
4. Wheel up to reveal more connected area.
5. Wheel down to return inward.
6. `Ctrl+wheel` keeps normal viewport zoom.
7. Use **Create Part Layer** when the selection is useful.
8. Use Mask Add / Mask Erase or Part Polygon when exact manual correction is faster.
9. `Esc` restores the previous pending selection.

The pending Guriguri mask is shown in magenta for visibility.

## Why Boundary Guriguri changed

The first colour-continuity version used floating-range flood fill. It could walk through a chain of locally similar colours and unexpectedly escape from skin into hair/background.

The first boundary version replaced colour continuity with one global boundary threshold. It improved directionality, but showed a percolation cliff: one wheel notch could open a narrow weak corridor and suddenly connect a few hundred selected pixels to most of the image.

The current version separates two questions:

- **Boundary evidence decides where growth prefers to go.**
- **The wheel decides how much connected area is revealed.**

`boundary_guriguri.py` builds the same semantic-free visual boundary map, then performs an incremental Dijkstra-style priority flood. Strong line-art/colour boundaries are expensive, gentle interior shading is cheaper, and a small distance cost prevents unlimited free travel through a flat region. Each wheel step requests a gradually larger prefix of that stable connected expansion order, so a newly crossed corridor cannot select the rest of the image in one notch.

## Design principles

- The user supplies meaning; the selector does not infer part names.
- Clicking on different places inside an eye should still be usable.
- Growth and shrink are reversible nested prefixes.
- AI, GrabCut and generative redraw are not required.
- Part Polygon remains the exact fallback and success is measured by reduced manual polygon work.

## Not included yet

- multi-seed add/subtract
- user-placed hard barriers
- selection-scoped feather / blend / blur
- automatic front/back decisions
- semantic segmentation
- performance optimization for extremely large selections
