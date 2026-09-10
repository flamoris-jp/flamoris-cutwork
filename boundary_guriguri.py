from __future__ import annotations

import heapq

import cv2
import numpy as np


MAX_BOUNDARY_STRENGTH = 100.0
SEED_SNAP_RADIUS = 6
SEED_SNAP_DISTANCE_PENALTY = 1.5
INITIAL_TARGET_PIXELS = 32
TARGET_GROWTH_FACTOR = 1.55


def build_boundary_map(original_rgb: np.ndarray) -> np.ndarray:
    """Return a deterministic 0..100 visual-boundary strength map.

    This remains semantic-free. Luminance/chroma gradients in Lab space create
    the local boundary evidence. Strong line art and colour transitions score
    high; gentle shading scores lower.
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
    return (np.sqrt(normalized) * MAX_BOUNDARY_STRENGTH).astype(np.float32)


def snap_seed_to_basin(
    boundary_map: np.ndarray,
    seed: tuple[int, int],
    *,
    radius: int = SEED_SNAP_RADIUS,
    distance_penalty: float = SEED_SNAP_DISTANCE_PENALTY,
) -> tuple[int, int]:
    """Move a click only a few pixels toward a nearby low-boundary basin."""
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


def target_pixels_for_step(
    step: int,
    image_pixels: int,
    *,
    initial_pixels: int = INITIAL_TARGET_PIXELS,
    growth_factor: float = TARGET_GROWTH_FACTOR,
) -> int:
    """Map wheel steps to a smooth bounded selection-size target.

    The wheel controls *how much* region is revealed, not a single global
    threshold. This prevents one newly-opened weak corridor from selecting the
    rest of the image in one notch.
    """
    image_pixels = max(1, int(image_pixels))
    step = max(0, int(step))
    initial_pixels = max(1, int(initial_pixels))
    growth_factor = max(1.01, float(growth_factor))
    target = int(round(initial_pixels * (growth_factor ** step)))
    return min(image_pixels, max(1, target))


def _edge_cost(boundary_a: float, boundary_b: float) -> float:
    """Cost for crossing one pixel edge.

    A tiny distance cost stops infinite travel through a flat basin, while the
    fourth-power boundary term makes strong line art much more expensive than
    gentle interior shading.
    """
    strength = max(float(boundary_a), float(boundary_b)) / MAX_BOUNDARY_STRENGTH
    return 0.08 + 8.0 * (strength ** 4)


class BoundaryPriorityGrower:
    """Incremental connected region grower ordered by boundary-aware path cost.

    Dijkstra-style expansion records a stable pixel order. Any prefix of that
    order is connected, so wheel-down can simply request a shorter prefix and
    wheel-up can continue the existing search. Boundary evidence determines
    *where* growth goes; wheel step determines *how much* growth is shown.
    """

    def __init__(self, boundary_map: np.ndarray, seed: tuple[int, int]) -> None:
        if boundary_map.ndim != 2:
            raise ValueError("boundary_map must be HxW")

        self.boundary_map = boundary_map.astype(np.float32, copy=False)
        self.height, self.width = self.boundary_map.shape
        x, y = int(seed[0]), int(seed[1])
        if not (0 <= x < self.width and 0 <= y < self.height):
            raise ValueError("seed must be inside the image")

        self.seed = (x, y)
        total = self.height * self.width
        self.best = np.full(total, np.inf, dtype=np.float64)
        self.finalized = np.zeros(total, dtype=np.bool_)
        self.order: list[int] = []
        seed_index = y * self.width + x
        self.best[seed_index] = 0.0
        self.heap: list[tuple[float, int]] = [(0.0, seed_index)]

    def _neighbors(self, index: int):
        y, x = divmod(index, self.width)
        if x > 0:
            yield index - 1
        if x + 1 < self.width:
            yield index + 1
        if y > 0:
            yield index - self.width
        if y + 1 < self.height:
            yield index + self.width

    def ensure_count(self, target_count: int) -> None:
        target_count = min(self.height * self.width, max(1, int(target_count)))
        boundary_flat = self.boundary_map.ravel()

        while self.heap and len(self.order) < target_count:
            cost, index = heapq.heappop(self.heap)
            if self.finalized[index] or cost != self.best[index]:
                continue

            self.finalized[index] = True
            self.order.append(index)
            current_boundary = float(boundary_flat[index])

            for neighbor in self._neighbors(index):
                if self.finalized[neighbor]:
                    continue
                candidate = cost + _edge_cost(current_boundary, float(boundary_flat[neighbor]))
                if candidate < self.best[neighbor]:
                    self.best[neighbor] = candidate
                    heapq.heappush(self.heap, (candidate, neighbor))

    def mask_for_count(self, target_count: int) -> np.ndarray:
        target_count = min(self.height * self.width, max(1, int(target_count)))
        self.ensure_count(target_count)
        actual = min(target_count, len(self.order))
        mask = np.zeros(self.height * self.width, dtype=np.uint8)
        if actual:
            mask[np.asarray(self.order[:actual], dtype=np.int64)] = 255
        return mask.reshape((self.height, self.width))
