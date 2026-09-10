from __future__ import annotations

import unittest

import numpy as np

from clone_brush import normalize_rect, paint_aligned_clone, rect_center


class CloneBrushTests(unittest.TestCase):
    def test_normalize_rect_is_half_open_and_direction_independent(self) -> None:
        self.assertEqual(normalize_rect((8, 7), (3, 2), 20, 20), (3, 2, 9, 8))
        self.assertEqual(rect_center((3, 2, 9, 8)), (5, 4))

    def test_aligned_clone_copies_source_offset_into_destination(self) -> None:
        original = np.zeros((20, 20, 3), dtype=np.uint8)
        yy, xx = np.indices((20, 20))
        original[:, :, 0] = xx * 7
        original[:, :, 1] = yy * 5
        repair = np.zeros((20, 20, 4), dtype=np.uint8)

        touched = paint_aligned_clone(
            repair,
            original,
            (2, 2, 10, 10),
            (5, 5),
            (13, 13),
            (13, 13),
            (13, 13),
            1,
            feather=0.0,
        )

        self.assertGreater(touched, 0)
        np.testing.assert_array_equal(repair[13, 13, :3], original[5, 5])
        self.assertEqual(int(repair[13, 13, 3]), 255)

    def test_hole_only_blocks_pixels_outside_cutout_hole(self) -> None:
        original = np.full((20, 20, 3), (120, 80, 40), dtype=np.uint8)
        repair = np.zeros((20, 20, 4), dtype=np.uint8)
        hole = np.zeros((20, 20), dtype=np.uint8)
        hole[12:15, 12:15] = 255

        paint_aligned_clone(
            repair,
            original,
            (2, 2, 10, 10),
            (5, 5),
            (13, 13),
            (10, 13),
            (16, 13),
            3,
            hole_mask=hole,
            feather=0.0,
        )

        self.assertTrue(np.all(repair[:, :, 3][hole == 0] == 0))
        self.assertGreater(int(np.count_nonzero(repair[:, :, 3][hole > 0])), 0)

    def test_source_rectangle_is_a_hard_sampling_fence(self) -> None:
        original = np.full((20, 20, 3), 200, dtype=np.uint8)
        repair = np.zeros((20, 20, 4), dtype=np.uint8)

        paint_aligned_clone(
            repair,
            original,
            (2, 2, 5, 5),
            (3, 3),
            (10, 10),
            (10, 10),
            (18, 10),
            1,
            feather=0.0,
        )

        self.assertGreater(int(repair[10, 10, 3]), 0)
        self.assertEqual(int(repair[10, 18, 3]), 0)


if __name__ == "__main__":
    unittest.main()
