# Logging integration

Cutwork consumes `Flamoris.Logging` 1.0.0 from nuget.org. The package is referenced normally; its DLL is not vendored in this repository.

Logging observes the existing `EditorSession`, `ProjectWorkspace`, rendering, and live MCP paths. It does not own document state, history, permissions, or transport lifecycle.

## Configuration

The packaged `appsettings.json` beside `Cutwork.exe` contains the ordinary application logging settings. Supported level, hierarchical category, output, and rotation behavior comes from `Flamoris.Logging`.

```json
{
  "logging": {
    "level": "debug",
    "categories": {
      "mcp": "info",
      "mcp.transport": "debug",
      "mcp.auth": "warn"
    },
    "outputs": [
      { "type": "console" },
      {
        "type": "file",
        "path": "logs/cutwork.log",
        "format": "text",
        "rotation": {
          "enabled": true,
          "maxFileSizeMb": 20,
          "maxFiles": 10
        }
      }
    ]
  }
}
```

Relative output paths are resolved below the writable per-user base path `%LOCALAPPDATA%\FLAMORIS\Cutwork`, never the installation directory.

The MCP bridge reads the same packaged configuration but deliberately ignores console outputs because its stdout is the MCP protocol stream. It writes under the same per-user base path while adding `-mcp-bridge` to each configured file name (for example, `cutwork-mcp-bridge.log`). The editor and bridge therefore never target the same file from separate processes.

## Categories

- `app`, `app.startup`, `app.shutdown`
- `document.open`, `document.save`
- `command.failure`
- `preview`, `render`
- `mcp`, `mcp.transport`, `mcp.protocol`, `mcp.auth`, `mcp.session`, `mcp.command`, `mcp.query`

Lifecycle events use `info`, recoverable connection and concurrency failures use `warn`, operation failures use `error`, and protocol/detail diagnostics use `debug`. Successful brush dabs and other high-volume edits are not emitted at `info`.

Log calls carry bounded identifiers, dimensions, revision numbers, operation kinds, and error codes as structured properties. They must not receive artwork pixels, document/media content, MCP payloads, pipe names, tokens, API keys, authorization values, or other credential material.

## Package restore

`NuGet.config` uses only public nuget.org; local development and CI restore the package without GitHub package credentials.
