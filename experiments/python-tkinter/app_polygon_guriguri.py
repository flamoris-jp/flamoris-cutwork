from __future__ import annotations

import tkinter as tk
from tkinter import ttk

import numpy as np
from PIL import Image, ImageTk

from app import CutoutSpikeApp
from boundary_guriguri import build_boundary_map
from image_ops import active_mask, polygon_mask
from polygon_guriguri import PolygonBoundaryShrinker, target_keep_pixels_for_step


class PolygonGuriguriApp(CutoutSpikeApp):
    """Loose polygon first, then wheel-controlled outside-in boundary fitting.

    The human provides semantic intent by drawing a deliberately loose polygon.
    The polygon is a hard search fence. The software only peels pixels from the
    polygon boundary inward, preferring weak visual boundaries and resisting
    strong line-art/colour transitions.
    """

    CTRL_MASK = 0x0004

    def __init__(self, root: tk.Tk) -> None:
        self.boundary_map: np.ndarray | None = None
        self.loose_polygon_mask: np.ndarray | None = None
        self.shrinker: PolygonBoundaryShrinker | None = None
        self.guriguri_step = 0
        self.guriguri_previous_pending: np.ndarray | None = None
        super().__init__(root)
        self.root.title("FLAMORIS Polygon Guriguri Spike")

    def _build_ui(self) -> None:
        super()._build_ui()

        bar = ttk.Frame(self.root, padding=(8, 0, 8, 7))
        bar.pack(side=tk.TOP, fill=tk.X, before=self.canvas.master)
        ttk.Radiobutton(
            bar,
            text="🌀 Polygon Guriguri",
            value="polygon-guriguri",
            variable=self.mode,
            command=self.on_mode_changed,
        ).pack(side=tk.LEFT, padx=(0, 10))
        ttk.Label(
            bar,
            text=("Draw a loose polygon around the intended part, Enter/double-click to close, "
                  "then wheel up to shrink inward / wheel down to restore. Ctrl+wheel zooms."),
            foreground="#555555",
        ).pack(side=tk.LEFT)

    def _bind_events(self) -> None:
        super()._bind_events()
        self.root.bind("<Escape>", self.cancel_polygon_guriguri_or_base)
        self.canvas.bind("<Button-4>", lambda event: self._on_button_wheel(event, +1))
        self.canvas.bind("<Button-5>", lambda event: self._on_button_wheel(event, -1))

    def on_mode_changed(self) -> None:
        if self.mode.get() == "polygon-guriguri":
            self.cancel_polygon(silent=True)
            self._clear_shrink_session(keep_pending=True)
            self.status.set(
                "Polygon Guriguri: loosely surround the intended part. Enter/double-click closes the fence; "
                "wheel up shrinks inward and wheel down restores."
            )
            return

        self._clear_shrink_session(keep_pending=True)
        super().on_mode_changed()

    def open_image(self) -> None:
        self._clear_shrink_session(keep_pending=False)
        self.boundary_map = None
        super().open_image()
        if self.state.original_rgb is not None:
            self.root.title(
                f"FLAMORIS Polygon Guriguri Spike - {self.source_path.name if self.source_path else 'image'}"
            )
            self.status.set("Building visual boundary map...")
            self.root.update_idletasks()
            self.boundary_map = build_boundary_map(self.state.original_rgb)
            self.status.set(
                "Image loaded. Choose Polygon Guriguri, draw a loose fence, then use the mouse wheel to fit inward."
            )

    def on_pointer_down(self, event: tk.Event) -> None:
        if self.mode.get() != "polygon-guriguri":
            super().on_pointer_down(event)
            return
        if self.state.original_rgb is None:
            return

        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return

        # Clicking after a completed shrink starts a fresh loose polygon.
        if self.shrinker is not None and not self.polygon_points:
            self._clear_shrink_session(keep_pending=False)

        if not self.polygon_points:
            self.guriguri_previous_pending = (
                None if self.state.pending_part_mask is None else self.state.pending_part_mask.copy()
            )

        if not self.polygon_points or point != self.polygon_points[-1]:
            self.polygon_points.append(point)
            self.status.set(
                f"Loose polygon: {len(self.polygon_points)} points. Keep it rough; Enter/double-click closes the fence."
            )
            self.refresh_preview()

    def on_pointer_drag(self, event: tk.Event) -> None:
        if self.mode.get() != "polygon-guriguri":
            super().on_pointer_drag(event)
        # Polygon Guriguri deliberately ignores left-drag.

    def on_pointer_up(self, event: tk.Event) -> None:
        if self.mode.get() != "polygon-guriguri":
            super().on_pointer_up(event)

    def finalize_polygon(self, _event: tk.Event | None = None) -> str | None:
        if self.mode.get() != "polygon-guriguri":
            return super().finalize_polygon(_event)
        if self.state.original_rgb is None:
            return None
        if len(self.polygon_points) < 3:
            self.status.set("Loose Polygon Guriguri fence needs at least three points.")
            return "break"

        mask = polygon_mask(self.state.original_rgb.shape[:2], self.polygon_points)
        pixels = int(np.count_nonzero(mask))
        if pixels < 16:
            self.status.set("Loose polygon is too small. Draw a larger fence.")
            return "break"
        if self.boundary_map is None:
            self.boundary_map = build_boundary_map(self.state.original_rgb)

        self.loose_polygon_mask = mask.copy()
        self.shrinker = PolygonBoundaryShrinker(self.boundary_map, self.loose_polygon_mask)
        self.guriguri_step = 0
        self.state.pending_part_mask = self.loose_polygon_mask.copy()
        self.polygon_points.clear()
        self.status.set(
            f"Loose fence ready: {pixels:,} px. Wheel up = shrink inward, wheel down = restore. "
            "Create Part Layer when the boundary looks right."
        )
        self.refresh_preview()
        return "break"

    def on_mouse_wheel(self, event: tk.Event) -> None:
        if self.mode.get() != "polygon-guriguri" or self._ctrl_pressed(event):
            super().on_mouse_wheel(event)
            return

        delta = float(getattr(event, "delta", 0.0))
        if delta == 0:
            return
        steps = delta / 120.0 if abs(delta) >= 120.0 else (1.0 if delta > 0 else -1.0)
        self._adjust_guriguri(steps)

    def _on_button_wheel(self, event: tk.Event, direction: int) -> None:
        if self.mode.get() == "polygon-guriguri" and not self._ctrl_pressed(event):
            self._adjust_guriguri(float(direction))
            return
        self._zoom_at(event.x, event.y, 1.15 if direction > 0 else 1 / 1.15)

    def _ctrl_pressed(self, event: tk.Event) -> bool:
        return bool(int(getattr(event, "state", 0)) & self.CTRL_MASK)

    def _adjust_guriguri(self, wheel_steps: float) -> None:
        if self.shrinker is None or self.loose_polygon_mask is None:
            self.status.set("Polygon Guriguri: draw and finalize a loose polygon first, then use the wheel.")
            return

        direction = 1 if wheel_steps > 0 else -1
        notch_count = max(1, int(round(abs(wheel_steps))))
        new_step = max(0, self.guriguri_step + direction * notch_count)
        if new_step == self.guriguri_step:
            return

        self.guriguri_step = new_step
        target_keep = target_keep_pixels_for_step(self.guriguri_step, self.shrinker.polygon_count)
        self.state.pending_part_mask = self.shrinker.mask_for_keep_count(target_keep)
        actual = int(np.count_nonzero(self.state.pending_part_mask))
        self.status.set(
            f"Polygon Guriguri: step {self.guriguri_step}, {actual:,}/{self.shrinker.polygon_count:,} px remain. "
            "Wheel up = shrink, wheel down = restore. Ctrl+wheel = zoom."
        )
        self._schedule_refresh(delay_ms=8)

    def cancel_polygon_guriguri_or_base(self, event: tk.Event | None = None) -> str | None:
        if self.mode.get() != "polygon-guriguri":
            return super().cancel_polygon(event)

        if self.polygon_points:
            self.polygon_points.clear()
            self.status.set("Loose polygon cancelled.")
            self.refresh_preview()
            return "break"

        if self.shrinker is None:
            return None

        self.state.pending_part_mask = (
            None if self.guriguri_previous_pending is None else self.guriguri_previous_pending.copy()
        )
        self._clear_shrink_session(keep_pending=True)
        self.status.set("Polygon Guriguri cancelled; previous pending selection restored.")
        self.refresh_preview()
        return "break"

    def _clear_shrink_session(self, *, keep_pending: bool) -> None:
        self.loose_polygon_mask = None
        self.shrinker = None
        self.guriguri_step = 0
        self.guriguri_previous_pending = None
        self.polygon_points.clear()
        if not keep_pending and hasattr(self, "state"):
            self.state.pending_part_mask = None

    def refresh_preview(self) -> None:
        """Render pending selection in magenta, preserving the base spike UX."""
        original = self.state.original_rgb
        if original is None:
            self.canvas.delete("all")
            return

        if self.state.viewport.fit_pending and self.canvas.winfo_width() > 2:
            canvas_w, canvas_h = max(1, self.canvas.winfo_width()), max(1, self.canvas.winfo_height())
            image_h, image_w = original.shape[:2]
            zoom = max(0.02, min(canvas_w / image_w, canvas_h / image_h))
            self.state.viewport.zoom = zoom
            self.state.viewport.offset_x = (canvas_w - image_w * zoom) / 2
            self.state.viewport.offset_y = (canvas_h - image_h * zoom) / 2
            self.state.viewport.fit_pending = False

        canvas_w, canvas_h = max(1, self.canvas.winfo_width()), max(1, self.canvas.winfo_height())
        preview = self._composite_preview().copy()
        magenta = np.array([235, 70, 170], dtype=np.float32)

        if self.state.pending_part_mask is not None:
            selected = self.state.pending_part_mask > 0
            preview[selected] = (preview[selected] * 0.68 + magenta * 0.32).astype(np.uint8)

        active = self.state.active_layer
        if self.layer_panel.mask_overlay.get() and active is not None:
            mask_alpha = (
                active_mask(original, self.state.layers, active)
                if active.kind == "base"
                else self._render_cached(active)[:, :, 3]
            )
            selected = mask_alpha > 0
            preview[selected] = (preview[selected] * 0.72 + magenta * 0.28).astype(np.uint8)

        viewport = self.state.viewport
        inverse = (
            1.0 / viewport.zoom,
            0.0,
            -viewport.offset_x / viewport.zoom,
            0.0,
            1.0 / viewport.zoom,
            -viewport.offset_y / viewport.zoom,
        )
        displayed = Image.fromarray(preview).transform(
            (canvas_w, canvas_h),
            Image.Transform.AFFINE,
            inverse,
            resample=Image.Resampling.BILINEAR,
            fillcolor=(32, 32, 32),
        )
        self.preview_photo = ImageTk.PhotoImage(displayed)
        self.canvas.delete("all")
        self.canvas.create_image(0, 0, anchor=tk.NW, image=self.preview_photo, tags="image")
        self._draw_polygon_preview()


def main() -> None:
    root = tk.Tk()
    PolygonGuriguriApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
