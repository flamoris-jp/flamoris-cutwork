from __future__ import annotations

import cv2
import numpy as np


DEFAULT_BOUNDARY_THRESHOLD = 10.0
MAX_BOUNDARY_THRESHOLD = 100.0
WHEEL_BOUNDARY_PER_NOTCH = 2.5
SEED_SNAP_RADIUS = 6
SEED_SNAP_DISTANCE_PENALTY = 1.5


def wheel_boundary_threshold(
    current: float,
    wheel_steps: float,
    *,
    minimum: float = 0.0,
    maximum: float = MAX_BOUNDARY_THRESHOLD,
    per_notch: float = WHEEL_BOUNDARY_PER_NOTCH,
) -> float:
    """Convert wheel motion into a bounded boundary-crossing threshold."""
    value = float(current) + float(wheel_steps) * float(per_notch)
    return float(np.clip(value, minimum, maximum))


def build_boundary_map(original_rgb: np.ndarray) -> np.ndarray:
    """Return a deterministic 0..100 per-pixel visual-boundary strength map.

    The map is intentionally semantic-free. It combines luminance and chroma
    gradients in Lab space, then robustly normalizes and square-roots the
    result so weak boundaries occupy more of the wheel range. This makes small
    wheel movements useful inside softly shaded anime regions while leaving
    strong line-art/colour transitions near the top of the scale.
    """
    if original_rgb.ndim != 3 or original_rgb.shape[2] != 3:
        raise ValueError("original_rgb must be an HxWx3 RGB image")

    lab = cv2.cvtColor(original_rgb, cv2.COLOR_RGB2LAB).astype(np.float32)
    lab = cv2.GaussianBlur(lab, (0, 0), 0.8)

    gradient_sq = np.zeros(lab.shape[:2], dtype=np.float32)
    for channel, weight in ((0, 1.0), (1, 0.65), (2, 0.65)):
        gx = cv2.Scharr(lab[:, :, channel], cv2.CV_32F, 1, 0)
        gy = cv2.Scharr(lab[:, :, channel], cv2.CV_32F, 0, 1)
        gradient_sq += float(weight) * (gx * gx + gy * gy)

    gradient = np.sqrt(gradient_sq)
    scale = float(np.percentile(gradient, 97.5))
    if scale <= 1e-6:
        return np.zeros(lab.shape[:2], dtype=np.float32)

    normalized = np.clip(gradient / scale, 0.0, 1.0)
    return (np.sqrt(normalized) * MAX_BOUNDARY_THRESHOLD).astype(np.float32)


def snap_seed_to_basin(
    boundary_map: np.ndarray,
    seed: tuple[int, int],
    *,
    radius: int = SEED_SNAP_RADIUS,
    distance_penalty: float = SEED_SNAP_DISTANCE_PENALTY,
) -> tuple[int, int]:
    """Move a click only a few pixels toward a nearby low-boundary basin.

    This keeps a click on an eyelash/iris edge usable without assigning any
    semantic meaning to the click. Distance is penalized so the seed cannot
    jump across the image to an unrelated flat region.
    """
    if boundary_map.ndim != 2:
        raise ValueError("boundary_map must be HxW")

    height, width = boundary_map.shape
    x, y = int(seed[0]), int(seed[1])
    if not (0 <= x < width and 0 <= y < height):
        raise ValueError("seed must be inside the image")

    radius = max(0, int(radius))
    x0, x1 = max(0, x - radius), min(width, x + radius + 1)
    y0, y1 = max(0, y - radius), min(height, y + radius + 1)
    roi = boundary_map[y0:y1, x0:x1]

    yy, xx = np.indices(roi.shape, dtype=np.float32)
    dx = xx + float(x0 - x)
    dy = yy + float(y0 - y)
    score = roi.astype(np.float32) + float(distance_penalty) * np.sqrt(dx * dx + dy * dy)
    local_y, local_x = np.unravel_index(int(np.argmin(score)), score.shape)
    return int(x0 + local_x), int(y0 + local_y)


def initial_boundary_threshold(boundary_map: np.ndarray, seed: tuple[int, int]) -> float:
    """Choose a conservative initial level that still contains the snapped seed."""
    x, y = int(seed[0]), int(seed[1])
    seed_strength = float(boundary_map[y, x])
    return float(np.clip(max(DEFAULT_BOUNDARY_THRESHOLD, seed_strength + 1.5), 0.0, MAX_BOUNDARY_THRESHOLD))


def grow_boundary_mask(
    boundary_map: np.ndarray,
    seed: tuple[int, int],
    threshold: float,
) -> np.ndarray:
    """Return the connected basin reachable without crossing stronger boundaries.

    Increasing ``threshold`` only makes more pixels passable, so repeated wheel
    growth produces nested connected selections. The source artwork is not
    recoloured, regenerated, or semantically classified.
    """
    if boundary_map.ndim != 2:
        raise ValueError("boundary_map must be HxW")

    height, width = boundary_map.shape
    x, y = int(seed[0]), int(seed[1])
    if not (0 <= x < width and 0 <= y < height):
        raise ValueError("seed must be inside the image")

    threshold = float(np.clip(threshold, 0.0, MAX_BOUNDARY_THRESHOLD))
    passable = np.where(boundary_map <= threshold, 255, 0).astype(np.uint8)
    passable[y, x] = 255

    flood_mask = np.zeros((height + 2, width + 2), dtype=np.uint8)
    flags = 4 | cv2.FLOODFILL_MASK_ONLY | (255 << 8)
    cv2.floodFill(
        passable,
        flood_mask,
        (x, y),
        128,
        loDiff=0,
        upDiff=0,
        flags=flags,
    )
    return np.where(flood_mask[1:-1, 1:-1] > 0, 255, 0).astype(np.uint8)
