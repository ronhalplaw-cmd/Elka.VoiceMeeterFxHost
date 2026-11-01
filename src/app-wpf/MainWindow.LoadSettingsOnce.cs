using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

// "Load Settings Once": a VST marked with it gets its saved state only when it is created (as a
// conventional host does), skipping the extra re-loads FX Host normally performs afterwards: the
// verification retry, the two delayed re-applies after startup, and the re-apply when its editor opens.
// Plugins that keep live data in their state (e.g. loudness meters storing their measurement session)
// stop working when that state is pushed into them again while they run.
public partial class MainWindow
{
    private static string LoadSettingsOnceKey(PluginNodeSnapshot node)
    {
        return $"{node.InstanceId}|{node.PluginIdentifier}";
    }

    private bool IsLoadSettingsOnce(PluginNodeSnapshot node)
    {
        return _settings.LoadSettingsOnce?.Contains(LoadSettingsOnceKey(node)) == true;
    }

    private bool SkipSettingsReload(PluginNodeSnapshot node, string step)
    {
        if (!IsLoadSettingsOnce(node))
        {
            return false;
        }

        AppendLog($"{node.Name}: Load Settings Once - skipped {step}.");
        return true;
    }

    private void SetLoadSettingsOnce(PluginNodeSnapshot node, bool enabled)
    {
        var keys = _settings.LoadSettingsOnce ??= [];
        var key = LoadSettingsOnceKey(node);
        keys.Remove(key);
        if (enabled)
        {
            keys.Add(key);
            _pluginNodesPendingSavedDataApply.Remove(node.Slot);
        }

        QueueSave();
        AppendLog($"{node.Name}: Load Settings Once {(enabled ? "on" : "off")}. Takes effect from the next start or reload.");
    }

    private MenuItem BuildLoadSettingsOnceMenuItem(PluginNodeSnapshot node)
    {
        var item = new MenuItem
        {
            Header = "Load Settings Once",
            IsCheckable = true,
            IsChecked = IsLoadSettingsOnce(node)
        };
        item.Click += (_, _) => SetLoadSettingsOnce(node, item.IsChecked);
        return item;
    }
}
