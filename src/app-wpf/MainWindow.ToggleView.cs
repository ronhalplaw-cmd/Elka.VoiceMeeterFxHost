using System.Windows;
using System.Windows.Interop;

namespace Elka.VoiceMeeterFxHost.App;

// Toggle View: a compact strip of VST squares next to the tray. Every action goes through the
// same SetNodeBypass / OpenPluginEditorWithSavedData paths the routing canvas uses; nothing here
// touches the audio engine directly.
public partial class MainWindow
{
    private ToggleViewWindow? _toggleViewWindow;
    private readonly ToggleViewHotkeyRegistration _toggleViewHotkey = new();
    private bool _toggleViewHotkeyHooked;

    private ToggleViewSettings ToggleViewConfig
    {
        get
        {
            if (_settings.ToggleView is null)
            {
                _settings.ToggleView = new ToggleViewSettings();
                _settings.ToggleView.Normalize();
            }

            return _settings.ToggleView;
        }
    }

    private bool ToggleViewEnabled => _settings.ToggleView?.Enabled == true;

    private void NormalizeToggleViewSettings()
    {
        _settings.ToggleView ??= new ToggleViewSettings();
        _settings.ToggleView.Normalize();
    }

    private void AttachToggleViewHotkey()
    {
        RunToggleViewAction(() =>
        {
            if (!_toggleViewHotkeyHooked && _windowMessageSource is not null)
            {
                _windowMessageSource.AddHook(ToggleViewMessageHook);
                _toggleViewHotkeyHooked = true;
            }

            if (!TryRegisterToggleViewHotkey(ToggleViewConfig.Hotkey, out var error))
            {
                AppendLog($"Toggle View shortcut was not registered: {error}. Change it in Toggle View settings.");
            }
        });
    }

    private bool TryRegisterToggleViewHotkey(string hotkey, out string error)
    {
        if (!ToggleViewHotkeyGesture.TryParse(hotkey, out var gesture))
        {
            error = $"'{hotkey}' is not a valid shortcut";
            return false;
        }

        return _toggleViewHotkey.Register(new WindowInteropHelper(this).Handle, gesture, out error);
    }

    private IntPtr ToggleViewMessageHook(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == ToggleViewHotkeyRegistration.WindowsMessageHotkey &&
            wParam.ToInt64() == ToggleViewHotkeyRegistration.HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(() => RunToggleViewAction(() =>
            {
                if (!_isShuttingDown)
                {
                    EnsureToggleViewWindow().ToggleFromHotkey();
                }
            })));
        }

        return IntPtr.Zero;
    }

    private void ToggleViewButton_Click(object sender, RoutedEventArgs e)
    {
        RunToggleViewAction(() =>
        {
            var config = ToggleViewConfig;
            config.Enabled = !config.Enabled;
            QueueSave();
            UpdateToggleViewButton();
            if (!config.Enabled)
            {
                _toggleViewWindow?.Hide();
                AppendLog("Toggle View off. The tray icon opens the main window again.");
                return;
            }

            AppendLog($"Toggle View on. Click the tray icon or press {config.Hotkey} for the VST squares.");
            HideToTray(showHint: false);
            EnsureToggleViewWindow().ShowNearTray();
        });
    }

    private void ToggleViewSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenToggleViewSettings();
    }

    private void UpdateToggleViewButton()
    {
        if (ToggleViewEnabled)
        {
            ToggleViewButton.Style = (Style)FindResource("VstButton");
        }
        else
        {
            ToggleViewButton.ClearValue(StyleProperty);
        }
    }

    private void TrayIconLeftClick()
    {
        RunToggleViewAction(() =>
        {
            if (ToggleViewEnabled && !_isShuttingDown)
            {
                EnsureToggleViewWindow().ToggleFromTray();
            }
        });
    }

    private void TrayIconDoubleClick()
    {
        RunToggleViewAction(() =>
        {
            if (TryOpenTrayDoubleClickPlugin())
            {
                return;
            }

            // With Toggle View on, the first click of the double-click already opened the squares.
            if (!ToggleViewEnabled)
            {
                RestoreFromTray();
            }
        });
    }

    // Opens or closes the editor chosen in Toggle View settings. Returns false when no VST is
    // chosen, so the double-click keeps its normal behaviour.
    private bool TryOpenTrayDoubleClickPlugin()
    {
        var choice = ToggleViewConfig.TrayDoubleClickPlugin;
        if (choice is null || choice.InstanceId < 0)
        {
            return false;
        }

        var node = ResolveToggleViewNode(choice);
        if (node is null)
        {
            ClearTrayDoubleClickPlugin(choice.PluginName);
            return false;
        }

        var status = ToggleViewStatus(node);
        if (status == ToggleViewTileStatus.Loading)
        {
            return true;
        }

        if (status is ToggleViewTileStatus.Missing or ToggleViewTileStatus.NotLoaded)
        {
            return false;
        }

        // Toggles, like a second click on a Toggle View square: an open editor is closed.
        if (PluginEditorIsOpen(node))
        {
            AppendLog(_engine.ClosePluginEditor(node.Slot));
        }
        else
        {
            OpenPluginEditorWithSavedData(node);
        }

        return true;
    }

    private void ClearTrayDoubleClickPluginIfRemoved(PluginNodeSnapshot removed)
    {
        var choice = ToggleViewConfig.TrayDoubleClickPlugin;
        if (choice is not null && choice.InstanceId == removed.InstanceId &&
            (string.IsNullOrEmpty(choice.PluginIdentifier) ||
             string.Equals(choice.PluginIdentifier, removed.PluginIdentifier, StringComparison.OrdinalIgnoreCase)))
        {
            ClearTrayDoubleClickPlugin(removed.Name);
        }
    }

    private void ClearTrayDoubleClickPlugin(string pluginName)
    {
        ToggleViewConfig.TrayDoubleClickPlugin = new ToggleViewSlotSettings();
        QueueSave();
        AppendLog($"Tray double-click no longer opens {(string.IsNullOrWhiteSpace(pluginName) ? "a VST" : pluginName)}: it was removed.");
    }

    private void ShowToggleViewFromTrayMenu()
    {
        RunToggleViewAction(() => EnsureToggleViewWindow().ShowNearTray());
    }

    private ToggleViewWindow EnsureToggleViewWindow()
    {
        return _toggleViewWindow ??= new ToggleViewWindow(
            BuildToggleViewTiles,
            (slotIndex, command) => RunToggleViewAction(() => RunToggleViewCommand(slotIndex, command)),
            ToggleViewEditorIsOpen,
            () => RunToggleViewAction(RestoreFromTray),
            OpenToggleViewSettings);
    }

    private IReadOnlyList<ToggleViewTileState> BuildToggleViewTiles()
    {
        var tiles = new List<ToggleViewTileState>();
        var slots = ToggleViewConfig.Slots;
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            if (!slot.Visible || slot.InstanceId < 0)
            {
                continue;
            }

            var node = ResolveToggleViewNode(slot);
            var name = node?.Name ?? (string.IsNullOrWhiteSpace(slot.PluginName) ? "VST" : slot.PluginName);
            var label = string.IsNullOrWhiteSpace(slot.CustomName) ? name : slot.CustomName.Trim();
            tiles.Add(new ToggleViewTileState(index, label, ToggleViewStatus(node), node is not null && IsLoadSettingsOnce(node)));
        }

        return tiles;
    }

    private ToggleViewTileStatus ToggleViewStatus(PluginNodeSnapshot? node)
    {
        if (node is null)
        {
            return ToggleViewTileStatus.NotLoaded;
        }

        if (ToggleViewRestorePending())
        {
            return ToggleViewTileStatus.Loading;
        }

        if (node.MissingPlugin)
        {
            return ToggleViewTileStatus.Missing;
        }

        if (IsPluginFailed(node))
        {
            return ToggleViewTileStatus.Failed;
        }

        if (PluginNodeStates.IsDeactivated(node))
        {
            return ToggleViewTileStatus.Deactivated;
        }

        if (!node.Enabled)
        {
            return ToggleViewTileStatus.TurnedOff;
        }

        return node.Bypassed ? ToggleViewTileStatus.Bypassed : ToggleViewTileStatus.Active;
    }

    // While the saved graph is still being reloaded, the node list holds saved snapshots whose
    // slots are not live yet, so squares stay disabled until the restore finishes.
    private bool ToggleViewRestorePending()
    {
        return !_savedPluginNodesRestored &&
            _settings.PluginNodes.Count > 0 &&
            (_startupEngineDelayActive || _pluginRestoreTimer.IsEnabled);
    }

    private PluginNodeSnapshot? ResolveToggleViewNode(ToggleViewSlotSettings slot)
    {
        var node = _settings.PluginNodes.FirstOrDefault(candidate => candidate.InstanceId == slot.InstanceId);
        if (node is null)
        {
            return null;
        }

        // VST IDs are reused after a VST is removed; never let a square drive a different plugin.
        if (!string.IsNullOrEmpty(slot.PluginIdentifier) &&
            !string.IsNullOrEmpty(node.PluginIdentifier) &&
            !string.Equals(slot.PluginIdentifier, node.PluginIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return node;
    }

    private PluginNodeSnapshot? ToggleViewNodeForSlot(int slotIndex)
    {
        var slots = ToggleViewConfig.Slots;
        return slotIndex >= 0 && slotIndex < slots.Count ? ResolveToggleViewNode(slots[slotIndex]) : null;
    }

    private void RunToggleViewCommand(int slotIndex, ToggleViewCommand command)
    {
        var node = ToggleViewNodeForSlot(slotIndex);
        if (node is null)
        {
            return;
        }

        var status = ToggleViewStatus(node);
        if (command == ToggleViewCommand.Reload)
        {
            // Manual only: a failed VST is never reloaded automatically.
            if (status == ToggleViewTileStatus.Failed)
            {
                ReloadPluginNode(node);
                CheckPluginFailures();
            }

            return;
        }

        var live = status is ToggleViewTileStatus.Active or ToggleViewTileStatus.Bypassed or
            ToggleViewTileStatus.TurnedOff or ToggleViewTileStatus.Deactivated;
        if (!live)
        {
            return;
        }

        switch (command)
        {
            case ToggleViewCommand.ToggleEditor:
                if (PluginEditorIsOpen(node))
                {
                    AppendLog(_engine.ClosePluginEditor(node.Slot));
                }
                else
                {
                    OpenPluginEditorWithSavedData(node);
                }

                break;

            case ToggleViewCommand.ToggleBypass:
                if (status is ToggleViewTileStatus.Active or ToggleViewTileStatus.Bypassed)
                {
                    var bypassed = !node.Bypassed;
                    SetNodeBypass(node, bypassed);
                    AppendLog($"VST ID {node.InstanceId} ({node.Name}) bypass {(bypassed ? "enabled" : "disabled")} from Toggle View.");
                }

                break;

            case ToggleViewCommand.TogglePower:
                if (status == ToggleViewTileStatus.Deactivated)
                {
                    // The light means "on/off"; turning a deactivated VST on brings it fully back.
                    SetNodeDeactivated(node, false);
                }
                else
                {
                    var enabled = !node.Enabled;
                    SetNodeEnabled(node, enabled);
                    AppendLog($"VST ID {node.InstanceId} ({node.Name}) turned {(enabled ? "on" : "off")} from Toggle View.");
                }

                break;

            case ToggleViewCommand.ToggleDeactivate:
                SetNodeDeactivated(node, status != ToggleViewTileStatus.Deactivated);
                break;

            case ToggleViewCommand.ToggleLoadSettingsOnce:
                SetLoadSettingsOnce(node, !IsLoadSettingsOnce(node));
                break;
        }
    }

    private bool ToggleViewEditorIsOpen(int slotIndex)
    {
        var node = ToggleViewNodeForSlot(slotIndex);
        return node is not null && !node.MissingPlugin && PluginEditorIsOpen(node);
    }

    private static string PluginEditorWindowTitle(PluginNodeSnapshot node)
    {
        return $"{node.Name} - ID {node.InstanceId}";
    }

    // Editors are native windows titled by PluginEditorWindowTitle, hosted in this process or in the
    // sandbox worker. The engine has no "is open" query, and the user may close an editor with its X.
    private static bool PluginEditorIsOpen(PluginNodeSnapshot node)
    {
        var title = PluginEditorWindowTitle(node);
        var ownProcessId = (uint)Environment.ProcessId;
        var found = false;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) != title.Length)
            {
                return true;
            }

            var buffer = new System.Text.StringBuilder(title.Length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            if (!string.Equals(buffer.ToString(), title, StringComparison.Ordinal))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == ownProcessId || IsPluginWorkerProcess(processId))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsPluginWorkerProcess(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "Elka.PluginWorker", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private void OpenToggleViewSettings()
    {
        RunToggleViewAction(() =>
        {
            _toggleViewWindow?.Hide();
            var options = _settings.PluginNodes
                .Where(static node => !node.MissingPlugin && node.InstanceId >= 0)
                .OrderBy(static node => node.InstanceId)
                .Select(static node => new ToggleViewPluginOption(
                    node.InstanceId,
                    node.PluginIdentifier,
                    node.Name,
                    $"ID {node.InstanceId} ({node.Mode})"))
                .ToList();

            var dialog = new ToggleViewSettingsWindow(ToggleViewConfig, options);
            if (IsVisible)
            {
                dialog.Owner = this;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            if (dialog.ShowDialog() != true || dialog.Result is not { } result)
            {
                return;
            }

            result.Enabled = ToggleViewConfig.Enabled;
            result.Normalize();
            var previousHotkey = ToggleViewConfig.Hotkey;
            if (!string.Equals(result.Hotkey, previousHotkey, StringComparison.OrdinalIgnoreCase) ||
                _toggleViewHotkey.Current is null)
            {
                if (TryRegisterToggleViewHotkey(result.Hotkey, out var error))
                {
                    AppendLog($"Toggle View shortcut set to {result.Hotkey}.");
                }
                else
                {
                    MessageBox.Show(
                        $"The shortcut could not be used: {error}.\n\nThe previous shortcut ({previousHotkey}) is kept.",
                        "Toggle View",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    result.Hotkey = previousHotkey;
                    TryRegisterToggleViewHotkey(previousHotkey, out _);
                }
            }

            _settings.ToggleView = result;
            QueueSave();
            _toggleViewWindow?.RefreshTiles(force: true);
            AppendLog($"Toggle View settings saved: {result.Slots.Count(static slot => slot.Visible && slot.InstanceId >= 0)} square(s).");
        });
    }

    private void ShutdownToggleView()
    {
        _toggleViewHotkey.Dispose();
        _toggleViewWindow?.CloseForShutdown();
        _toggleViewWindow = null;
    }

    // An exception reaching the dispatcher shuts the host (and its audio) down; Toggle View must never do that.
    private void RunToggleViewAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Toggle View: {ex}");
            try
            {
                AppendLog($"Toggle View error: {ex.Message}");
            }
            catch
            {
                // Logging is best effort here.
            }
        }
    }
}
