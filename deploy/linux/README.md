# LIME resident deployment: Kachinco parity

Cutwork uses the same Core outbound WSS provider as Kachinco. The old manual
systemd installation differed in deployment ownership, process health and retry
policy. The supported target is now an Updater-owned non-root Docker resident
with the existing ServiceWorkspace mounted at /data/state.

## Release and production status

The immutable [cli-v0.1.1 release](https://github.com/flamoris-jp/flamoris-cutwork/releases/tag/cli-v0.1.1)
is the accepted Linux deployment. On 2026-11-07, Updater installation,
state restoration and start/complete succeeded. The Hub setup verified 29 tools
and a live context with edit permission and no open document; an independent Hub
observation confirmed attachment without schema mismatch, alongside Linux 2D
and Kachinco. Real-artwork and production restart/reboot acceptance remain pending.

Do not deploy cli-v0.1.0: its image omitted the required application/version OCI
labels and was correctly rejected by Updater. The 0.1.1 image declares both.
Use the versioned `cli-v0.1.1/catalog.json` release asset, not the Windows
latest-release catalog. Publication reuses the accepted CI artifact without
rebuilding or replacing an existing release.

Preparing a catalog, a migration backup or an active process does not establish
a live Hub connection. The deployment helpers change hosts only when explicitly
applied. Do not run the old native installer; its `lime --apply` entry point
refuses a new installation.

The Linux workflow builds/tests the actual CLI, publishes its self-contained
bundle with dependency licenses, builds the image from those bytes, verifies
the read-only container's health, retained state, outage survival, SIGTERM and
restart, then records the image ID, archive SHA-256 and source revision in the
Updater candidate artifact. It also runs the real Updater install/start/complete
against the image and tests retained state/restart. The invalid-image recovery
fixture verifies refusal of populated state, audited retirement and a new install.
CI and the separately observed live external WSS acceptance have different scopes.

## Ordered cutover after the accepted image is available

1. Finish the concurrent 2D Hub operation. Register one upstream at a time.
2. Run migrate-resident.py prepare in plan mode on LIME first. For the reviewed
   --apply operation, keep this helper, setup-cutwork-lime.py and the legacy
   flamoris-cutwork-cli.service template together.
   Save/checkpoint any active editing session first and finish in-flight edits.
   The backup preserves durable files, not uncommitted in-memory changes.
3. Preparation stops the old native service, saves a root-only copy of the
   workspace/configuration/unit and original ownership/modes, retains the same
   dedicated endpoint/credential in private state/connection.json, changes the
   existing state directory to UID 10001 and disables the native unit. It then
   renames the whole native application directory to a retained sibling and
   writes a protected sibling migration pointer. The installation root is absent
   for Updater's fresh-install preflight.
   Failures restore original ownership and prior service activity/enabled state.
4. Install flamoris-cutwork-cli through Updater's accepted catalog, leaving it
   awaiting initial setup. Run migrate-resident.py restore --apply on LIME:
   it requires the provisioned container to be stopped and its state to be empty,
   restores saved state/connection with matching SHA-256 fingerprints, and never
   overwrites populated state. Then use Updater start/complete. The
   retained connection file lets installation proceed without sending a secret
   through chat. Its normal writable state mount is the original host state
   directory. The retired source is preserved for rollback; normal operation
   uses the managed state copy. No project codec conversion is used.
5. Verify local process health at 127.0.0.1:8775/healthz. That response explicitly
   defers Hub readiness to a separate MCP verification.
6. On DECO PON, run the reviewed setup-cutwork-lime.py hub --apply with the private
   cutwork-lime-connect.json transferred during native setup. Keep that file
   private (0600); it is deleted only after successful verification. The helper
   preserves current Hub settings/upstreams and restores them on failure.
   It first extends the active Windows Cutwork Nginx snippet with the working
   Kachinco WSS forwarding headers. It tests Nginx configuration before reload,
   restores the snippet on test/reload failure, and preserves existing routes.
7. Require all 29 Cutwork schemas plus every existing public schema across every
   tools/list page, then call only cutwork-lime.mcp.context. An empty cold
   document is valid; permission must be edit and productId flamoris.cutwork.

The same source archive pin identifies the existing native connection bundle;
the managed image has its own exact source/image/archive receipt. The descriptor
and credential are preserved through migration. Windows cutwork remains a
separate route from cutwork-lime.

## Recovery and cleanup

Do not run native systemd and the managed container together against this state
directory. During any manual rollback, stop the managed container first, restore
the saved per-path ownership/modes, remove only the migration-created connection
file, and then restore the prior native service activity/enabled state.

Keep the original unit, source environment, releases and private backup until
the managed service passes live Hub verification. This preparation command
performs no cleanup/deletion of the original installation. Cleanup requires a
separate verified cutover; an Updater installation record or health response
alone is insufficient. Do not delete shared 2D/Kachinco files or Hub containers.

## Failed 0.1.0 image-stage recovery

The released `quarantine-failed-install.py` is a narrowly pinned recovery helper,
not a generic retry or repair command. Default invocation only describes the
plan. Applied recovery validates the exact failed Job, recipe, installed Updater
runtime, initial-install journal, generated configuration, absent container and
empty partial state. Any mismatch stops the operation.

It retains the partial installation root, archives the full initial-install
record before removing its active pointer, and closes the original managed Job
as failed while preserving its error/history. It briefly stops/restarts the
Updater control-plane units under its lock; existing application containers
are not stopped. A subsequent corrected installation uses the normal Updater
path and image validation. Do not reset journals, replay the rejected Job or
replace the immutable 0.1.0 assets.

This specific recovery and the subsequent 0.1.1 installation were accepted on
LIME. Exact Job IDs, retired roots, receipts and private deployment placement
belong in Internal's dated maintenance record. Native state/backups and the
rejected partial root remain retained; cleanup has not been accepted.
