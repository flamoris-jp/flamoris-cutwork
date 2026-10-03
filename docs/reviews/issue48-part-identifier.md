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

- a selected preset survives real session refreshes before Apply;
- Apply updates the selected Part through shared Undo/Redo and survives `.flimg`
  serialization;
- custom text survives unrelated notifications and is normalized on Apply;
- changing the selected Part reloads its identifier;
- a normalized no-op adds no history and still removes pending whitespace;
- clearing the identifier and selecting Base update the editor correctly.

These tests run in the existing Windows build-and-test CI job. Real artwork and
pointer interaction remain useful hands-on checks; the fixture does not claim
perceptual acceptance.
