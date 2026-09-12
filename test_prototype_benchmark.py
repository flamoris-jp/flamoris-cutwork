from __future__ import annotations

import unittest

from prototype_benchmark import PrototypeBenchmark, StageTimer, summarize_values


class PrototypeBenchmarkTests(unittest.TestCase):
    def test_empty_summary_is_explicit(self) -> None:
        self.assertEqual(
            summarize_values([]),
            {"count": 0, "average": None, "minimum": None, "maximum": None},
        )

    def test_summary_reports_average_and_range(self) -> None:
        self.assertEqual(
            summarize_values([1.0, 2.0, 6.0]),
            {"count": 3, "average": 3.0, "minimum": 1.0, "maximum": 6.0},
        )

    def test_disabled_timer_does_not_report_measurement(self) -> None:
        timer = StageTimer(False)
        self.assertEqual(timer.mark_ms(), 0.0)
        self.assertEqual(timer.total_ms(), 0.0)

    def test_stroke_summary_keeps_kernel_and_refresh_costs_separate(self) -> None:
        summary = PrototypeBenchmark._summarize(
            {
                "kind": "clone_stroke",
                "setup_ms": [3.0],
                "pointer_interval_ms": [8.0, 12.0],
                "segments": [
                    {
                        "interpolated_point_count": 4,
                        "roi_pixels": 100,
                        "touched_pixels": 80,
                        "kernel_ms": 1.0,
                        "image_update_ms": 0.5,
                        "total_ms": 2.0,
                    }
                ],
                "refreshes": [
                    {
                        "composite_ms": 10.0,
                        "viewport_transform_ms": 4.0,
                        "photo_image_ms": 3.0,
                        "canvas_update_ms": 1.0,
                        "total_ms": 18.0,
                    }
                ],
            }
        )
        self.assertEqual(summary["kernel_ms"]["average"], 1.0)
        self.assertEqual(summary["visible_refresh_ms"]["average"], 18.0)
        self.assertEqual(summary["recorded_processing_ms"], 23.0)


if __name__ == "__main__":
    unittest.main()
