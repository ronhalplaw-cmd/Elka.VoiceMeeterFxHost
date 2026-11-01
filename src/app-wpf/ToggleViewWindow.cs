using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

// Small always-on-top strip of VST squares shown next to the tray. It only calls back into
// MainWindow, which uses the same bypass, power and editor paths as the routing canvas.
internal sealed class ToggleViewWindow : Window
{
    private const double DefaultTileSize = 72.0;
    private const double MinimumTileSize = 40.0;
    private const double TileGap = 6.0;
    private const double StripPadding = 6.0;
    private const double ScreenEdgeMargin = 8.0;
    private const double EmptyStateWidth = 340.0;
    private const string PluginWorkerProcessName = "Elka.PluginWorker";
    private static readonly TimeSpan DeactivationClickGrace = TimeSpan.FromMilliseconds(400);

    private readonly Func<IReadOnlyList<ToggleViewTileState>> _getTiles;
    private readonly Action<int, ToggleViewCommand> _runCommand;
    private readonly Func<int, bool> _isEditorOpen;
    private readonly Action _openMainWindow;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _pendingHideCheck;
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly WinEventDelegate _foregroundChanged;
    private readonly int _processId = Environment.ProcessId;
    private IntPtr _foregroundHook;
    private IReadOnlyList<ToggleViewTileState> _shownTiles = [];
    private DateTime _lastHiddenByDeactivationUtc = DateTime.MinValue;
    private bool _allowClose;
    private int? _pressedSlot;

    public ToggleViewWindow(
        Func<IReadOnlyList<ToggleViewTileState>> getTiles,
        Action<int, ToggleViewCommand> runCommand,
        Func<int, bool> isEditorOpen,
        Action openMainWindow,
        Action openSettings)
    {
        _getTiles = getTiles;
        _runCommand = runCommand;
        _isEditorOpen = isEditorOpen;
        _openMainWindow = openMainWindow;
        _openSettings = openSettings;
        _foregroundChanged = OnForegroundChanged;

        Title = "Toggle View";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = (Brush)FindResource("PanelBrush");
        BorderBrush = (Brush)FindResource("StrokeBrush");
        BorderThickness = new Thickness(1);
        Content = new Border { Padding = new Thickness(StripPadding), Child = _strip };

        var menu = new ContextMenu();
        AddWindowMenuItems(menu);
        ContextMenu = menu;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _refreshTimer.Tick += (_, _) => Guard(() => RefreshTiles(force: false));
        _pendingHideCheck = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _pendingHideCheck.Tick += (_, _) => Guard(CheckWhereFocusSettled);

        IsVisibleChanged += (_, _) => Guard(() =>
        {
            if (IsVisible)
            {
                _refreshTimer.Start();
                StartForegroundWatch();
            }
            else
            {
                _refreshTimer.Stop();
                StopForegroundWatch();
                _pendingHideCheck.Stop();
                _pressedSlot = null;
            }
        });
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Hide();
            }
        };
    }

    public void ShowNearTray()
    {
        RefreshTiles(force: true);
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    // Tray clicks hide the strip (foreground moves to the taskbar) before the click arrives,
    // so a click that just hid it must not reopen it.
    public void ToggleFromTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        if (DateTime.UtcNow - _lastHiddenByDeactivationUtc < DeactivationClickGrace)
        {
            return;
        }

        ShowNearTray();
    }

    public void ToggleFromHotkey()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        ShowNearTray();
    }

    public void RefreshTiles(bool force)
    {
        var tiles = _getTiles();
        if (!force && tiles.SequenceEqual(_shownTiles))
        {
            return;
        }

        _shownTiles = tiles;
        RebuildStrip();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        _refreshTimer.Stop();
        StopForegroundWatch();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    // The strip stays up while a plugin editor (in this process or the sandbox worker) is in front,
    // and hides when anything else - the desktop, a DAW, the taskbar, the main window - takes focus.
    // Opening an editor can pass focus through transient shell windows first, so the decision is made
    // on where focus settles shortly afterwards, not on every intermediate event.
    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        Guard(() =>
        {
            if (!IsVisible || ShouldStayOpenFor(window))
            {
                _pendingHideCheck.Stop();
                return;
            }

            _pendingHideCheck.Stop();
            _pendingHideCheck.Start();
        });
    }

    private void CheckWhereFocusSettled()
    {
        _pendingHideCheck.Stop();
        var foreground = GetForegroundWindow();
        if (!IsVisible || ShouldStayOpenFor(foreground))
        {
            return;
        }

        RuntimeLog.Write($"Toggle View hidden: focus moved to {DescribeWindow(foreground)}.");
        _lastHiddenByDeactivationUtc = DateTime.UtcNow;
        Hide();
    }

    private bool ShouldStayOpenFor(IntPtr window)
    {
        return window == IntPtr.Zero ||
            window == new WindowInteropHelper(this).Handle ||
            ContextMenu?.IsOpen == true ||
            IsPluginEditorWindow(window);
    }

    private static string DescribeWindow(IntPtr window)
    {
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return $"{className} in {process.ProcessName}";
        }
        catch (Exception)
        {
            return $"{className} in process {processId}";
        }
    }

    private bool IsPluginEditorWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId == _processId)
        {
            // WPF windows here are the main window and dialogs; everything else native is a plugin editor.
            return HwndSource.FromHwnd(window) is null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, PluginWorkerProcessName, StringComparison.OrdinalIgnoreCase);
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

    private void StartForegroundWatch()
    {
        if (_foregroundHook == IntPtr.Zero)
        {
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _foregroundChanged, 0, 0, WinEventOutOfContext);
        }
    }

    private void StopForegroundWatch()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    private void RebuildStrip()
    {
        _strip.Children.Clear();
        var workArea = SystemParameters.WorkArea;
        double contentWidth;
        double contentHeight;

        if (_shownTiles.Count == 0)
        {
            _strip.Children.Add(new TextBlock
            {
                Text = "No squares selected yet. Use the gear button next to Toggle View in the main window.",
                TextWrapping = TextWrapping.Wrap,
                Width = EmptyStateWidth - 2 * StripPadding - 12,
                Margin = new Thickness(6),
                Style = (Style)FindResource("MutedText")
            });
            contentWidth = EmptyStateWidth;
            contentHeight = DefaultTileSize + 2 * StripPadding + 2;
        }
        else
        {
            var count = _shownTiles.Count;
            var chrome = 2 * StripPadding + 2;
            var available = Math.Max(MinimumTileSize, workArea.Width - 2 * ScreenEdgeMargin - chrome - (count - 1) * TileGap);
            var tileSize = Math.Clamp(Math.Floor(available / count), MinimumTileSize, DefaultTileSize);
            for (var index = 0; index < count; index++)
            {
                var tile = BuildTile(_shownTiles[index], tileSize);
                tile.Margin = new Thickness(index == 0 ? 0 : TileGap, 0, 0, 0);
                _strip.Children.Add(tile);
            }

            contentWidth = count * tileSize + (count - 1) * TileGap + chrome;
            contentHeight = tileSize + chrome;
        }

        Width = contentWidth;
        Height = contentHeight;
        Left = TrayIsOnLeftHalf()
            ? workArea.Left + ScreenEdgeMargin
            : workArea.Right - contentWidth - ScreenEdgeMargin;
        Top = workArea.Top > 0
            ? workArea.Top + ScreenEdgeMargin
            : workArea.Bottom - contentHeight - ScreenEdgeMargin;
    }

    // Clicking the square opens (or closes) the plugin editor; the small
    // button toggles Bypass, and right-click offers every action including Deactivate.
    private FrameworkElement BuildTile(ToggleViewTileState state, double size)
    {
        var (background, border, foreground, statusText, statusBrush) = TileColors(state.Status);
        var tile = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(6),
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(state.Status is ToggleViewTileStatus.Active or ToggleViewTileStatus.Failed ? 2 : 1),
            Cursor = state.CanOpenEditor ? Cursors.Hand : Cursors.Arrow
        };

        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = state.Label,
            Foreground = foreground,
            FontSize = size >= 60 ? 12 : 10,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 6, 4, 20),
            MaxHeight = size - 28
        });

        if (state.CanToggle)
        {
            grid.Children.Add(BuildBypassButton(state));
        }
        else if (!string.IsNullOrEmpty(statusText))
        {
            grid.Children.Add(new Border
            {
                Background = state.Status == ToggleViewTileStatus.Deactivated ? (Brush)FindResource("FieldBrush") : Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 5),
                Child = new TextBlock
                {
                    Text = statusText,
                    Foreground = statusBrush,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold
                }
            });
        }

        tile.Child = grid;
        var slotIndex = state.SlotIndex;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            _pressedSlot = slotIndex;
            e.Handled = true;
        };
        tile.MouseLeftButtonUp += (_, e) => Guard(() =>
        {
            e.Handled = true;
            var pressed = _pressedSlot == slotIndex;
            _pressedSlot = null;
            if (pressed && CurrentTile(slotIndex)?.CanOpenEditor == true)
            {
                RunCommand(slotIndex, ToggleViewCommand.ToggleEditor);
            }
        });

        tile.ContextMenu = new ContextMenu();
        tile.ContextMenuOpening += (_, _) => Guard(() => FillTileMenu(tile.ContextMenu, slotIndex));
        return tile;
    }

    private void FillTileMenu(ContextMenu menu, int slotIndex)
    {
        menu.Items.Clear();
        if (CurrentTile(slotIndex) is { Status: ToggleViewTileStatus.Failed })
        {
            menu.Items.Add(new MenuItem
            {
                Header = "FAILED: audio passes unprocessed",
                IsEnabled = false,
                Foreground = (Brush)FindResource("DangerBrush")
            });
            menu.Items.Add(CreateMenuItem("Reload", () => RunCommand(slotIndex, ToggleViewCommand.Reload)));
            menu.Items.Add(new Separator());
        }

        if (CurrentTile(slotIndex) is { IsLive: true } tile)
        {
            var editorOpen = _isEditorOpen(slotIndex);
            var on = tile.Status is ToggleViewTileStatus.Active or ToggleViewTileStatus.Bypassed;
            var deactivated = tile.Status == ToggleViewTileStatus.Deactivated;
            menu.Items.Add(CreateMenuItem(editorOpen ? "Close Editor" : "Open Editor", () => RunCommand(slotIndex, ToggleViewCommand.ToggleEditor)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(on ? "Turn Off" : "Turn On", () => RunCommand(slotIndex, ToggleViewCommand.TogglePower)));
            var bypass = CreateMenuItem(tile.Status == ToggleViewTileStatus.Bypassed ? "Disable Bypass" : "Bypass", () => RunCommand(slotIndex, ToggleViewCommand.ToggleBypass));
            bypass.IsEnabled = tile.CanToggle;
            menu.Items.Add(bypass);
            menu.Items.Add(CreateMenuItem(deactivated ? "Activate" : "Deactivate", () => RunCommand(slotIndex, ToggleViewCommand.ToggleDeactivate)));
            var loadOnce = CreateMenuItem("Load Settings Once", () => RunCommand(slotIndex, ToggleViewCommand.ToggleLoadSettingsOnce));
            loadOnce.IsChecked = tile.LoadSettingsOnce;
            menu.Items.Add(loadOnce);
            menu.Items.Add(new Separator());
        }

        AddWindowMenuItems(menu);
    }

    private void AddWindowMenuItems(ContextMenu menu)
    {
        menu.Items.Add(CreateMenuItem("Open Main Window", _openMainWindow));
        menu.Items.Add(CreateMenuItem("Toggle View Settings", _openSettings));
    }

    private void RunCommand(int slotIndex, ToggleViewCommand command)
    {
        _runCommand(slotIndex, command);
        RefreshTiles(force: false);
    }

    private Button BuildBypassButton(ToggleViewTileState state)
    {
        var active = state.Status == ToggleViewTileStatus.Active;
        var accent = (Brush)FindResource(active ? "RouteAccentBrush" : "VolumeAccentBrush");
        var button = new Button
        {
            Content = active ? "ON" : "BYPASS",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            BorderBrush = accent,
            Background = (Brush)FindResource("FieldBrush"),
            Height = 16,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 4),
            Focusable = false
        };
        var slotIndex = state.SlotIndex;
        button.Click += (_, e) => Guard(() =>
        {
            e.Handled = true;
            if (CurrentTile(slotIndex)?.CanToggle == true)
            {
                RunCommand(slotIndex, ToggleViewCommand.ToggleBypass);
            }
        });
        return button;
    }

    private ToggleViewTileState? CurrentTile(int slotIndex)
    {
        return _shownTiles.FirstOrDefault(tile => tile.SlotIndex == slotIndex);
    }

    private (Brush Background, Brush Border, Brush Foreground, string StatusText, Brush StatusBrush) TileColors(ToggleViewTileStatus status)
    {
        var text = (Brush)FindResource("TextBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var stroke = (Brush)FindResource("StrokeBrush");
        var field = (Brush)FindResource("FieldBrush");
        return status switch
        {
            ToggleViewTileStatus.Active => ((Brush)FindResource("RouteActiveBrush"), (Brush)FindResource("RouteAccentBrush"), text, string.Empty, text),
            ToggleViewTileStatus.Bypassed => ((Brush)FindResource("PanelAltBrush"), stroke, muted, string.Empty, muted),
            ToggleViewTileStatus.TurnedOff => (field, stroke, muted, "OFF", muted),
            ToggleViewTileStatus.Deactivated => (PluginNodeStates.DeactivatedBrush(), stroke, text, "DEACTIVATED", text),
            ToggleViewTileStatus.Failed => (field, (Brush)FindResource("DangerBrush"), text, "FAILED", (Brush)FindResource("DangerBrush")),
            ToggleViewTileStatus.Missing => (field, (Brush)FindResource("DangerBrush"), muted, "MISSING", (Brush)FindResource("DangerBrush")),
            ToggleViewTileStatus.Loading => (field, stroke, muted, "LOADING", muted),
            _ => (field, stroke, muted, "NOT LOADED", muted)
        };
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Guard(action);
        return item;
    }

    // An exception escaping to the dispatcher shuts the whole host (and its audio) down, so the
    // strip logs failures instead of letting them propagate.
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Toggle View: {ex}");
        }
    }

    // The notification area sits on the left with right-to-left taskbar layouts; follow it.
    private static bool TrayIsOnLeftHalf()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var notifyArea = taskbar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notifyArea == IntPtr.Zero || !GetWindowRect(notifyArea, out var rect) || rect.Right <= rect.Left)
        {
            return false;
        }

        return (rect.Left + rect.Right) / 2 < GetSystemMetrics(SystemMetricScreenWidth) / 2;
    }

    private const int SystemMetricScreenWidth = 0;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
