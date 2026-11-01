using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

// Routine saves (QueueSave runs after every bypass, route, window or editor-placement change) do not ask
// the VSTs for their state. Reading a state runs on the JUCE message thread, which also drives every plugin
// editor, while the UI thread waits; a plugin that keeps large live data in its state (a loudness meter's
// measurement history) froze both for seconds on each routine save. Routine saves now write the states
// captured last, and VST states are read by an explicit Save, on exit, and once a minute here. The minute
// capture skips VSTs marked "Load Settings Once": those hold live data, so Save and exit capture them.
public partial class MainWindow
{
    private static readonly TimeSpan PluginStateAutosaveInterval = TimeSpan.FromMinutes(1);
    private DispatcherTimer? _pluginStateAutosaveTimer;

    private void SaveSettingsWithoutPluginStates()
    {
        PrepareSettingsForSave();
        FxHostSettingsStore.SaveInBackground(_settings);
    }

    private void StartPluginStateAutosave()
    {
        _pluginStateAutosaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PluginStateAutosaveInterval
        };
        _pluginStateAutosaveTimer.Tick += (_, _) => AutosavePluginStates();
        _pluginStateAutosaveTimer.Start();
    }

    private void StopPluginStateAutosave()
    {
        _pluginStateAutosaveTimer?.Stop();
    }

    private void AutosavePluginStates()
    {
        if (_loading || _isShuttingDown || _pluginNodeLoadInProgress)
        {
            return;
        }

        CapturePluginNodeStates(skipLoadSettingsOnce: true, out var changed);
        if (changed > 0)
        {
            SaveSettingsWithoutPluginStates();
        }
    }
}
