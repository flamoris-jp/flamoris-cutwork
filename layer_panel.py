from __future__ import annotations

import tkinter as tk
from collections.abc import Callable
from tkinter import ttk

from model import EditorLayer


class LayerPanel(ttk.Frame):
    """Small experimental layer/mask panel, intentionally not a full editor UI."""

    def __init__(
        self,
        parent: tk.Misc,
        presets: list[str],
        *,
        on_select: Callable[[str], None],
        on_toggle: Callable[[str], None],
        on_rename: Callable[[str], None],
        on_delete: Callable[[], None],
        on_move: Callable[[int], None],
        on_transform: Callable[[float, float], None],
        on_mask_toggle: Callable[[], None],
        on_save_mask: Callable[[], None],
    ) -> None:
        super().__init__(parent, padding=8, width=300)
        self.on_select = on_select
        self.on_toggle = on_toggle
        self.on_rename = on_rename
        self.on_transform = on_transform
        self._syncing = False
        self._active_id: str | None = None
        self._active_name = ""

        ttk.Label(self, text="Layers", font=("TkDefaultFont", 11, "bold")).pack(anchor=tk.W)
        self.tree = ttk.Treeview(self, columns=("visible", "name", "kind"), show="headings", height=13)
        self.tree.heading("visible", text="On")
        self.tree.heading("name", text="Layer")
        self.tree.heading("kind", text="Kind")
        self.tree.column("visible", width=38, anchor=tk.CENTER, stretch=False)
        self.tree.column("name", width=150, anchor=tk.W)
        self.tree.column("kind", width=65, anchor=tk.W, stretch=False)
        self.tree.pack(fill=tk.X, pady=(5, 6))
        self.tree.bind("<<TreeviewSelect>>", self._selected)
        self.tree.bind("<Button-1>", self._clicked)

        ordering = ttk.Frame(self)
        ordering.pack(fill=tk.X)
        ttk.Button(ordering, text="Up", command=lambda: on_move(-1)).pack(side=tk.LEFT, expand=True, fill=tk.X, padx=(0, 2))
        ttk.Button(ordering, text="Down", command=lambda: on_move(1)).pack(side=tk.LEFT, expand=True, fill=tk.X, padx=2)
        ttk.Button(ordering, text="Delete", command=on_delete).pack(side=tk.LEFT, expand=True, fill=tk.X, padx=(2, 0))

        ttk.Label(self, text="Semantic name").pack(anchor=tk.W, pady=(10, 2))
        self.name_var = tk.StringVar()
        self.name_combo = ttk.Combobox(self, textvariable=self.name_var, values=presets)
        self.name_combo.pack(fill=tk.X)
        self.name_combo.bind("<<ComboboxSelected>>", self._rename)
        self.name_combo.bind("<Return>", self._rename)
        self.name_combo.bind("<FocusOut>", self._rename)

        transform = ttk.LabelFrame(self, text="Patch transform", padding=6)
        transform.pack(fill=tk.X, pady=(10, 0))
        self.scale_var = tk.DoubleVar(value=100.0)
        self.rotation_var = tk.DoubleVar(value=0.0)
        ttk.Label(transform, text="Scale 10–1000%").pack(anchor=tk.W)
        ttk.Scale(transform, from_=10, to=1000, variable=self.scale_var, command=self._transform).pack(fill=tk.X)
        ttk.Label(transform, text="Rotation -180–180°").pack(anchor=tk.W, pady=(5, 0))
        ttk.Scale(transform, from_=-180, to=180, variable=self.rotation_var, command=self._transform).pack(fill=tk.X)

        masks = ttk.LabelFrame(self, text="Masks", padding=6)
        masks.pack(fill=tk.X, pady=(10, 0))
        self.mask_overlay = tk.BooleanVar(value=False)
        ttk.Checkbutton(masks, text="Show active mask overlay", variable=self.mask_overlay, command=on_mask_toggle).pack(anchor=tk.W)
        ttk.Button(masks, text="Save Active Mask", command=on_save_mask).pack(fill=tk.X, pady=(5, 0))

        ttk.Label(
            self,
            text="Stack is shown top-to-bottom.\nDefault composite order: Patch → Base → Part.",
            foreground="#666666",
            wraplength=270,
        ).pack(anchor=tk.W, pady=(12, 0))

    def refresh(self, layers: list[EditorLayer], active_id: str | None) -> None:
        self._syncing = True
        try:
            self._active_id = active_id
            self.tree.delete(*self.tree.get_children())
            active: EditorLayer | None = None
            for layer in layers:
                self.tree.insert("", tk.END, iid=layer.id, values=("✓" if layer.visible else "", layer.name, layer.kind))
                if layer.id == active_id:
                    active = layer
            if active is not None:
                self.tree.selection_set(active.id)
                self.tree.focus(active.id)
                self.name_var.set(active.name)
                self._active_name = active.name
                self.scale_var.set(active.transform.scale * 100.0)
                self.rotation_var.set(active.transform.rotation_degrees)
            else:
                self.name_var.set("")
                self._active_name = ""
        finally:
            self._syncing = False

    def _selected(self, _event: tk.Event) -> None:
        if self._syncing:
            return
        selection = self.tree.selection()
        if selection and selection[0] != self._active_id:
            self.on_select(selection[0])

    def _clicked(self, event: tk.Event) -> None:
        if self.tree.identify_region(event.x, event.y) != "cell":
            return
        row = self.tree.identify_row(event.y)
        column = self.tree.identify_column(event.x)
        if row and column == "#1":
            self.on_toggle(row)
            return "break"

    def _rename(self, _event: tk.Event | None = None) -> None:
        name = self.name_var.get().strip()
        if not self._syncing and name and name != self._active_name:
            self.on_rename(name)

    def _transform(self, _value: str | None = None) -> None:
        if not self._syncing:
            self.on_transform(self.scale_var.get() / 100.0, self.rotation_var.get())
