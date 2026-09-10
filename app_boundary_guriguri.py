from __future__ import annotations

import tkinter as tk
from tkinter import ttk

import numpy as np
from PIL import Image, ImageTk

from app import CutoutSpikeApp
from boundary_guriguri import (
    BoundaryPriorityGrower,
    build_boundary_map,
    snap_seed_to_basin,
    target_pixels_for_step,
)
from image_ops import active_mask


class BoundaryGuriguriApp(CutoutSpikeApp):
    """Classical-cutout spike with wheel-controlled boundary-priority growth.

    A click only says "include something around here". Boundary evidence chooses
    where expansion goes, while the mouse wheel controls how much connected area
    is revealed. No semantic meaning is assigned to the click.
    """

    CTRL_MASK = 0x0004

    def __init__(self, root: tk.Tk) -> None:
        self.boundary_map: np.ndarray | None = None
        self.guriguri_click: tuple[int, int] | None = None
        self.guriguri_seed: tuple[int, int] | None = None
        self.guriguri_step = 0
        self.guriguri_grower: BoundaryPriorityGrower | None = None
        self.guriguri_previous_pending: np.ndarray | None = None
        super().__init__(root)
        self.root.title("FLAMORIS Boundary Guriguri Spike")

    def _build_ui(self) -> None:
        super()._build_ui()

        bar = ttk.Frame(self.root, padding=(8, 0, 8, 7))
        bar.pack(side=tk.TOP, fill=tk.X, before=self.canvas.master)
        ttk.Radiobutton(
            bar,
            text="🌀 Boundary Guriguri",
            value="boundary-guriguri",
            variable=self.mode,
            command=self.on_mode_changed,
        ).pack(side=tk.LEFT, padx=(0, 10))
        ttk.Label(
            bar,
            text=("Click anywhere in the intended part; wheel reveals more/less area, "
                  "preferring weak boundaries first. Ctrl+wheel keeps normal zoom."),
            foreground="#555555",
        ).pack(side=tk.LEFT)

    def _bind_events(self) -> None:
        super()._bind_events()
        self.root.bind("<Escape>", self.cancel_guriguri_or_polygon)
        self.canvas.bind("<Button-4>", lambda event: self._on_button_wheel(event, +1))
        self.canvas.bind("<Button-5>", lambda event: self._on_button_wheel(event, -1))

    def on_mode_changed(self) -> None:
        if self.mode.get() == "boundary-guriguri":
            self.cancel_polygon(silent=True)
            self._clear_guriguri_session(keep_pending=True)
            self.status.set(
                "Boundary Guriguri: click anywhere in the intended part, then wheel to reveal more/less connected area. "
                "Ctrl+wheel zooms."
            )
            return

        self._clear_guriguri_session(keep_pending=True)
        super().on_mode_changed()

    def open_image(self) -> None:
        self._clear_guriguri_session(keep_pending=True)
        self.boundary_map = None
        super().open_image()
        if self.state.original_rgb is not None:
            self.root.title(
                f"FLAMORIS Boundary Guriguri Spike - {self.source_path.name if self.source_path else 'image'}"
            )
            self.status.set("Building visual boundary map...")
            self.root.update_idletasks()
            self.boundary_map = build_boundary_map(self.state.original_rgb)
            self.status.set(
                "Image loaded. Boundary map ready. Click anywhere in an intended part and use the wheel. "
                "Part Polygon remains the exact manual fallback."
            )

    def on_pointer_down(self, event: tk.Event) -> None:
        if self.mode.get() != "boundary-guriguri":
            super().on_pointer_down(event)
            return
        if self.state.original_rgb is None or self.boundary_map is None:
            return

        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return

        self.guriguri_previous_pending = (
            None if self.state.pending_part_mask is None else self.state.pending_part_mask.copy()
        )
        self.guriguri_click = point
        self.guriguri_seed = snap_seed_to_basin(self.boundary_map, point)
        self.guriguri_step = 0
        self.guriguri_grower = BoundaryPriorityGrower(self.boundary_map, self.guriguri_seed)
        self._update_guriguri_mask(refresh_now=True)

    def on_pointer_drag(self, event: tk.Event) -> None:
        if self.mode.get() != "boundary-guriguri":
            super().on_pointer_drag(event)
        # Boundary Guriguri deliberately ignores left-drag. The wheel is the control.

    def on_pointer_up(self, event: tk.Event) -> None:
        if self.mode.get() != "boundary-guriguri":
            super().on_pointer_up(event)
            return
        if self.guriguri_seed is not None and self.state.pending_part_mask is not None:
            pixels = int(np.count_nonzero(self.state.pending_part_mask))
            self.status.set(
                f"Boundary Guriguri ready: step {self.guriguri_step}, {pixels:,} px. "
                "Wheel to adjust, click somewhere else, or Create Part Layer."
            )

    def on_mouse_wheel(self, event: tk.Event) -> None:
        if self.mode.get() != "boundary-guriguri" or self._ctrl_pressed(event):
            super().on_mouse_wheel(event)
            return

        delta = float(getattr(event, "delta", 0.0))
        if delta == 0:
            return
        steps = delta / 120.0 if abs(delta) >= 120.0 else (1.0 if delta > 0 else -1.0)
        self._adjust_guriguri(steps)

    def _on_button_wheel(self, event: tk.Event, direction: int) -> None:
        if self.mode.get() == "boundary-guriguri" and not self._ctrl_pressed(event):
            self._adjust_guriguri(float(direction))
            return
        self._zoom_at(event.x, event.y, 1.15 if direction > 0 else 1 / 1.15)

    def _ctrl_pressed(self, event: tk.Event) -> bool:
        return bool(int(getattr(event, "state", 0)) & self.CTRL_MASK)

    def _adjust_guriguri(self, wheel_steps: float) -> None:
        if self.guriguri_grower is None:
            self.status.set("Boundary Guriguri: click the intended part first, then use the mouse wheel.")
            return

        direction = 1 if wheel_steps > 0 else -1
        notch_count = max(1, int(round(abs(wheel_steps))))
        new_step = max(0, self.guriguri_step + direction * notch_count)
        if new_step == self.guriguri_step:
            return
        self.guriguri_step = new_step
        self._update_guriguri_mask(refresh_now=False)

    def _update_guriguri_mask(self, *, refresh_now: bool) -> None:
        original = self.state.original_rgb
        grower = self.guriguri_grower
        if original is None or grower is None:
            return

        target = target_pixels_for_step(self.guriguri_step, original.shape[0] * original.shape[1])
        self.state.pending_part_mask = grower.mask_for_count(target)
        pixels = int(np.count_nonzero(self.state.pending_part_mask))
        self.status.set(
            f"Boundary Guriguri: step {self.guriguri_step}, {pixels:,} px. "
            "Wheel up = reveal more, wheel down = return inward. Ctrl+wheel = zoom."
        )
        if refresh_now:
            self.refresh_preview()
        else:
            self._schedule_refresh(delay_ms=8)

    def cancel_guriguri_or_polygon(self, event: tk.Event | None = None) -> str | None:
        if self.mode.get() != "boundary-guriguri":
            return super().cancel_polygon(event)
        if self.guriguri_seed is None:
            return None

        self.state.pending_part_mask = (
            None if self.guriguri_previous_pending is None else self.guriguri_previous_pending.copy()
        )
        self._clear_guriguri_session(keep_pending=True)
        self.status.set("Boundary Guriguri cancelled; previous pending selection restored.")
        self.refresh_preview()
        return "break"

    def _clear_guriguri_session(self, *, keep_pending: bool) -> None:
        self.guriguri_click = None
        self.guriguri_seed = None
        self.guriguri_step = 0
        self.guriguri_grower = None
        self.guriguri_previous_pending = None
        if not keep_pending:
            self.state.pending_part_mask = None

    def refresh_preview(self) -> None:
        """Render pending selection in magenta for easier visual inspection."""
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

        if self.guriguri_click is None:
            return
        click_x, click_y = self._image_to_canvas(self.guriguri_click)
        self.canvas.create_line(
            click_x - 8, click_y, click_x + 8, click_y,
            fill="#52d9a5", width=1, tags="boundary-guriguri-click",
        )
        self.canvas.create_line(
            click_x, click_y - 8, click_x, click_y + 8,
            fill="#52d9a5", width=1, tags="boundary-guriguri-click",
        )

        if self.guriguri_seed is None:
            return
        seed_x, seed_y = self._image_to_canvas(self.guriguri_seed)
        radius = 5
        self.canvas.create_oval(
            seed_x - radius,
            seed_y - radius,
            seed_x + radius,
            seed_y + radius,
            outline="#f4c16a",
            width=2,
            tags="boundary-guriguri-seed",
        )


def main() -> None:
    root = tk.Tk()
    BoundaryGuriguriApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
