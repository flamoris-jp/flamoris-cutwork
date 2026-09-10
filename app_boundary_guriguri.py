from __future__ import annotations

import tkinter as tk
from tkinter import ttk

import numpy as np

from app import CutoutSpikeApp
from boundary_guriguri import (
    build_boundary_map,
    grow_boundary_mask,
    initial_boundary_threshold,
    snap_seed_to_basin,
    wheel_boundary_threshold,
)


class BoundaryGuriguriApp(CutoutSpikeApp):
    """Classical-cutout spike with wheel-controlled boundary hierarchy selection.

    The click only says "include something around here". The software does not
    assign semantic meaning. Mouse-wheel motion changes how strong a visual
    boundary the connected selection is allowed to cross.
    """

    CTRL_MASK = 0x0004

    def __init__(self, root: tk.Tk) -> None:
        self.boundary_map: np.ndarray | None = None
        self.guriguri_click: tuple[int, int] | None = None
        self.guriguri_seed: tuple[int, int] | None = None
        self.guriguri_threshold = 0.0
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
            text=("Click anywhere in the intended part; wheel up crosses stronger boundaries, "
                  "wheel down returns inward. Ctrl+wheel keeps normal zoom."),
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
                "Boundary Guriguri: click anywhere in the intended part, then wheel through nested boundary levels. "
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
            self.status.set("Building visual boundary map...")
            self.root.update_idletasks()
            self.boundary_map = build_boundary_map(self.state.original_rgb)
            self.status.set(
                "Image loaded. Boundary map ready. Click anywhere in an intended part and use the wheel, "
                "or use Part Polygon for exact manual fallback."
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
        self.guriguri_threshold = initial_boundary_threshold(self.boundary_map, self.guriguri_seed)
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
                f"Boundary Guriguri ready: level {self.guriguri_threshold:.1f}, {pixels:,} px. "
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
        if self.guriguri_seed is None:
            self.status.set("Boundary Guriguri: click the intended part first, then use the mouse wheel.")
            return

        new_threshold = wheel_boundary_threshold(self.guriguri_threshold, wheel_steps)
        if abs(new_threshold - self.guriguri_threshold) < 1e-6:
            return
        self.guriguri_threshold = new_threshold
        self._update_guriguri_mask(refresh_now=False)

    def _update_guriguri_mask(self, *, refresh_now: bool) -> None:
        if self.boundary_map is None or self.guriguri_seed is None:
            return

        self.state.pending_part_mask = grow_boundary_mask(
            self.boundary_map,
            self.guriguri_seed,
            self.guriguri_threshold,
        )
        pixels = int(np.count_nonzero(self.state.pending_part_mask))
        self.status.set(
            f"Boundary Guriguri: level {self.guriguri_threshold:.1f}, {pixels:,} px. "
            "Wheel up = cross stronger boundary, wheel down = return inward. Ctrl+wheel = zoom."
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
        self.guriguri_threshold = 0.0
        self.guriguri_previous_pending = None
        if not keep_pending:
            self.state.pending_part_mask = None

    def refresh_preview(self) -> None:
        super().refresh_preview()
        if self.guriguri_click is None or self.state.original_rgb is None:
            return

        click_x, click_y = self._image_to_canvas(self.guriguri_click)
        self.canvas.create_line(
            click_x - 8, click_y, click_x + 8, click_y,
            fill="#7de2a8", width=1, tags="boundary-guriguri-click",
        )
        self.canvas.create_line(
            click_x, click_y - 8, click_x, click_y + 8,
            fill="#7de2a8", width=1, tags="boundary-guriguri-click",
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
