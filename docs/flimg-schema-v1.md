# `.flimg` schema v1

`.flimg` is a Cutwork-owned ZIP container for authored project state. Version 1 reconstructs the current production `CutworkDocument`; it is not a generic layered-image or history format.

## Canonical archive layout

```text
manifest.json
assets/original.png
layers/<layer-id-n>/mask.png       # Part only
layers/<layer-id-n>/pixels.png     # Patch and Repair only
```

`<layer-id-n>` is the stable UUID in lowercase 32-digit `N` format. Entry names use forward slashes, Unicode NFC, and ordinal lowercase ASCII for fixed path components. v1 contains no directory entries or unreferenced entries. ZIP entry timestamps are fixed to `1980-01-01T00:00:00Z`; byte-identical archives are not otherwise promised.

PNG is the only asset encoding. Original, Patch, and Repair are straight-alpha BGRA8 in memory and lossless RGBA PNG in the container. Part is lossless Gray8 PNG. The normalized Original PNG—not the initially imported JPEG/PNG bytes—is the immutable authoring authority after reopen.

## Manifest

The UTF-8 `manifest.json` uses invariant English property names and enum tokens. Layers are ordered exactly as the production stack, top to bottom.

```json
{
  "format": "flamoris-cutwork",
  "schemaVersion": 1,
  "documentId": "00000000-0000-0000-0000-000000000000",
  "canvas": {
    "width": 1920,
    "height": 1080,
    "colorSpace": "srgb8",
    "pixelFormat": "straight-bgra32"
  },
  "original": {
    "asset": "assets/original.png",
    "sha256": "lowercase-hex",
    "sourceName": "artwork.jpg"
  },
  "layers": [
    {
      "id": "uuid",
      "kind": "part",
      "name": "left eye",
      "semanticName": "eye_left",
      "visible": true,
      "bounds": { "x": 10, "y": 20, "width": 100, "height": 80 },
      "asset": "layers/<id-n>/mask.png",
      "sha256": "lowercase-hex"
    },
    {
      "id": "uuid",
      "kind": "base",
      "name": "",
      "semanticName": null,
      "visible": true,
      "bounds": { "x": 0, "y": 0, "width": 1920, "height": 1080 }
    }
  ]
}
```

Common layer fields are `id`, `kind`, `name`, nullable `semanticName`, `visible`, and `bounds`. Kind-specific fields are:

| Kind | `bounds` meaning | Asset and metadata |
|---|---|---|
| `part` | authored document-space mask bounds | Gray8 `asset` + `sha256` |
| `base` | full canvas | no asset |
| `patch` | frozen source-local bounds in Original document coordinates | BGRA8 `asset` + `sha256`, `transform`, and `sourcePolygon` |
| `repair` | authored document-space raster bounds | BGRA8 `asset` + `sha256` |

Patch `transform` contains finite `centerX`, `centerY`, `scale`, and normalized `rotationDegrees`. Scale follows the authored production range 0.01–10.0. `sourcePolygon` is an array of finite document points and preserves current provenance; it is not resampled during load or display.

Asset hashes are SHA-256 over the encoded PNG entry bytes. JSON is written with a stable property and layer order. A loader must not depend on insignificant whitespace.

## Validation and safety limits

The reader treats the entire archive as untrusted and constructs a fresh document before any session replacement. It rejects:

- missing, duplicate, malformed, or oversized manifests;
- format tokens other than `flamoris-cutwork` and schema versions other than `1`;
- duplicate JSON properties or unknown v1 properties;
- empty/duplicate stable IDs;
- missing or extra Base layers, a Base outside the fixed divider position, or band-crossing order;
- non-positive/out-of-range canvas dimensions or more than 100,000,000 pixels;
- bounds outside the canvas, PNG dimension/format mismatches, malformed PNG, or checksum mismatch;
- missing, unexpected, duplicate, absolute, backslash, drive-qualified, dot-segment, non-NFC, or case/canonical-colliding entry paths;
- invalid/non-finite Patch transforms, out-of-range scale, transformed bounds outside the canvas, or invalid provenance points; and
- more than 4,096 entries, any entry over 512 MiB uncompressed, more than 1 GiB total uncompressed data, or a manifest over 4 MiB.

Archive entries are read directly; they are never extracted to the filesystem. Ambiguous input is rejected rather than guessed.

## Version dispatch

The container is dispatched by integer `schemaVersion`. The registry has exactly one implementation in Phase 7: v1. Unknown older or future versions produce a stable unsupported-version error. No speculative v2 migration exists.

## Save and session boundary

Save writes and closes a sibling temporary file, validates the completed archive, flushes it, and only then replaces the destination. An existing destination remains intact on any pre-replacement failure; temporary cleanup is best effort. `ProjectPath` and `EditorSession.MarkSaved()` update only after replacement succeeds.

History, current selection, active/pending tool state, cursor/hover, overlays, viewport, cache buffers/revisions, and dirty/saved revision numbers are not serialized. A successful Open creates a fresh clean session with zero Undo/Redo entries.

## Export conventions

Composite export encodes the full document composite generated by `CompositeCache`, independent of viewport state.

Layer handoff is an atomic ZIP containing `handoff.json` plus lossless PNG assets named `<order-4>-<kind>-<stable-id-n>.png`. Display names never determine filenames. The JSON includes document size and each layer's stable ID, order, kind, names, visibility, relevant bounds, asset path, and Patch transform/provenance.

- Part: cropped Original RGB with alpha multiplied by the authored mask; JSON bounds are document-space crop bounds.
- Base: metadata only; consumers may derive it from Original plus the authored Part masks.
- Patch: frozen patch-local RGBA without transform baking; JSON carries source bounds, transformed document bounds, transform, and provenance.
- Repair: cropped authored RGBA with document-space bounds.

The handoff format is neutral and has no FLAMORIS 2D runtime dependency.
