# Parts Guriguri experiment

This spike explores human-guided selection for the classical-cutout experiment. Original artwork pixels are never regenerated.

The latest experiment reverses the previous idea: instead of asking the software to discover the correct outer boundary from one seed pixel, the human draws a **loose polygon fence first**, then the mouse wheel contracts that fence inward toward image boundaries.

## Run the latest experiment

From `experiments/classical-cutout`:

```bash
python app_polygon_guriguri.py
```

Comparison prototypes remain available:

```bash
python app_boundary_guriguri.py   # seed -> boundary-priority outward growth
python app_guriguri.py            # seed -> colour-continuity flood fill
```

## Polygon Guriguri interaction

1. Open an image.
2. Choose **Polygon Guriguri**.
3. Draw a deliberately loose polygon around the intended part. Precision is not required; the polygon only defines the search fence.
4. Press `Enter` or double-click to close the polygon.
5. Wheel up to shrink the mask inward.
6. Wheel down to restore toward the original loose polygon.
7. `Ctrl+wheel` keeps normal viewport zoom.
8. Stop when the magenta mask reaches a useful visual boundary.
9. Use **Create Part Layer** to commit it.
10. Use Mask Add / Mask Erase or exact Part Polygon when manual correction is faster.

## Why the direction was reversed

The colour-continuity prototype could escape through chains of locally similar pixels. The seed-based boundary prototype improved directionality but still had to answer a difficult question: from a point inside an eye, which strong internal boundaries belong to the same semantic part, and which boundary is the desired outside edge?

Polygon Guriguri gives that semantic responsibility back to the human in a cheap form. The user says only **"the part is somewhere inside this rough fence"**. The software never needs to infer whether the click meant pupil, iris, sclera, eyelash, hair, or skin.

## Outer-in algorithm

`polygon_guriguri.py` treats the finalized loose polygon as a hard upper bound. Pixels outside the polygon can never enter the result.

Deletion begins on the polygon's inside edge and walks inward with a deterministic Dijkstra-style search:

- flat or gently shaded areas are cheap to peel away;
- strong line-art and colour boundaries are expensive to cross;
- every intermediate result is a nested subset of the original polygon;
- wheel-down restores exactly the same previous masks;
- the wheel controls **how much** is peeled, avoiding one-threshold percolation cliffs.

The intended success case is not automatic perfect segmentation. It is reducing a careful 20-50 point final polygon to a rough 4-10 point fence plus a few wheel notches.

## Design principles

- Human decides meaning.
- Polygon provides the semantic/search scope.
- Algorithm performs boundary fitting, not semantic segmentation.
- Source pixels are immutable.
- AI, GrabCut, and generative redraw are unnecessary for the core selection interaction.
- Exact Polygon and Mask Add / Erase remain fallbacks.
- Selection preview is magenta for visibility on skin, blonde hair, and green eyes.

## Not included yet

- multi-polygon union/subtract
- user-placed hard keep/remove hints
- selection-scoped feather / blend / blur
- automatic front/back decisions
- semantic segmentation
- performance optimization for very large loose polygons
