using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Elka.VoiceMeeterFxHost.App;

// Surfaces VSTs the engine has silently stopped processing: a plugin that throws inside its audio
// callback is switched to unprocessed pass-through until it is reloaded, and a sandboxed plugin whose
// worker exits goes quiet. The engine only reports this through a read-only query, polled here once a
// second; nothing is reloaded automatically. Failures are shown on the canvas cards, the VST list, group
// cards, Toggle View squares, the status line, the tray icon (red badge) and the tray menu.
public partial class MainWindow
{
    private const string FailedTrayMenuTag = "failed-vst";
    private readonly HashSet<int> _failedPluginSlots = [];
    private DispatcherTimer? _pluginFailureTimer;
    private Drawing.Icon? _trayFailureIcon;

    private void InitializePluginFailureMonitor()
    {
        _pluginFailureTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pluginFailureTimer.Tick += (_, _) => CheckPluginFailures();
        _pluginFailureTimer.Start();

        if (_trayIcon?.ContextMenuStrip is { } menu)
        {
            menu.Opening += (_, _) => RunToggleViewAction(() => RefreshTrayFailureMenuItems(menu));
        }
    }

    internal bool IsPluginFailed(PluginNodeSnapshot node)
    {
        return !node.MissingPlugin && _failedPluginSlots.Contains(node.Slot);
    }

    private void CheckPluginFailures()
    {
        try
        {
            if (_isShuttingDown || !_engine.PluginFailureQuerySupported || ToggleViewRestorePending() || _pluginNodeLoadInProgress)
            {
                return;
            }

            var failedNow = new HashSet<int>();
            foreach (var node in _settings.PluginNodes.Where(static node => !node.MissingPlugin))
            {
                if (_engine.IsPluginNodeFailed(node.Slot))
                {
                    failedNow.Add(node.Slot);
                    if (!_failedPluginSlots.Contains(node.Slot))
                    {
                        AppendLog($"VST ID {node.InstanceId} ({node.Name}) FAILED: it crashed while processing audio and is no longer being run. " +
                            "Audio passes through it unprocessed until you Reload it.");
                    }
                }
            }

            if (failedNow.SetEquals(_failedPluginSlots))
            {
                return;
            }

            _failedPluginSlots.Clear();
            _failedPluginSlots.UnionWith(failedNow);
            ShowPluginFailureState();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"VST failure check: {ex.Message}");
        }
    }

    private void ShowPluginFailureState()
    {
        UpdateTrayFailureBadge();
        UpdateLiveStatusText();
        RebuildRoutingCanvas();
        _toggleViewWindow?.RefreshTiles(force: true);
    }

    private string FailedPluginSummary()
    {
        return _failedPluginSlots.Count == 1 ? "1 VST FAILED" : $"{_failedPluginSlots.Count} VSTs FAILED";
    }

    // Shown under the engine status by UpdateLiveStatusText.
    private void ApplyFailureToStatusLine()
    {
        EngineStatus.SetFailure(_failedPluginSlots.Count == 0
            ? null
            : $"{FailedPluginSummary()} - right-click it and choose Reload");
    }

    private void UpdateTrayFailureBadge()
    {
        if (_trayIcon is null || _trayIconHandle is null)
        {
            return;
        }

        if (_failedPluginSlots.Count == 0)
        {
            _trayIcon.Icon = _trayIconHandle;
            _trayIcon.Text = "Elka VoiceMeeter FX Host";
            return;
        }

        _trayFailureIcon ??= TrayIconBadge.CreateWarning(_trayIconHandle);
        _trayIcon.Icon = _trayFailureIcon;
        _trayIcon.Text = $"Elka VoiceMeeter FX Host - {FailedPluginSummary()}";
    }

    // One "Reload <name>" entry per failed VST at the top of the tray menu.
    private void RefreshTrayFailureMenuItems(Forms.ContextMenuStrip menu)
    {
        foreach (var stale in menu.Items.Cast<Forms.ToolStripItem>().Where(static item => Equals(item.Tag, FailedTrayMenuTag)).ToList())
        {
            menu.Items.Remove(stale);
            stale.Dispose();
        }

        var failed = _settings.PluginNodes.Where(IsPluginFailed).ToList();
        if (failed.Count == 0)
        {
            return;
        }

        var index = 0;
        var header = new Forms.ToolStripMenuItem($"{FailedPluginSummary()}:") { Enabled = false, Tag = FailedTrayMenuTag };
        menu.Items.Insert(index++, header);
        foreach (var node in failed)
        {
            var slot = node.Slot;
            var item = new Forms.ToolStripMenuItem($"Reload {node.Name}")
            {
                Tag = FailedTrayMenuTag,
                ForeColor = Drawing.Color.FromArgb(200, 50, 50)
            };
            item.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ReloadFailedPlugin(slot)));
            menu.Items.Insert(index++, item);
        }

        menu.Items.Insert(index, new Forms.ToolStripSeparator { Tag = FailedTrayMenuTag });
    }

    private void ReloadFailedPlugin(int slot)
    {
        RunToggleViewAction(() =>
        {
            if (_settings.PluginNodes.FirstOrDefault(node => node.Slot == slot) is { } node)
            {
                ReloadPluginNode(node);
                CheckPluginFailures();
            }
        });
    }

    private void DisposePluginFailureMonitor()
    {
        _pluginFailureTimer?.Stop();
        _trayFailureIcon?.Dispose();
        _trayFailureIcon = null;
    }
}
