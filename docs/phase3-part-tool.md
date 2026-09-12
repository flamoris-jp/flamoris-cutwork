# Phase 3 unified Part Tool

This document records the production behavior implemented for Issue #14. It
extends the Phase 2 editing spine without adding a second document, compositor,
or history path.

## Input and tool boundary

`DocumentCanvas` normalizes WPF input and forwards it to one
`CanvasInputRouter`. The router owns viewport conversion and navigation
precedence. The active `PartToolController` receives document-space points only
and owns all uncommitted fence/fitting state.

The Part workflow is one state machine:

1. `Idle`
2. `DrawingFence`
3. `FittingPreview`
4. commit or cancel back to `Idle`

Enter or double-click finalizes a fence with at least three points. At fitting
step zero, the exact polygon mask remains available as the manual fallback; no
separate production Polygon mode exists.

## Guriguri behavior

The C# implementation ports the deterministic behavior of the preserved Python
`PolygonBoundaryShrinker`:

- the rasterized loose polygon is a hard fence;
- a four-neighbour Dijkstra search starts at the fence's inside edge;
- crossing cost is `1 + 60 × strength⁴`, preferring flat/shaded margin pixels
  and resisting line-art or colour boundaries;
- retained area follows `round(polygonPixels × 0.88^step)`, clamped to at least
  eight pixels; and
- any fitting result is a prefix removal from one stable order, so moving
  outward restores the exact prior mask.

Boundary preparation runs only for the polygon ROI over immutable Original
pixels. It uses deterministic sRGB-to-Lab conversion, Gaussian smoothing,
Scharr luminance/chroma gradients, and 97.5-percentile normalization to 0–100.
It has no WPF, Python runtime, OpenCV runtime, GPU, or AI dependency.

## Wheel mapping

- In `FittingPreview`, wheel up restores outward toward the rough fence and
  wheel down moves inward (shrinks). WPF reports those directions as positive
  and negative wheel deltas, respectively; the conversion is centralized
  before the tool receives steps.
- `Ctrl+wheel` always zooms the viewport.
- Outside fitting preview, ordinary wheel retains normal viewport zoom.

This precedence is implemented once in `CanvasInputRouter`, not in individual
WPF handlers.

## Preview, commit, and cancel

Fence lines/points and the magenta fitting mask are rendered on the independent
overlay surface. Pointer movement and fitting wheel changes do not touch the
authored document, history, dirty state, compositor, or main WriteableBitmap.
The fitting bitmap covers only the local polygon bounds.

Commit creates one `PartLayer` through one existing `AddLayer` transaction. The
transaction records the new layer as selection, publishes its local bounds as
both composite and Base-hole dirty regions, and becomes exactly one Undo entry.
Undo removes that same layer and hole; Redo restores the same identity and mask.

Cancel clears pending tool/overlay state and performs no document command. It
does not advance document/current/saved revision, add history, or mark the
document dirty.

## Windows hands-on checklist

An interactive Windows desktop was not available during implementation. Verify
the following before merge:

1. Open representative PNG and JPEG artwork.
2. Activate Part Tool and rough-fence an eye, ear, and hand with 4–10 clicks.
3. Finalize once with Enter and once with double-click.
4. Confirm wheel down contracts inward and wheel up exactly restores outward.
5. Confirm `Ctrl+wheel` zooms during fitting without changing the mask step.
6. Pan with middle drag and Space+drag before and during drawing; confirm fence
   points do not drift after repeated zoom/pan.
7. Confirm the magenta fitting preview is responsive and the main composite is
   not rebuilt until commit.
8. Commit, inspect Composite/Base hole, hide the Part, then Undo and Redo once.
9. Confirm Redo restores the same Part and hole.
10. Start a new fence, press Escape in drawing and fitting states, and confirm no
    layer or dirty marker remains.
11. Switch Japanese/English during each state and check toolbar/status guidance.

## Deferred

Mask Brush, Patch, Clone, Blur/Smudge, persistence, export, packaging, GPU, AI,
and automatic semantic/multi-part extraction remain Phase 4+ work.
