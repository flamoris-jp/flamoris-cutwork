from __future__ import annotations

import heapq

import cv2
import numpy as np


KEEP_FACTOR_PER_STEP = 0.88
MIN_KEEP_PIXELS = 8
BOUNDARY_WEIGHT = 60.0


def target_keep_pixels_for_step(
    step: int,
    polygon_pixels: int,
    *,
    keep_factor: float = KEEP_FACTOR_PER_STEP,
    minimum_keep: int = MIN_KEEP_PIXELS,
) -> int:
    """Map wheel steps to a smooth retained-area target inside a loose polygon.

    Step 0 keeps the entire polygon. Increasing the step removes more pixels
    from the polygon boundary inward. The geometric schedule gives coarse
    control for large loose polygons while still allowing the user to wheel
    backward to restore exactly the same nested masks.
    """
    polygon_pixels = max(1, int(polygon_pixels))
    step = max(0, int(step))
    keep_factor = float(np.clip(keep_factor, 0.50, 0.999))
    minimum_keep = min(polygon_pixels, max(1, int(minimum_keep)))
    target = int(round(polygon_pixels * (keep_factor ** step)))
    return max(minimum_keep, min(polygon_pixels, target))


def _crossing_cost(boundary_a: float, boundary_b: float) -> float:
    """Cost for deleting one more pixel while moving inward.

    Flat/shaded areas are cheap to peel away. Strong line-art or colour edges
    are expensive, so a loose polygon tends to contract until it meets a real
    image boundary instead of blindly eroding thin eyelashes or hair strands.
    """
    strength = max(float(boundary_a), float(boundary_b)) / 100.0
    return 1.0 + BOUNDARY_WEIGHT * (strength ** 4)


class PolygonBoundaryShrinker:
    """Deterministic outside-in shrinker constrained to one loose polygon.

    Deletion starts only from the polygon's outer boundary. A Dijkstra-style
    search walks inward through pixels that belong to the polygon. The path
    cost strongly penalizes crossing visual boundaries. Any prefix of
    ``removal_order`` is therefore a valid deterministic amount of outer peel,
    and wheel-down can restore a longer prefix of the original polygon exactly.

    The polygon is a hard fence: pixels outside it can never enter the mask.
    """

    def __init__(self, boundary_map: np.ndarray, polygon_mask: np.ndarray) -> None:
        if boundary_map.ndim != 2:
            raise ValueError("boundary_map must be HxW")
        if polygon_mask.ndim != 2 or polygon_mask.shape != boundary_map.shape:
            raise ValueError("polygon_mask must be HxW and match boundary_map")

        self.boundary_map = boundary_map.astype(np.float32, copy=False)
        self.inside = polygon_mask > 0
        self.height, self.width = self.inside.shape
        self.polygon_count = int(np.count_nonzero(self.inside))
        if self.polygon_count == 0:
            raise ValueError("polygon_mask must contain foreground pixels")

        total = self.height * self.width
        self.best = np.full(total, np.inf, dtype=np.float64)
        self.finalized = np.zeros(total, dtype=np.bool_)
        self.removal_order: list[int] = []
        self.heap: list[tuple[float, int]] = []

        # Seed the peel from the inside edge of the loose polygon. A cross
        # kernel matches the 4-neighbour graph used by the Dijkstra expansion.
        binary = np.where(self.inside, 255, 0).astype(np.uint8)
        kernel = cv2.getStructuringElement(cv2.MORPH_CROSS, (3, 3))
        eroded = cv2.erode(binary, kernel, iterations=1, borderType=cv2.BORDER_CONSTANT, borderValue=0)
        frontier = self.inside & (eroded == 0)
        boundary_flat = self.boundary_map.ravel()

        for index in np.flatnonzero(frontier.ravel()):
            initial = _crossing_cost(float(boundary_flat[index]), float(boundary_flat[index]))
            self.best[index] = initial
            heapq.heappush(self.heap, (initial, int(index)))

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

    def ensure_removed_count(self, target_removed: int) -> None:
        target_removed = min(self.polygon_count, max(0, int(target_removed)))
        boundary_flat = self.boundary_map.ravel()
        inside_flat = self.inside.ravel()

        while self.heap and len(self.removal_order) < target_removed:
            cost, index = heapq.heappop(self.heap)
            if self.finalized[index] or cost != self.best[index]:
                continue

            self.finalized[index] = True
            self.removal_order.append(index)
            current_boundary = float(boundary_flat[index])

            for neighbor in self._neighbors(index):
                if not inside_flat[neighbor] or self.finalized[neighbor]:
                    continue
                candidate = cost + _crossing_cost(current_boundary, float(boundary_flat[neighbor]))
                if candidate < self.best[neighbor]:
                    self.best[neighbor] = candidate
                    heapq.heappush(self.heap, (candidate, neighbor))

    def mask_for_keep_count(self, keep_count: int) -> np.ndarray:
        keep_count = min(self.polygon_count, max(1, int(keep_count)))
        remove_count = self.polygon_count - keep_count
        self.ensure_removed_count(remove_count)

        mask = np.where(self.inside.ravel(), 255, 0).astype(np.uint8)
        actual_remove = min(remove_count, len(self.removal_order))
        if actual_remove:
            mask[np.asarray(self.removal_order[:actual_remove], dtype=np.int64)] = 0
        return mask.reshape((self.height, self.width))
