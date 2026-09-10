from __future__ import annotations

import unittest

import numpy as np

from polygon_guriguri import PolygonBoundaryShrinker, target_keep_pixels_for_step


class PolygonGuriguriTests(unittest.TestCase):
    def test_step_zero_keeps_all_and_later_steps_keep_less(self) -> None:
        polygon_pixels = 10_000
        counts = [target_keep_pixels_for_step(step, polygon_pixels) for step in range(8)]

        self.assertEqual(counts[0], polygon_pixels)
        self.assertTrue(all(later < earlier for earlier, later in zip(counts, counts[1:])))
        self.assertTrue(all(count > 0 for count in counts))

    def test_shrunk_masks_are_nested_subsets_of_loose_polygon(self) -> None:
        boundary = np.zeros((31, 31), dtype=np.float32)
        polygon = np.zeros((31, 31), dtype=np.uint8)
        polygon[3:28, 3:28] = 255
        shrinker = PolygonBoundaryShrinker(boundary, polygon)

        large = shrinker.mask_for_keep_count(500)
        medium = shrinker.mask_for_keep_count(250)
        small = shrinker.mask_for_keep_count(100)

        self.assertEqual(int(np.count_nonzero(large)), 500)
        self.assertEqual(int(np.count_nonzero(medium)), 250)
        self.assertEqual(int(np.count_nonzero(small)), 100)
        self.assertTrue(np.all(large[polygon == 0] == 0))
        self.assertTrue(np.all(medium[large == 0] == 0))
        self.assertTrue(np.all(small[medium == 0] == 0))

    def test_strong_inner_boundary_resists_outer_peel(self) -> None:
        size = 41
        polygon = np.zeros((size, size), dtype=np.uint8)
        polygon[3:38, 3:38] = 255
        boundary = np.zeros((size, size), dtype=np.float32)

        # Strong square boundary around the intended inner object.
        boundary[12, 12:29] = 95.0
        boundary[28, 12:29] = 95.0
        boundary[12:29, 12] = 95.0
        boundary[12:29, 28] = 95.0

        shrinker = PolygonBoundaryShrinker(boundary, polygon)
        mask = shrinker.mask_for_keep_count(400)

        # Loose-polygon margin should peel away before the protected centre.
        self.assertEqual(int(mask[4, 4]), 0)
        self.assertEqual(int(mask[20, 20]), 255)
        self.assertTrue(np.all(mask[polygon == 0] == 0))

    def test_restoring_keep_count_is_exact_and_deterministic(self) -> None:
        boundary = np.zeros((25, 25), dtype=np.float32)
        polygon = np.zeros((25, 25), dtype=np.uint8)
        polygon[2:23, 2:23] = 255
        shrinker = PolygonBoundaryShrinker(boundary, polygon)

        first_300 = shrinker.mask_for_keep_count(300)
        _ = shrinker.mask_for_keep_count(120)
        restored_300 = shrinker.mask_for_keep_count(300)

        self.assertTrue(np.array_equal(first_300, restored_300))


if __name__ == "__main__":
    unittest.main()
