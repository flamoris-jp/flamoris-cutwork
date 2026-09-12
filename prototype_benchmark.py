from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from pathlib import Path
from time import perf_counter_ns
from typing import Any


BENCHMARK_ENV = "CUTWORK_BENCHMARK"
BENCHMARK_OUTPUT_ENV = "CUTWORK_BENCHMARK_OUTPUT"


def _enabled_from_environment(value: str | None) -> bool:
    return value is not None and value.strip().lower() in {"1", "true", "yes", "on"}


def summarize_values(values: list[float]) -> dict[str, float | int | None]:
    if not values:
        return {"count": 0, "average": None, "minimum": None, "maximum": None}
    return {
        "count": len(values),
        "average": sum(values) / len(values),
        "minimum": min(values),
        "maximum": max(values),
    }


class StageTimer:
    """Small opt-in timer that avoids clock reads while instrumentation is off."""

    def __init__(self, enabled: bool) -> None:
        self.enabled = enabled
        self._started_ns = perf_counter_ns() if enabled else 0
        self._last_ns = self._started_ns

    def mark_ms(self) -> float:
        if not self.enabled:
            return 0.0
        now = perf_counter_ns()
        elapsed = (now - self._last_ns) / 1_000_000.0
        self._last_ns = now
        return elapsed

    def total_ms(self) -> float:
        if not self.enabled:
            return 0.0
        return (perf_counter_ns() - self._started_ns) / 1_000_000.0


@dataclass
class PrototypeBenchmark:
    """Collects one Clone stroke without changing the prototype's behavior."""

    enabled: bool = False
    output_path: Path | None = None
    _stroke: dict[str, Any] | None = field(default=None, init=False)
    _last_pointer_ns: int | None = field(default=None, init=False)

    @classmethod
    def from_environment(cls) -> "PrototypeBenchmark":
        enabled = _enabled_from_environment(os.environ.get(BENCHMARK_ENV))
        output = os.environ.get(BENCHMARK_OUTPUT_ENV)
        return cls(enabled=enabled, output_path=Path(output) if enabled and output else None)

    def begin_clone_stroke(
        self,
        *,
        document_width: int,
        document_height: int,
        brush_radius: int,
        hole_only: bool,
    ) -> None:
        if not self.enabled:
            return
        self._last_pointer_ns = None
        self._stroke = {
            "kind": "clone_stroke",
            "document": {"width": document_width, "height": document_height},
            "brush_radius": brush_radius,
            "hole_only": hole_only,
            "started_ns": perf_counter_ns(),
            "setup_ms": [],
            "pointer_interval_ms": [],
            "segments": [],
            "refreshes": [],
        }

    def cancel_stroke(self) -> None:
        self._stroke = None
        self._last_pointer_ns = None

    def record_pointer_sample(self, now_ns: int | None = None) -> float | None:
        if not self.enabled or self._stroke is None:
            return None
        current = perf_counter_ns() if now_ns is None else now_ns
        interval = None if self._last_pointer_ns is None else (current - self._last_pointer_ns) / 1_000_000.0
        self._last_pointer_ns = current
        if interval is not None:
            self._stroke["pointer_interval_ms"].append(interval)
        return interval

    def record_setup(self, elapsed_ms: float) -> None:
        if self.enabled and self._stroke is not None:
            self._stroke["setup_ms"].append(elapsed_ms)

    def record_clone_segment(
        self,
        *,
        pointer_interval_ms: float | None,
        interpolated_point_count: int,
        roi_width: int,
        roi_height: int,
        touched_pixels: int,
        kernel_ms: float,
        image_update_ms: float,
        scheduling_ms: float,
        total_ms: float,
    ) -> None:
        if not self.enabled or self._stroke is None:
            return
        self._stroke["segments"].append(
            {
                "pointer_interval_ms": pointer_interval_ms,
                "interpolated_point_count": interpolated_point_count,
                "roi_width": roi_width,
                "roi_height": roi_height,
                "roi_pixels": roi_width * roi_height,
                "touched_pixels": touched_pixels,
                "kernel_ms": kernel_ms,
                "image_update_ms": image_update_ms,
                "scheduling_ms": scheduling_ms,
                "total_ms": total_ms,
            }
        )

    def record_visible_refresh(
        self,
        *,
        composite_ms: float,
        overlay_ms: float,
        viewport_transform_ms: float,
        photo_image_ms: float,
        canvas_update_ms: float,
        total_ms: float,
    ) -> None:
        if not self.enabled or self._stroke is None:
            return
        self._stroke["refreshes"].append(
            {
                "composite_ms": composite_ms,
                "overlay_ms": overlay_ms,
                "viewport_transform_ms": viewport_transform_ms,
                "photo_image_ms": photo_image_ms,
                "canvas_update_ms": canvas_update_ms,
                "total_ms": total_ms,
            }
        )

    def end_stroke(self) -> dict[str, Any] | None:
        if not self.enabled or self._stroke is None:
            return None
        stroke = self._stroke
        stroke["stroke_wall_ms"] = (perf_counter_ns() - stroke.pop("started_ns")) / 1_000_000.0
        summary = self._summarize(stroke)
        self._emit(summary)
        self._stroke = None
        self._last_pointer_ns = None
        return summary

    @staticmethod
    def _summarize(stroke: dict[str, Any]) -> dict[str, Any]:
        segments = stroke.pop("segments")
        refreshes = stroke.pop("refreshes")

        def segment_values(key: str) -> list[float]:
            return [float(segment[key]) for segment in segments]

        def refresh_values(key: str) -> list[float]:
            return [float(refresh[key]) for refresh in refreshes]

        recorded_processing_ms = (
            sum(float(value) for value in stroke["setup_ms"])
            + sum(segment_values("total_ms"))
            + sum(refresh_values("total_ms"))
        )

        return {
            **stroke,
            "segment_count": len(segments),
            "refresh_count": len(refreshes),
            "recorded_processing_ms": recorded_processing_ms,
            "interpolated_point_count": sum(int(item["interpolated_point_count"]) for item in segments),
            "touched_pixels": sum(int(item["touched_pixels"]) for item in segments),
            "setup_ms": summarize_values([float(value) for value in stroke["setup_ms"]]),
            "pointer_interval_ms": summarize_values(stroke["pointer_interval_ms"]),
            "roi_pixels": summarize_values(segment_values("roi_pixels")),
            "kernel_ms": summarize_values(segment_values("kernel_ms")),
            "image_update_ms": summarize_values(segment_values("image_update_ms")),
            "sample_total_ms": summarize_values(segment_values("total_ms")),
            "composite_ms": summarize_values(refresh_values("composite_ms")),
            "viewport_transform_ms": summarize_values(refresh_values("viewport_transform_ms")),
            "photo_image_ms": summarize_values(refresh_values("photo_image_ms")),
            "canvas_update_ms": summarize_values(refresh_values("canvas_update_ms")),
            "visible_refresh_ms": summarize_values(refresh_values("total_ms")),
        }

    def _emit(self, summary: dict[str, Any]) -> None:
        line = json.dumps(summary, ensure_ascii=False, sort_keys=True)
        print(f"[cutwork-benchmark] {line}", flush=True)
        if self.output_path is not None:
            self.output_path.parent.mkdir(parents=True, exist_ok=True)
            with self.output_path.open("a", encoding="utf-8") as stream:
                stream.write(line + "\n")
