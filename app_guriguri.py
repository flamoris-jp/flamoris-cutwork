from __future__ import annotations

import tkinter as tk
from tkinter import ttk

import numpy as np

from app import CutoutSpikeApp
from guriguri import DEFAULT_TOLERANCE, grow_selection_mask, wheel_tolerance


class PartsGuriguriApp(CutoutSpikeApp):
    """Classical-cutout spike with an interactive colour-continuity selector.

    The human supplies meaning by clicking the intended region. The tool only
    grows or shrinks a connected colour-continuous mask with the mouse wheel.
    Original pixels are never regenerated.
    """

    CTRL_MASK = 0x0004

    def __init__(self, root: tk.Tk) -> None:
        self.guriguri_seed: tuple[int, int] | None = None
        self.guriguri_tolerance = DEFAULT_TOLERANCE
        self.guriguri_previous_pending: np.ndarray | None = None
        super().__init__(root)
        self.root.title("FLAMORIS Parts Guriguri Spike")

    def _build_ui(self) -> None:
        super()._build_ui()

        bar = ttk.Frame(self.root, padding=(8, 0, 8, 7))
        # Insert the experimental control immediately above the canvas body
        # without changing the original spike's toolbar layout.
        bar.pack(side=tk.TOP, fill=tk.X, before=self.canvas.master)
        ttk.Radiobutton(
            bar,
            text="🌀 Parts Guriguri",
            value="guriguri",
            variable=self.mode,
            command=self.on_mode_changed,
        ).pack(side=tk.LEFT, padx=(0, 10))
        ttk.Label(
            bar,
            text="Click a semantic seed, then wheel up to grow / wheel down to shrink. Ctrl+wheel keeps normal zoom.",
            foreground="#555555",
        ).pack(side=tk.LEFT)

    def _bind_events(self) -> None:
        super()._bind_events()
        self.root.bind("<Escape>", self.cancel_guriguri_or_polygon)
        # Replace the base Linux wheel aliases so Guriguri behaves the same on
        # Windows/macOS-style MouseWheel events and X11 Button-4/5 events.
        self.canvas.bind("<Button-4>", lambda event: self._on_button_wheel(event, +1))
        self.canvas.bind("<Button-5>", lambda event: self._on_button_wheel(event, -1))

    def on_mode_changed(self) -> None:
        if self.mode.get() == "guriguri":
            self.cancel_polygon(silent=True)
            self._clear_guriguri_session(keep_pending=True)
            self.status.set(
                "Parts Guriguri: click what you mean, then wheel up to grow / wheel down to shrink. "
                "Ctrl+wheel zooms."
            )
            return

        self._clear_guriguri_session(keep_pending=True)
        super().on_mode_changed()

    def open_image(self) -> None:
        self._clear_guriguri_session(keep_pending=True)
        super().open_image()
        if self.state.original_rgb is not None:
            self.status.set(
                "Image loaded. Choose Parts Guriguri for click + wheel selection, or Part Polygon for exact manual fallback."
            )

    def on_pointer_down(self, event: tk.Event) -> None:
        if self.mode.get() != "guriguri":
            super().on_pointer_down(event)
            return
        if self.state.original_rgb is None:
            return

        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return

        self.guriguri_previous_pending = (
            None if self.state.pending_part_mask is None else self.state.pending_part_mask.copy()
        )
        self.guriguri_seed = point
        self.guriguri_tolerance = DEFAULT_TOLERANCE
        self._update_guriguri_mask(refresh_now=True)

    def on_pointer_drag(self, event: tk.Event) -> None:
        if self.mode.get() != "guriguri":
            super().on_pointer_drag(event)
        # Guriguri deliberately ignores left-drag. The wheel is the control.

    def on_pointer_up(self, event: tk.Event) -> None:
        if self.mode.get() != "guriguri":
            super().on_pointer_up(event)
            return
        if self.guriguri_seed is not None and self.state.pending_part_mask is not None:
            pixels = int(np.count_nonzero(self.state.pending_part_mask))
            self.status.set(
                f"Guriguri ready: tolerance {self.guriguri_tolerance:.1f}, {pixels:,} px. "
                "Wheel to adjust, click a new seed, or Create Part Layer."
            )

    def on_mouse_wheel(self, event: tk.Event) -> None:
        if self.mode.get() != "guriguri" or self._ctrl_pressed(event):
            super().on_mouse_wheel(event)
            return

        delta = float(getattr(event, "delta", 0.0))
        if delta == 0:
            return
        # Windows commonly reports ±120 per notch. High-resolution wheels and
        # trackpads may report smaller values, so preserve fractional motion.
        steps = delta / 120.0 if abs(delta) >= 120.0 else (1.0 if delta > 0 else -1.0)
        self._adjust_guriguri(steps)

    def _on_button_wheel(self, event: tk.Event, direction: int) -> None:
        if self.mode.get() == "guriguri" and not self._ctrl_pressed(event):
            self._adjust_guriguri(float(direction))
            return
        self._zoom_at(event.x, event.y, 1.15 if direction > 0 else 1 / 1.15)

    def _ctrl_pressed(self, event: tk.Event) -> bool:
        return bool(int(getattr(event, "state", 0)) & self.CTRL_MASK)

    def _adjust_guriguri(self, wheel_steps: float) -> None:
        if self.guriguri_seed is None:
            self.status.set("Parts Guriguri: click the region first, then use the mouse wheel.")
            return

        new_tolerance = wheel_tolerance(self.guriguri_tolerance, wheel_steps)
        if abs(new_tolerance - self.guriguri_tolerance) < 1e-6:
            return
        self.guriguri_tolerance = new_tolerance
        self._update_guriguri_mask(refresh_now=False)

    def _update_guriguri_mask(self, *, refresh_now: bool) -> None:
        original = self.state.original_rgb
        seed = self.guriguri_seed
        if original is None or seed is None:
            return

        self.state.pending_part_mask = grow_selection_mask(original, seed, self.guriguri_tolerance)
        pixels = int(np.count_nonzero(self.state.pending_part_mask))
        self.status.set(
            f"Guriguri: tolerance {self.guriguri_tolerance:.1f}, {pixels:,} px. "
            "Wheel up = grow, wheel down = shrink. Ctrl+wheel = zoom."
        )
        if refresh_now:
            self.refresh_preview()
        else:
            self._schedule_refresh(delay_ms=8)

    def cancel_guriguri_or_polygon(self, event: tk.Event | None = None) -> str | None:
        if self.mode.get() != "guriguri":
            return super().cancel_polygon(event)
        if self.guriguri_seed is None:
            return None

        self.state.pending_part_mask = (
            None if self.guriguri_previous_pending is None else self.guriguri_previous_pending.copy()
        )
        self._clear_guriguri_session(keep_pending=True)
        self.status.set("Guriguri selection cancelled; previous pending selection restored.")
        self.refresh_preview()
        return "break"

    def _clear_guriguri_session(self, *, keep_pending: bool) -> None:
        self.guriguri_seed = None
        self.guriguri_tolerance = DEFAULT_TOLERANCE
        self.guriguri_previous_pending = None
        if not keep_pending:
            self.state.pending_part_mask = None

    def refresh_preview(self) -> None:
        super().refresh_preview()
        if self.guriguri_seed is None or self.state.original_rgb is None:
            return
        x, y = self._image_to_canvas(self.guriguri_seed)
        radius = 6
        self.canvas.create_oval(
            x - radius,
            y - radius,
            x + radius,
            y + radius,
            outline="#7de2a8",
            width=2,
            tags="guriguri-seed",
        )
        self.canvas.create_line(x - 9, y, x + 9, y, fill="#7de2a8", width=1, tags="guriguri-seed")
        self.canvas.create_line(x, y - 9, x, y + 9, fill="#7de2a8", width=1, tags="guriguri-seed")


def main() -> None:
    root = tk.Tk()
    PartsGuriguriApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
