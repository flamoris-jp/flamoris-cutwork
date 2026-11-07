# First LIME / Hub connection

PR #69 is merged. `setup-cutwork-lime.py` prepares the two host-side operations
that the read-only Server Manager and application Updater interfaces cannot run.
Its default mode prints a plan; `--apply` is explicit. Run it as the normal
operator through sudo, so GitHub CLI and SSH keep that user's authentication.

The LIME phase installs the already accepted Linux artifact from Actions run
42355265811, source 36b443c7e793896583a842876ef8b1073fee28e6. It verifies the
pinned tar SHA-256 before extracting, retains that archive locally, sets up the
dedicated service account/systemd service and preserves existing workspace data.
The native application is not registered as an Updater-managed Docker app. Do not
install a second host for the same workspace later.

It derives the dedicated Cutwork WSS URL from the existing Kachinco LIME endpoint
without copying the Kachinco credential. `--hub-endpoint` supplies an explicit
override when required. A new Cutwork connector secret is kept in the service's
root-only environment. The operator-owned mode-0600 `~/cutwork-lime-connect.json`
is copied with the operator's SSH key to the same account on DECO PON. If key-based
copy is unavailable, the script prints the one `scp` command needed. This private
file contains the connector credential: do not paste or upload its contents.

On DECO PON, the Hub phase validates the new catalog with the installed Hub code,
keeps Windows `cutwork` unchanged and adds only `cutwork-lime`. It appends the new
credential to the retained runtime.env. Docker does not reload `--env-file` on
restart. When the credential is missing from the container environment, the
helper creates a replacement from the same immutable local image ID, retaining
the full container configuration, bind mounts, host network, read-only root,
user, capability restrictions and restart policy. It checks those settings before
stopping the original. Credentials travel over the local Docker socket, never
command arguments. Writable-root/other-network/anonymous-volume layouts stop
before changes rather than risking unmounted runtime data.

The original container and mode/owner-preserving configuration backups remain
available. The original is stopped and its restart policy disabled before the
replacement starts; both never run together. Startup or verification failures
restore the original container, runtime.env and catalog, retaining the private
connection file. Errors identify the failed phase without printing secrets.
Existing clients reconnect normally;
uncertain editing calls are never replayed.

The final verifier uses an authenticated official MCP client to check all 29
schemas, retain every pre-existing tool/schema and call only `cutwork-lime.mcp.context`.
It can wait up to four minutes
for the permanent LIME connector's retry cycle. `connected:true` is evidence of a
live registered upstream, unlike systemd `active` alone. The transferred private
file on DECO PON is removed only after that verification succeeds.

## Execute in this order

Download the script at the exact reviewed Git commit using your authenticated
GitHub CLI, then run:

```bash
# LIME
sudo python3 setup-cutwork-lime.py lime --apply
# DECO PON, after the private connection file has arrived
sudo python3 setup-cutwork-lime.py hub --apply
```

No GitHub password or connector token belongs in a command argument. If GitHub
CLI needs login, run `gh auth login` as the normal account, not under sudo. CI
artifacts expire after three days; the installed archive is retained at
`/srv/flamoris/apps/flamoris-cutwork-cli/packages/`. `--archive` can use an existing
copy with the exact pinned digest. The helper does not silently substitute a new
build if the artifact has expired.

The setup tests cover endpoint selection, credential preservation/idempotency,
archive digest/path validation, annotation parity, read-only dotenv probing,
container/configuration rollback and the actual official HTTP-client verifier
against a deterministic transport. CI also exercises replacement and failed-start
rollback using a real read-only Docker container with non-root execution,
retained credentials, a catalog bind mount and the production hardening flags. They
do not claim these privileged operations have already run on the real hosts.
