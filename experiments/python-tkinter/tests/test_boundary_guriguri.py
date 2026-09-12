from __future__ import annotations

import unittest

import numpy as np

from boundary_guriguri import (
    BoundaryPriorityGrower,
    build_boundary_map,
    snap_seed_to_basin,
    target_pixels_for_step,
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

    def test_seed_snap_prefers_nearby_basin_without_large_jump(self) -> None:
        boundary = np.full((25, 25), 80.0, dtype=np.float32)
        boundary[12, 12] = 60.0
        boundary[12, 14] = 5.0
        boundary[12, 22] = 0.0

        snapped = snap_seed_to_basin(boundary, (12, 12), radius=6)

        self.assertEqual(snapped, (14, 12))

    def test_target_pixels_grows_smoothly_and_clamps_to_image(self) -> None:
        targets = [target_pixels_for_step(step, 100_000) for step in range(8)]
        self.assertEqual(targets, sorted(targets))
        self.assertTrue(all(b > a for a, b in zip(targets, targets[1:])))
        self.assertLess(targets[1], targets[0] * 2)
        self.assertEqual(target_pixels_for_step(999, 1234), 1234)

    def test_priority_growth_prefixes_are_nested_and_exact_size(self) -> None:
        boundary = np.zeros((40, 40), dtype=np.float32)
        boundary[:, 20] = 90.0
        grower = BoundaryPriorityGrower(boundary, (5, 20))

        small = grower.mask_for_count(50)
        medium = grower.mask_for_count(200)
        shrunk = grower.mask_for_count(80)

        self.assertEqual(int(np.count_nonzero(small)), 50)
        self.assertEqual(int(np.count_nonzero(medium)), 200)
        self.assertEqual(int(np.count_nonzero(shrunk)), 80)
        self.assertTrue(np.all(medium[small > 0] == 255))
        self.assertTrue(np.all(medium[shrunk > 0] == 255))

    def test_strong_barrier_is_deferred_while_same_side_has_room(self) -> None:
        boundary = np.zeros((30, 30), dtype=np.float32)
        boundary[:, 15] = 100.0
        grower = BoundaryPriorityGrower(boundary, (4, 15))

        mask = grower.mask_for_count(200)

        # The left half contains 450 pixels, so a 200-pixel request should not
        # need to cross the strong vertical barrier.
        self.assertEqual(int(np.count_nonzero(mask[:, 16:])), 0)


if __name__ == "__main__":
    unittest.main()
