namespace Elka.VoiceMeeterFxHost.App;

// Deactivate (see PluginNodeStates) is the powered-off + bypassed combination, set in one step through
// PluginNodePowerState.SetCombined, the same path as the combined bypass/power hotkey.
public partial class MainWindow
{
    private void SetNodeDeactivated(PluginNodeSnapshot node, bool deactivated)
    {
        if (node.MissingPlugin)
        {
            return;
        }

        PluginNodePowerState.SetCombined(node, deactivated, _engine.SetPluginNodePowered, _engine.SetPluginNodeBypassed);
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
        AppendLog($"VST ID {node.InstanceId} ({node.Name}) {(deactivated ? "deactivated: audio passes through, plugin not processing" : "activated")}.");
    }
}
