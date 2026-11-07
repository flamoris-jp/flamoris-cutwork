# ADR 0010: Kachinco-compatible LIME resident deployment

Use Kachinco's ownership boundary: Updater owns the immutable Docker image,
non-root UID 10001, retained state mount, restart and loopback process health.
Cutwork owns its existing ServiceWorkspace, document codec and Core outbound WSS
provider. Keep the separate cutwork-lime route, credential and 29 tool schemas.

The previous systemd installer is a legacy deployment. Prepare a full private
backup after stopping it, retire the native root to leave Updater an absent fresh
installation destination, then restore saved state and the private credential
into its provisioned empty state mount with matching digests. Never run two hosts on the same
workspace. Keep original unit/releases and the backup until actual Hub context
verification succeeds. Preparation is not installation or connection evidence.

Use Kachinco's 60-second refresh on LastError after the Core retry cycle. Keep
the grant/workspace alive across outages; never replay an editing call. Report a
compact started event for the container supervisor. Process readiness and actual
Hub connectivity are separate checks.

Hub verification must consume every tools/list page and preserve existing public
schemas. Serialize catalogs through the installed YAML writer and check the
round trip. Run one Hub registration at a time across Cutwork and 2D. Container
deployment alone does not establish proxy routing, authentication or live WSS.
