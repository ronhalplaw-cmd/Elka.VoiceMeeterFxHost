namespace Elka.VoiceMeeterFxHost.App;

// Persisted inside wpf-main-window.json as "ToggleView". Builds without Toggle View ignore the
// property when loading, so the settings file stays compatible in both directions.
internal sealed class ToggleViewSettings
{
    public const int MaxSlots = 20;
    public const string DefaultHotkey = "Ctrl+Alt+Shift+F";

    public bool Enabled { get; set; }
    public string Hotkey { get; set; } = DefaultHotkey;
    public List<ToggleViewSlotSettings> Slots { get; set; } = [];
    // VST whose editor a double-click on the tray icon opens; InstanceId -1 = none.
    public ToggleViewSlotSettings TrayDoubleClickPlugin { get; set; } = new();

    public void Normalize()
    {
        Slots ??= [];
        if (Slots.Count > MaxSlots)
        {
            Slots.RemoveRange(MaxSlots, Slots.Count - MaxSlots);
        }

        while (Slots.Count < MaxSlots)
        {
            Slots.Add(new ToggleViewSlotSettings());
        }

        for (var index = 0; index < Slots.Count; index++)
        {
            var slot = Slots[index] ?? new ToggleViewSlotSettings();
            slot.PluginIdentifier ??= string.Empty;
            slot.PluginName ??= string.Empty;
            slot.CustomName ??= string.Empty;
            Slots[index] = slot;
        }

        if (string.IsNullOrWhiteSpace(Hotkey))
        {
            Hotkey = DefaultHotkey;
        }

        TrayDoubleClickPlugin ??= new ToggleViewSlotSettings();
        TrayDoubleClickPlugin.PluginIdentifier ??= string.Empty;
        TrayDoubleClickPlugin.PluginName ??= string.Empty;
        TrayDoubleClickPlugin.CustomName ??= string.Empty;
    }

    public ToggleViewSettings Clone()
    {
        return new ToggleViewSettings
        {
            Enabled = Enabled,
            Hotkey = Hotkey,
            Slots = Slots.Select(static slot => slot.Clone()).ToList(),
            TrayDoubleClickPlugin = (TrayDoubleClickPlugin ?? new ToggleViewSlotSettings()).Clone()
        };
    }
}

internal sealed class ToggleViewSlotSettings
{
    public bool Visible { get; set; }
    // A slot is bound to a VST node by its stable VST ID. The identifier and name are kept so a
    // reused ID never silently points the square at a different plugin.
    public int InstanceId { get; set; } = -1;
    public string PluginIdentifier { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public string CustomName { get; set; } = string.Empty;

    public ToggleViewSlotSettings Clone()
    {
        return new ToggleViewSlotSettings
        {
            Visible = Visible,
            InstanceId = InstanceId,
            PluginIdentifier = PluginIdentifier,
            PluginName = PluginName,
            CustomName = CustomName
        };
    }
}

internal enum ToggleViewTileStatus
{
    Active,
    Bypassed,
    TurnedOff,
    Deactivated,
    Failed,
    Missing,
    NotLoaded,
    Loading
}

internal enum ToggleViewCommand
{
    ToggleEditor,
    TogglePower,
    ToggleBypass,
    ToggleDeactivate,
    ToggleLoadSettingsOnce,
    Reload
}

internal sealed record ToggleViewTileState(int SlotIndex, string Label, ToggleViewTileStatus Status, bool LoadSettingsOnce = false)
{
    public bool CanToggle => Status is ToggleViewTileStatus.Active or ToggleViewTileStatus.Bypassed;

    public bool IsLive => Status is ToggleViewTileStatus.Active or ToggleViewTileStatus.Bypassed or
        ToggleViewTileStatus.TurnedOff or ToggleViewTileStatus.Deactivated;

    public bool CanOpenEditor => IsLive;
}
