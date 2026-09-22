# Cutwork MCP / AI

Cutworkを起動して画像または`.flimg`を開き、上部の **MCP / AI** から
接続方法を選び、「読み取り専用で有効」または「編集を許可して有効」を選びます。
ブリッジは配布物内の`mcp/Flamoris.Mcp.Bridge.exe`です。起動済みの、
このウィンドウだけに接続します。

接続方法は次の2つです。

- **Manual**: 従来どおり、表示されたJSONを同じWindowsユーザーで動く
  MCPクライアントへコピーします。managed helperを起動しなくても利用できます。
- **OpenAI tunnel-client 0.0.14**: 接続設定で実行ファイル、profile、
  tunnel ID、loopback health addressを指定し、MCP有効時にowned helperを
  自動起動できます。現在のpipe/capabilityはenableや権限変更のたびに自動反映されます。

OpenAI control-plane API keyはWindows Credential Managerへ保存され、
`CONTROL_PLANE_API_KEY`としてowned childへだけ渡されます。
`FLAMORIS_MCP_CAPABILITY`もchild environmentで渡され、YAML、argv、log、
通常settingsへ保存されません。通常settingsには接続方法、auto-start、
実行ファイル/profile/tunnel/healthの安定した設定だけを保存します。

読み取り専用でも現在の画像ピクセルをクライアントへ公開します。
編集を許可すると、切り出し・補修・レイヤー編集と共有Undo/Redoが使えます。
Open/Save/書き出し先の指定、フォルダー閲覧、シェル実行は公開していません。
保存は通常のWPFメニューから行ってください。

同時接続は1つです。無効化、権限変更、文書を開き直す操作、終了によって
古い接続は失効します。同じファイルの再Openも、新しく有効化が必要です。
grant/pipe/capabilityは保存されず、再起動時はMCP Offです。Manual通信停止時は、
エディターを確認してから新しい接続設定をコピーしてください。
編集要求を自動再送しないでください。

Manual接続は同一PC内のクライアント用です。managed modeでは、review済みの
`tunnel-client`が認証済み外部clientとlocal bridgeを中継します。Cutwork自身は
public/LAN listenerやdaemonを追加せず、bridge以降は同じowner-only named pipeと
transient capability境界を使います。

## Protocol / configuration

`Flamoris.Mcp.Core` **1.1.0** / official C# SDK **2.2.0**; MCP
**2026-07-28** with the Core-supported legacy initialization path, standard stdio.
Core owns SDK protocol objects; all editing stays in the existing running WPF
session. Example configuration (the UI supplies fresh exact values):

`ReadTimeoutMs` bounds a frame only after bytes for that frame have started.
Ordinary idle time between requests does not expire a healthy connection. Stop,
permission change, document replacement and application shutdown still revoke and
disconnect an idle bridge immediately.

```json
{"mcpServers":{"cutwork":{"command":"C:\\Cutwork\\mcp\\Flamoris.Mcp.Bridge.exe","args":["--pipe","flamoris-cutwork-<fresh address>"],"env":{"FLAMORIS_MCP_CAPABILITY":"<fresh 256-bit capability>"}}}}
```

Pipe authentication uses the same local Windows owner/elevation boundary as
Kachinco: protected owner-only DACL plus kernel remote-client rejection. The
address is not a password. A separate transient capability is passed only through
the bridge environment, removed by the bridge at startup and never placed in argv
or saved in `.flimg`/settings. Enabling trusts processes accepted by that local-user
policy and capability; no artwork/file permission is inherited by another document.

## Managed helper lifecycle

Cutworkは次の状態を別々に表示・管理します。

- MCP enabled / current grant available;
- managed helper starting/running/stopped/faulted;
- authenticated external client connected.

helperが起動しているだけでは、外部client接続中とは表示しません。Manual接続では
helper stoppedのまま外部clientが接続できます。helperの起動・更新・停止に失敗しても
通常の画像編集とManual接続は利用できます。

無効化、権限変更、文書置換、終了では、current grantを先にrevokeし、その後で
このCutwork instanceが起動・追跡している正確なchildだけを停止します。プロセス名の
検索や他の`tunnel-client`の停止は行いません。grant発行またはrotationが成功した後は、
caller cancellationが同時に成立してもlifecycle/providerの整合を完了します。

verified tunnel-client 0.0.14 profile schemaは
`config_version`, `control_plane`, `tunnel_id`, environment参照の
`api_key`, loopback `health`, `open_browser`, `log`,
`mcp.commands`を使用します。YAMLのcommandにはbridge executableとcurrent pipeだけを
書き、capabilityは書きません。詳細な責務と順序は
[ADR 0004](decisions/0004-managed-mcp-connections.md)を参照してください。

## Tool workflow

1. `mcp.context {}` returns the common product/runtime/document identity, revision
   and permission. Cutwork `context` returns dimensions,
   selection, busy, dirty and shared history state. `historyStateRevision` is a
   different value: it can go backward on Undo and must not qualify edits.
2. `layers {"offset":0,"count":64}` returns bounded metadata, stable IDs,
   compositor indices, semantic `partOrder` and Repair `ownerPartId`.
3. `image` returns one PNG as bounded `pngBase64` with `mimeType: image/png`, its
   captured token/revision, source bounds, crop, dimensions and exact nearest-pixel mapping. Sources: Original, Composite,
   Part, Mask; specify the Part UUID for Part/Mask. Null ROI means source bounds.
4. `part_preview` takes document-space fence, step and maxEdge. Repeat with
   different existing adjustment steps to compare; it never changes selection,
   dirty state, revision or history. Preview and create use the same fitter. Bounded
   fitting runs against a captured immutable Original outside the WPF serialization
   lane, then re-enters the guarded read/commit gate before disclosure or installation.
5. Core wraps each tool's Cutwork-owned `input` in a `guard` containing runtime ID,
   document token and (for mutation) expected revision. `edit` takes a complete
   atomic operation array. One accepted
   batch is one ordinary Undo entry. `@0` refers to the object returned by operation
   zero; only earlier results can be referenced. A no-op Clone may return null.
6. WPF Ctrl+Z/Ctrl+Y and MCP `undo`/`redo` use the same history. MCP history calls
   also require token/revision. Query again after conflict or ambiguous disconnect.

Conceptual guarded edit payload (the MCP client library supplies it as tool arguments):

```json
{
  "guard":{"runtimeId":"<mcp.context>","documentToken":"<mcp.context>","expectedRevision":"<mcp.context>"},
  "input":{"operations":[
      {"type":"part.create","fence":[{"x":10,"y":10},{"x":60,"y":10},{"x":60,"y":60},{"x":10,"y":60}],"step":0,"name":"Face"},
      {"type":"mask.stroke","target":"@0","points":[{"x":25.5,"y":25.5},{"x":35.5,"y":25.5}],"radius":3,"polarity":"Erase"},
      {"type":"clone.stroke","target":null,"ownerPart":"@0","global":false,"source":{"x":15.5,"y":15.5},"points":[{"x":25.5,"y":25.5},{"x":35.5,"y":25.5}],"radius":3,"mode":"Fixed"}
  ]}
}
```

The complete closed schemas are returned by `tools/list` and used for validation.
Clone explicitly chooses one of existing Repair `target`, new Repair `ownerPart`,
or new legacy/global underpaint (`global:true`); it never uses ambient selection.
Offset uses immutable source-anchor minus first destination; Fixed stamps the
same Original neighborhood at each dab. Patch freezes Original source geometry,
then applies the ordinary scale/rotation/placement rules.

Brushes use document pixels and shared StrokeSampler; UI zoom/DPI/radius does not
change an external operation. Human strokes, pending fences/fitting/Patch and
metadata/file coordination return `busy`; they are never silently committed or
cancelled. A mutation enters Core's single synchronous commit gate on the WPF
serialization lane; Cutwork checks cancellation at bounded operation/stroke and
pre-commit boundaries. A cancelled precommit batch restores
previous authored state and history. Rollback may increase the document revision.

## Bounds and dispositions

See [ADR 0002](decisions/0002-live-mcp.md) for exact aggregate work, frame, image,
ROI, dab and memory limits. Smaller ROI/shorter strokes resolve resource errors.
A complete call is limited to 15 seconds, including dispatcher admission. Slow
frames, malformed JSON/UTF-8, duplicate envelope fields and excessive pipelining
close only that client connection. Normal schema, conflict, busy and resource
failures return bounded tool errors. Logs do not contain artwork or arguments.

| Existing feature | MCP disposition |
|---|---|
| Part fitting/create, mask Add/Erase, Fixed/Offset Clone, Patch create/transform | Exposed, bounded, explicit targets |
| Rename, semanticName, visibility, legal reorder, Part cascade delete | Exposed through ordinary commands |
| Atomic batch, shared Undo/Redo | Exposed with token/revision |
| Original/Composite/Part/mask/ROI | Base64 PNG query, bounded; no disk write |
| Blur/Smudge | UI only in this release |
| Selection, tool settings, hover, pan/zoom | UI only |
| Open/import/document replacement, Save/MarkSaved, export, filesystem/process | Denied; later exact-target file grant would require separate review |
| Raw RasterPatch, arbitrary commands/uploads, future operation types | Denied until explicitly reviewed |

## Verification boundary

The Production workflow builds/tests, uses the existing packaging script, then
runs `tests/Cutwork.WindowsSmoke` against freshly published editor and bridge in a
new temporary directory outside the repository. Child PATH is System32 only.
The official client is a test dependency; the product includes the official SDK
server runtime, not a separate client test executable or synthetic artwork.

The smoke opens synthetic PNG via the real file dialog, exercises read-only images
and direct denial, Part/Mask/Clone/Patch, automatic WPF projection, WPF history,
ordinary WPF visibility edit, batch rollback, v2 Save/reopen, downgrade, Stop,
same-file reopen, app close and bridge stdin EOF. Passing runs are recorded in
PR evidence; source inspection alone is not a passing execution.

Remaining human acceptance is separate: real artwork seams/latency; actual
canvas-focus keyboard Ctrl+Z/Ctrl+Y; Japanese/English copy flow; 100/125/150/200%
DPI. Different-user, elevation and separate-machine SMB tests are not implied by
same-user automated pipe tests. Intended local AI client setup is also separate
from official SDK interoperability.
