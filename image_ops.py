from __future__ import annotations

import math
from typing import Iterable

import cv2
import numpy as np

from model import EditorLayer


def polygon_mask(shape: tuple[int, int], points: Iterable[tuple[int, int]]) -> np.ndarray:
    """Return an exact binary polygon mask without GrabCut or border rewriting."""
    mask = np.zeros(shape, dtype=np.uint8)
    contour = np.asarray(list(points), dtype=np.int32).reshape((-1, 1, 2))
    if len(contour) >= 3:
        cv2.fillPoly(mask, [contour], 255)
    return mask


def checkerboard(height: int, width: int, square: int = 16) -> np.ndarray:
    yy, xx = np.indices((height, width))
    tiles = ((xx // square) + (yy // square)) % 2
    return np.where(tiles[..., None] == 0, (72, 72, 72), (112, 112, 112)).astype(np.uint8)


def alpha_over(background_rgb: np.ndarray, foreground_rgba: np.ndarray) -> np.ndarray:
    alpha = foreground_rgba[:, :, 3:4].astype(np.float32) / 255.0
    return (foreground_rgba[:, :, :3] * alpha + background_rgb * (1.0 - alpha)).astype(np.uint8)


def union_part_masks(layers: list[EditorLayer], shape: tuple[int, int]) -> np.ndarray:
    result = np.zeros(shape, dtype=np.uint8)
    for layer in layers:
        if layer.kind == "part" and layer.mask is not None:
            result = cv2.bitwise_or(result, layer.mask)
    return result


def _original_layer(original_rgb: np.ndarray, alpha: np.ndarray) -> np.ndarray:
    return np.dstack((original_rgb, alpha.astype(np.uint8)))


def _patch_rgba(original_rgb: np.ndarray, layer: EditorLayer) -> np.ndarray:
    height, width = original_rgb.shape[:2]
    output = np.zeros((height, width, 4), dtype=np.uint8)
    points = layer.source_polygon or []
    if len(points) < 3:
        return output

    source_mask = polygon_mask((height, width), points)
    x, y, crop_w, crop_h = cv2.boundingRect(np.asarray(points, dtype=np.int32))
    if crop_w <= 0 or crop_h <= 0:
        return output
    crop = np.dstack((original_rgb[y : y + crop_h, x : x + crop_w], source_mask[y : y + crop_h, x : x + crop_w]))

    scale = max(0.01, min(10.0, float(layer.transform.scale)))
    angle = math.radians(float(layer.transform.rotation_degrees))
    cos_a, sin_a = math.cos(angle) * scale, math.sin(angle) * scale
    source_center_x = (crop_w - 1) / 2.0
    source_center_y = (crop_h - 1) / 2.0
    matrix = np.array(
        [
            [cos_a, sin_a, layer.transform.center_x - cos_a * source_center_x - sin_a * source_center_y],
            [-sin_a, cos_a, layer.transform.center_y + sin_a * source_center_x - cos_a * source_center_y],
        ],
        dtype=np.float32,
    )
    # Warp directly into the canvas. Even a 1000% patch never allocates a huge
    # intermediate raster, which keeps this disposable tool predictable.
    output = cv2.warpAffine(
        crop,
        matrix,
        (width, height),
        flags=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0, 0),
    )
    output[:, :, 3] = cv2.GaussianBlur(output[:, :, 3], (0, 0), 1.2)
    return output


def _apply_blur(rgba: np.ndarray, edit: dict[str, float]) -> np.ndarray:
    radius = max(1, int(edit["radius"]))
    strength = float(np.clip(edit["strength"], 0.0, 1.0))
    x, y = int(edit["x"]), int(edit["y"])
    local_mask = np.zeros(rgba.shape[:2], dtype=np.uint8)
    cv2.circle(local_mask, (x, y), radius, 255, -1, lineType=cv2.LINE_AA)
    local_mask = cv2.GaussianBlur(local_mask, (0, 0), max(0.8, radius * 0.18))
    sigma = max(0.8, radius * 0.16)
    blurred = cv2.GaussianBlur(rgba, (0, 0), sigma)
    weight = (local_mask.astype(np.float32) / 255.0 * strength)[..., None]
    return np.clip(rgba * (1.0 - weight) + blurred * weight, 0, 255).astype(np.uint8)


def _apply_smudge(rgba: np.ndarray, edit: dict[str, float]) -> np.ndarray:
    x0, y0 = int(edit["from_x"]), int(edit["from_y"])
    x1, y1 = int(edit["to_x"]), int(edit["to_y"])
    radius = max(1, int(edit["radius"]))
    strength = float(np.clip(edit["strength"], 0.0, 1.0))
    dx, dy = x1 - x0, y1 - y0
    if dx == 0 and dy == 0:
        return rgba
    translated = cv2.warpAffine(
        rgba,
        np.float32([[1, 0, dx], [0, 1, dy]]),
        (rgba.shape[1], rgba.shape[0]),
        flags=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0, 0),
    )
    local_mask = np.zeros(rgba.shape[:2], dtype=np.uint8)
    cv2.circle(local_mask, (x1, y1), radius, 255, -1, lineType=cv2.LINE_AA)
    local_mask = cv2.GaussianBlur(local_mask, (0, 0), max(0.8, radius * 0.15))
    weight = (local_mask.astype(np.float32) / 255.0 * strength)[..., None]
    return np.clip(rgba * (1.0 - weight) + translated * weight, 0, 255).astype(np.uint8)


def apply_local_edits(rgba: np.ndarray, edits: list[dict[str, float]]) -> np.ndarray:
    result = rgba
    for edit in edits:
        if edit.get("kind") == "blur":
            result = _apply_blur(result, edit)
        elif edit.get("kind") == "smudge":
            result = _apply_smudge(result, edit)
    return result


def render_layer(original_rgb: np.ndarray, layers: list[EditorLayer], layer: EditorLayer) -> np.ndarray:
    height, width = original_rgb.shape[:2]
    if layer.kind == "base":
        alpha = cv2.bitwise_not(union_part_masks(layers, (height, width)))
        rgba = _original_layer(original_rgb, alpha)
    elif layer.kind == "part":
        alpha = layer.mask if layer.mask is not None else np.zeros((height, width), dtype=np.uint8)
        rgba = _original_layer(original_rgb, alpha)
    else:
        rgba = _patch_rgba(original_rgb, layer)
    return apply_local_edits(rgba, layer.local_edits)


def active_mask(original_rgb: np.ndarray, layers: list[EditorLayer], layer: EditorLayer) -> np.ndarray:
    if layer.kind == "base":
        return cv2.bitwise_not(union_part_masks(layers, original_rgb.shape[:2]))
    return render_layer(original_rgb, layers, layer)[:, :, 3]


def composite_visible(original_rgb: np.ndarray, layers: list[EditorLayer]) -> np.ndarray:
    result = checkerboard(*original_rgb.shape[:2])
    for layer in reversed(layers):
        if layer.visible:
            result = alpha_over(result, render_layer(original_rgb, layers, layer))
    return result


def composite_visible_rgba(original_rgb: np.ndarray, layers: list[EditorLayer]) -> np.ndarray:
    height, width = original_rgb.shape[:2]
    result = np.zeros((height, width, 4), dtype=np.uint8)
    for layer in reversed(layers):
        if not layer.visible:
            continue
        foreground = render_layer(original_rgb, layers, layer)
        fg_alpha = foreground[:, :, 3:4].astype(np.float32) / 255.0
        bg_alpha = result[:, :, 3:4].astype(np.float32) / 255.0
        out_alpha = fg_alpha + bg_alpha * (1.0 - fg_alpha)
        numerator = foreground[:, :, :3] * fg_alpha + result[:, :, :3] * bg_alpha * (1.0 - fg_alpha)
        color = np.divide(numerator, np.maximum(out_alpha, 1e-6), out=np.zeros_like(numerator), where=out_alpha > 0)
        result[:, :, :3] = np.clip(color, 0, 255).astype(np.uint8)
        result[:, :, 3:4] = np.clip(out_alpha * 255.0, 0, 255).astype(np.uint8)
    return result
