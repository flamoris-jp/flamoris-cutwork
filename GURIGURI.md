# Parts Guriguri experiment

This spike adds a deliberately human-guided selection mode to the existing classical-cutout experiment.

## Goal

Do not ask AI or an automatic segmenter to decide semantic meaning.

The user decides the meaning by clicking a seed pixel. The software only expands or contracts a connected region by local colour continuity while the user turns the mouse wheel.

The original source pixels are never regenerated.

## Run

From `experiments/classical-cutout`:

```bash
python app_guriguri.py
```

## Interaction

1. Open an image.
2. Choose **Parts Guriguri**.
3. Click inside the region you mean.
4. Turn the mouse wheel up to grow the connected region.
5. Turn the mouse wheel down to shrink it.
6. Use `Ctrl+wheel` when you want normal viewport zoom while Guriguri is active.
7. Use **Create Part Layer** to commit the pending selection.
8. Click a new seed to restart Guriguri from another semantic location.
9. Use the existing Mask Add / Mask Erase or Part Polygon tools when manual correction is faster.
10. Press `Esc` during a Guriguri selection to restore the previous pending selection.

## Algorithm

`guriguri.py` converts the source image to Lab and uses OpenCV flood-fill in floating-range mode. A newly accepted pixel is compared with adjacent accepted pixels rather than only with the seed colour. This lets the selection walk through gentle shading while tending to stop at stronger colour boundaries.

Mouse-wheel motion changes the flood-fill tolerance. There is no semantic model, GrabCut, generative redraw, or automatic claim that the resulting boundary is artistically correct.

The experiment is successful if it reduces the number of polygon points the user needs to place while preserving Polygon as the exact fallback.

## Focus of this spike

Included:

- click-to-seed connected selection
- mouse-wheel grow and shrink
- `Ctrl+wheel` viewport zoom while Guriguri is active
- pending-mask preview using the existing UI
- deterministic local colour-continuity core
- Escape cancellation
- unit tests for region growth and wheel bounds

Not included yet:

- multi-seed add/subtract
- edge-aware weighting beyond Lab colour continuity
- selection-scoped blur / blend / feather
- automatic front/back or semantic layer decisions
- AI segmentation
