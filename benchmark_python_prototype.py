from __future__ import annotations

import argparse
import json
import os
import platform
import sys
from pathlib import Path
from time import perf_counter_ns

import cv2
import numpy as np
from PIL import Image

from app import CutoutSpikeApp
from clone_brush import clone_segment_geometry, paint_aligned_clone
from model import EditorState
from prototype_benchmark import summarize_values


CASES = ((1280, 720), (1920, 1080), (3840, 2160))
SEGMENTS_PER_STROKE = 24
REFRESH_EVERY_SEGMENTS = 4
MEASURED_STROKES = 4
VIEWPORT_SIZE = (1280, 720)
BRUSH_RADIUS = 14


def _elapsed_ms(started_ns: int) -> float:
    return (perf_counter_ns() - started_ns) / 1_000_000.0


def _synthetic_original(width: int, height: int) -> np.ndarray:
    yy, xx = np.indices((height, width), dtype=np.uint32)
    return np.dstack(
        (
            (xx * 7 + yy * 3) % 256,
            (xx * 2 + yy * 5) % 256,
            (xx + yy * 11) % 256,
        )
    ).astype(np.uint8)


def _make_headless_app(width: int, height: int) -> tuple[CutoutSpikeApp, np.ndarray, object]:
    original = _synthetic_original(width, height)
    state = EditorState()
    state.reset(original)
    hole = np.zeros((height, width), dtype=np.uint8)
    margin_x, margin_y = width // 5, height // 5
    cv2.rectangle(hole, (margin_x, margin_y), (width - margin_x - 1, height - margin_y - 1), 255, -1)
    state.add_part("benchmark_part", hole)
    repair = state.add_repair("benchmark_repair")

    app = CutoutSpikeApp.__new__(CutoutSpikeApp)
    app.state = state
    app._layer_cache = {repair.id: repair.paint_rgba}
    app._composite_cache_rgb = None
    app._checker_cache_rgb = None
    return app, hole, repair


def _stroke_points(width: int, height: int) -> list[tuple[int, int]]:
    start_x = width * 9 // 20
    distance = min(width // 5, 360)
    y = height // 2
    return [
        (start_x + round(distance * index / SEGMENTS_PER_STROKE), y)
        for index in range(SEGMENTS_PER_STROKE + 1)
    ]


def _run_case(width: int, height: int) -> dict[str, object]:
    app, hole, repair = _make_headless_app(width, height)
    assert repair.paint_rgba is not None
    points = _stroke_points(width, height)
    source_anchor = (width // 4, height // 2)
    destination_anchor = points[0]
    source_rect = (0, 0, width, height)

    kernel_times: list[float] = []
    image_update_times: list[float] = []
    sample_total_times: list[float] = []
    roi_pixels: list[float] = []
    interpolated_points: list[float] = []
    touched_pixels: list[float] = []
    composite_times: list[float] = []
    viewport_transform_times: list[float] = []
    display_prepare_times: list[float] = []
    stroke_times: list[float] = []

    image_scale = min(VIEWPORT_SIZE[0] / width, VIEWPORT_SIZE[1] / height)
    offset_x = (VIEWPORT_SIZE[0] - width * image_scale) / 2
    offset_y = (VIEWPORT_SIZE[1] - height * image_scale) / 2
    inverse = (
        1.0 / image_scale,
        0.0,
        -offset_x / image_scale,
        0.0,
        1.0 / image_scale,
        -offset_y / image_scale,
    )

    for stroke_index in range(MEASURED_STROKES + 1):
        repair.paint_rgba.fill(0)
        app._layer_cache[repair.id] = repair.paint_rgba
        app._composite_cache_rgb = None
        stroke_started = perf_counter_ns()

        measured = stroke_index > 0
        for segment_index, (start, end) in enumerate(zip(points, points[1:]), start=1):
            sample_started = perf_counter_ns()
            (x0, y0, x1, y1), point_count = clone_segment_geometry(
                start,
                end,
                BRUSH_RADIUS,
                width,
                height,
            )

            kernel_started = perf_counter_ns()
            touched = paint_aligned_clone(
                repair.paint_rgba,
                app.state.original_rgb,
                source_rect,
                source_anchor,
                destination_anchor,
                start,
                end,
                BRUSH_RADIUS,
                hole_mask=hole,
            )
            kernel_ms = _elapsed_ms(kernel_started)

            update_started = perf_counter_ns()
            app._layer_cache[repair.id] = repair.paint_rgba
            app._composite_cache_rgb = None
            image_update_ms = _elapsed_ms(update_started)

            if measured:
                kernel_times.append(kernel_ms)
                image_update_times.append(image_update_ms)
                sample_total_times.append(_elapsed_ms(sample_started))
                roi_pixels.append(float(max(0, x1 - x0) * max(0, y1 - y0)))
                interpolated_points.append(float(point_count))
                touched_pixels.append(float(touched))

            if segment_index % REFRESH_EVERY_SEGMENTS == 0:
                display_started = perf_counter_ns()
                composite_started = perf_counter_ns()
                preview = app._composite_preview().copy()
                composite_ms = _elapsed_ms(composite_started)

                transform_started = perf_counter_ns()
                Image.fromarray(preview).transform(
                    VIEWPORT_SIZE,
                    Image.Transform.AFFINE,
                    inverse,
                    resample=Image.Resampling.BILINEAR,
                    fillcolor=(32, 32, 32),
                )
                viewport_transform_ms = _elapsed_ms(transform_started)

                if measured:
                    composite_times.append(composite_ms)
                    viewport_transform_times.append(viewport_transform_ms)
                    display_prepare_times.append(_elapsed_ms(display_started))

        if measured:
            stroke_times.append(_elapsed_ms(stroke_started))

    return {
        "document": {"width": width, "height": height},
        "viewport": {"width": VIEWPORT_SIZE[0], "height": VIEWPORT_SIZE[1]},
        "brush_radius": BRUSH_RADIUS,
        "measured_strokes": MEASURED_STROKES,
        "segments_per_stroke": SEGMENTS_PER_STROKE,
        "refresh_every_segments": REFRESH_EVERY_SEGMENTS,
        "kernel_ms": summarize_values(kernel_times),
        "image_update_ms": summarize_values(image_update_times),
        "sample_total_ms": summarize_values(sample_total_times),
        "roi_pixels": summarize_values(roi_pixels),
        "interpolated_point_count": summarize_values(interpolated_points),
        "touched_pixels": summarize_values(touched_pixels),
        "composite_ms": summarize_values(composite_times),
        "viewport_transform_ms": summarize_values(viewport_transform_times),
        "display_prepare_ms": summarize_values(display_prepare_times),
        "stroke_processing_ms": summarize_values(stroke_times),
        "photo_image_ms": None,
        "canvas_update_ms": None,
        "pointer_interval_ms": None,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Measure the existing Python prototype path without a Tk display.")
    parser.add_argument("--output", type=Path, help="Optional JSON output path.")
    args = parser.parse_args()

    result = {
        "environment": {
            "platform": platform.platform(),
            "python": sys.version.split()[0],
            "opencv": cv2.__version__,
            "numpy": np.__version__,
            "pillow": Image.__version__,
            "processor": platform.processor() or "not reported",
            "cpu_count": os.cpu_count(),
            "display": "headless; ImageTk.PhotoImage and Tk Canvas update not measured",
        },
        "method": "same paint_aligned_clone kernel and CutoutSpikeApp._composite_preview path; deterministic synthetic RGB input",
        "cases": [_run_case(width, height) for width, height in CASES],
    }
    serialized = json.dumps(result, indent=2, sort_keys=True)
    print(serialized)
    if args.output is not None:
        args.output.write_text(serialized + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
