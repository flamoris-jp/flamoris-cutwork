# Issue #48: Part identifier Apply

## Cause and scope

The domain `SetLayerSemanticName` command, history and `.flimg` persistence already
support Part identifiers. The shell discarded an unapplied preset or custom value:
`RefreshLayerPanel` rewrote the editable ComboBox from the document on every session
and tool notification. Tool hover can reach this refresh before the author clicks
Apply, so the command receives the old identifier instead of the pending input.

## Decision

The editor keeps its pending text while the document token, selected Part ID and
authored identifier remain unchanged. Selecting a different Part, replacing the
document or changing the authored identifier (including Undo/Redo) reloads the
document value. Unrelated preview, save-state and tool notifications preserve the
pending text.

Apply targets `EditorSession.SelectedLayerId` after confirming it is a Part, then
executes the existing `SetLayerSemanticName` command. It displays the normalized
authored value even when Apply is a no-op. The UI cache contains only the last
projected metadata; the session remains the editing and history authority.

There is no format change, new auto-apply behavior or display-name rename.

## Regression boundary

`PartIdentifierUiTests` instantiates the actual WPF window without showing it and
uses its editable ComboBox and Apply button on an STA dispatcher. It checks:

- a selected preset survives real session refreshes and Mask tool hover before
  Apply;
- Apply updates the selected Part through shared Undo/Redo and survives `.flimg`
  serialization;
- custom text survives unrelated notifications and is normalized on Apply;
- changing the selected Part reloads its identifier;
- reopening the same document reloads the selected Part without an old draft;
- a normalized no-op adds no history and still removes pending whitespace;
- clearing the identifier and selecting Base update the editor correctly.

These tests run in the existing Windows build-and-test CI job. Real artwork and
pointer interaction remain useful hands-on checks; the fixture does not claim
perceptual acceptance.

## Test fixture lifecycle correction

Review of the stalled Windows test step found an unbounded teardown wait in the
new fixture. It started `Dispatcher.Run()` directly, then called
`Application.Shutdown()` and joined the UI thread. WPF shuts down its dispatcher
only when the application started that loop through `Application.Run()`; the
direct dispatcher loop therefore remained running after application shutdown.
See WPF's `ShutdownImpl` and `RunDispatcher` in
[Application.cs](https://source.dot.net/PresentationFramework/System/Windows/Application.cs.html).

The fixture now runs its plain, resource-only `Application` through
`Application.Run()`. No production `App`, startup hooks or `StartupUri` are used.
Startup readiness, dispatcher operations and the final thread join each have a
30-second limit, so a lifecycle failure reports a test failure rather than
blocking the entire job indefinitely. The readiness task does not dispose an
event that a late-starting thread could still signal.

Local Linux cross-compilation of the Windows test project, using
`EnableWindowsTargeting=true`, completed with zero warnings and zero errors.
That checks compilation only; actual WPF execution and teardown must pass the
Windows CI test step before merge.
