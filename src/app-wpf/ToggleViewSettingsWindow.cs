using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed record ToggleViewPluginOption(int InstanceId, string PluginIdentifier, string Name, string Detail);

internal sealed class ToggleViewSettingsWindow : Window
{
    private readonly ToggleViewSettings _settings;
    private readonly IReadOnlyList<ToggleViewPluginOption> _options;
    private readonly List<SlotRow> _rows = [];
    private readonly TextBox _hotkeyTextBox = new();
    private readonly TextBlock _hotkeyHintTextBlock = new();
    private readonly ComboBox _trayPluginCombo = new();
    private string _hotkey;

    private sealed class SlotRow
    {
        public required CheckBox Visible { get; init; }
        public required ComboBox Plugin { get; init; }
        public required TextBox CustomName { get; init; }
    }

    private sealed record PluginComboItem(string Display, int InstanceId, string PluginIdentifier, string PluginName)
    {
        public override string ToString()
        {
            return Display;
        }
    }

    public ToggleViewSettingsWindow(ToggleViewSettings settings, IReadOnlyList<ToggleViewPluginOption> options)
    {
        _settings = settings.Clone();
        _settings.Normalize();
        _options = options;
        _hotkey = _settings.Hotkey;

        Title = "Toggle View Settings";
        Width = 700;
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 40);
        MinWidth = 600;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildLayout();
    }

    public ToggleViewSettings? Result { get; private set; }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = "Choose which loaded VSTs appear as squares in Toggle View. Clicking a square opens the plugin editor; " +
                "the small ON / BYPASS button toggles Bypass. Unchecked squares are hidden.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Style = (Style)FindResource("MutedText")
        };
        Grid.SetRow(intro, 0);
        root.Children.Add(intro);

        var optionsPanel = new StackPanel();
        optionsPanel.Children.Add(BuildHotkeyPanel());
        optionsPanel.Children.Add(BuildTrayDoubleClickPanel());
        Grid.SetRow(optionsPanel, 1);
        root.Children.Add(optionsPanel);

        var header = BuildSlotGrid();
        AddHeaderCell(header, "Show", 0);
        AddHeaderCell(header, "#", 1);
        AddHeaderCell(header, "VST", 2);
        AddHeaderCell(header, "Square name (empty = VST name)", 3);
        header.Margin = new Thickness(0, 4, 18, 6);
        Grid.SetRow(header, 2);
        root.Children.Add(header);

        var slotsPanel = new StackPanel();
        for (var index = 0; index < _settings.Slots.Count; index++)
        {
            slotsPanel.Children.Add(BuildSlotRow(index, _settings.Slots[index]));
        }

        var scroll = new ScrollViewer
        {
            Content = slotsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 3);
        root.Children.Add(scroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var save = new Button { Content = "Save", Width = 96, IsDefault = true, Style = (Style)FindResource("RouteButton") };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Content = "Cancel", Width = 96, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        return root;
    }

    // Lists only the VSTs loaded right now; a saved choice whose VST is gone falls back to "(none)".
    private FrameworkElement BuildTrayDoubleClickPanel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        row.Children.Add(new TextBlock
        {
            Text = "Tray double-click opens",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });

        var none = new PluginComboItem("(none)", -1, string.Empty, string.Empty);
        _trayPluginCombo.Items.Add(none);
        PluginComboItem selected = none;
        var saved = _settings.TrayDoubleClickPlugin;
        foreach (var option in _options)
        {
            var item = new PluginComboItem($"{option.Name}  -  {option.Detail}", option.InstanceId, option.PluginIdentifier, option.Name);
            _trayPluginCombo.Items.Add(item);
            if (option.InstanceId == saved.InstanceId &&
                (string.IsNullOrEmpty(saved.PluginIdentifier) ||
                 string.Equals(option.PluginIdentifier, saved.PluginIdentifier, StringComparison.OrdinalIgnoreCase)))
            {
                selected = item;
            }
        }

        _trayPluginCombo.SelectedItem = selected;
        _trayPluginCombo.Width = 330;
        row.Children.Add(_trayPluginCombo);
        return row;
    }

    private FrameworkElement BuildHotkeyPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "Keyboard shortcut",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });

        _hotkeyTextBox.Width = 220;
        _hotkeyTextBox.IsReadOnly = true;
        _hotkeyTextBox.IsReadOnlyCaretVisible = false;
        _hotkeyTextBox.Text = _hotkey;
        _hotkeyTextBox.PreviewKeyDown += HotkeyTextBox_PreviewKeyDown;
        row.Children.Add(_hotkeyTextBox);

        var reset = new Button { Content = "Default", Width = 90, Margin = new Thickness(8, 0, 0, 0) };
        reset.Click += (_, _) =>
        {
            _hotkey = ToggleViewSettings.DefaultHotkey;
            _hotkeyTextBox.Text = _hotkey;
            _hotkeyHintTextBlock.Text = DefaultHotkeyHint;
        };
        row.Children.Add(reset);
        panel.Children.Add(row);

        _hotkeyHintTextBlock.Text = DefaultHotkeyHint;
        _hotkeyHintTextBlock.Margin = new Thickness(0, 6, 0, 0);
        _hotkeyHintTextBlock.Style = (Style)FindResource("MutedText");
        panel.Children.Add(_hotkeyHintTextBlock);
        return panel;
    }

    private const string DefaultHotkeyHint = "Works everywhere, even inside your DAW. Click the box and press a new combination (Ctrl, Alt or Win + key).";

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key is Key.Tab or Key.Escape or Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (ToggleViewHotkeyGesture.IsModifierKey(key))
        {
            return;
        }

        if (!ToggleViewHotkeyGesture.IsUsable(modifiers, key))
        {
            _hotkeyHintTextBlock.Text = "Use Ctrl, Alt or Win together with a key.";
            return;
        }

        _hotkey = new ToggleViewHotkeyGesture(modifiers, key).ToString();
        _hotkeyTextBox.Text = _hotkey;
        _hotkeyHintTextBlock.Text = DefaultHotkeyHint;
    }

    private static Grid BuildSlotGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private void AddHeaderCell(Grid grid, string text, int column)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(column >= 2 ? 6 : 0, 0, 0, 0),
            Style = (Style)FindResource("MutedText")
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private FrameworkElement BuildSlotRow(int index, ToggleViewSlotSettings slot)
    {
        var grid = BuildSlotGrid();
        grid.Margin = new Thickness(0, 0, 0, 6);

        var visible = new CheckBox
        {
            IsChecked = slot.Visible,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(visible, 0);
        grid.Children.Add(visible);

        var number = new TextBlock
        {
            Text = (index + 1).ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("MutedText")
        };
        Grid.SetColumn(number, 1);
        grid.Children.Add(number);

        var plugin = new ComboBox { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var none = new PluginComboItem("(none)", -1, string.Empty, string.Empty);
        plugin.Items.Add(none);
        PluginComboItem selected = none;
        foreach (var option in _options)
        {
            var item = new PluginComboItem($"{option.Name}  -  {option.Detail}", option.InstanceId, option.PluginIdentifier, option.Name);
            plugin.Items.Add(item);
            if (option.InstanceId == slot.InstanceId &&
                (string.IsNullOrEmpty(slot.PluginIdentifier) ||
                 string.Equals(option.PluginIdentifier, slot.PluginIdentifier, StringComparison.OrdinalIgnoreCase)))
            {
                selected = item;
            }
        }

        // Keep an assignment whose VST is not loaded right now instead of silently dropping it.
        if (ReferenceEquals(selected, none) && slot.InstanceId >= 0)
        {
            var name = string.IsNullOrWhiteSpace(slot.PluginName) ? "VST" : slot.PluginName;
            selected = new PluginComboItem($"{name}  -  ID {slot.InstanceId} (not loaded)", slot.InstanceId, slot.PluginIdentifier, slot.PluginName);
            plugin.Items.Add(selected);
        }

        plugin.SelectedItem = selected;
        plugin.SelectionChanged += (_, _) =>
        {
            if (plugin.SelectedItem is PluginComboItem { InstanceId: >= 0 } && plugin.IsKeyboardFocusWithin)
            {
                visible.IsChecked = true;
            }
        };
        Grid.SetColumn(plugin, 2);
        grid.Children.Add(plugin);

        var customName = new TextBox
        {
            Text = slot.CustomName,
            MaxLength = 40,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(customName, 3);
        grid.Children.Add(customName);

        _rows.Add(new SlotRow { Visible = visible, Plugin = plugin, CustomName = customName });
        return grid;
    }

    private void SaveAndClose()
    {
        var result = _settings.Clone();
        result.Hotkey = _hotkey;
        var trayItem = _trayPluginCombo.SelectedItem as PluginComboItem;
        result.TrayDoubleClickPlugin = new ToggleViewSlotSettings
        {
            Visible = trayItem is { InstanceId: >= 0 },
            InstanceId = trayItem?.InstanceId ?? -1,
            PluginIdentifier = trayItem?.PluginIdentifier ?? string.Empty,
            PluginName = trayItem?.PluginName ?? string.Empty
        };
        for (var index = 0; index < _rows.Count && index < result.Slots.Count; index++)
        {
            var row = _rows[index];
            var slot = result.Slots[index];
            var item = row.Plugin.SelectedItem as PluginComboItem;
            slot.Visible = row.Visible.IsChecked == true;
            slot.InstanceId = item?.InstanceId ?? -1;
            slot.PluginIdentifier = item?.PluginIdentifier ?? string.Empty;
            slot.PluginName = item?.PluginName ?? string.Empty;
            slot.CustomName = row.CustomName.Text.Trim();
        }

        Result = result;
        DialogResult = true;
    }
}
