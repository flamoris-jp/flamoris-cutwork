from __future__ import annotations

import tkinter as tk
from tkinter import ttk

import numpy as np

from app import CutoutSpikeApp
from guriguri import DEFAULT_TOLERANCE, grow_selection_mask, scrub_tolerance


class PartsGuriguriApp(CutoutSpikeApp):
    """Classical-cutout spike with an interactive colour-continuity selector.

    The human supplies meaning by clicking the intended region.  The tool only
    grows or shrinks a connected colour-continuous mask while the mouse is
    scrubbed horizontally.  Original pixels are never regenerated.
    """

    def __init__(self, root: tk.Tk) -> None:
        self.guriguri_seed: tuple[int, int] | None = None
        self.guriguri_tolerance = DEFAULT_TOLERANCE
        self.guriguri_last_canvas_x: int | None = None
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
            text="Click a semantic seed, hold left mouse, scrub right to grow / left to shrink. Release when the boundary looks right.",
            foreground="#555555",
        ).pack(side=tk.LEFT)

    def _bind_events(self) -> None:
        super()._bind_events()
        self.root.bind("<Escape>", self.cancel_guriguri_or_polygon)

    def on_mode_changed(self) -> None:
        if self.mode.get() == "guriguri":
            self.cancel_polygon(silent=True)
            self._clear_guriguri_session(keep_pending=True)
            self.status.set(
                "Parts Guriguri: click what you mean, then scrub right to grow / left to shrink. "
                "Release and Create Part Layer when the boundary looks right."
            )
            return

        self._clear_guriguri_session(keep_pending=True)
        super().on_mode_changed()

    def open_image(self) -> None:
        self._clear_guriguri_session(keep_pending=True)
        super().open_image()
        if self.state.original_rgb is not None:
            self.status.set(
                "Image loaded. Choose Parts Guriguri for click + scrub selection, or Part Polygon for exact manual fallback."
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
        self.guriguri_last_canvas_x = event.x
        self._update_guriguri_mask(refresh_now=True)

    def on_pointer_drag(self, event: tk.Event) -> None:
        if self.mode.get() != "guriguri":
            super().on_pointer_drag(event)
            return
        if self.guriguri_seed is None or self.guriguri_last_canvas_x is None:
            return

        delta_x = event.x - self.guriguri_last_canvas_x
        self.guriguri_last_canvas_x = event.x
        if delta_x == 0:
            return

        new_tolerance = scrub_tolerance(self.guriguri_tolerance, delta_x)
        if abs(new_tolerance - self.guriguri_tolerance) < 0.25:
            return
        self.guriguri_tolerance = new_tolerance
        self._update_guriguri_mask(refresh_now=False)

    def on_pointer_up(self, event: tk.Event) -> None:
        if self.mode.get() != "guriguri":
            super().on_pointer_up(event)
            return
        self.guriguri_last_canvas_x = None
        if self.guriguri_seed is not None and self.state.pending_part_mask is not None:
            pixels = int(np.count_nonzero(self.state.pending_part_mask))
            self.status.set(
                f"Guriguri ready: tolerance {self.guriguri_tolerance:.1f}, {pixels:,} px. "
                "Create Part Layer, scrub again from a new seed, or press Esc to cancel."
            )

    def _update_guriguri_mask(self, *, refresh_now: bool) -> None:
        original = self.state.original_rgb
        seed = self.guriguri_seed
        if original is None or seed is None:
            return

        self.state.pending_part_mask = grow_selection_mask(original, seed, self.guriguri_tolerance)
        pixels = int(np.count_nonzero(self.state.pending_part_mask))
        self.status.set(
            f"Guriguri: tolerance {self.guriguri_tolerance:.1f}, {pixels:,} px. "
            "Right = grow, left = shrink."
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
        self.guriguri_last_canvas_x = None
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
