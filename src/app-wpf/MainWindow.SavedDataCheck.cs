namespace Elka.VoiceMeeterFxHost.App;

// After a restore, FX Host pushes a VST's saved data in again (300 ms and 1.2 s later, and when its editor
// is first opened) to catch plugins that reset themselves after loading. Each push makes the plugin reload
// everything; for a convolution reverb that means reloading its impulse response, a pause of seconds.
// A push now happens only when the VST no longer holds its saved data.
//
// Some plugins (e.g. MeldaProduction) never return the same bytes twice, so their state can never be
// compared. When a restore retry still does not read back identically, the VST is marked here and its
// parameter values are compared with the saved parameter snapshot instead.
public partial class MainWindow
{
    private readonly HashSet<int> _pluginNodesWithUnstableState = [];

    private void MarkUnstablePluginState(PluginNodeSnapshot node)
    {
        _pluginNodesWithUnstableState.Add(node.Slot);
    }

    private void ForgetUnstablePluginState(int slot)
    {
        _pluginNodesWithUnstableState.Remove(slot);
    }

    // True when pushing the saved data in again would change nothing.
    private bool SavedPluginDataStillApplied(PluginNodeSnapshot node)
    {
        if (_pluginNodesWithUnstableState.Contains(node.Slot))
        {
            return string.IsNullOrWhiteSpace(node.PluginParameterStateBase64) ||
                   (_engine.TryGetPluginNodeParameterState(node.Slot, out var parameters) &&
                    PluginParameterSnapshot.Matches(node.PluginParameterStateBase64, parameters));
        }

        var stateMatches = string.IsNullOrWhiteSpace(node.PluginStateBase64) ||
                           (_engine.TryGetPluginNodeState(node.Slot, out var state) &&
                            string.Equals(state, node.PluginStateBase64, StringComparison.Ordinal));
        var presetMatches = string.IsNullOrWhiteSpace(node.PluginPresetBase64) ||
                            (_engine.TryGetPluginNodePreset(node.Slot, out var preset) &&
                             string.Equals(preset, node.PluginPresetBase64, StringComparison.Ordinal));
        return stateMatches && presetMatches;
    }
}
