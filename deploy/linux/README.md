# LIME resident deployment: Kachinco parity

Cutwork uses the same Core outbound WSS provider as Kachinco. The old manual
systemd installation differed in deployment ownership, process health and retry
policy. The supported target is now an Updater-owned non-root Docker resident
with the existing ServiceWorkspace mounted at /data/state.

## Release and production status

The Docker image/catalog are candidates until the reviewed Linux CI artifact is
published as an immutable cli-v0.1.0 release and registered with Updater.
Preparing a catalog, a migration backup or an active process does not establish
a live Hub connection. This source change does not install, stop or delete
anything on LIME/DECO PON. Do not run the old native installer; its lime --apply
entry point now refuses a new installation.

The Linux workflow builds/tests the actual CLI, publishes its self-contained
bundle with dependency licenses, builds the image from those bytes, verifies
the read-only container's health, retained state, outage survival, SIGTERM and
restart, then records the image ID, archive SHA-256 and source revision in the
Updater candidate artifact. Live external WSS acceptance is a separate step.

## Ordered cutover after the accepted image is available

1. Finish the concurrent 2D Hub operation. Register one upstream at a time.
2. Run migrate-resident.py prepare in plan mode on LIME first. For the reviewed
   --apply operation, keep this helper, setup-cutwork-lime.py and the legacy
   flamoris-cutwork-cli.service template together.
3. Preparation stops the old native service, saves a root-only copy of the
   workspace/configuration/unit and original ownership/modes, retains the same
   dedicated endpoint/credential in private state/connection.json, changes the
   existing state directory to UID 10001 and disables the native unit.
   Failures restore original ownership and prior service activity/enabled state.
4. Install/start flamoris-cutwork-cli through Updater's accepted catalog. The
   retained connection file lets installation proceed without sending a secret
   through chat. Its normal writable state mount is the original host state
   directory; no project codec conversion or copied second workspace is used.
5. Verify local process health at 127.0.0.1:8775/healthz. That response explicitly
   defers Hub readiness to a separate MCP verification.
6. On DECO PON, run the reviewed setup-cutwork-lime.py hub --apply with the private
   cutwork-lime-connect.json transferred during native setup. Keep that file
   private (0600); it is deleted only after successful verification. The helper
   preserves current Hub settings/upstreams and restores them on failure.
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

