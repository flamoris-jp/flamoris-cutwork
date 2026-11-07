# ADR 0009: Linux Cutwork service

The requested production host is a permanent Linux/LIME service, independent of
Mango. The earlier Windows-only CLI direction is superseded. Windows keeps the
GUI and its MCP endpoint. The CLI targets net10.0, without WPF or a display server.

Cutwork.Core remains the one document/history authority. Cutwork.Imaging and
Cutwork.Mcp gain portable targets; the GUI retains its WPF codec. The Linux raster
boundary uses pinned SkiaSharp 4.154.0 (MIT and bundled Skia notices). Cutwork owns
straight BGRA/Gray8 conversion, PNG header/CRC validation and size limits. Existing
flimg v1/v2/v3 migration/persistence and handoff v2 exporters remain shared. No
second editor/document/mask algorithm is introduced. Transparent source pixels,
mask bytes and compositor results require round-trip tests.

One UI-free synchronization lane serializes state and save completions. Core owns
stdio protocol and outbound WSS; the CLI adds no protocol server implementation.
Application-lifetime grants survive document replacement while target guards rotate.
A cold empty service uses its runtime ID as a nonempty guard token, with hasDocument
false. Existing document tools still reject no_document. New filesystem/lifecycle
tools are service-owned, not added to the Windows editor's registry or consent UI.

serve --workspace explicitly authorizes one private local managed storage root.
Guarded, bounded transfer creates new projects from PNG/JPEG/flimg bytes; MCP cannot
supply paths or execute commands. Dirty replacement requires explicit intent.
Saving uses existing ProjectWorkspace/checkpoints. Linux save identity uses statx
(device/inode), not creation-time fallbacks; Windows keeps handle identities.

artifact_export uses immutable UUID artifacts and revision-bound receipts; reads
use bounded chunks and digests. Startup recovers the last active saved project.
No autosave or automatic mutation replay occurs. SIGTERM revokes first and drains
pending checkpoint completion. Hub connection cycles recover from extended outage.

The cutwork-lime upstream is separate from Windows cutwork. Its descriptor comes
from the actual registry. Deployment is a separate operator step: native linux-x64
artifact, systemd unit, private state and a dedicated Hub credential/descriptor.
No live machine or Hub configuration is changed by implementing this PR.

Acceptance separates portable engine/codec regressions, actual-process official
MCP client on Linux, unchanged Windows acceptance, real LIME/Hub deployment, and
perceptual artwork review. The service does not claim automatic segmentation or
hidden-background repair merely because a Part/export operation succeeds.
