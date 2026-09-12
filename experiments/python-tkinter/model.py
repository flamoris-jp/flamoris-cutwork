from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Literal
from uuid import uuid4

import numpy as np


LayerKind = Literal["base", "part", "patch", "repair"]


@dataclass
class LayerTransform:
    center_x: float = 0.0
    center_y: float = 0.0
    scale: float = 1.0
    rotation_degrees: float = 0.0


@dataclass
class EditorLayer:
    name: str
    kind: LayerKind
    visible: bool = True
    mask: np.ndarray | None = None
    source_polygon: list[tuple[int, int]] | None = None
    paint_rgba: np.ndarray | None = None
    transform: LayerTransform = field(default_factory=LayerTransform)
    local_edits: list[dict[str, Any]] = field(default_factory=list)
    id: str = field(default_factory=lambda: uuid4().hex)


@dataclass
class ViewportState:
    zoom: float = 1.0
    offset_x: float = 0.0
    offset_y: float = 0.0
    fit_pending: bool = True


@dataclass
class EditorState:
    original_rgb: np.ndarray | None = None
    # Display order is top-to-bottom. Compositing therefore iterates reversed.
    layers: list[EditorLayer] = field(default_factory=list)
    active_layer_id: str | None = None
    pending_part_mask: np.ndarray | None = None
    viewport: ViewportState = field(default_factory=ViewportState)

    def reset(self, original_rgb: np.ndarray) -> None:
        self.original_rgb = original_rgb.copy()
        base = EditorLayer(name="base", kind="base")
        self.layers = [base]
        self.active_layer_id = base.id
        self.pending_part_mask = None
        self.viewport = ViewportState()

    @property
    def active_layer(self) -> EditorLayer | None:
        return next((layer for layer in self.layers if layer.id == self.active_layer_id), None)

    def select(self, layer_id: str) -> None:
        if any(layer.id == layer_id for layer in self.layers):
            self.active_layer_id = layer_id

    def add_part(self, name: str, mask: np.ndarray) -> EditorLayer:
        layer = EditorLayer(name=name, kind="part", mask=mask.copy())
        base_index = next((i for i, item in enumerate(self.layers) if item.kind == "base"), len(self.layers))
        self.layers.insert(base_index, layer)
        self.active_layer_id = layer.id
        self.pending_part_mask = None
        return layer

    def add_patch(
        self,
        name: str,
        source_polygon: list[tuple[int, int]],
        center: tuple[float, float],
    ) -> EditorLayer:
        layer = EditorLayer(
            name=name,
            kind="patch",
            source_polygon=list(source_polygon),
            transform=LayerTransform(center_x=center[0], center_y=center[1]),
        )
        # Default drawing order is Patch/Repair -> Base -> Parts, so derived
        # underpaint appears only through holes cut out of Base.
        self.layers.append(layer)
        self.active_layer_id = layer.id
        return layer

    def add_repair(self, name: str) -> EditorLayer:
        if self.original_rgb is None:
            raise ValueError("cannot add a repair layer without an image")
        height, width = self.original_rgb.shape[:2]
        layer = EditorLayer(
            name=name,
            kind="repair",
            paint_rgba=np.zeros((height, width, 4), dtype=np.uint8),
        )
        self.layers.append(layer)
        self.active_layer_id = layer.id
        return layer

    def delete_active(self) -> bool:
        layer = self.active_layer
        if layer is None or layer.kind == "base":
            return False
        index = self.layers.index(layer)
        self.layers.remove(layer)
        if self.layers:
            self.active_layer_id = self.layers[min(index, len(self.layers) - 1)].id
        return True

    def move_active(self, direction: int) -> bool:
        """Move active layer visually; -1 is up and +1 is down."""
        layer = self.active_layer
        if layer is None:
            return False
        index = self.layers.index(layer)
        target = index + direction
        if target < 0 or target >= len(self.layers):
            return False
        self.layers[index], self.layers[target] = self.layers[target], self.layers[index]
        return True

    def next_name(self, stem: str) -> str:
        existing = {layer.name for layer in self.layers}
        if stem not in existing:
            return stem
        number = 2
        while f"{stem}_{number}" in existing:
            number += 1
        return f"{stem}_{number}"
