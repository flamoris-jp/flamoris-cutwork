from __future__ import annotations

import unittest

import numpy as np

from app import CutoutSpikeApp
from image_ops import active_mask, composite_visible_rgba, polygon_mask, render_layer
from model import EditorState


class ImageOpsTests(unittest.TestCase):
    def setUp(self) -> None:
        yy, xx = np.indices((40, 60))
        self.original = np.dstack((xx * 3, yy * 4, (xx + yy) * 2)).clip(0, 255).astype(np.uint8)

    def test_polygon_is_final_exact_binary_mask(self) -> None:
        mask = polygon_mask((40, 60), [(10, 10), (30, 10), (30, 25), (10, 25)])
        self.assertEqual(int(mask[10, 10]), 255)
        self.assertEqual(int(mask[25, 30]), 255)
        self.assertEqual(int(mask[9, 10]), 0)
        self.assertEqual(set(np.unique(mask)), {0, 255})

    def test_parts_are_recreated_from_immutable_original(self) -> None:
        state = EditorState()
        state.reset(self.original)
        snapshot = state.original_rgb.copy()
        mask = polygon_mask((40, 60), [(5, 5), (20, 5), (20, 20), (5, 20)])
        part = state.add_part("eye_left", mask)
        rendered = render_layer(state.original_rgb, state.layers, part)
        self.assertTrue(np.array_equal(rendered[mask > 0, :3], snapshot[mask > 0]))
        self.assertTrue(np.array_equal(state.original_rgb, snapshot))

    def test_default_order_draws_patch_then_base_then_part(self) -> None:
        state = EditorState()
        state.reset(self.original)
        mask = polygon_mask((40, 60), [(22, 12), (37, 12), (37, 27), (22, 27)])
        part = state.add_part("eye_left", mask)
        patch = state.add_patch("patch", [(1, 1), (12, 1), (12, 10), (1, 10)], (29, 19))
        self.assertEqual([layer.kind for layer in state.layers], ["part", "base", "patch"])
        part.visible = False
        composite = composite_visible_rgba(state.original_rgb, state.layers)
        self.assertGreater(int(composite[19, 29, 3]), 0)

    def test_visible_part_reconstructs_original_over_patch(self) -> None:
        state = EditorState()
        state.reset(self.original)
        mask = polygon_mask((40, 60), [(22, 12), (37, 12), (37, 27), (22, 27)])
        state.add_part("eye_left", mask)
        state.add_patch("patch_eye_left", [(1, 1), (12, 1), (12, 10), (1, 10)], (29, 19))
        composite = composite_visible_rgba(state.original_rgb, state.layers)
        self.assertTrue(np.array_equal(composite[:, :, :3], self.original))
        self.assertTrue(np.all(composite[:, :, 3] == 255))

    def test_patch_supports_1000_percent_rotation_without_mutation(self) -> None:
        state = EditorState()
        state.reset(self.original)
        snapshot = state.original_rgb.copy()
        patch = state.add_patch("patch", [(1, 1), (5, 1), (5, 5), (1, 5)], (30, 20))
        patch.transform.scale = 10.0
        patch.transform.rotation_degrees = 33.0
        rendered = render_layer(state.original_rgb, state.layers, patch)
        self.assertEqual(rendered.shape, (40, 60, 4))
        self.assertGreater(int(np.count_nonzero(rendered[:, :, 3])), 0)
        self.assertTrue(np.array_equal(state.original_rgb, snapshot))

    def test_local_edits_do_not_modify_original_or_other_layer(self) -> None:
        state = EditorState()
        state.reset(self.original)
        snapshot = state.original_rgb.copy()
        mask = polygon_mask((40, 60), [(5, 5), (30, 5), (30, 25), (5, 25)])
        part = state.add_part("face", mask)
        untouched_mask = active_mask(state.original_rgb, state.layers, part).copy()
        part.local_edits.extend(
            [
                {"kind": "blur", "x": 15, "y": 15, "radius": 6, "strength": 0.5},
                {"kind": "smudge", "from_x": 14, "from_y": 15, "to_x": 18, "to_y": 15, "radius": 5, "strength": 0.3},
            ]
        )
        render_layer(state.original_rgb, state.layers, part)
        self.assertTrue(np.array_equal(state.original_rgb, snapshot))
        self.assertTrue(np.array_equal(part.mask, mask))
        self.assertTrue(np.array_equal(active_mask(state.original_rgb, state.layers, state.layers[-1]), active_mask(snapshot, state.layers, state.layers[-1])))
        self.assertEqual(untouched_mask.shape, mask.shape)

    def test_zoom_pan_coordinate_round_trip(self) -> None:
        app = CutoutSpikeApp.__new__(CutoutSpikeApp)
        app.state = EditorState()
        app.state.reset(self.original)
        app.state.viewport.zoom = 3.25
        app.state.viewport.offset_x = -71.5
        app.state.viewport.offset_y = 42.0
        point = (23, 17)
        canvas_point = app._image_to_canvas(point)
        restored = app._canvas_to_image(*canvas_point)
        self.assertIsNotNone(restored)
        self.assertLessEqual(abs(restored[0] - point[0]), 1)
        self.assertLessEqual(abs(restored[1] - point[1]), 1)


if __name__ == "__main__":
    unittest.main()
