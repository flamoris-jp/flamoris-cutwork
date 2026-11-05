# Studio image repair / AIで穴埋め

## Use

1. Cut out a Part with the existing tools and adjust its mask. Select that Part
   (not Base or a Repair) and finish any active gesture.
2. Choose **Edit → AI hole fill / 編集 → AIで穴埋め**.
3. Check the displayed context and gray missing region. The initial version limits
   the complete ROI, including 32px context, to 262,144 document pixels. Split large
   work into smaller Parts. It does not silently crop a Part's mask.
4. Enter the Studio origin URL, email and password. Only HTTPS and loopback HTTP
   are accepted. `CUTWORK_STUDIO_URL` may prefill the URL. Credentials and session
   cookies are not written to settings, project files or logs.
5. Select a discovered supported single-image workflow. Edit its instruction and
   scalar/model parameters if required. Width/height default to the context size,
   rounded up to multiples of 8 and at least 64. The working image is explicitly
   resized with no crop, then mapped back after validating output dimensions.
6. **Send image and generate** uploads the displayed reference crop. It replaces
   mask-support pixels with neutral gray to identify the missing area. Generation
   may change the whole crop; only the original mask support can be applied.
7. Compare the left (before) and right (candidate) previews, both with the selected
   Part hidden. Other Part visibility and existing repairs are respected. Apply
   adds an ordinary repair at the top of the underpaint band, owned by the Part.
   Original, Part pixels and the live preview visibility are unchanged. One Undo
   removes it; `.flimg` save/open and layer export use the existing raster contract.

The initial entry point repairs a Part's hidden region. It does not replace an
arbitrary visible image region. Existing Clone/Patch/Blur/Smudge remain available.
For another candidate, close and reopen the dialog. No automatic provider fallback,
runtime activation, prompt replay or generation retry is performed.

## Connection and failures

Use the ordinary **Studio user account**, never `STUDIO_GENERATION_TOKEN`, a
Controller secret or an OpenAI API key. The client calls `/api/session`, login,
image discovery, reference upload, image job submission and owned execution/asset
endpoints. It validates opaque UUIDs and reconstructs same-origin asset URLs; it
does not follow provider URLs or HTTP redirects. Image/JSON reads and deadlines
are bounded. Registered schema-7 single-reference img2img and schema-10 declared
image workflows are supported; other profiles remain unavailable in this dialog.
Schema-10 must declare integer width/height fields. Output must be exactly one PNG
at the requested working size, with opaque coverage inside the repair mask.

An error shows its stable code, HTTP status and safe request ID when available;
`generation.repair` logs contain those fields without the image, instruction,
password or session cookie. Authentication/expired session requires reconnecting.
If Studio reports no supported workflow, check its deployment/catalog rather than
assuming any installed model is compatible. Source and deployed catalogs can differ.

Job submission is fenced before its non-idempotent POST. A lost acknowledgement
blocks further submission in this dialog. Use Studio's execution history to find
the submitted work. With a known execution ID, **Check status / retrieve result**
resumes observation only. **Request job cancellation** asks Studio to cancel and
displays the actual returned state; `unknown` is not presented as stopped.
Closing the window stops local polling and discards the uncommitted candidate;
it does not assert that a remote job stopped. Studio owns input/result retention.
MCP edits report busy while this modal UI operation is open. All candidate adoption
also checks the frozen document token/revision and active transaction independently.

## Validation boundary

Regression coverage: mask-only pixels, soft mask support, pure preview, matching
preview/adoption over existing underpaint, Original preservation, exact Undo/Redo,
single-use/stale results after edit/Undo/Open, bounds, HTTP CSRF/discovery and lost
submission acknowledgement. Windows tests additionally cover PNG size checks,
resampling and ordinary `.flimg` persistence of generated repair bytes.

Live acceptance remains pending: on Windows connect to the actual Studio, run one
small eye/forelock fill, inspect seams at export/playback size, hide/move the Part,
Undo/Redo, save/reopen/export, test server loss/cancel/unknown, and verify no secret
or artwork appears in logs. A provider-completed state is not visual acceptance.
See [ADR 0008](decisions/0008-studio-image-repair.md).
