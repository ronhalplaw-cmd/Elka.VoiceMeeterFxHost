namespace Elka.VoiceMeeterFxHost.App;

// Per-VST editor window preferences (keyed by VST ID + plugin identifier): the title-bar
// "Always on Top" choice and the last position/size. Both are re-applied whenever that VST's
// editor window is shown again.
public partial class MainWindow
{
    private PluginEditorWindows? _pluginEditorWindows;

    private void StartPluginEditorWindows()
    {
        try
        {
            _pluginEditorWindows ??= new PluginEditorWindows(
                IsEditorAlwaysOnTop,
                // Raised on the editor's (JUCE) thread: hand off without waiting.
                (instanceId, pinned) => Dispatcher.BeginInvoke(new Action(() => SetEditorAlwaysOnTop(instanceId, pinned))),
                SavedEditorBounds,
                SetEditorBounds);
            _pluginEditorWindows.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Plugin editor windows: {ex}");
        }
    }

    private string? EditorPreferenceKey(int instanceId)
    {
        var node = _settings.PluginNodes.FirstOrDefault(candidate => candidate.InstanceId == instanceId);
        return node is null ? null : $"{instanceId}|{node.PluginIdentifier}";
    }

    private bool IsEditorAlwaysOnTop(int instanceId)
    {
        return EditorPreferenceKey(instanceId) is { } key && _settings.EditorsAlwaysOnTop?.Contains(key) == true;
    }

    private void SetEditorAlwaysOnTop(int instanceId, bool pinned)
    {
        try
        {
            if (EditorPreferenceKey(instanceId) is not { } key)
            {
                return;
            }

            var keys = _settings.EditorsAlwaysOnTop ??= [];
            keys.Remove(key);
            if (pinned)
            {
                keys.Add(key);
            }

            QueueSave();
            AppendLog($"VST ID {instanceId} editor Always on Top {(pinned ? "on" : "off")}.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Always on Top: {ex}");
        }
    }

    private int[]? SavedEditorBounds(int instanceId)
    {
        return EditorPreferenceKey(instanceId) is { } key &&
            _settings.EditorWindowBounds?.TryGetValue(key, out var bounds) == true
                ? bounds
                : null;
    }

    // Called for every move/resize step; the save is debounced by QueueSave.
    private void SetEditorBounds(int instanceId, int[] bounds)
    {
        try
        {
            if (EditorPreferenceKey(instanceId) is not { } key)
            {
                return;
            }

            var all = _settings.EditorWindowBounds ??= [];
            if (all.TryGetValue(key, out var previous) && previous.SequenceEqual(bounds))
            {
                return;
            }

            all[key] = bounds;
            QueueSave();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Plugin editor placement: {ex}");
        }
    }

    // The instance (and its hook delegates) stays referenced so a hook call already in flight on the
    // JUCE thread during shutdown never lands on a collected delegate.
    private void StopPluginEditorWindows()
    {
        _pluginEditorWindows?.Dispose();
    }
}
