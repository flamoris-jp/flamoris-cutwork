# `.flimg` schema v2

Version 2 is the current Cutwork project format. It retains the v1 ZIP layout, pixel assets,
stable layer identities, compositor stack order, validation limits, and atomic save boundary.
It adds only the authored metadata needed to keep semantic Parts independent from compositor
Layers and to preserve Part-owned underpaint.

## Manifest additions

Every `part` layer has a required non-negative, document-unique `partOrder` integer. This is
the stable semantic/creation order shown in the Parts pane. Reordering compositor Layers does
not change it.

A `repair` layer may have an `ownerPartId` containing the lowercase `D` UUID of an existing
Part in the same document. A missing value means legacy/global underpaint. The property is not
valid on Base, Part, or Patch layers.

```json
{
  "format": "flamoris-cutwork",
  "schemaVersion": 2,
  "layers": [
    {
      "id": "10000000-0000-0000-0000-000000000000",
      "kind": "part",
      "name": "Face",
      "semanticName": "face",
      "partOrder": 0,
      "visible": true,
      "bounds": { "x": 10, "y": 20, "width": 100, "height": 80 },
      "asset": "layers/10000000000000000000000000000000/mask.png",
      "sha256": "lowercase-hex"
    },
    {
      "id": "20000000-0000-0000-0000-000000000000",
      "kind": "repair",
      "name": "",
      "semanticName": null,
      "ownerPartId": "10000000-0000-0000-0000-000000000000",
      "visible": true,
      "bounds": { "x": 10, "y": 20, "width": 100, "height": 80 },
      "asset": "layers/20000000000000000000000000000000/pixels.png",
      "sha256": "lowercase-hex"
    }
  ]
}
```

All common and kind-specific v1 fields not shown above remain required exactly as documented
in [schema v1](flimg-schema-v1.md). Unknown properties, mixed missing/present `partOrder`
values, duplicate/negative Part orders, empty or dangling owner IDs, and ownership targeting a
non-Part layer are rejected.

## v1 migration

The reader accepts frozen v1 archives. v1 has no Part ownership, so Repair layers migrate as
legacy/global underpaint with no owner. v1 created Parts at the top of the Part compositor band;
the reader therefore reverses their stored top-to-bottom order once to recover the closest
deterministic creation order. Saving the migrated document writes v2 and persists that order.

## Delete and handoff

Deleting a Part deletes its owned Repair layers in the same transaction. Undo restores every
stable identity, stack index, owner link, bounds, and raster byte. Base remains undeletable.

Layer handoff continues to use compositor `order`, and additionally exports `partOrder` for
Parts and `ownerPartId` for owned Repairs. This preserves both semantic ownership and draw order
without introducing a second layer stack or document model. Handoff writers emit version 2 so
strict version-1 consumers never silently misinterpret the added ownership metadata.
