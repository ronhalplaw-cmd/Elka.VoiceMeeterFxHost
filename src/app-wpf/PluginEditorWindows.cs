using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Elka.VoiceMeeterFxHost.App;

// Plugin editor windows hosted in this process: adds "Always on Top" to their title-bar (system) menu
// and remembers their position and size. Editors are native JUCE windows on the JUCE message thread, so
// nothing here replaces their window procedure: thread message hooks only observe the menu command, and
// the unknown command still falls through to DefWindowProc, which ignores it. Moves are observed through
// accessibility events. Sandboxed editors live in the worker process and are skipped.
internal sealed partial class PluginEditorWindows : IDisposable
{
    internal const int MenuCommandId = 0x0A70;
    private const string MenuText = "Always on Top";
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint SwpNoZOrder = 0x0004;
    private const uint MonitorDefaultToNull = 0x0000;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjectIdWindow = 0;
    private const int HookGetMessage = 3;
    private const int HookCallWndProc = 4;
    private const int WindowsMessageSysCommand = 0x0112;
    private const int PeekRemove = 0x0001;
    private const int ScClose = 0xF060;
    private const uint MenuByCommand = 0x0000;
    private const uint MenuByPosition = 0x0400;
    private const uint MenuString = 0x0000;
    private const uint MenuSeparator = 0x0800;
    private const uint MenuChecked = 0x0008;
    private const uint MenuUnchecked = 0x0000;
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleTopmost = 0x0008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpAsyncWindowPos = 0x4000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private readonly Func<int, bool> _isPinned;
    private readonly Action<int, bool> _pinnedChanged;
    private readonly Func<int, int[]?> _savedBounds;
    private readonly Action<int, int[]> _boundsChanged;
    private readonly WinEventDelegate _windowShown;
    private readonly WinEventDelegate _windowMoved;
    private readonly HookProc _getMessageHook;
    private readonly HookProc _callWndProcHook;
    private readonly ConcurrentDictionary<IntPtr, int> _editorWindows = new();
    private readonly ConcurrentDictionary<uint, (IntPtr GetMessage, IntPtr CallWndProc)> _threadHooks = new();
    private readonly ConcurrentDictionary<IntPtr, long> _lastToggleTicks = new();
    private IntPtr _showHook;
    private IntPtr _moveHook;

    // Callbacks receive the editor's VST ID. pinnedChanged runs on the editor's thread; the others run
    // on the thread that called Start. Bounds are screen pixels: [left, top, width, height].
    public PluginEditorWindows(Func<int, bool> isPinned, Action<int, bool> pinnedChanged,
        Func<int, int[]?> savedBounds, Action<int, int[]> boundsChanged)
    {
        _isPinned = isPinned;
        _pinnedChanged = pinnedChanged;
        _savedBounds = savedBounds;
        _boundsChanged = boundsChanged;
        _windowShown = OnWindowShown;
        _windowMoved = OnWindowMoved;
        _getMessageHook = OnGetMessage;
        _callWndProcHook = OnCallWndProc;
    }

    // Must run on a thread with a message loop (the WPF UI thread): out-of-context events are delivered there.
    public void Start()
    {
        if (_showHook == IntPtr.Zero)
        {
            _showHook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _windowShown,
                (uint)Environment.ProcessId, 0, WinEventOutOfContext);
        }

        if (_moveHook == IntPtr.Zero)
        {
            _moveHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _windowMoved,
                (uint)Environment.ProcessId, 0, WinEventOutOfContext);
        }
    }

    public void Dispose()
    {
        if (_showHook != IntPtr.Zero)
        {
            UnhookWinEvent(_showHook);
            _showHook = IntPtr.Zero;
        }

        if (_moveHook != IntPtr.Zero)
        {
            UnhookWinEvent(_moveHook);
            _moveHook = IntPtr.Zero;
        }

        foreach (var hooks in _threadHooks.Values)
        {
            UnhookWindowsHookEx(hooks.GetMessage);
            UnhookWindowsHookEx(hooks.CallWndProc);
        }

        _threadHooks.Clear();
        _editorWindows.Clear();
    }

    // Editor windows are titled "<name> - ID <n>" by MainWindow.PluginEditorWindowTitle.
    internal static bool TryParseEditorInstanceId(string title, out int instanceId)
    {
        instanceId = -1;
        var match = EditorTitlePattern().Match(title);
        return match.Success && int.TryParse(match.Groups[1].Value, out instanceId);
    }

    [GeneratedRegex(@" - ID (\d{1,3})$")]
    private static partial Regex EditorTitlePattern();

    private void OnWindowShown(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (objectId != ObjectIdWindow || window == IntPtr.Zero || GetAncestor(window, 2) != window)
            {
                return;
            }

            if (!TryParseEditorInstanceId(WindowTitle(window), out var instanceId))
            {
                return;
            }

            Attach(window, instanceId);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Always on Top: {ex.Message}");
        }
    }

    internal void Attach(IntPtr window, int instanceId)
    {
        var editorThread = GetWindowThreadProcessId(window, out var processId);
        if (processId != (uint)Environment.ProcessId || editorThread == 0)
        {
            return;
        }

        RestoreBounds(window, _savedBounds(instanceId));
        _editorWindows[window] = instanceId;
        EnsureMenuItem(window);
        EnsureThreadHooks(editorThread);
        Apply(window, _isPinned(instanceId), synchronous: false);
    }

    // Only restores a placement that is still at least partly on a connected monitor.
    private static void RestoreBounds(IntPtr window, int[]? bounds)
    {
        if (bounds is not { Length: 4 } || bounds[2] < 50 || bounds[3] < 50)
        {
            return;
        }

        var rect = new NativeRect { Left = bounds[0], Top = bounds[1], Right = bounds[0] + bounds[2], Bottom = bounds[1] + bounds[3] };
        if (MonitorFromRect(ref rect, MonitorDefaultToNull) == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(window, IntPtr.Zero, bounds[0], bounds[1], bounds[2], bounds[3],
            SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
    }

    private void OnWindowMoved(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (objectId != ObjectIdWindow || !_editorWindows.TryGetValue(window, out var instanceId))
            {
                return;
            }

            // Minimized/maximized placements are not what the user wants back next time.
            if (IsIconic(window) || IsZoomed(window) || !IsWindowVisible(window) || !GetWindowRect(window, out var rect))
            {
                return;
            }

            _boundsChanged(instanceId, [rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top]);
        }
        catch (Exception ex)
        {
            RuntimeLog.Write($"Plugin editor placement: {ex.Message}");
        }
    }

    private static void EnsureMenuItem(IntPtr window)
    {
        var menu = GetSystemMenu(window, false);
        if (menu == IntPtr.Zero || GetMenuState(menu, MenuCommandId, MenuByCommand) != uint.MaxValue)
        {
            return;
        }

        // Place it above Close, like other apps that offer the option.
        var count = GetMenuItemCount(menu);
        var closeIndex = -1;
        for (var index = 0; index < count; index++)
        {
            if (GetMenuItemID(menu, index) == ScClose)
            {
                closeIndex = index;
                break;
            }
        }

        if (closeIndex < 0)
        {
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, MenuString, MenuCommandId, MenuText);
            return;
        }

        InsertMenu(menu, (uint)closeIndex, MenuByPosition | MenuString, MenuCommandId, MenuText);
        InsertMenu(menu, (uint)closeIndex + 1, MenuByPosition | MenuSeparator, 0, null);
    }

    private void EnsureThreadHooks(uint editorThread)
    {
        if (_threadHooks.ContainsKey(editorThread))
        {
            return;
        }

        // Thread hooks inside our own process need no module handle and no DLL injection.
        var getMessage = SetWindowsHookEx(HookGetMessage, _getMessageHook, IntPtr.Zero, editorThread);
        var callWndProc = SetWindowsHookEx(HookCallWndProc, _callWndProcHook, IntPtr.Zero, editorThread);
        _threadHooks[editorThread] = (getMessage, callWndProc);
    }

    // Runs on the JUCE message thread for every message there: keep it tiny and never throw.
    private IntPtr OnGetMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && wParam.ToInt64() == PeekRemove)
            {
                var message = Marshal.PtrToStructure<NativeMessage>(lParam);
                HandleMessage(message.Window, message.Message, message.WParam);
            }
        }
        catch
        {
            // Observation only; the message continues unchanged.
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnCallWndProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var message = Marshal.PtrToStructure<CallWndProcStruct>(lParam);
                HandleMessage(message.Window, message.Message, message.WParam);
            }
        }
        catch
        {
            // Observation only; the message continues unchanged.
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void HandleMessage(IntPtr window, uint message, IntPtr wParam)
    {
        if (message != WindowsMessageSysCommand || (wParam.ToInt64() & 0xFFF0) != MenuCommandId)
        {
            return;
        }

        if (!_editorWindows.TryGetValue(window, out var instanceId))
        {
            return;
        }

        // A command can be seen by both hooks; act on it once.
        var now = Environment.TickCount64;
        if (_lastToggleTicks.TryGetValue(window, out var last) && now - last < 250)
        {
            return;
        }

        _lastToggleTicks[window] = now;
        var pinned = !IsTopmost(window);
        Apply(window, pinned, synchronous: true);
        _pinnedChanged(instanceId, pinned);
    }

    internal static bool IsTopmost(IntPtr window)
    {
        return (GetWindowLongPtr(window, ExtendedStyleIndex).ToInt64() & ExtendedStyleTopmost) != 0;
    }

    // From another thread the move is posted (SWP_ASYNCWINDOWPOS) so the UI never waits on the JUCE thread.
    private static void Apply(IntPtr window, bool pinned, bool synchronous)
    {
        var menu = GetSystemMenu(window, false);
        if (menu != IntPtr.Zero)
        {
            CheckMenuItem(menu, MenuCommandId, MenuByCommand | (pinned ? MenuChecked : MenuUnchecked));
        }

        if (IsTopmost(window) == pinned)
        {
            return;
        }

        var flags = SwpNoMove | SwpNoSize | SwpNoActivate | (synchronous ? 0 : SwpAsyncWindowPos);
        SetWindowPos(window, pinned ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, flags);
    }

    private static string WindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0 || length > 512)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallWndProcStruct
    {
        public IntPtr LParam;
        public IntPtr WParam;
        public uint Message;
        public IntPtr Window;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint id, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InsertMenu(IntPtr menu, uint position, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr menu, uint id, uint check);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
