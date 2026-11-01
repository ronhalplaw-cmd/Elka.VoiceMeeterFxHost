# Fork changes (ron/upgrades)

Based on `dev` at `d06f137` (Release 0.8.3.10). Source only — no binaries, no signed build.
Where something overlapped with features you already added, I kept your version and moved my
part on top of it (or dropped it).

## Features

- **Toggle View** — a small always-on-top strip of VST squares next to the tray, opened by a
  global hotkey (configurable, must include Ctrl/Alt/Win) or the "Toggle View" button. Click =
  bypass / power / open editor through the same paths as the canvas. It stays up while a plugin
  editor is in front and hides when focus goes anywhere else.
  Files: `ToggleView*.cs`, `MainWindow.ToggleView.cs`.
- **Deactivate** — powered off + bypassed in one step (audio passes through, no DSP time), set via
  your `PluginNodePowerState.SetCombined`. Shown in grey with light stripes. Menu shows your Power
  controls plus Deactivate/Activate.
  Files: `PluginNodeStates.cs`, `MainWindow.Deactivate.cs`.
- **Failed VST warning** — polls the engine's read-only failure query once a second; a plugin that
  threw in its audio callback or a sandbox worker that exited is shown on canvas cards, VST list,
  group cards, Toggle View, the status line (inside your `EngineStatusView`), a red tray badge and
  a "Reload <name>" tray menu entry. Nothing reloads automatically.
  Files: `MainWindow.PluginFailures.cs`, `TrayIconBadge.cs`, `EngineStatusView.*`.
- **Plugin editor windows** — "Always on Top" in the editor's title-bar menu, and the last
  position/size is remembered per VST. Observed via thread message hooks / accessibility events,
  the JUCE window procedure is not replaced. Sandboxed editors are skipped.
  Files: `PluginEditorWindows.cs`, `MainWindow.PluginEditorWindows.cs`.
- **Load Settings Once** (per VST) — the saved state is applied only on creation, skipping the
  verification retry, the delayed re-applies and the re-apply on editor open. Needed for plugins
  that keep live data in their state (loudness meters lose their measurement session).
  Files: `MainWindow.LoadSettingsOnce.cs`.
- **No VST state read on routine saves** — routine `QueueSave` calls write the last captured states;
  VST states are read on explicit Save, on exit, and once a minute. Fixes multi-second UI/editor
  freezes with plugins that have large states.
  Files: `MainWindow.PluginStateAutosave.cs`.
- **Saved-data check before re-push** — the post-restore re-pushes (300 ms / 1.2 s / editor open)
  only happen when the VST no longer holds its saved data. Avoids convolution reverbs reloading
  their IR several times. For plugins that never return identical bytes (e.g. Melda), the native
  parameter snapshot is compared instead.
  Files: `MainWindow.SavedDataCheck.cs`, `PluginParameterSnapshot.cs`, `native_api/FxHostNativeApi.cpp`,
  `plugins/PluginHostLayer.*`, `plugins/RealtimePluginProcessor.h`.
- **Signal Monitor** — not rewritten, only extended: per-channel max peak values under the bars
  (reset together with your Peak button), and double-clicking your endpoint meter strip opens the
  monitor on the meters/spectrum view.
  Files: `SignalMonitorWindow.cs`, `MainWindow.SignalMonitor.cs`, `MainWindow.EndpointMeters.cs`.

## Tests

`tests/ParameterSnapshotChecks` is new; a few existing test projects link the new
`ToggleViewSettings.cs` because `Models.cs` references it.
