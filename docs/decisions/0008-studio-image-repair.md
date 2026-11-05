# ADR 0008: Cutwork UI image repair through Studio

Status: implementation candidate, 2026-11-06. Related: #56.

The owner requested image repair directly from the Cutwork screen. This supersedes
the orchestration-only proposal in #56 for this optional UI entry point. Studio's
existing account-scoped HTTP API supplies authentication, input ownership, jobs
and assets; Cutwork never receives a Controller service credential or embeds a
model/GPU client. Existing manual editing remains independent of the connection.

First slice: select one Part, choose **Edit → AI hole fill**, review the cropped
reference and mask, sign into Studio, choose a discovered image editing workflow,
edit its declared parameters, and explicitly send the crop. Passwords and cookies
remain in memory for the dialog only. HTTPS (or loopback HTTP for development),
no redirects, bounded responses, CSRF and exact owned IDs are required.

Freeze document token/revision, Part mask, Original context and before-preview.
The masked reference replaces the hole with neutral gray; model outputs are
suggestions. Registered single-reference schema-7 img2img and schema-10 declared
image contracts are supported only when their descriptor declares dimensions and
prompt inputs. No model selection, runtime activation or cloud fallback is implicit.
Input is explicitly resized to the requested working dimensions; output must match
those dimensions before the inverse mapping to the captured document ROI.

Preview hides the selected Part without changing the live document. Acceptance
creates one ordinary Part-owned Repair below Base, clipped to that Part's nonzero
mask support; zero-mask pixels stay transparent. Soft-mask support receives the
generated alpha once, rather than squaring the Base-hole coverage. Original and
Part pixels remain immutable. One Undo removes the repair; existing save/open and
handoff persist its ordinary raster bytes with no format change. Any edit, Undo,
Open or active transaction invalidates the candidate, even if history returns to
the same state. Candidate adoption is single-use and explicitly user initiated.

Limits: 262,144 document ROI pixels, 1,048,576 working pixels, 8 MiB upload/image,
2 MiB JSON, one job per dialog. Generation requests are never retried automatically.
An ambiguous submission locks further submissions in that dialog and points to
Studio's execution history. Closing stops local polling only; cancellation of a
known job is an explicit separate operation, with its returned state shown.
No automatic input/asset deletion: Studio owns normal retention and results.

Validation: deterministic clipping/preview/Undo/stale result and HTTP contract
tests, full Windows build/test/package CI. Real Windows × Studio × provider quality
acceptance remains a separate hands-on gate; static descriptors do not certify
that a model can produce an acceptable fill.
