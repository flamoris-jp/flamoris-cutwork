from __future__ import annotations

import cv2
import numpy as np


Rect = tuple[int, int, int, int]
Point = tuple[int, int]


def normalize_rect(first: Point, second: Point, width: int, height: int) -> Rect:
    """Return an image-clamped, half-open rectangle (x0, y0, x1, y1)."""
    x0, x1 = sorted((int(first[0]), int(second[0])))
    y0, y1 = sorted((int(first[1]), int(second[1])))
    x0 = int(np.clip(x0, 0, width))
    x1 = int(np.clip(x1 + 1, 0, width))
    y0 = int(np.clip(y0, 0, height))
    y1 = int(np.clip(y1 + 1, 0, height))
    return x0, y0, x1, y1


def rect_center(rect: Rect) -> Point:
    x0, y0, x1, y1 = rect
    return int((x0 + x1 - 1) // 2), int((y0 + y1 - 1) // 2)


def paint_aligned_clone(
    repair_rgba: np.ndarray,
    original_rgb: np.ndarray,
    source_rect: Rect,
    source_anchor: Point,
    destination_anchor: Point,
    stroke_start: Point,
    stroke_end: Point,
    radius: int,
    *,
    hole_mask: np.ndarray | None = None,
    feather: float = 0.12,
) -> int:
    """Paint immutable source pixels along a destination stroke.

    Source and destination move in lockstep: every destination pixel samples the
    source pixel at the same offset from the two stroke anchors. Sampling is
    constrained to ``source_rect``. When ``hole_mask`` is supplied, only pixels
    inside that mask are changed. The function mutates ``repair_rgba`` and
    returns the number of destination pixels touched by the stroke.
    """
    if original_rgb.ndim != 3 or original_rgb.shape[2] != 3:
        raise ValueError("original_rgb must be HxWx3")
    if repair_rgba.shape[:2] != original_rgb.shape[:2] or repair_rgba.shape[2] != 4:
        raise ValueError("repair_rgba must match original image size and have four channels")
    if hole_mask is not None and hole_mask.shape != original_rgb.shape[:2]:
        raise ValueError("hole_mask must match original image size")

    height, width = original_rgb.shape[:2]
    sx0, sy0, sx1, sy1 = source_rect
    if sx0 >= sx1 or sy0 >= sy1:
        return 0

    radius = max(1, int(radius))
    x0 = max(0, min(stroke_start[0], stroke_end[0]) - radius - 2)
    x1 = min(width, max(stroke_start[0], stroke_end[0]) + radius + 3)
    y0 = max(0, min(stroke_start[1], stroke_end[1]) - radius - 2)
    y1 = min(height, max(stroke_start[1], stroke_end[1]) + radius + 3)
    if x0 >= x1 or y0 >= y1:
        return 0

    brush = np.zeros((y1 - y0, x1 - x0), dtype=np.uint8)
    local_start = (int(stroke_start[0] - x0), int(stroke_start[1] - y0))
    local_end = (int(stroke_end[0] - x0), int(stroke_end[1] - y0))
    cv2.line(brush, local_start, local_end, 255, thickness=radius * 2, lineType=cv2.LINE_AA)
    cv2.circle(brush, local_end, radius, 255, -1, lineType=cv2.LINE_AA)
    if feather > 0:
        sigma = max(0.35, radius * float(feather))
        brush = cv2.GaussianBlur(brush, (0, 0), sigma)

    yy, xx = np.indices(brush.shape, dtype=np.int32)
    dest_x = xx + x0
    dest_y = yy + y0
    offset_x = int(source_anchor[0] - destination_anchor[0])
    offset_y = int(source_anchor[1] - destination_anchor[1])
    source_x = dest_x + offset_x
    source_y = dest_y + offset_y

    valid = brush > 0
    valid &= source_x >= sx0
    valid &= source_x < sx1
    valid &= source_y >= sy0
    valid &= source_y < sy1
    valid &= source_x >= 0
    valid &= source_x < width
    valid &= source_y >= 0
    valid &= source_y < height
    if hole_mask is not None:
        valid &= hole_mask[y0:y1, x0:x1] > 0
    if not np.any(valid):
        return 0

    sampled = original_rgb[source_y[valid], source_x[valid]].astype(np.float32)
    roi = repair_rgba[y0:y1, x0:x1]
    old_rgb = roi[:, :, :3][valid].astype(np.float32)
    weight = (brush[valid].astype(np.float32) / 255.0)[:, None]
    roi[:, :, :3][valid] = np.clip(old_rgb * (1.0 - weight) + sampled * weight, 0, 255).astype(np.uint8)
    roi[:, :, 3][valid] = np.maximum(roi[:, :, 3][valid], brush[valid])
    return int(np.count_nonzero(valid))
