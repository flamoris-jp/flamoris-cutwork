from __future__ import annotations

import unittest

import numpy as np

from boundary_guriguri import (
    build_boundary_map,
    grow_boundary_mask,
    snap_seed_to_basin,
    wheel_boundary_threshold,
)


class BoundaryGuriguriTests(unittest.TestCase):
    def test_boundary_map_is_bounded_and_same_size(self) -> None:
        image = np.zeros((40, 60, 3), dtype=np.uint8)
        image[:, :30] = (80, 80, 80)
        image[:, 30:] = (220, 220, 220)

        boundary = build_boundary_map(image)

        self.assertEqual(boundary.shape, image.shape[:2])
        self.assertGreaterEqual(float(boundary.min()), 0.0)
        self.assertLessEqual(float(boundary.max()), 100.0)
        self.assertGreater(float(boundary[:, 29:32].mean()), float(boundary[:, 5:10].mean()))

    def test_nested_thresholds_cross_concentric_barriers_in_order(self) -> None:
        size = 61
        yy, xx = np.indices((size, size))
        cx = cy = size // 2
        radius = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)

        boundary = np.zeros((size, size), dtype=np.float32)
        boundary[(radius >= 9.5) & (radius <= 10.5)] = 30.0
        boundary[(radius >= 19.5) & (radius <= 20.5)] = 70.0

        inner = grow_boundary_mask(boundary, (cx, cy), 20.0)
        middle = grow_boundary_mask(boundary, (cx, cy), 40.0)
        outer = grow_boundary_mask(boundary, (cx, cy), 80.0)

        self.assertLess(int(np.count_nonzero(inner)), int(np.count_nonzero(middle)))
        self.assertLess(int(np.count_nonzero(middle)), int(np.count_nonzero(outer)))
        self.assertTrue(np.all(middle[inner > 0] == 255))
        self.assertTrue(np.all(outer[middle > 0] == 255))
        self.assertEqual(int(inner[cy, cx]), 255)

    def test_seed_snap_prefers_nearby_basin_without_large_jump(self) -> None:
        boundary = np.full((25, 25), 80.0, dtype=np.float32)
        boundary[12, 12] = 60.0
        boundary[12, 14] = 5.0
        boundary[12, 22] = 0.0

        snapped = snap_seed_to_basin(boundary, (12, 12), radius=6)

        self.assertEqual(snapped, (14, 12))

    def test_wheel_adjustment_grows_shrinks_and_clamps(self) -> None:
        self.assertGreater(wheel_boundary_threshold(20.0, +1), 20.0)
        self.assertLess(wheel_boundary_threshold(20.0, -1), 20.0)
        self.assertEqual(wheel_boundary_threshold(1.0, -1000), 0.0)
        self.assertEqual(wheel_boundary_threshold(99.0, +1000), 100.0)


if __name__ == "__main__":
    unittest.main()
