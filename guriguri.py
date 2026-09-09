from __future__ import annotations

import cv2
import numpy as np


DEFAULT_TOLERANCE = 6.0
MAX_TOLERANCE = 64.0
SCRUB_TOLERANCE_PER_PIXEL = 0.12


def scrub_tolerance(
    current: float,
    horizontal_delta: float,
    *,
    minimum: float = 0.0,
    maximum: float = MAX_TOLERANCE,
    per_pixel: float = SCRUB_TOLERANCE_PER_PIXEL,
) -> float:
    """Convert horizontal mouse scrubbing into a bounded grow/shrink tolerance.

    Dragging right grows the region; dragging left shrinks it.  The function is
    intentionally stateless so UI code can accumulate repeated back-and-forth
    "guriguri" movement without hidden momentum.
    """
    value = float(current) + float(horizontal_delta) * float(per_pixel)
    return float(np.clip(value, minimum, maximum))


def grow_selection_mask(
    original_rgb: np.ndarray,
    seed: tuple[int, int],
    tolerance: float,
) -> np.ndarray:
    """Grow a connected binary mask from ``seed`` using local colour continuity.

    The implementation deliberately uses OpenCV flood-fill in floating-range
    mode.  Each newly accepted pixel is compared to neighbouring accepted
    pixels rather than only to the original seed colour.  That matches the
    intended interaction: a user chooses the semantic seed, then scrubs the
    tolerance until the region reaches the desired visual boundary.

    No semantic model, GrabCut, redraw, or source-pixel mutation is involved.
    The returned mask contains only 0 and 255.
    """
    if original_rgb.ndim != 3 or original_rgb.shape[2] != 3:
        raise ValueError("original_rgb must be an HxWx3 RGB image")

    height, width = original_rgb.shape[:2]
    x, y = int(seed[0]), int(seed[1])
    if not (0 <= x < width and 0 <= y < height):
        raise ValueError("seed must be inside the image")

    tolerance = float(np.clip(tolerance, 0.0, MAX_TOLERANCE))

    # Lab makes the tolerance less sensitive to RGB channel orientation while
    # keeping the whole operation deterministic and local.
    lab = cv2.cvtColor(original_rgb, cv2.COLOR_RGB2LAB)
    flood_mask = np.zeros((height + 2, width + 2), dtype=np.uint8)

    lightness_tolerance = int(round(tolerance))
    chroma_tolerance = int(round(tolerance * 0.65))
    diff = (lightness_tolerance, chroma_tolerance, chroma_tolerance)

    flags = 4 | cv2.FLOODFILL_MASK_ONLY | (255 << 8)
    cv2.floodFill(
        lab.copy(),
        flood_mask,
        (x, y),
        0,
        loDiff=diff,
        upDiff=diff,
        flags=flags,
    )

    return np.where(flood_mask[1:-1, 1:-1] > 0, 255, 0).astype(np.uint8)
