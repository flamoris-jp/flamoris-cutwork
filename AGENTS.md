# AGENTS.md

This file defines repository-wide rules for AI agents and automated development tools operating in `flamoris-jp/flamoris-cutwork`.

These rules adapt the useful repository-boundary, milestone-test, CI, and GitHub workflow discipline from `flamoris-jp/flamoris-2D` while removing 2D-animation-specific rules that do not apply to Cutwork.

## 1. Repository purpose and authority

FLAMORIS Cutwork is a standalone image decomposition and repair application for preparing illustration parts used by short 2D moving-picture workflows.

Cutwork is separate from `flamoris-jp/flamoris-2D`.

`main` is the reviewed current baseline.

Chat attachments, local ZIPs, old experimental branches, screenshots, issue discussion, and LLM output are not current Product authority unless explicitly adopted through reviewed repository changes.

The migrated Python/Tkinter implementation is preserved as experimental/reference material and executable specification. It does not automatically define the production UI or architecture.

## 2. Product principles

1. Optimize for **perceptual sufficiency × editing speed**.
2. Do not force pixel-perfect cleanup when defects are invisible at normal intended playback scale.
3. Human intent decides semantic part boundaries and repair source choices.
4. Deterministic classical tools should remain understandable and locally reproducible.
5. Generative AI is not required for the core Cutwork workflow.
6. Production UI should favor familiar Windows image-editor conventions over prototype/debug controls.
7. Cutwork remains usable independently of FLAMORIS 2D.
8. Cutwork may export/handoff prepared assets to other applications, but must not depend on FLAMORIS 2D runtime internals.

## 3. Repository boundaries

As production implementation grows, responsibilities must remain explicit.

Recommended direction:

```text
src/ or product/   production application/runtime code
experiments/       preserved prototypes and comparison implementations
tests/             regression/integration tests outside production runtime
docs/              current design, decisions, research, reviews
history/           superseded material if later separated explicitly
```

The repository has not yet completed this physical reorganization. Do not invent a directory migration casually. When production implementation begins, define the boundary explicitly and move files through a reviewed change.

Absolute rules:

- Product runtime must not depend on experiments, test fixtures, private artwork, or history-only code/assets.
- Experiments may inform Product; Product must not import experiment UI state or launchers as hidden runtime dependencies.
- Production/release artifacts should come from explicit build inputs.
- Do not accumulate duplicate validators, test harnesses, launchers, or compatibility layers without a concrete requirement.

## 4. Current experimental authority

The current Python files are useful for behavioral reference, especially:

- Guriguri selection behavior
- Clone Repair semantics
- immutable-original sampling
- Hole Only behavior
- Patch/Repair composition findings
- layer interaction findings
- regression tests around deterministic image operations

They are **not** authority for the final:

- application framework
- window layout
- widget toolkit
- rendering architecture
- GPU strategy
- production document format
- i18n implementation

When production code replaces an experimental behavior, preserve the user-visible contract intentionally or document why it changed.

## 5. Production UI direction

The current design authority is tracked in GitHub Issues, especially the standalone v0.1 design work.

Preferred layout direction:

- top: conventional File / Edit / View / Layer / Export / Help menus
- left: compact vertical tool icons
- center: canvas/viewport
- right: Layers + contextual Properties
- Japanese-first UI through i18n resources

Preferred tool consolidation:

- Part Polygon + Polygon Guriguri -> Part Tool
- Mask Add + Mask Erase -> Mask Brush
- Patch Source + Move Layer -> Patch workflow/tool
- Clone Source + Clone Paint -> Clone Tool

Prototype comparison modes should not automatically become production toolbar items.

## 6. Input and tool interaction principles

Brush-like tools should behave like brush tools.

Current preferred interaction direction includes:

- visible circular brush footprint at the cursor
- brush display matches the actual affected image-space radius
- viewport zoom must not change the underlying domain-space brush meaning unexpectedly
- `Alt` may temporarily switch tool sub-modes where this reduces mode switching
- Clone candidate direction: `Alt+click` selects source center, release Alt to resume painting
- wheel may adjust active brush size where appropriate
- `Ctrl+wheel` may preserve viewport zoom when wheel is otherwise tool-specific

Exact shortcuts remain subject to hands-on testing. Do not hard-code conflicting shortcuts in scattered event handlers; centralize input mapping in the production architecture.

## 7. Image/data invariants

Unless an accepted design explicitly changes them:

1. The decoded original artwork is immutable source data during an editing session.
2. Derived layers/masks/repairs must not silently overwrite source pixels.
3. Part masks and repair operations use image/domain coordinates, not viewport pixels as persistent identity.
4. Viewport zoom/pan is presentation state and must not corrupt saved geometry or masks.
5. Clone source semantics must be deterministic from saved state and user input.
6. Layer order and visibility must produce deterministic composites.
7. Save/load must never silently discard authored parts, masks, transforms, or repair work.

## 8. Performance discipline

Interactive painting is a Product requirement, not optional polish.

For performance work:

1. Measure before redesigning.
2. Separate algorithm cost from preview/compositing/display cost.
3. Prefer local ROI / dirty-region work over full-frame recomputation where practical.
4. Avoid rebuilding expensive immutable data per pointer event.
5. Preserve continuous strokes when pointer events are coalesced.
6. Keep input latency visible in profiling results.
7. Do not assume GPU acceleration is the answer without evidence.
8. If a compiled/native/GPU path is introduced, keep deterministic behavior testable outside the display layer where possible.

The current Python/Tk/PIL bottleneck is evidence for investigation, not automatic authority for a specific replacement stack.

## 9. Development order

Unless an accepted design/ADR changes it:

1. Confirm the current requirement and affected boundary.
2. Update design/decision records first when changing document persistence, coordinate semantics, layer/compositing semantics, tool contracts, rendering architecture, or repository boundaries.
3. Implement the smallest understandable change.
4. Add/run the smallest deterministic local tests for changed behavior.
5. Perform hands-on real-image testing when the risk is inherently interactive/visual.
6. At meaningful milestones, identify stable behavior that must not regress.
7. Add CI only when there is a stable risk worth protecting.
8. Review through a PR before merging to `main`.

Do not bolt production behavior directly onto experimental Tkinter state merely because it already exists.

## 10. Change discipline

- Prefer small, purpose-driven changes.
- Commit frequently at meaningful boundaries.
- Keep commits as single-purpose as practical.
- Do not leave large working implementations uncommitted for long periods.
- Do not create parallel implementations of the same document model or tool semantics without an explicit migration reason.
- Avoid framework-building before Product needs justify it.
- Keep process/debug state out of the production document format.
- Do not add hidden cloud or AI dependencies to core editing operations.
- Preserve deterministic/manual fallbacks when automated helpers are introduced.

For large work, useful commit boundaries include:

- foundation
- document/domain
- rendering
- tool/input
- UI
- persistence
- tests
- fixes

## 11. Testing policy

During active implementation:

- run the smallest checks relevant to changed code;
- prefer deterministic unit tests for masks, coordinate transforms, compositing, clone mapping, serialization, layer order, tool state transitions, and persistence;
- add focused regression tests for every fixed deterministic bug when practical;
- avoid broad GUI automation before stable production UI contracts exist.

Hands-on/manual testing is appropriate for:

- brush latency
- cursor/brush alignment
- Guriguri feel
- visual seams
- viewport interaction
- Windows native-feeling workflow
- export inspection

Do not pretend unit tests can replace perceptual/interactive acceptance where they cannot.

## 12. CI policy

CI is a regression boundary, not a development orchestration system.

- Add CI only for stable behavior worth protecting.
- Prefer fast deterministic checks.
- Avoid duplicate checks that protect the same risk.
- Do not run expensive GPU/visual/packaging jobs automatically without a clear reason.
- Windows packaging checks should be added when packaging becomes a stable Product boundary.
- Do not require CI merely because a branch exists.

## 13. GitHub workflow

Recommended branch model:

```text
main          reviewed current baseline
feature/*     implementation work
fix/*         focused bug/performance fixes
docs/*        design/research/repository documentation
experiment/*  temporary or comparison experiments when needed
integration/* temporary reconciliation work
```

For meaningful work:

1. Issue or documented acceptance criteria.
2. Branch from `main`.
3. Small purpose-driven commits.
4. Relevant local tests / hands-on checks.
5. PR review.
6. Squash merge to `main` when acceptance criteria are met.
7. Delete merged feature branch.
8. Tag meaningful product milestones when useful.

Repository settings intentionally use squash merge as the normal merge strategy.

Never leave the latest working implementation only in chat, Downloads, or an untracked local folder.

## 14. Work handoff guidance

When handing Cutwork work to ChatGPT Work or another coding agent, include when practical:

- repository
- Issue
- target branch
- baseline commit/ref
- implementation scope
- explicit non-goals
- current design authority
- experimental behavior to reuse or preserve
- commit policy
- tests / hands-on acceptance to run
- whether PR creation/review is expected
- recommended model and reasoning strength

Model/reasoning guidance:

- routine local fixes, small UI changes, test additions: lighter/medium reasoning
- production architecture, rendering stack selection, persistence design, cross-layer changes, hard performance debugging, final review: higher reasoning

Use the lowest reasoning strength that is appropriate for the task.

## 15. Current authority references

Until dedicated docs/ADRs are added, current authority is primarily:

- `README.md` for repository/product orientation
- this `AGENTS.md` for development rules
- GitHub Issue #1 for hands-on findings
- GitHub Issue #2 for Clone Paint performance work
- GitHub Issue #3 for standalone v0.1 UI/architecture/i18n direction
- deterministic experiment tests for already-defined algorithm behavior

When architecture stabilizes, move long-lived design authority into versioned `docs/` documents and ADRs rather than relying indefinitely on Issue text.


## Shared FLAMORIS repository policy

Organization-wide repository, licensing, security, contribution, and public-release principles are defined in:

- `flamoris-jp/flamoris-commons/docs/repository-policy.md`
- `flamoris-jp/flamoris-commons/AGENTS.md`

This repository-specific `AGENTS.md` remains authoritative for product/domain rules. Where the shared policy and repository-specific rules differ, preserve the more specific product rule unless an explicit FLAMORIS-wide policy change says otherwise.
