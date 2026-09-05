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


def lasso_grabcut_mask(shape: tuple[int, int], points: list[tuple[int, int]]) -> np.ndarray:
    """Create GrabCut labels for a rough polygon selection.

    The polygon is only a foreground candidate.  Everything outside it is
    definite background, which is deliberately a broad, quick selection model
    rather than a contour-tracing tool.
    """
    height, width = shape
    candidate = np.zeros((height, width), dtype=np.uint8)
    contour = np.asarray(points, dtype=np.int32).reshape((-1, 1, 2))
    cv2.fillPoly(candidate, [contour], 255)

    # Keep a definite-background sample even when the user loosely encloses the
    # whole displayed image. A one-pixel border is an acceptable trade-off for
    # this rough cutout experiment and lets GrabCut initialize predictably.
    candidate[0, :] = 0
    candidate[-1, :] = 0
    candidate[:, 0] = 0
    candidate[:, -1] = 0

    return np.where(candidate > 0, GC_PR_FG, GC_BG).astype(np.uint8)


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
        self.lasso_points: list[tuple[int, int]] = []
        self.undo_stack: list[np.ndarray] = []
        self.layers_created = False
        self.base_hole_filled = False
        self.base_visible = tk.BooleanVar(value=True)
        self.cutout_visible = tk.BooleanVar(value=True)

        self._build_ui()
        self._bind_events()

    def _build_ui(self) -> None:
        toolbar = ttk.Frame(self.root, padding=8)
        toolbar.pack(side=tk.TOP, fill=tk.X)

        ttk.Button(toolbar, text="Open Image", command=self.open_image).pack(side=tk.LEFT, padx=(0, 8))

        for label, value in (
            ("Box", "box"),
            ("Polygon Lasso", "lasso"),
            ("FG Brush", "fg"),
            ("BG Brush", "bg"),
        ):
            ttk.Radiobutton(
                toolbar,
                text=label,
                value=value,
                variable=self.mode,
                command=self.on_mode_changed,
            ).pack(side=tk.LEFT, padx=3)

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
        ttk.Button(refine, text="Create Layers", command=self.create_layers).pack(side=tk.LEFT, padx=(12, 3))
        ttk.Button(refine, text="Fill Base Hole", command=self.fill_base_hole).pack(side=tk.LEFT, padx=3)
        ttk.Button(refine, text="Save Mask", command=self.save_mask).pack(side=tk.RIGHT, padx=3)
        ttk.Button(refine, text="Export Cutout", command=self.export_cutout).pack(side=tk.RIGHT, padx=3)

        body = ttk.Frame(self.root)
        body.pack(fill=tk.BOTH, expand=True)
        self.canvas = tk.Canvas(body, background="#202020", highlightthickness=0)
        self.canvas.pack(fill=tk.BOTH, expand=True)

        layers = ttk.LabelFrame(self.root, text="Experiment Layers", padding=(8, 4))
        layers.pack(side=tk.BOTTOM, fill=tk.X, padx=8, pady=(0, 4))
        ttk.Checkbutton(layers, text="Base", variable=self.base_visible, command=self.refresh_preview).pack(side=tk.LEFT, padx=3)
        ttk.Checkbutton(layers, text="Cutout", variable=self.cutout_visible, command=self.refresh_preview).pack(side=tk.LEFT, padx=3)

        statusbar = ttk.Label(self.root, textvariable=self.status, anchor=tk.W, padding=(8, 5))
        statusbar.pack(side=tk.BOTTOM, fill=tk.X)

    def _bind_events(self) -> None:
        self.canvas.bind("<Configure>", lambda _event: self.refresh_preview())
        self.canvas.bind("<ButtonPress-1>", self.on_pointer_down)
        self.canvas.bind("<B1-Motion>", self.on_pointer_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_pointer_up)
        self.canvas.bind("<Double-Button-1>", self.finalize_lasso)
        self.root.bind("<Return>", self.finalize_lasso)
        self.root.bind("<Escape>", self.cancel_lasso)

    def on_mode_changed(self) -> None:
        if self.mode.get() != "lasso":
            self._clear_lasso_preview()
        if self.image_bgr is None:
            return
        messages = {
            "box": "Drag a loose box around the moving person or body part.",
            "lasso": "Click a loose polygon around the target. Enter or double-click finishes; Esc cancels.",
            "fg": "Paint only areas that must remain foreground, then Refine GrabCut.",
            "bg": "Paint only visible unwanted areas as background, then Refine GrabCut.",
        }
        self.status.set(messages[self.mode.get()])

    def open_image(self) -> None:
        path = filedialog.askopenfilename(
            title="Open image",
            filetypes=[("Images", "*.png *.jpg *.jpeg *.webp *.bmp"), ("All files", "*.*")],
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
        self.layers_created = False
        self.base_hole_filled = False
        self.undo_stack.clear()
        self._clear_lasso_preview()
        self.root.title(f"FLAMORIS Classical Cutout Spike - {Path(path).name}")
        self.status.set(f"Loaded {image.shape[1]}x{image.shape[0]}. Draw a Box or Polygon Lasso around the target.")
        self.refresh_preview()

    def reset_mask(self) -> None:
        if self.image_bgr is None:
            return
        self._push_undo()
        self.gc_mask = np.full(self.image_bgr.shape[:2], GC_BG, dtype=np.uint8)
        self.base_hole_filled = False
        self._clear_lasso_preview()
        self.status.set("Mask reset. Draw a new Box or Polygon Lasso around the target.")
        self.refresh_preview()

    def on_pointer_down(self, event: tk.Event) -> None:
        if self.image_bgr is None or self.gc_mask is None:
            return
        if self.mode.get() == "box":
            self.drag_start_canvas = (event.x, event.y)
            if self.drag_rect_id is not None:
                self.canvas.delete(self.drag_rect_id)
            self.drag_rect_id = self.canvas.create_rectangle(event.x, event.y, event.x, event.y, outline="white", width=2)
            return
        if self.mode.get() == "lasso":
            point = self._canvas_to_image(event.x, event.y)
            if point is not None:
                if not self.lasso_points or point != self.lasso_points[-1]:
                    self.lasso_points.append(point)
                    self._draw_lasso_preview()
                    self.status.set(f"Lasso: {len(self.lasso_points)} points. Enter/double-click finishes; Esc cancels.")
            return

        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return
        self._push_undo()
        self._paint_hint(point)
        self.refresh_preview()

    def on_pointer_drag(self, event: tk.Event) -> None:
        if self.image_bgr is None or self.gc_mask is None or self.mode.get() == "lasso":
            return
        if self.mode.get() == "box":
            if self.drag_start_canvas is not None and self.drag_rect_id is not None:
                self.canvas.coords(self.drag_rect_id, *self.drag_start_canvas, event.x, event.y)
            return
        point = self._canvas_to_image(event.x, event.y)
        if point is not None:
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
        width, height = right - left, bottom - top
        if width < 4 or height < 4:
            self.status.set("Box too small. Draw a larger rectangle around the target.")
            return
        image_h, image_w = self.gc_mask.shape
        left = max(0, min(left, image_w - 2))
        top = max(0, min(top, image_h - 2))
        width = min(width, image_w - left - 1)
        height = min(height, image_h - top - 1)
        self._push_undo()
        self.gc_mask[:] = GC_BG
        self.base_hole_filled = False
        started = time.perf_counter()
        bg_model = np.zeros((1, 65), np.float64)
        fg_model = np.zeros((1, 65), np.float64)
        cv2.grabCut(self.image_bgr, self.gc_mask, (left, top, width, height), bg_model, fg_model, 5, cv2.GC_INIT_WITH_RECT)
        elapsed_ms = (time.perf_counter() - started) * 1000
        self.status.set(f"Box GrabCut complete: {elapsed_ms:.0f} ms. Paint FG/BG hints only where artifacts matter, then Refine GrabCut.")
        self.refresh_preview()

    def finalize_lasso(self, _event: tk.Event | None = None) -> str | None:
        if self.image_bgr is None or self.gc_mask is None or self.mode.get() != "lasso":
            return None
        if len(self.lasso_points) < 3:
            self.status.set("Lasso needs at least three points. Keep clicking or press Esc to cancel.")
            return "break"
        contour = np.asarray(self.lasso_points, dtype=np.float32)
        if cv2.contourArea(contour) < 16:
            self.status.set("Lasso too small. Draw a larger loose polygon around the target.")
            return "break"

        self._push_undo()
        self.gc_mask = lasso_grabcut_mask(self.gc_mask.shape, self.lasso_points)
        self.base_hole_filled = False
        started = time.perf_counter()
        bg_model = np.zeros((1, 65), np.float64)
        fg_model = np.zeros((1, 65), np.float64)
        cv2.grabCut(self.image_bgr, self.gc_mask, None, bg_model, fg_model, 5, cv2.GC_INIT_WITH_MASK)
        elapsed_ms = (time.perf_counter() - started) * 1000
        self._clear_lasso_preview()
        self.status.set(f"Polygon GrabCut complete: {elapsed_ms:.0f} ms. Paint FG/BG hints only where artifacts matter, then Refine GrabCut.")
        self.refresh_preview()
        return "break"

    def cancel_lasso(self, _event: tk.Event | None = None) -> str | None:
        if self.mode.get() == "lasso" and self.lasso_points:
            self._clear_lasso_preview()
            self.status.set("Polygon Lasso cancelled. Click to start a new loose selection.")
            return "break"
        return None

    def _paint_hint(self, point: tuple[int, int]) -> None:
        if self.gc_mask is None:
            return
        x, y = point
        radius = max(1, int(self.brush_size.get() / max(self.scale, 0.001) / 2))
        value = GC_FG if self.mode.get() == "fg" else GC_BG
        cv2.circle(self.gc_mask, (x, y), radius, int(value), thickness=-1)
        self.base_hole_filled = False

    def refine_grabcut(self) -> None:
        if self.image_bgr is None or self.gc_mask is None:
            return
        if not np.any((self.gc_mask == GC_FG) | (self.gc_mask == GC_PR_FG)):
            self.status.set("No foreground candidate yet. Draw a Box or Polygon Lasso first.")
            return
        self._push_undo()
        self.base_hole_filled = False
        started = time.perf_counter()
        bg_model = np.zeros((1, 65), np.float64)
        fg_model = np.zeros((1, 65), np.float64)
        cv2.grabCut(self.image_bgr, self.gc_mask, None, bg_model, fg_model, 3, cv2.GC_INIT_WITH_MASK)
        elapsed_ms = (time.perf_counter() - started) * 1000
        self.status.set(f"GrabCut refined: {elapsed_ms:.0f} ms.")
        self.refresh_preview()

    def fill_holes(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        self.base_hole_filled = False
        padded = cv2.copyMakeBorder(binary, 1, 1, 1, 1, cv2.BORDER_CONSTANT, value=0)
        flood = padded.copy()
        flood_mask = np.zeros((flood.shape[0] + 2, flood.shape[1] + 2), np.uint8)
        cv2.floodFill(flood, flood_mask, (0, 0), 255)
        holes = cv2.bitwise_not(flood)[1:-1, 1:-1]
        self._set_from_binary(cv2.bitwise_or(binary, holes))
        self.status.set("Filled enclosed holes.")
        self.refresh_preview()

    def remove_islands(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        self.base_hole_filled = False
        count, labels, stats, _ = cv2.connectedComponentsWithStats(binary, connectivity=8)
        if count <= 1:
            return
        min_area = max(16, int(binary.size * 0.0002))
        cleaned = np.zeros_like(binary)
        for label in range(1, count):
            if int(stats[label, cv2.CC_STAT_AREA]) >= min_area:
                cleaned[labels == label] = 255
        self._set_from_binary(cleaned)
        self.status.set(f"Removed foreground islands smaller than {min_area} px.")
        self.refresh_preview()

    def morph(self, operation: str) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        self.base_hole_filled = False
        kernel = np.ones((3, 3), np.uint8)
        if operation == "dilate":
            result, label = cv2.dilate(binary, kernel, iterations=1), "Expanded mask by ~1 px."
        else:
            result, label = cv2.erode(binary, kernel, iterations=1), "Shrank mask by ~1 px."
        self._set_from_binary(result)
        self.status.set(label)
        self.refresh_preview()

    def smooth(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        self._push_undo()
        self.base_hole_filled = False
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
        if self.gc_mask is not None:
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

    def create_layers(self) -> None:
        binary = self._binary_mask()
        if binary is None or not np.any(binary):
            self.status.set("Create a foreground mask before creating layers.")
            return
        self.layers_created = True
        self.base_hole_filled = False
        self.base_visible.set(True)
        self.cutout_visible.set(True)
        self.status.set("Created Base + Cutout experiment layers from the original image and current mask.")
        self.refresh_preview()

    def fill_base_hole(self) -> None:
        if not self.layers_created or self._binary_mask() is None:
            self.status.set("Create Layers after making a mask first.")
            return
        self.base_hole_filled = True
        self.status.set("Filled Base hole with OpenCV Telea inpainting. Cutout remains unchanged.")
        self.refresh_preview()

    @staticmethod
    def _checkerboard(height: int, width: int) -> np.ndarray:
        squares = (np.indices((height, width)).sum(axis=0) // 16) % 2
        return np.where(squares[..., None] == 0, (72, 72, 72), (112, 112, 112)).astype(np.uint8)

    @staticmethod
    def _over(background: np.ndarray, foreground: np.ndarray, alpha: np.ndarray) -> np.ndarray:
        weight = (alpha.astype(np.float32) / 255.0)[..., None]
        return (foreground * weight + background * (1.0 - weight)).astype(np.uint8)

    def _layer_preview(self, binary: np.ndarray) -> np.ndarray:
        assert self.image_rgb is not None and self.image_bgr is not None
        preview = self._checkerboard(*binary.shape)
        if self.base_visible.get():
            if self.base_hole_filled:
                base_rgb = cv2.cvtColor(cv2.inpaint(self.image_bgr, binary, 3, cv2.INPAINT_TELEA), cv2.COLOR_BGR2RGB)
                base_alpha = np.full(binary.shape, 255, dtype=np.uint8)
            else:
                base_rgb = self.image_rgb
                base_alpha = cv2.bitwise_not(binary)
            preview = self._over(preview, base_rgb, base_alpha)
        if self.cutout_visible.get():
            preview = self._over(preview, self.image_rgb, binary)
        return preview

    def refresh_preview(self) -> None:
        if self.image_rgb is None:
            self.canvas.delete("all")
            return
        canvas_w, canvas_h = max(1, self.canvas.winfo_width()), max(1, self.canvas.winfo_height())
        image_h, image_w = self.image_rgb.shape[:2]
        self.scale = max(min(min(canvas_w / image_w, canvas_h / image_h), 1.0), 0.01)
        display_w, display_h = max(1, int(round(image_w * self.scale))), max(1, int(round(image_h * self.scale)))
        self.preview_size = (display_w, display_h)
        self.preview_origin = ((canvas_w - display_w) // 2, (canvas_h - display_h) // 2)
        base = self.image_rgb.copy()
        binary = self._binary_mask()
        if self.layers_created and binary is not None:
            base = self._layer_preview(binary)
        elif binary is not None and np.any(binary):
            base[binary == 0] = (base[binary == 0] * 0.25).astype(np.uint8)
        preview = Image.fromarray(base).resize((display_w, display_h), Image.Resampling.LANCZOS)
        self.preview_photo = ImageTk.PhotoImage(preview)
        self.canvas.delete("image")
        self.canvas.delete("selection")
        self.canvas.create_image(*self.preview_origin, anchor=tk.NW, image=self.preview_photo, tags="image")
        self.canvas.tag_lower("image")
        self._draw_lasso_preview()

    def _draw_lasso_preview(self) -> None:
        self.canvas.delete("selection")
        if not self.lasso_points:
            return
        canvas_points = [self._image_to_canvas(point) for point in self.lasso_points]
        if len(canvas_points) > 1:
            preview_points = canvas_points + [canvas_points[0]] if len(canvas_points) > 2 else canvas_points
            flattened = [coordinate for point in preview_points for coordinate in point]
            self.canvas.create_line(*flattened, fill="#f4c16a", width=2, dash=(5, 3), tags="selection")
        for x, y in canvas_points:
            self.canvas.create_oval(x - 3, y - 3, x + 3, y + 3, outline="#f4c16a", fill="#202020", tags="selection")

    def _clear_lasso_preview(self) -> None:
        self.lasso_points.clear()
        self.canvas.delete("selection")

    def _image_to_canvas(self, point: tuple[int, int]) -> tuple[int, int]:
        return (int(round(self.preview_origin[0] + point[0] * self.scale)), int(round(self.preview_origin[1] + point[1] * self.scale)))

    def _canvas_to_image(self, canvas_x: int, canvas_y: int) -> tuple[int, int] | None:
        if self.image_rgb is None:
            return None
        origin_x, origin_y = self.preview_origin
        display_w, display_h = self.preview_size
        if not (origin_x <= canvas_x < origin_x + display_w and origin_y <= canvas_y < origin_y + display_h):
            return None
        x, y = int((canvas_x - origin_x) / self.scale), int((canvas_y - origin_y) / self.scale)
        height, width = self.image_rgb.shape[:2]
        return max(0, min(x, width - 1)), max(0, min(y, height - 1))

    def save_mask(self) -> None:
        binary = self._binary_mask()
        if binary is None:
            return
        path = filedialog.asksaveasfilename(title="Save mask", defaultextension=".png", filetypes=[("PNG", "*.png")])
        if path:
            cv2.imwrite(path, binary)
            self.status.set(f"Saved mask: {Path(path).name}")

    def export_cutout(self) -> None:
        if self.image_rgb is None:
            return
        binary = self._binary_mask()
        if binary is None or not np.any(binary):
            self.status.set("No foreground mask to export.")
            return
        path = filedialog.asksaveasfilename(title="Export cutout", defaultextension=".png", filetypes=[("PNG", "*.png")])
        if path:
            Image.fromarray(np.dstack([self.image_rgb, binary]), mode="RGBA").save(path)
            self.status.set(f"Exported cutout: {Path(path).name}")


def main() -> None:
    root = tk.Tk()
    CutoutSpikeApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
