# MCP startup and independent content consent

The application can connect before opening a file. `mcp.context` reports connection/runtime identity, `hasDocument`, current document token/revision and busy state. Document-dependent operations return `no_document`; opening, closing and replacing a file keep the connection and require fresh guards. Startup never creates an MCP-only project/history.

New connection preferences automatically start the local read-only Manual bridge. For Hub/tunnel, configure the existing method/credentials and enable **起動時に自動接続**. An existing saved OFF remains OFF. The MCP menu can connect/reconnect with no open file. Explicit Stop and app shutdown revoke the grant.

Connection is independent from screen sharing and input recording. Screen sharing starts OFF, stays OFF on connect/reconnect, and must be explicitly enabled in its own control. Turning it OFF stops live, pending and historical pixel disclosure while preserving MCP. Input events require their separate operation-recording consent. Cutwork/2D expose the independent checkbox in the bottom bar; target loss, lock, suspend and document replacement stop sharing without granting it for the next target. Kachinco/kinetai preview tools operate on the authorized project/composition rather than capturing the desktop.

Physical Windows acceptance: launch with no file, verify connection/context/list; attempt a document query, open a file and retry; toggle sharing and verify pending and stored frames cannot leak; open/switch/reopen the same file and reject old guards; stop/reconnect and confirm sharing remains OFF; exit without leaving helpers running. PONPON/AudioAnalyzer in StudioClient currently have no MCP surface and are not given one by this change.

Requires Core/Wpf 1.4.0, published from reviewed MCP Core main. Cutwork/2D also require the coordinated Capture submodule revision. Synthetic CI and physical Windows acceptance must be reported separately.
