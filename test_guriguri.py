from __future__ import annotations

import unittest

import numpy as np

from guriguri import grow_selection_mask, scrub_tolerance, wheel_tolerance


class GuriguriTests(unittest.TestCase):
    def test_floating_range_walks_across_small_local_steps_but_stops_at_hard_boundary(self) -> None:
        image = np.zeros((12, 30, 3), dtype=np.uint8)
        image[:, :10] = 80
        image[:, 10:20] = 84
        image[:, 20:] = 160

        mask = grow_selection_mask(image, (2, 5), tolerance=6.0)

        self.assertEqual(int(mask[5, 2]), 255)
        self.assertEqual(int(mask[5, 15]), 255)
        self.assertEqual(int(mask[5, 25]), 0)
        self.assertEqual(set(np.unique(mask)), {0, 255})

    def test_larger_tolerance_never_shrinks_this_banded_region(self) -> None:
        image = np.zeros((10, 30, 3), dtype=np.uint8)
        image[:, :10] = 70
        image[:, 10:20] = 76
        image[:, 20:] = 130

        small = grow_selection_mask(image, (2, 5), tolerance=2.0)
        large = grow_selection_mask(image, (2, 5), tolerance=10.0)

        self.assertLessEqual(int(np.count_nonzero(small)), int(np.count_nonzero(large)))
        self.assertTrue(np.all(large[small > 0] == 255))

    def test_wheel_up_grows_down_shrinks_and_clamps(self) -> None:
        self.assertGreater(wheel_tolerance(6.0, +1), 6.0)
        self.assertLess(wheel_tolerance(6.0, -1), 6.0)
        self.assertEqual(wheel_tolerance(1.0, -1000), 0.0)
        self.assertEqual(wheel_tolerance(60.0, +1000), 64.0)

    def test_fractional_wheel_motion_is_supported(self) -> None:
        self.assertGreater(wheel_tolerance(6.0, 0.5), 6.0)
        self.assertLess(wheel_tolerance(6.0, -0.5), 6.0)

    def test_legacy_scrub_adjustment_remains_bounded_for_comparison(self) -> None:
        self.assertGreater(scrub_tolerance(6.0, 20), 6.0)
        self.assertLess(scrub_tolerance(6.0, -20), 6.0)
        self.assertEqual(scrub_tolerance(1.0, -1000), 0.0)
        self.assertEqual(scrub_tolerance(60.0, 1000), 64.0)

    def test_out_of_bounds_seed_is_rejected(self) -> None:
        image = np.zeros((8, 8, 3), dtype=np.uint8)
        with self.assertRaises(ValueError):
            grow_selection_mask(image, (8, 0), tolerance=6.0)


if __name__ == "__main__":
    unittest.main()
