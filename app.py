from __future__ import annotations

import json
import re
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

import cv2
import numpy as np
from PIL import Image, ImageTk

from image_ops import active_mask, composite_visible, composite_visible_rgba, polygon_mask, render_layer
from layer_panel import LayerPanel
from model import EditorState


APP_DIR = Path(__file__).resolve().parent
PART_NAMES_PATH = APP_DIR / "config" / "part-names.json"


class CutoutSpikeApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("FLAMORIS Manual Part Editor Spike")
        self.root.geometry("1440x900")
        self.root.minsize(980, 640)
        self.state = EditorState()
        self.source_path: Path | None = None
        self.part_names = self._load_part_names()
        self.mode = tk.StringVar(value="part-polygon")
        self.new_part_name = tk.StringVar(value=self.part_names[0] if self.part_names else "part")
        self.brush_size = tk.IntVar(value=28)
        self.brush_strength = tk.DoubleVar(value=0.35)
        self.status = tk.StringVar(value="Open an image to begin.")
        self.polygon_points: list[tuple[int, int]] = []
        self.preview_photo: ImageTk.PhotoImage | None = None
        self.drag_image_point: tuple[int, int] | None = None
        self.drag_canvas_point: tuple[int, int] | None = None
        self.last_brush_point: tuple[int, int] | None = None
        self._build_ui()
        self._bind_events()

    @staticmethod
    def _load_part_names() -> list[str]:
        try:
            values = json.loads(PART_NAMES_PATH.read_text(encoding="utf-8"))
            return [str(value) for value in values if str(value).strip()]
        except (OSError, json.JSONDecodeError):
            return ["eye_left", "eye_right", "face", "hair", "arm", "hand", "leg", "patch"]

    def _build_ui(self) -> None:
        toolbar = ttk.Frame(self.root, padding=(8, 7))
        toolbar.pack(side=tk.TOP, fill=tk.X)
        ttk.Button(toolbar, text="Open Image", command=self.open_image).pack(side=tk.LEFT, padx=(0, 8))
        for label, value in (
            ("Part Polygon", "part-polygon"),
            ("Patch Source", "patch-polygon"),
            ("Move Layer", "move"),
            ("Blur Brush", "blur"),
            ("Smudge Brush", "smudge"),
            ("Pan", "pan"),
        ):
            ttk.Radiobutton(toolbar, text=label, value=value, variable=self.mode, command=self.on_mode_changed).pack(side=tk.LEFT, padx=3)
        ttk.Label(toolbar, text="Size").pack(side=tk.LEFT, padx=(12, 3))
        ttk.Scale(toolbar, from_=2, to=200, variable=self.brush_size, orient=tk.HORIZONTAL, length=90).pack(side=tk.LEFT)
        ttk.Label(toolbar, text="Strength").pack(side=tk.LEFT, padx=(8, 3))
        ttk.Scale(toolbar, from_=0.05, to=1.0, variable=self.brush_strength, orient=tk.HORIZONTAL, length=80).pack(side=tk.LEFT)

        actions = ttk.Frame(self.root, padding=(8, 0, 8, 7))
        actions.pack(side=tk.TOP, fill=tk.X)
        ttk.Label(actions, text="New part:").pack(side=tk.LEFT)
        ttk.Combobox(actions, textvariable=self.new_part_name, values=self.part_names, width=16).pack(side=tk.LEFT, padx=(4, 4))
        ttk.Button(actions, text="Create Part Layer", command=self.create_part_layer).pack(side=tk.LEFT, padx=(0, 12))
        ttk.Button(actions, text="Undo Local Edit", command=self.undo_local_edit).pack(side=tk.LEFT, padx=3)
        ttk.Button(actions, text="Fit", command=self.fit_view).pack(side=tk.LEFT, padx=(12, 3))
        ttk.Button(actions, text="100%", command=self.actual_size).pack(side=tk.LEFT, padx=3)
        ttk.Button(actions, text="Zoom −", command=lambda: self.zoom_center(1 / 1.25)).pack(side=tk.LEFT, padx=(8, 3))
        ttk.Button(actions, text="Zoom +", command=lambda: self.zoom_center(1.25)).pack(side=tk.LEFT, padx=3)
        ttk.Button(actions, text="Export Composite PNG", command=self.export_composite).pack(side=tk.RIGHT, padx=3)
        ttk.Button(actions, text="Export Layer PNGs", command=self.export_layers).pack(side=tk.RIGHT, padx=3)

        ttk.Label(
            self.root,
            text=("Part Polygon = exact manual mask (Enter/double-click finalize, Esc cancel).  "
                  "Patch Source = polygon sampled only from immutable original."),
            padding=(10, 0, 8, 6),
            foreground="#555555",
        ).pack(side=tk.TOP, fill=tk.X)

        body = ttk.Frame(self.root)
        body.pack(fill=tk.BOTH, expand=True)
        self.canvas = tk.Canvas(body, background="#202020", highlightthickness=0)
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.layer_panel = LayerPanel(
            body,
            self.part_names,
            on_select=self.select_layer,
            on_toggle=self.toggle_layer,
            on_rename=self.rename_active_layer,
            on_delete=self.delete_active_layer,
            on_move=self.move_active_layer,
            on_transform=self.transform_active_layer,
            on_mask_toggle=self.refresh_preview,
            on_save_mask=self.save_active_mask,
        )
        self.layer_panel.pack(side=tk.RIGHT, fill=tk.Y)
        self.layer_panel.pack_propagate(False)
        ttk.Label(self.root, textvariable=self.status, anchor=tk.W, padding=(8, 5)).pack(side=tk.BOTTOM, fill=tk.X)

    def _bind_events(self) -> None:
        self.canvas.bind("<Configure>", lambda _event: self.refresh_preview())
        self.canvas.bind("<ButtonPress-1>", self.on_pointer_down)
        self.canvas.bind("<B1-Motion>", self.on_pointer_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_pointer_up)
        self.canvas.bind("<Double-Button-1>", self.finalize_polygon)
        self.canvas.bind("<MouseWheel>", self.on_mouse_wheel)
        self.canvas.bind("<Button-4>", lambda event: self._zoom_at(event.x, event.y, 1.15))
        self.canvas.bind("<Button-5>", lambda event: self._zoom_at(event.x, event.y, 1 / 1.15))
        self.canvas.bind("<ButtonPress-2>", self.start_middle_pan)
        self.canvas.bind("<B2-Motion>", self.middle_pan)
        self.canvas.bind("<ButtonRelease-2>", self.end_middle_pan)
        self.root.bind("<Return>", self.finalize_polygon)
        self.root.bind("<Escape>", self.cancel_polygon)

    def on_mode_changed(self) -> None:
        if self.mode.get() not in ("part-polygon", "patch-polygon"):
            self.cancel_polygon(silent=True)
        messages = {
            "part-polygon": "Click an exact part polygon. Enter/double-click finalizes; Esc cancels. No GrabCut is run.",
            "patch-polygon": "Click a polygon around clean source pixels. The sample always comes from immutable original.",
            "move": "Drag the active Patch layer. Scale and rotate it in the right panel.",
            "blur": "Paint a light blur onto the active derived layer only.",
            "smudge": "Drag gently to push pixels on the active derived layer only.",
            "pan": "Drag the viewport. Mouse wheel zooms around the cursor.",
        }
        self.status.set(messages[self.mode.get()])

    def open_image(self) -> None:
        path = filedialog.askopenfilename(title="Open image", filetypes=[("Images", "*.png *.jpg *.jpeg *.webp *.bmp"), ("All files", "*.*")])
        if not path:
            return
        try:
            original = np.asarray(Image.open(path).convert("RGB")).copy()
        except (OSError, ValueError) as exc:
            messagebox.showerror("Open failed", f"Could not decode image:\n{path}\n\n{exc}")
            return
        self.source_path = Path(path)
        self.state.reset(original)
        self.polygon_points.clear()
        self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
        self.root.title(f"FLAMORIS Manual Part Editor Spike - {self.source_path.name}")
        self.status.set(f"Loaded {original.shape[1]}×{original.shape[0]}. Part Polygon is exact; Patch Source samples the original.")
        self.refresh_preview()

    def on_pointer_down(self, event: tk.Event) -> None:
        if self.state.original_rgb is None:
            return
        mode = self.mode.get()
        if mode in ("part-polygon", "patch-polygon"):
            point = self._canvas_to_image(event.x, event.y)
            if point is not None and (not self.polygon_points or point != self.polygon_points[-1]):
                self.polygon_points.append(point)
                self.status.set(f"Polygon: {len(self.polygon_points)} points. Enter/double-click finalizes; Esc cancels.")
                self.refresh_preview()
            return
        if mode == "pan":
            self.drag_canvas_point = (event.x, event.y)
            return
        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return
        if mode == "move":
            self.drag_image_point = point
        elif mode == "blur":
            self._add_blur(point)
        elif mode == "smudge":
            self.last_brush_point = point

    def on_pointer_drag(self, event: tk.Event) -> None:
        if self.state.original_rgb is None:
            return
        mode = self.mode.get()
        if mode == "pan" and self.drag_canvas_point is not None:
            dx, dy = event.x - self.drag_canvas_point[0], event.y - self.drag_canvas_point[1]
            self.state.viewport.offset_x += dx
            self.state.viewport.offset_y += dy
            self.drag_canvas_point = (event.x, event.y)
            self.refresh_preview()
            return
        point = self._canvas_to_image(event.x, event.y)
        if point is None:
            return
        active = self.state.active_layer
        if mode == "move" and self.drag_image_point is not None and active is not None and active.kind == "patch":
            dx, dy = point[0] - self.drag_image_point[0], point[1] - self.drag_image_point[1]
            active.transform.center_x += dx
            active.transform.center_y += dy
            self.drag_image_point = point
            self.refresh_preview()
        elif mode == "blur":
            if self.last_brush_point is None or self._distance(self.last_brush_point, point) >= max(1, self.brush_size.get() // 4):
                self._add_blur(point)
        elif mode == "smudge" and self.last_brush_point is not None and self._distance(self.last_brush_point, point) >= 1:
            self._add_smudge(self.last_brush_point, point)
            self.last_brush_point = point

    def on_pointer_up(self, _event: tk.Event) -> None:
        self.drag_image_point = None
        self.drag_canvas_point = None
        self.last_brush_point = None

    @staticmethod
    def _distance(first: tuple[int, int], second: tuple[int, int]) -> float:
        return float(np.hypot(second[0] - first[0], second[1] - first[1]))

    def finalize_polygon(self, _event: tk.Event | None = None) -> str | None:
        if self.state.original_rgb is None or self.mode.get() not in ("part-polygon", "patch-polygon"):
            return None
        if len(self.polygon_points) < 3:
            self.status.set("Polygon needs at least three points. Continue clicking or press Esc.")
            return "break"
        mask = polygon_mask(self.state.original_rgb.shape[:2], self.polygon_points)
        if int(np.count_nonzero(mask)) < 16:
            self.status.set("Polygon is too small. Draw a larger selection.")
            return "break"
        if self.mode.get() == "part-polygon":
            self.state.pending_part_mask = mask
            self.status.set("Exact polygon mask ready. Choose/edit the semantic name, then press Create Part Layer.")
        else:
            center = self._preferred_patch_center(mask)
            name = self.state.next_name("patch")
            self.state.add_patch(name, self.polygon_points, center)
            self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
            self.status.set("Patch created from immutable original below Base. Move it or use scale/rotation in the right panel.")
        self.polygon_points = []
        self.refresh_preview()
        return "break"

    def cancel_polygon(self, _event: tk.Event | None = None, *, silent: bool = False) -> str | None:
        if self.polygon_points:
            self.polygon_points.clear()
            if not silent:
                self.status.set("Polygon cancelled.")
            self.refresh_preview()
            return "break"
        return None

    def _preferred_patch_center(self, source_mask: np.ndarray) -> tuple[float, float]:
        active = self.state.active_layer
        candidate = active.mask if active is not None and active.kind == "part" else None
        if candidate is None or not np.any(candidate):
            parts = [layer.mask for layer in self.state.layers if layer.kind == "part" and layer.mask is not None]
            if parts:
                candidate = np.zeros_like(parts[0])
                for item in parts:
                    candidate = cv2.bitwise_or(candidate, item)
        target = candidate if candidate is not None and np.any(candidate) else source_mask
        ys, xs = np.where(target > 0)
        return float(np.median(xs)), float(np.median(ys))

    def create_part_layer(self) -> None:
        if self.state.pending_part_mask is None or not np.any(self.state.pending_part_mask):
            self.status.set("Finalize a Part Polygon first.")
            return
        requested = self.new_part_name.get().strip() or "part"
        name = self.state.next_name(requested)
        self.state.add_part(name, self.state.pending_part_mask)
        self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
        self.status.set(f"Created exact manual part layer: {name}. Original source remains unchanged.")
        self.refresh_preview()

    def select_layer(self, layer_id: str) -> None:
        self.state.select(layer_id)
        self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
        active = self.state.active_layer
        if active is not None:
            self.status.set(f"Active layer: {active.name} ({active.kind}).")
        self.refresh_preview()

    def toggle_layer(self, layer_id: str) -> None:
        layer = next((item for item in self.state.layers if item.id == layer_id), None)
        if layer is not None:
            layer.visible = not layer.visible
            self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
            self.refresh_preview()

    def rename_active_layer(self, name: str) -> None:
        active = self.state.active_layer
        if active is not None and name:
            active.name = name
            self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
            self.status.set(f"Renamed active layer to {name}.")

    def delete_active_layer(self) -> None:
        active = self.state.active_layer
        if active is None:
            return
        name = active.name
        if not self.state.delete_active():
            self.status.set("Base is required and cannot be deleted.")
            return
        self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
        self.status.set(f"Deleted layer: {name}.")
        self.refresh_preview()

    def move_active_layer(self, direction: int) -> None:
        if self.state.move_active(direction):
            self.layer_panel.refresh(self.state.layers, self.state.active_layer_id)
            self.status.set("Layer order updated (panel is top-to-bottom).")
            self.refresh_preview()

    def transform_active_layer(self, scale: float, rotation: float) -> None:
        active = self.state.active_layer
        if active is None or active.kind != "patch":
            return
        active.transform.scale = float(np.clip(scale, 0.1, 10.0))
        active.transform.rotation_degrees = float(np.clip(rotation, -180.0, 180.0))
        self.status.set(f"Patch transform: {active.transform.scale * 100:.0f}% / {active.transform.rotation_degrees:.0f}°.")
        self.refresh_preview()

    def _add_blur(self, point: tuple[int, int]) -> None:
        active = self.state.active_layer
        if active is None:
            return
        active.local_edits.append({"kind": "blur", "x": point[0], "y": point[1], "radius": self.brush_size.get() / 2, "strength": self.brush_strength.get()})
        self.last_brush_point = point
        self.status.set(f"Blur applied to derived layer {active.name}; original is unchanged.")
        self.refresh_preview()

    def _add_smudge(self, start: tuple[int, int], end: tuple[int, int]) -> None:
        active = self.state.active_layer
        if active is None:
            return
        active.local_edits.append({"kind": "smudge", "from_x": start[0], "from_y": start[1], "to_x": end[0], "to_y": end[1], "radius": self.brush_size.get() / 2, "strength": self.brush_strength.get()})
        self.status.set(f"Smudge applied to derived layer {active.name}; original is unchanged.")
        self.refresh_preview()

    def undo_local_edit(self) -> None:
        active = self.state.active_layer
        if active is None or not active.local_edits:
            self.status.set("Active layer has no local edit to undo.")
            return
        active.local_edits.pop()
        self.status.set(f"Undid the last local edit on {active.name}.")
        self.refresh_preview()

    def fit_view(self) -> None:
        if self.state.original_rgb is None:
            return
        canvas_w, canvas_h = max(1, self.canvas.winfo_width()), max(1, self.canvas.winfo_height())
        image_h, image_w = self.state.original_rgb.shape[:2]
        zoom = max(0.02, min(canvas_w / image_w, canvas_h / image_h))
        self.state.viewport.zoom = zoom
        self.state.viewport.offset_x = (canvas_w - image_w * zoom) / 2
        self.state.viewport.offset_y = (canvas_h - image_h * zoom) / 2
        self.state.viewport.fit_pending = False
        self.status.set(f"Fit view: {zoom * 100:.0f}%.")
        self.refresh_preview()

    def actual_size(self) -> None:
        if self.state.original_rgb is None:
            return
        canvas_w, canvas_h = max(1, self.canvas.winfo_width()), max(1, self.canvas.winfo_height())
        image_h, image_w = self.state.original_rgb.shape[:2]
        self.state.viewport.zoom = 1.0
        self.state.viewport.offset_x = (canvas_w - image_w) / 2
        self.state.viewport.offset_y = (canvas_h - image_h) / 2
        self.state.viewport.fit_pending = False
        self.status.set("View: 100%.")
        self.refresh_preview()

    def on_mouse_wheel(self, event: tk.Event) -> None:
        self._zoom_at(event.x, event.y, 1.15 if event.delta > 0 else 1 / 1.15)

    def zoom_center(self, factor: float) -> None:
        self._zoom_at(max(1, self.canvas.winfo_width()) // 2, max(1, self.canvas.winfo_height()) // 2, factor)

    def _zoom_at(self, canvas_x: int, canvas_y: int, factor: float) -> None:
        if self.state.original_rgb is None:
            return
        viewport = self.state.viewport
        old_zoom = viewport.zoom
        new_zoom = float(np.clip(old_zoom * factor, 0.02, 16.0))
        image_x = (canvas_x - viewport.offset_x) / old_zoom
        image_y = (canvas_y - viewport.offset_y) / old_zoom
        viewport.zoom = new_zoom
        viewport.offset_x = canvas_x - image_x * new_zoom
        viewport.offset_y = canvas_y - image_y * new_zoom
        viewport.fit_pending = False
        self.status.set(f"View: {new_zoom * 100:.0f}%.")
        self.refresh_preview()

    def start_middle_pan(self, event: tk.Event) -> None:
        self.drag_canvas_point = (event.x, event.y)

    def middle_pan(self, event: tk.Event) -> None:
        if self.drag_canvas_point is None:
            return
        self.state.viewport.offset_x += event.x - self.drag_canvas_point[0]
        self.state.viewport.offset_y += event.y - self.drag_canvas_point[1]
        self.drag_canvas_point = (event.x, event.y)
        self.refresh_preview()

    def end_middle_pan(self, _event: tk.Event) -> None:
        self.drag_canvas_point = None

    def refresh_preview(self) -> None:
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
        preview = composite_visible(original, self.state.layers)
        if self.state.pending_part_mask is not None:
            selected = self.state.pending_part_mask > 0
            preview[selected] = (preview[selected] * 0.72 + np.array([70, 220, 130]) * 0.28).astype(np.uint8)
        active = self.state.active_layer
        if self.layer_panel.mask_overlay.get() and active is not None:
            mask = active_mask(original, self.state.layers, active) > 0
            preview[mask] = (preview[mask] * 0.72 + np.array([235, 70, 170]) * 0.28).astype(np.uint8)
        viewport = self.state.viewport
        inverse = (1.0 / viewport.zoom, 0.0, -viewport.offset_x / viewport.zoom, 0.0, 1.0 / viewport.zoom, -viewport.offset_y / viewport.zoom)
        displayed = Image.fromarray(preview).transform((canvas_w, canvas_h), Image.Transform.AFFINE, inverse, resample=Image.Resampling.BILINEAR, fillcolor=(32, 32, 32))
        self.preview_photo = ImageTk.PhotoImage(displayed)
        self.canvas.delete("all")
        self.canvas.create_image(0, 0, anchor=tk.NW, image=self.preview_photo, tags="image")
        self._draw_polygon_preview()

    def _draw_polygon_preview(self) -> None:
        if not self.polygon_points:
            return
        points = [self._image_to_canvas(point) for point in self.polygon_points]
        if len(points) > 1:
            line_points = points + ([points[0]] if len(points) > 2 else [])
            self.canvas.create_line(*[coordinate for point in line_points for coordinate in point], fill="#f4c16a", width=2, dash=(5, 3), tags="selection")
        for x, y in points:
            self.canvas.create_oval(x - 3, y - 3, x + 3, y + 3, outline="#f4c16a", fill="#202020", tags="selection")

    def _image_to_canvas(self, point: tuple[int, int]) -> tuple[int, int]:
        viewport = self.state.viewport
        return int(round(viewport.offset_x + point[0] * viewport.zoom)), int(round(viewport.offset_y + point[1] * viewport.zoom))

    def _canvas_to_image(self, canvas_x: int, canvas_y: int) -> tuple[int, int] | None:
        original = self.state.original_rgb
        if original is None:
            return None
        viewport = self.state.viewport
        x = int((canvas_x - viewport.offset_x) / viewport.zoom)
        y = int((canvas_y - viewport.offset_y) / viewport.zoom)
        height, width = original.shape[:2]
        return (x, y) if 0 <= x < width and 0 <= y < height else None

    def save_active_mask(self) -> None:
        original, active = self.state.original_rgb, self.state.active_layer
        if original is None or active is None:
            return
        path = filedialog.asksaveasfilename(title="Save active mask", defaultextension=".png", filetypes=[("PNG", "*.png")])
        if path:
            Image.fromarray(active_mask(original, self.state.layers, active)).save(path)
            self.status.set(f"Saved active mask: {Path(path).name}")

    def export_composite(self) -> None:
        original = self.state.original_rgb
        if original is None:
            return
        path = filedialog.asksaveasfilename(title="Export flattened composite", defaultextension=".png", filetypes=[("PNG", "*.png")])
        if path:
            Image.fromarray(composite_visible_rgba(original, self.state.layers), mode="RGBA").save(path)
            self.status.set(f"Exported flattened RGBA composite: {Path(path).name}")

    @staticmethod
    def _safe_filename(name: str) -> str:
        cleaned = re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("._")
        return cleaned or "layer"

    def export_layers(self) -> None:
        original = self.state.original_rgb
        if original is None:
            return
        directory = filedialog.askdirectory(title="Export transparent layer PNGs")
        if not directory:
            return
        target = Path(directory)
        used: set[str] = set()
        for index, layer in enumerate(self.state.layers, start=1):
            stem = self._safe_filename(layer.name)
            filename = f"{index:02d}_{stem}.png"
            suffix = 2
            while filename in used:
                filename = f"{index:02d}_{stem}_{suffix}.png"
                suffix += 1
            used.add(filename)
            Image.fromarray(render_layer(original, self.state.layers, layer), mode="RGBA").save(target / filename)
        self.status.set(f"Exported {len(self.state.layers)} transparent layer PNGs to {target.name}.")


def main() -> None:
    root = tk.Tk()
    CutoutSpikeApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
