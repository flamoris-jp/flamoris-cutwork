from __future__ import annotations

import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

import cv2
import numpy as np
from PIL import Image, ImageTk


GC_BG = cv2.GC_BGD
GC_FG = cv2.GC_FGD
GC_PR_BG = cv2.GC_PR_BGD
GC_PR_FG = cv2.GC_PR_FGD


class CutoutSpikeApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("FLAMORIS Classical Cutout Spike")
        self.root.geometry("1280x840")

        self.image_bgr: np.ndarray | None = None
        self.image_rgb: np.ndarray | None = None
        self.gc_mask: np.ndarray | None = None
        self.preview_photo: ImageTk.PhotoImage | None = None
        self.preview_size = (1, 1)
        self.preview_origin = (0, 0)
        self.scale = 1.0

        self.mode = tk.StringVar(value="box")
        self.brush_size = tk.IntVar(value=18)
        self.status = tk.StringVar(value="Open an image to begin.")

        self.drag_start_canvas: tuple[int, int] | None = None
        self.drag_rect_id: int | None = None
        self.undo_stack: list[np.ndarray] = []

        self._build_ui()
        self._bind_events()

    def _build_ui(self) -> None:
        toolbar = ttk.Frame(self.root, padding=8)
        toolbar.pack(side=tk.TOP, fill=tk.X)

        ttk.Button(toolbar, text="Open Image", command=self.open_image).pack(side=tk.LEFT, padx=(0, 8))

        for label, value in (("Box", "box"), ("FG Brush", "fg"), ("BG Brush", "bg")):
            ttk.Radiobutton(toolbar, text=label, value=value, variable=self.mode).pack(side=tk.LEFT, padx=3)

        ttk.Label(toolbar, text="Brush").pack(side=tk.LEFT, padx=(12, 4))
        ttk.Scale(toolbar, from_=2, to=80, variable=self.brush_size, orient=tk.HORIZONTAL, length=120).pack(side=tk.LEFT)

        ttk.Button(toolbar, text="Refine GrabCut", command=self.refine_grabcut).pack(side=tk.LEFT, padx=(12, 3))
        ttk.Button(toolbar, text="Undo", command=self.undo).pack(side=tk.LEFT, padx=3)
        ttk.Button(toolbar, text="Reset", command=self.reset_mask).pack(side=tk.LEFT, padx=3)

        refine = ttk.Frame(self.root, padding=(8, 0, 8, 8))
        refine.pack(side=tk.TOP, fill=tk.X)
        ttk.Button(refine, text="Fill Holes", command=self.fill_holes).pack(side=tk.LEFT, padx=3)
        ttk.Button(refine, text="Remove Islands", command=self.remove_islands).pack(side=tk.LEFT, padx=3)
        ttk.Button(refine, text="Expand", command=lambda: self.morph("dilate")).pack(side=tk.LEFT, padx=3)
        ttk.Button(refine, text="Shrink", command=lambda: self.morph("erode")).pack(side=tk.LEFT, padx=3)
        ttk.Button(refine, text="Smooth", command=self.smooth).pack(side=tk.LEFT, padx=3)
        ttk.Button(refine, text="Save Mask", command=self.save_mask).pack(side=tk.RIGHT, padx=3)
        ttk.Button(refine, text="Export Cutout", command=self.export_cutout).pack(side=tk.RIGHT, padx=3)

        body = ttk.Frame(self.root)
        body.pack(fill=tk.BOTH, expand=True)

        self.canvas = tk.Canvas(body, background="#202020", highlightthickness=0)
        self.canvas.pack(fill=tk.BOTH, expand=True)

        statusbar = ttk.Label(self.root, textvariable=self.status, anchor=tk.W, padding=(8, 5))
        statusbar.pack(side=tk.BOTTOM, fill=tk.X)

    def _bind_events(self) -> None:
        self.canvas.bind("<Configure>", lambda _event: self.refresh_preview())
        self.canvas.bind("<ButtonPress-1>", self.on_pointer_down)
        self.canvas.bind("<B1-Motion>", self.on_pointer_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_pointer_up)

    def open_image(self) -> None:
        path = filedialog.askopenfilename(
            title="Open image",
            filetypes=[
                ("Images", "*.png *.jpg *.jpeg *.webp *.bmp"),
                ("All files", "*.*"),
            ],
        )
        if not path:
            return

        image = cv2.imread(path, cv2.IMREAD_COLOR)
        if image is None:
            messagebox.showerror("Open failed", f"Could not decode image:\n{path}")
            return

        self.image_bgr = image
        self.image_rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        self.gc_mask = np.full(image.shape[:2], GC_BG, dtype=np.uint8)
        self.undo_stack.clear()
        self.root.title(f"FLAMORIS Classical Cutout Spike - {Path(path).name}")
        self.status.set(f"Loaded {image.shape[1]}x{image.shape[0]}. Draw a box around the target object.")
        self.refresh_preview()

    def reset_mask(self) -> None:
        if self.image_bgr is None:
            return
        self._push_undo()
        self.gc_mask = np.full(self.image_bgr.shape[:2], GC_BG, dtype=np.uint8)
        self.status.set("Mask reset. Draw a new box around the target.")
        self.refresh_preview()

    def on_pointer_down(self, event: tk.Event) -> None:
        if self.image_bgr is None or self.gc_mask is None:
            return

        if self.mode.get() == "box":
            self.drag_start_canvas = (event.x, event.y)
            if self.drag_rect_id is not None:
                self.canvas.delete(self.drag_rect_id)
            self.drag_rect_id = self.canvas.create_rectangle(event.x, event.y, event.x, event.y, outline="white", width=2)
        else:
            point = self._canvas_to_image(event.x, event.y)
            if point is None:
                return
            self._push_undo()
            self._paint_hint(point)
            self.refresh_preview()

    def on_pointer_drag(self, event: tk.Event) -> None:
        if self.image_bgr is None or self.gc_mask is None:
            return

        if self.mode.get() == "box":
            if self.drag_start_canvas is None or self.drag_rect_id is None:
                return
            x0, y0 = self.drag_start_canvas
            self.canvas.coords(self.drag_rect_id, x0, y0, event.x, event.y)
        else:
            point = self._canvas_to_image(event.x, event.y)
            if point is None:
                return
            self._paint_hint(point)
            self.refresh_preview()

    def on_pointer_up(self, event: tk.Event) -> None:
        if self.image_bgr is None or self.gc_mask is None:
            return
        if self.mode.get() != "box" or self.drag_start_canvas is None:
            return

        start = self._canvas_to_image(*self.drag_start_canvas)
        end = self._canvas_to_image(event.x, event.y)
        self.drag_start_canvas = None
        if self.drag_rect_id is not None:
            self.canvas.delete(self.drag_rect_id)
            self.drag_rect_id = None
        if start is None or end is None:
            return

        x0, y0 = start
        x1, y1 = end
        left, right = sorted((x0, x1))
        top, bottom = sorted((y0, y1))
        width = max(1, right - left)
        height = max(1, bottom - top)
        if width < 4 or height < 4:
            self.status.set("Box too small. Draw a larger rectangle around the target.")
            return

        h, w = self.gc_mask.shape
        left = max(0, min(left, w - 2))
        top = max(0, min(top, h - 2))
        width = min(width, w - left - 1)
        height = min(height, h - top - 1)

        self._push_undo()
        self.gc_mask[:] = GC_BG
        started = time.perf_counter()
        bg_model = np.zeros((1, 65), np.float64)
        fg_model = np.zeros((1, 65), np.float64)
        cv2.grabCut(
            self.image_bgr,
            self.gc_mask,
            (left, top, width, height),
            bg_model,
            fg_model,
            5,
            cv2.GC_INIT_WITH_RECT,
        )
        elapsed_ms = (time.perf_counter() - started) * 1000
        self.status.set(f"Initial GrabCut: {elapsed_ms:.0f} ms. Paint FG/BG hints, then Refine GrabCut.")
        self.refresh_preview()

    def _paint_hint(self, point: tuple[int, int]) -> None:
        if self.gc_mask is None:
            return
        x, y = point
        radius = max(1, int(self.brush_size.get() / max(self.scale, 0.001) / 2))
        value = GC_FG if self.mode.get() == "fg" else GC_BG
        cv2.circle(self.gc_mask, (x, y), radius, int(value), thickness=-1)

    def refine_grabcut(self) -> None:
        if self.image_bgr is None or self.gc_mask is None:
            return
        if not np.any((self.gc_mask == GC_FG) | (self.gc_mask == GC_PR_FG)):
            self.status.set("No foreground candidate yet. Draw a box first.")
            return

        self._push_undo()
        started = time.perf_counter()
        bg_model = np.zeros((1, 65), np.float64)
        fg_model = np.zeros((1, 65), np.float64)
        cv2.grabCut(
            self.image_bgr,
            self.gc_mask,
            None,
            bg_model,
            fg_model,
            3,
            cv2.GC_INIT_WITH_MASK,
        )
        elapsed_ms = (time.perf_counter() - started) * 1000
        self.status.set(f"GrabCut refined: {elapsed_ms:.0f} ms.")
        self.refresh_preview()

    def fill_holes(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        padded = cv2.copyMakeBorder(binary, 1, 1, 1, 1, cv2.BORDER_CONSTANT, value=0)
        flood = padded.copy()
        flood_mask = np.zeros((flood.shape[0] + 2, flood.shape[1] + 2), np.uint8)
        cv2.floodFill(flood, flood_mask, (0, 0), 255)
        holes = cv2.bitwise_not(flood)[1:-1, 1:-1]
        filled = cv2.bitwise_or(binary, holes)
        self._set_from_binary(filled)
        self.status.set("Filled enclosed holes.")
        self.refresh_preview()

    def remove_islands(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        count, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
        if count <= 1:
            return
        min_area = max(16, int(binary.size * 0.0002))
        cleaned = np.zeros_like(binary)
        for label in range(1, count):
            area = int(stats[label, cv2.CC_STAT_AREA])
            if area >= min_area:
                cleaned[labels == label] = 255
        self._set_from_binary(cleaned)
        self.status.set(f"Removed foreground islands smaller than {min_area} px.")
        self.refresh_preview()

    def morph(self, operation: str) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        kernel = np.ones((3, 3), np.uint8)
        if operation == "dilate":
            result = cv2.dilate(binary, kernel, iterations=1)
            label = "Expanded mask by ~1 px."
        else:
            result = cv2.erode(binary, kernel, iterations=1)
            label = "Shrank mask by ~1 px."
        self._set_from_binary(result)
        self.status.set(label)
        self.refresh_preview()

    def smooth(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        blurred = cv2.GaussianBlur(binary, (5, 5), 0)
        _, result = cv2.threshold(blurred, 127, 255, cv2.THRESH_BINARY)
        self._set_from_binary(result)
        self.status.set("Smoothed mask edge lightly.")
        self.refresh_preview()

    def undo(self) -> None:
        if not self.undo_stack:
            self.status.set("Nothing to undo.")
            return
        self.gc_mask = self.undo_stack.pop()
        self.status.set("Undid mask change.")
        self.refresh_preview()

    def _push_undo(self) -> None:
        if self.gc_mask is None:
            return
        self.undo_stack.append(self.gc_mask.copy())
        if len(self.undo_stack) > 30:
            self.undo_stack.pop(0)

    def _set_from_binary(self, binary: np.ndarray) -> None:
        self.gc_mask = np.where(binary > 0, GC_PR_FG, GC_PR_BG).astype(np.uint8)

    def _binary_mask(self) -> np.ndarray | None:
        if self.gc_mask is None:
            return None
        fg = (self.gc_mask == GC_FG) | (self.gc_mask == GC_PR_FG)
        return fg.astype(np.uint8) * 255

    def refresh_preview(self) -> None:
        if self.image_rgb is None:
            self.canvas.delete("all")
            return

        canvas_w = max(1, self.canvas.winfo_width())
        canvas_h = max(1, self.canvas.winfo_height())
        image_h, image_w = self.image_rgb.shape[:2]
        self.scale = min(canvas_w / image_w, canvas_h / image_h)
        self.scale = max(min(self.scale, 1.0), 0.01)
        display_w = max(1, int(round(image_w * self.scale)))
        display_h = max(1, int(round(image_h * self.scale)))
        self.preview_size = (display_w, display_h)
        origin_x = (canvas_w - display_w) // 2
        origin_y = (canvas_h - display_h) // 2
        self.preview_origin = (origin_x, origin_y)

        base = self.image_rgb.copy()
        if self.gc_mask is not None and np.any(self.gc_mask != GC_BG):
            binary = self._binary_mask()
            if binary is not None:
                fg = binary > 0
                overlay = base.copy()
                overlay[~fg] = (overlay[~fg] * 0.25).astype(np.uint8)
                base = overlay

        preview = Image.fromarray(base).resize((display_w, display_h), Image.Resampling.LANCZOS)
        self.preview_photo = ImageTk.PhotoImage(preview)
        self.canvas.delete("image")
        self.canvas.create_image(origin_x, origin_y, anchor=tk.NW, image=self.preview_photo, tags="image")
        self.canvas.tag_lower("image")

    def _canvas_to_image(self, canvas_x: int, canvas_y: int) -> tuple[int, int] | None:
        if self.image_rgb is None:
            return None
        origin_x, origin_y = self.preview_origin
        display_w, display_h = self.preview_size
        if not (origin_x <= canvas_x < origin_x + display_w and origin_y <= canvas_y < origin_y + display_h):
            return None
        x = int((canvas_x - origin_x) / self.scale)
        y = int((canvas_y - origin_y) / self.scale)
        h, w = self.image_rgb.shape[:2]
        return max(0, min(x, w - 1)), max(0, min(y, h - 1))

    def save_mask(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        path = filedialog.asksaveasfilename(
            title="Save mask",
            defaultextension=".png",
            filetypes=[("PNG", "*.png")],
        )
        if not path:
            return
        cv2.imwrite(path, binary)
        self.status.set(f"Saved mask: {Path(path).name}")

    def export_cutout(self) -> None:
        if self.image_rgb is None:
            return
        binary = self._binary_mask()
        if binary is None or not np.any(binary):
            self.status.set("No foreground mask to export.")
            return
        path = filedialog.asksaveasfilename(
            title="Export cutout",
            defaultextension=".png",
            filetypes=[("PNG", "*.png")],
        )
        if not path:
            return
        rgba = np.dstack([self.image_rgb, binary])
        Image.fromarray(rgba, mode="RGBA").save(path)
        self.status.set(f"Exported cutout: {Path(path).name}")


def main() -> None:
    root = tk.Tk()
    CutoutSpikeApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
