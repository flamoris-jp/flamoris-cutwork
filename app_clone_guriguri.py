from __future__ import annotations

import tkinter as tk
from tkinter import ttk

import numpy as np
from PIL import Image, ImageTk

from app_polygon_guriguri import PolygonGuriguriApp
from clone_brush import normalize_rect, paint_aligned_clone, rect_center
from image_ops import union_part_masks


class CloneGuriguriApp(PolygonGuriguriApp):
    """Polygon Guriguri plus a simple aligned clone brush for filling cutout holes.

    Workflow:
    1. Cut a Part with Polygon Guriguri.
    2. Hide the Part to inspect the Base hole.
    3. Drag a green source rectangle over clean original pixels.
    4. Paint into the hole. Source pixels move with the destination stroke.
    """

    def __init__(self, root: tk.Tk) -> None:
        self.clone_source_rect: tuple[int, int, int, int] | None = None
        self.clone_source_start: tuple[int, int] | None = None
        self.clone_source_drag_end: tuple[int, int] | None = None
        self.clone_source_anchor: tuple[int, int] | None = None
        self.clone_destination_anchor: tuple[int, int] | None = None
        self.clone_last_destination: tuple[int, int] | None = None
        self.clone_source_cursor: tuple[int, int] | None = None
        self.clone_undo_stack: list[tuple[str, np.ndarray]] = []
        self.clone_stroke_hole_mask: np.ndarray | None = None
        self.clone_stroke_touched = 0
        self.clone_hole_only = tk.BooleanVar(master=root, value=True)
        self.show_original_preview = tk.BooleanVar(master=root, value=False)
        super().__init__(root)
        self.root.title("FLAMORIS Parts Guriguri + Clone Repair Spike")

    def _build_ui(self) -> None:
        super()._build_ui()
        bar = ttk.Frame(self.root, padding=(8, 0, 8, 7))
        bar.pack(side=tk.TOP, fill=tk.X, before=self.canvas.master)
        ttk.Radiobutton(
            bar,
            text="🟩 Clone Source",
            value="clone-source",
            variable=self.mode,
            command=self.on_mode_changed,
        ).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Radiobutton(
            bar,
            text="🖌 Clone Paint",
            value="clone-paint",
            variable=self.mode,
            command=self.on_mode_changed,
        ).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Checkbutton(bar, text="Hole Only", variable=self.clone_hole_only).pack(side=tk.LEFT, padx=(0, 10))
        ttk.Checkbutton(
            bar,
            text="Original Preview",
            variable=self.show_original_preview,
            command=self._toggle_original_preview,
        ).pack(side=tk.LEFT, padx=(0, 10))
        ttk.Button(bar, text="Undo Clone Stroke", command=self.undo_clone_stroke).pack(side=tk.LEFT, padx=(0, 10))
        ttk.Label(
            bar,
            text="Drag a clean green source box, then paint. Source follows the stroke direction; original pixels are copied unchanged.",
            foreground="#555555",
        ).pack(side=tk.LEFT)

    def _toggle_original_preview(self) -> None:
        if self.show_original_preview.get():
            self.status.set(
                "Original Preview: showing immutable source artwork. Existing layers stay unchanged; selections still overlay the original."
            )
        else:
            self.status.set("Original Preview off: showing the current layer composite again.")
        self.refresh_preview()

    def on_mode_changed(self) -> None:
        mode = self.mode.get()
        if mode == "clone-source":
            self.polygon_points.clear()
            self.clone_source_start = None
            self.clone_source_drag_end = None
            self.status.set("Clone Source: drag a rectangle over clean original pixels. Choose a direction-friendly texture patch.")
            self.refresh_preview()
            return
        if mode == "clone-paint":
            self.polygon_points.clear()
            if self.clone_source_rect is None:
                self.status.set("Clone Paint: choose Clone Source first and drag a green source rectangle.")
            else:
                self.status.set("Clone Paint: drag inside the cutout hole. The source cursor moves in the same direction. Hole Only is on by default.")
            self.refresh_preview()
            return
        super().on_mode_changed()

    def open_image(self) -> None:
        self._reset_clone_state()
        self.show_original_preview.set(False)
        super().open_image()
        if self.state.original_rgb is not None:
            self.root.title(
                f"FLAMORIS Parts Guriguri + Clone Repair Spike - {self.source_path.name if self.source_path else 'image'}"
            )

    def on_pointer_down(self, event: tk.Event) -> None:
        mode = self.mode.get()
        if mode not in ("clone-source", "clone-paint"):
            super().on_pointer_down(event)
            return
        if self.state.original_rgb is None:
            return
        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return

        if mode == "clone-source":
            self.clone_source_start = point
            self.clone_source_drag_end = point
            self.status.set("Clone Source: drag to size the green source rectangle.")
            self.refresh_preview()
            return

        if self.clone_source_rect is None:
            self.status.set("Choose Clone Source and drag a source rectangle first.")
            return

        repair = self._ensure_repair_layer()
        if repair.paint_rgba is None:
            return
        self.clone_undo_stack.append((repair.id, repair.paint_rgba.copy()))
        if len(self.clone_undo_stack) > 20:
            self.clone_undo_stack.pop(0)

        # Hole geometry cannot change during one clone stroke. Computing this once
        # avoids a full-image union on every pointer-motion segment.
        self.clone_stroke_hole_mask = (
            union_part_masks(self.state.layers, self.state.original_rgb.shape[:2])
            if self.clone_hole_only.get()
            else None
        )
        self.clone_stroke_touched = 0

        # A repair layer without local edits is already the rendered RGBA layer.
        # Keep the cache pointing at that live buffer instead of rebuilding/copying
        # the full image for every tiny clone segment.
        if not repair.local_edits:
            self._layer_cache[repair.id] = repair.paint_rgba

        self.clone_source_anchor = rect_center(self.clone_source_rect)
        self.clone_destination_anchor = point
        self.clone_last_destination = point
        self.clone_source_cursor = self.clone_source_anchor
        self._paint_clone_segment(point, point)

    def on_pointer_drag(self, event: tk.Event) -> None:
        mode = self.mode.get()
        if mode not in ("clone-source", "clone-paint"):
            super().on_pointer_drag(event)
            return
        if self.state.original_rgb is None:
            return
        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return

        if mode == "clone-source":
            if self.clone_source_start is not None:
                self.clone_source_drag_end = point
                self.refresh_preview()
            return

        if self.clone_last_destination is None or self.clone_destination_anchor is None or self.clone_source_anchor is None:
            return
        self._paint_clone_segment(self.clone_last_destination, point)
        self.clone_last_destination = point
        self.clone_source_cursor = (
            self.clone_source_anchor[0] + point[0] - self.clone_destination_anchor[0],
            self.clone_source_anchor[1] + point[1] - self.clone_destination_anchor[1],
        )

    def on_pointer_up(self, event: tk.Event) -> None:
        mode = self.mode.get()
        if mode not in ("clone-source", "clone-paint"):
            super().on_pointer_up(event)
            return

        if mode == "clone-source":
            if self.state.original_rgb is None or self.clone_source_start is None:
                return
            point = self._canvas_to_image(event.x, event.y) or self.clone_source_drag_end or self.clone_source_start
            height, width = self.state.original_rgb.shape[:2]
            rect = normalize_rect(self.clone_source_start, point, width, height)
            self.clone_source_start = None
            self.clone_source_drag_end = None
            if rect[2] - rect[0] < 3 or rect[3] - rect[1] < 3:
                self.status.set("Clone Source rectangle is too small. Drag a larger clean patch.")
                self.refresh_preview()
                return
            self.clone_source_rect = rect
            self.clone_source_anchor = rect_center(rect)
            self.clone_source_cursor = self.clone_source_anchor
            self.status.set(
                f"Clone Source ready: {rect[2] - rect[0]}×{rect[3] - rect[1]} px. Switch to Clone Paint and stroke along the texture direction."
            )
            self.refresh_preview()
            return

        touched = self.clone_stroke_touched
        self.clone_stroke_hole_mask = None
        self.clone_stroke_touched = 0
        self.clone_destination_anchor = None
        self.clone_last_destination = None
        self.clone_source_anchor = rect_center(self.clone_source_rect) if self.clone_source_rect is not None else None
        self.clone_source_cursor = self.clone_source_anchor
        self.status.set(
            f"Clone stroke ready: copied {touched:,} destination px. Paint another direction, choose a new source patch, Blur/Smudge, or Undo Clone Stroke."
        )
        self.refresh_preview()

    def _ensure_repair_layer(self):
        active = self.state.active_layer
        if active is not None and active.kind == "repair":
            return active
        repair = next((layer for layer in self.state.layers if layer.kind == "repair"), None)
        if repair is None:
            repair = self.state.add_repair(self.state.next_name("repair"))
        else:
            self.state.select(repair.id)
        self._invalidate_all()
        self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
        return repair

    def _paint_clone_segment(self, start: tuple[int, int], end: tuple[int, int]) -> None:
        original = self.state.original_rgb
        active = self.state.active_layer
        if (
            original is None
            or active is None
            or active.kind != "repair"
            or active.paint_rgba is None
            or self.clone_source_rect is None
            or self.clone_source_anchor is None
            or self.clone_destination_anchor is None
        ):
            return

        touched = paint_aligned_clone(
            active.paint_rgba,
            original,
            self.clone_source_rect,
            self.clone_source_anchor,
            self.clone_destination_anchor,
            start,
            end,
            max(1, self.brush_size.get() // 2),
            hole_mask=self.clone_stroke_hole_mask,
        )
        self.clone_stroke_touched += touched

        if active.local_edits:
            # Local edits must be replayed over the changed paint buffer.
            self._invalidate_layer(active.id)
        else:
            # Keep the rendered-layer cache alive. The clone kernel mutates this
            # same RGBA buffer in place, so only the final composite is stale.
            self._layer_cache[active.id] = active.paint_rgba
            self._composite_cache_rgb = None

        # Full Tk/PIL preview composition is much more expensive than the local
        # clone kernel. Cap redraw requests around 30 fps and let multiple motion
        # events accumulate between frames; line interpolation still connects the
        # last and current image-space points so no stroke gaps are introduced.
        self._schedule_refresh(delay_ms=33)

    def undo_clone_stroke(self) -> None:
        if not self.clone_undo_stack:
            self.status.set("No clone stroke to undo.")
            return
        layer_id, snapshot = self.clone_undo_stack.pop()
        layer = next((item for item in self.state.layers if item.id == layer_id and item.kind == "repair"), None)
        if layer is None:
            self.status.set("The repair layer for that clone undo no longer exists.")
            return
        layer.paint_rgba = snapshot
        self.state.select(layer.id)
        self._invalidate_layer(layer.id)
        self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
        self.status.set("Undid the last clone stroke.")
        self.refresh_preview()

    def _reset_clone_state(self) -> None:
        self.clone_source_rect = None
        self.clone_source_start = None
        self.clone_source_drag_end = None
        self.clone_source_anchor = None
        self.clone_destination_anchor = None
        self.clone_last_destination = None
        self.clone_source_cursor = None
        self.clone_stroke_hole_mask = None
        self.clone_stroke_touched = 0
        self.clone_undo_stack.clear()

    def _refresh_original_preview(self) -> None:
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
        preview = original.copy()
        if self.state.pending_part_mask is not None:
            selected = self.state.pending_part_mask > 0
            magenta = np.array([235, 70, 170], dtype=np.float32)
            preview[selected] = (preview[selected] * 0.68 + magenta * 0.32).astype(np.uint8)

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

    def refresh_preview(self) -> None:
        if self.show_original_preview.get() and self.state.original_rgb is not None:
            self._refresh_original_preview()
        else:
            super().refresh_preview()
        if self.state.original_rgb is None:
            return

        rect = self.clone_source_rect
        if self.clone_source_start is not None and self.clone_source_drag_end is not None:
            height, width = self.state.original_rgb.shape[:2]
            rect = normalize_rect(self.clone_source_start, self.clone_source_drag_end, width, height)
        if rect is not None:
            x0, y0 = self._image_to_canvas((rect[0], rect[1]))
            x1, y1 = self._image_to_canvas((max(rect[0], rect[2] - 1), max(rect[1], rect[3] - 1)))
            self.canvas.create_rectangle(
                x0, y0, x1, y1,
                outline="#38d05f",
                width=3,
                tags="clone-source-rect",
            )

        if self.clone_source_cursor is not None:
            x, y = self._image_to_canvas(self.clone_source_cursor)
            radius = max(4, int(self.brush_size.get() * self.state.viewport.zoom / 2))
            self.canvas.create_oval(
                x - radius, y - radius, x + radius, y + radius,
                outline="#38d05f",
                width=2,
                dash=(4, 3),
                tags="clone-source-cursor",
            )


def main() -> None:
    root = tk.Tk()
    CloneGuriguriApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
