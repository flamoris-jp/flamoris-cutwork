# Repair tools and revisioned saving

Implementation added 2026-10-24. Real portrait seam quality and final animation playback
remain separate acceptance work; synthetic regression success is not visual approval.
The portrait session notes describe the tools available **at the time of that session**.

## Observe, compare, commit

1. Read `context` and `layers`; use the actual document token, monotonic revision and layer UUIDs.
2. `repair_inspect`: explicit Part `target`, document `roi` and `maxEdge`. Returns the actual
   Part hole mask, up to 4096 boundary pixels, mean Original color/alpha outside the mask,
   Original and Composite PNGs in the same coordinates. It does not infer hair or cheek intent.
3. `clone_candidates`: `variants` (1–3 ordinary `clone.stroke` operations), a common `roi`
   containing **all destination footprints**, and `maxEdge`. Returns before/after/difference,
   changed pixel count, source bounds (including bilinear halo), pixel SHA256 and budget usage.
4. `repair_commit_candidate`: candidateId and the ordinary edit guard. Uses the exact previewed
   Repair pixels, one Undo entry. Latest set only; expires after five minutes or any document,
   revision or grant change; consumed on commit. Preview is available read-only; commit requires Edit.
5. Hide the relevant Part and inspect repaired boundaries; restore display and test final motion.

Clone additions are optional. Old payloads retain their byte-exact floor/last-dab semantics:

| Field | Range / default | Meaning |
|---|---|---|
| sourceKind | Original / CompositeAtStrokeStart; Original | Visible Composite is frozen before painting |
| sourceScale | .25–8; 1 | Source image enlargement; destination diameter 40 at 4 samples diameter 10 |
| sourceRotationDegrees | -180–180; 0 | Clockwise source rotation in image coordinates |
| hardness | 0–1; 1 | Smoothstep falloff outside hardness × radius |
| opacity | 0–1; 1 | Maximum coverage in this stroke; later strokes may build opacity |

`radius`, `mode` Fixed/Offset, `source`, `points` and explicit existing Repair target OR
ownerPart OR global=true remain required. Source transparency contributes nothing. Transformed
sampling interpolates premultiplied color and composites source-over, storing straight BGRA.
Composite samples reflect visibility exactly; hide eyes before sampling their underpaint.
A new stroke can sample prior repairs; a stroke never recursively samples its own paint.

MCP validates the entire path and freezes only its transformed source ROI. Existing radius,
work, history, growth, layer and PNG budgets remain. UI pointer strokes do not know their
future path: Fixed freezes its source footprint; Offset Composite conservatively freezes the
whole source, and refuses above 262144 pixels / 8 million layer visits. Use Fixed or bounded
MCP paths for large artwork. The UI reports refusal instead of painting partial results.

`blur.stroke` / `smudge.stroke` are `edit` operations with Repair `target`, `points`, `radius`
and `strength` (.01–1). They share UI kernels, including the final endpoint. They cannot
modify Patch or Base. `patch.bakeToRepair` explicitly converts a fully in-document Patch into
pixel-identical Repair at the same compositor index, preserving name/semantic/visibility.
It is one Undo, restoring the original Patch ID and transform. Baking clips no off-document pixels;
move such a Patch inside the document first. Transform editing ends after bake.

## Save a revision

First save the project through UI. Under **File**, enable **AIからこのプロジェクトの版付き保存を許可**.
This authorizes only that exact target. It is separate from MCP Edit and resets on disconnect,
permission change, Open or Save As to a different target. No path is accepted from AI.

1. Read `project_context`: `saveAuthorized`, `busy` and `target`. The target contains the opaque
   `saveTargetId`, current `checkpointId` (null initially), `expectedWorkingFileHash`, count/capacity.
2. Call `project_checkpoint` (archive only) or `project_save` (archive then working copy), using
   ordinary edit guard plus saveTargetId, expectedCheckpointId, expectedWorkingFileHash,
   UUID `requestId` and label (≤128 chars, never a filename).
3. The immediate result is a running job. Poll `project_save_status` with requestId. A completed
   job is not enough: read receipt.checkpointSaved and receipt.workingCopySaved separately.
4. Checkpoint alone never clears dirty. Save marks saved only if the exact captured document
   and history state still match. Snapshot / deadline failures release the save gate.
5. For partial success, retain the checkpoint. An explicit repeat of the same save with
   `resume:true` can update the working copy without adding a version, provided it is still the
   latest checkpoint and the working hash matches. Ordinary repeats return the existing receipt;
   the same requestId with different metadata or canonical project bytes is rejected.
6. `project_checkpoints_list` accepts offset/count (≤20). `project_save_cancel` requests cancellation
   before checkpoint commit; a committed checkpoint survives cancellation or disconnect.

Archives are complete v2 `.flimg` files in a sibling `<project>.flimg.revisions` directory.
`r000000001.flimg` numbering includes orphan files, so failed index publication never reuses
numbers. `index.json` publishes durable receipts by atomic replacement. Working replacement
copies the exact verified archive. The UI ordinary Save shares the target lock once authorized.
The head archive digest is checked before advertising it. Crash recovery can reconcile a
working file matching a committed save receipt's digest. Restore means opening a version via UI.

Limits: 1000 indexed checkpoints, 2 GiB archives, 256 MiB document snapshot and five-minute
job deadline. Old versions are never automatically deleted. Current support is regular local
Windows paths; UNC / reparse paths are rejected. File IDs and ancestor identities detect target
replacement; hashes detect external content changes; an exclusive sidecar lock serializes
Cutwork instances. This does not promise transactions against uncooperative processes racing
an external write in the final hash-to-rename interval, or power-loss durability on every filesystem.
Cross-machine shared-folder operation is not supported.

## Hub catalog

[config/cutwork.json](../../config/cutwork.json) includes 20 tools (11 previous + 9 additions),
including embedded capture and unchanged guard wrappers. Its inner input schemas are tested
against LiveSchema. Deploy the catalog with the existing Internal catalog-update workflow;
preserve deployment identity and credential references. Rebuilding Cutwork alone does not
update a static Hub catalog. The live Hub deployment is a separate operation.
