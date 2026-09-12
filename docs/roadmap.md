# Cutwork v0.1 implementation roadmap

Status: proposed roadmap for Issue [#3](https://github.com/flamoris-jp/flamoris-cutwork/issues/3)

This roadmap implements [the production architecture](production-architecture.md) in small, reviewable phases. The order optimizes for the earliest point at which Akino can open real artwork and judge the interaction, then the earliest end-to-end useful cutout, rather than completing invisible infrastructure first.

## Working rules

- Branch each phase from reviewed `main`.
- Keep the Python/Tkinter application as executable reference until the corresponding production behavior is accepted.
- Commit at meaningful foundation/domain/tool/UI/test boundaries; do not hold a whole phase uncommitted.
- Every phase identifies its smallest deterministic regression boundary and its hands-on acceptance.
- Do not add GPU, tiling, framework, CI, or packaging machinery before the phase that measures or needs it.
- Production code must never import the experiment directory.
- Design changes to persistence, compositing, coordinates, or tool contracts update the authority docs before implementation.

## Phase 0 — Baseline and production boundary

### Goal

Record the performance evidence needed to judge the new canvas and establish a clean repository boundary without changing product behavior.

### Scope

- profile current Clone Paint using one synthetic/public fixture and one local representative artwork;
- report clone kernel, Hole Only, composite, PIL affine, PhotoImage, and redraw cadence separately;
- record fixture dimensions, zoom, brush size, machine, and measurement method;
- define v0.1 supported input assumptions and provisional latency gates from ADR 0001;
- plan the reviewed move of current Python files into `experiments/python-tkinter/`; and
- decide the minimum supported Windows version before package metadata is created.

### Not in scope

- optimizing the old Tkinter application beyond a tiny measurement-enabling change;
- production UI scaffold; or
- GPU selection.

### Acceptance

- Issue #2 has reproducible stage-level measurements;
- no private artwork is committed;
- the experiment still launches/tests after any later dedicated directory move; and
- the production directory can be created without ambiguous ownership.

### Suggested commits

1. `test: record Clone Paint performance baseline`
2. `chore: separate Python experiment reference` (only if performed as its own reviewed change)

## Phase 1 — Windows foundation and first real-image canvas

### Goal

Reach the first hands-on checkpoint: launch one obvious Windows app, open a real image, and inspect it with correct fit/pan/zoom behavior.

### Scope

- .NET 10 solution and minimal production projects;
- WPF shell with File/Edit/View/Layer/Export/Help menus;
- compact left tool rail and right Layers/Properties placeholders;
- Japanese default and English resource catalogs from the first visible string;
- image open through the initial PNG/JPEG codec boundary;
- immutable Original asset in a minimal document;
- custom canvas presentation surface;
- fit, actual size, wheel zoom, `Ctrl+wheel` routing placeholder, middle/Space pan;
- explicit viewport/document coordinate conversion; and
- Original/Composite view command, even if both are identical before layers exist.

### Not in scope

- editable layers;
- Part/Guriguri;
- persistence; or
- installer.

### Automated checks

- document/viewport coordinate round trips;
- original immutability;
- Japanese/English catalog parity;
- import dimension and unsupported-format errors; and
- core projects build without starting WPF in tests.

### Hands-on acceptance

Akino can open a representative illustration, fit it, zoom around the eye/ear/hand areas, pan at high zoom, switch Japanese/English, and see no geometry drift or blurred cursor-coordinate mismatch.

### Suggested commits

1. `build: add minimal Cutwork production solution`
2. `feat: add Japanese-first Windows application shell`
3. `feat: open immutable artwork in document canvas`
4. `feat: add deterministic viewport navigation`

## Phase 2 — Document, layer stack, compositor, and Undo/Redo

### Goal

Establish the smallest production editing spine before tool ports begin.

### Scope

- stable document/layer IDs and common properties;
- OriginalAsset, BaseLayer, PartLayer, PatchLayer, Repair/PaintLayer types;
- fixed Base divider and valid reorder/delete constraints;
- deterministic source-over compositor and Base-hole projection;
- per-layer revisions, dirty rectangles, composite cache, and `WriteableBitmap` dirty updates;
- Layers panel selection/visibility/rename/reorder/delete;
- contextual Properties switching by selected layer kind;
- `EditorSession` dirty/saved revision;
- value, structural, mask-patch, raster-patch commands; and
- transaction-based Undo/Redo with a provisional memory budget.

### Automated checks

- Base uniqueness and stack constraints;
- layer order/visibility composite fixtures;
- hiding a Part preserves its authored Base hole, if accepted;
- one gesture transaction round-trip infrastructure;
- dirty rectangle propagation; and
- original pixels remain unchanged.

### Hands-on acceptance

Akino can see a conventional layer panel, create test layers through developer fixtures, select/rename/show/hide/reorder valid items, and Undo/Redo those actions without canvas corruption.

### Suggested commits

1. `feat: add Cutwork document and layer domain`
2. `feat: add dirty-region composition and presentation`
3. `feat: add layer panel and contextual properties`
4. `feat: add transactional edit history`

## Phase 3 — Unified Part Tool and Guriguri

### Goal

Reach the first useful production cutout: rough polygon to fitted Part layer on real artwork.

### Scope

- centralized input router and `ITool` lifecycle;
- Part Tool fence drawing, Enter/double-click commit, Escape cancel;
- deterministic port of `PolygonBoundaryShrinker` and boundary-map preparation;
- asynchronous/cancellable preprocessing over immutable snapshots when needed;
- wheel-controlled fitting preview with `Ctrl+wheel` reserved for zoom;
- exact/manual polygon fallback inside the Part workflow;
- Part mask overlay and circular/appropriate cursor feedback;
- create Part layer as one history transaction; and
- resolve and document the final Guriguri wheel direction from Issue #1 hands-on testing.

### Automated checks

- ported retained-count, nesting, boundary, and restoration tests;
- tool state transitions and cancel behavior;
- viewport-independent document polygon points;
- deterministic mask for fixed fixture/input; and
- Part creation Undo/Redo.

### Hands-on acceptance

Akino can draw a deliberately rough fence around an eye/ear/hand, fit it with a few wheel movements, commit a usable Part, inspect the Base hole, and undo the entire creation. This is the first production milestone judged against **perceptual sufficiency × editing speed**.

### Suggested commits

1. `feat: add production tool and input foundation`
2. `feat: port deterministic Polygon Guriguri`
3. `feat: add unified Part Tool workflow`
4. `test: lock Part Tool production behavior`

## Phase 4 — Mask Brush and Patch workflow

### Goal

Make rough cutouts correctable and make manual underpaint practical without AI.

### Scope

- shared document-space stroke sampler;
- visible circular cursor matching the effective radius;
- Mask Brush with primary polarity and temporary Alt inverse;
- one mask stroke per history entry using ROI patches;
- Patch Tool source polygon, creation, placement, scale, and rotation phases;
- frozen patch-local RGBA content plus provenance;
- contextual Part/Patch properties;
- default Patch placement below Base; and
- hands-on confirmation of scale limits and commit/cancel affordances.

### Automated checks

- binary/8-bit mask rules and Alt polarity;
- event-gap interpolation without holes;
- brush footprint under multiple zoom levels;
- Patch affine fixtures including accepted maximum scale;
- patch source immutability; and
- multi-phase Patch commit/cancel/Undo.

### Hands-on acceptance

Akino can correct a fitted edge with one brush, sample a nearby skin/hair patch, rotate/scale/place it under the Base hole, and inspect the result without changing Original.

### Suggested commits

1. `feat: add shared stroke sampling and brush overlay`
2. `feat: add unified Mask Brush`
3. `feat: add Patch source and transform workflow`
4. `test: lock mask and patch interaction contracts`

## Phase 5 — Point-source Clone Repair

### Goal

Deliver the production interaction from Issue #1 at real-image speed and close the main blocker in Issue #2.

### Scope

- Alt+click immutable-Original source point;
- source marker and circular destination cursor;
- relative source/destination offset preserved for the stroke;
- wheel brush diameter and `Ctrl+wheel` viewport zoom;
- Hole Only snapshot semantics;
- Clone writes to selected/new Repair/Paint layer below Base;
- local ROI kernel and dirty composite/presentation;
- whole-stroke Undo/Redo without full-frame snapshots;
- stage-level runtime counters available only in developer diagnostics; and
- before/after comparison with Phase 0 measurements.

The rectangle source fence is not ported.

### Automated checks

- source/destination mapping and image bounds;
- immutable source even when destination overlaps it;
- Hole Only outside-pixel rejection;
- interpolation continuity under sparse pointer events;
- feather/alpha behavior;
- one-stroke Undo/Redo; and
- deterministic output for fixed samples.

### Hands-on/performance acceptance

- an eye-sized hole can be covered in one natural stroke without repeated catch-up strokes;
- cursor remains visually attached to the pointer;
- no gaps appear when events are coalesced;
- representative 4K test meets at least 30 fps continuous presentation or records a focused blocker with stage timings; and
- pointer-up full-quality settle meets the provisional target or identifies the dominant stage.

Do not advance a renderer rewrite merely because a total frame target misses; the recorded dominant stage determines the next focused action.

### Suggested commits

1. `feat: add point-source Clone Tool interaction`
2. `feat: add local ROI clone and Hole Only processing`
3. `feat: add Clone stroke history and dirty presentation`
4. `perf: record production Clone Paint acceptance`

## Phase 6 — Blur, Smudge, and repair finishing

### Goal

Provide only the local finishing operations needed to hide repair seams.

### Scope

- Blur and Smudge tools using the shared stroke engine;
- eligibility rules for derived raster layers;
- local ROI operations and dirty rectangles;
- contextual strength/radius properties;
- whole-stroke Undo/Redo; and
- clear localized feedback when a selected layer cannot be edited.

### Automated checks

- local edits do not affect pixels outside conservative ROI bounds;
- Original and unrelated layers remain unchanged;
- strength/radius boundaries;
- smudge direction; and
- cancel/Undo/Redo exact restoration.

### Hands-on acceptance

Akino can soften a Patch/Clone seam and nudge local texture without accidentally painting on Original, Base, or a Part mask.

### Suggested commits

1. `feat: add derived-layer Blur Tool`
2. `feat: add derived-layer Smudge Tool`
3. `test: lock repair finishing boundaries`

## Phase 7 — `.flimg` persistence and export

### Goal

Make a useful Cutwork session durable and portable between sessions before packaging polish.

### Scope

- schema-v1 `.flimg` manifest and ZIP layout;
- persist a canonical normalized Original PNG and preserve exact imported bytes as optional provenance;
- encode masks and layer content as lossless PNG;
- serialize stable IDs, order, names, semantics, visibility, and transforms;
- strict reader validation and migration registry;
- atomic save/Save As/open behavior;
- dirty/saved revision integration and close-with-unsaved-changes prompt;
- composite PNG export;
- layer PNGs plus deterministic handoff JSON; and
- safe filename collision handling.

### Automated checks

- round-trip all layer kinds and compare reconstructed composite;
- corrupt archive, path traversal, duplicate entry, size mismatch, checksum mismatch, missing asset, and unsupported-version rejection;
- atomic-save failure leaves previous project valid;
- export layer order/identity/transform manifest; and
- no session-only state or history leaks into the file.

### Hands-on acceptance

Akino can cut and repair a representative image, save it, close the app, reopen the same editable project, and export assets whose visual stack matches the pre-save canvas.

### Suggested commits

1. `feat: define flimg schema v1 and validated reader`
2. `feat: add atomic Cutwork project writer`
3. `feat: add project open/save workflow`
4. `feat: export composite and layer handoff`

## Phase 8 — Windows packaging

### Goal

Produce one obvious application that runs on a clean supported Windows machine.

### Scope

- self-contained `win-x64` release publish;
- app icon, version metadata, license/notices, and one launcher;
- native OpenCV runtime inclusion if still required;
- clean-machine smoke checklist;
- evaluate portable ZIP versus MSIX using concrete file-association/update needs;
- optional `.flimg` association only after Save/Open is stable; and
- concise Japanese-first installation/run documentation.

### Automated checks

- release build/publish succeeds;
- deterministic core tests still pass in Release; and
- packaged file inventory contains required native/runtime assets and no experiment sources/private artwork.

### Hands-on acceptance

On a clean Windows environment without a .NET SDK, Akino can launch Cutwork, open artwork, make a small Part/repair edit, save/reopen `.flimg`, and export layers offline.

### Suggested commits

1. `build: add self-contained Windows release publish`
2. `docs: add Windows install and smoke checklist`
3. `build: add MSIX packaging` (only if the decision is accepted)

## Phase 9 — Performance and workflow polish

### Goal

Tune the complete real workflow using evidence after every required tool and persistence boundary exists.

### Scope

- re-profile Part, Mask, Patch transforms, Clone, Blur/Smudge, save, and export;
- reduce proven hot copies/composites;
- finalize brush defaults, cursor visuals, shortcut discoverability, and Japanese wording;
- test representative 1080p, 4K-class, and agreed large-image cases;
- decide whether dense buffers remain sufficient;
- evaluate Win2D/Direct2D renderer escalation only if presentation dominates; and
- evaluate a native hot kernel only if a specific kernel dominates.

### Acceptance

- the complete cutout/repair/save/export path has recorded timings and hands-on notes;
- Clone no longer remains an unmeasured subjective blocker;
- no visual correctness regression is hidden by low-quality preview;
- memory behavior is bounded for the agreed image sizes; and
- any technology escalation has its own focused ADR and acceptance target.

### Suggested commits

Use one focused performance commit per measured cause. Do not combine renderer replacement, storage redesign, and tool behavior changes in one PR.

## Milestones

| Milestone | End phase | What Akino can do |
|---|---:|---|
| M0: real-image viewer | 1 | Open, fit, zoom, and pan real artwork in the production shell |
| M1: production cutout | 3 | Rough-fence and Guriguri-fit a usable Part |
| M2: repaired cutout | 5 | Correct masks and fill holes with Patch/Clone |
| M3: durable workflow | 7 | Save/reopen editable `.flimg` and export layers |
| M4: distributable v0.1 | 8 | Run the standalone app on a clean Windows machine |
| M5: measured polish | 9 | Use the full workflow at recorded interactive performance |

## Minimal CI progression

1. Phases 1–2: one Windows build and deterministic unit-test job.
2. Phases 3–7: extend the same job; do not add one workflow per feature.
3. Phase 8: add a release-publish smoke job only when packaging becomes stable.
4. Performance measurements remain explicit/manual or dedicated opt-in runs until the runner/hardware is stable enough to make thresholds meaningful.
5. Legacy Python tests remain available while serving as migration oracle, but do not become a permanent duplicate required gate after production behavior is accepted.

## Deferred backlog

The following do not belong in v0.1 phases without a new accepted requirement:

- semantic/AI segmentation;
- automatic patch or clone-source suggestions;
- clone rotation/scale;
- multi-polygon boolean authoring beyond the manual fallback needed by real use;
- PSD import/export;
- 16-bit/wide-gamut editing;
- cross-platform UI;
- plugin system;
- network services; and
- FLAMORIS 2D runtime integration.
