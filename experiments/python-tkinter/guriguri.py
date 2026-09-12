from __future__ import annotations

import cv2
import numpy as np


DEFAULT_TOLERANCE = 6.0
MAX_TOLERANCE = 64.0
SCRUB_TOLERANCE_PER_PIXEL = 0.12
WHEEL_TOLERANCE_PER_NOTCH = 1.5


def scrub_tolerance(
    current: float,
    horizontal_delta: float,
    *,
    minimum: float = 0.0,
    maximum: float = MAX_TOLERANCE,
    per_pixel: float = SCRUB_TOLERANCE_PER_PIXEL,
) -> float:
    """Legacy horizontal-scrub adjustment kept for comparison tests."""
    value = float(current) + float(horizontal_delta) * float(per_pixel)
    return float(np.clip(value, minimum, maximum))


def wheel_tolerance(
    current: float,
    wheel_steps: float,
    *,
    minimum: float = 0.0,
    maximum: float = MAX_TOLERANCE,
    per_notch: float = WHEEL_TOLERANCE_PER_NOTCH,
) -> float:
    """Convert mouse-wheel motion into a bounded grow/shrink tolerance.

    Positive wheel motion grows the region and negative wheel motion shrinks it.
    Fractional steps are accepted so high-resolution wheels/trackpads can still
    adjust the selection smoothly.
    """
    value = float(current) + float(wheel_steps) * float(per_notch)
    return float(np.clip(value, minimum, maximum))


def grow_selection_mask(
    original_rgb: np.ndarray,
    seed: tuple[int, int],
    tolerance: float,
) -> np.ndarray:
    """Grow a connected binary mask from ``seed`` using local colour continuity.

    OpenCV flood-fill runs in floating-range mode. Each newly accepted pixel is
    compared with neighbouring accepted pixels rather than only with the seed
    colour. The user supplies semantic intent by choosing the seed and then
    adjusts tolerance interactively with the mouse wheel.

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
