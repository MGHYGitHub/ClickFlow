using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ClickFlow.Core;

namespace ClickFlow.Desktop.Services;

/// <summary>Records physical input on the owning UI thread; notifications are delivered outside hook callbacks.</summary>
public sealed class RecorderService : IDisposable
{
    private const int MaxEvents = PlanValidator.MaxSteps;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<bool>? _shouldIgnore;
    private readonly uint _processId = (uint)Environment.ProcessId;
    private readonly HookProc _mouseProc, _keyboardProc;
    private readonly HashSet<int> _keys = [];
    private readonly HashSet<int> _physicalKeys = [];
    private readonly HashSet<int> _suppressedKeys = [];
    private readonly Dictionary<int, long> _pendingAlt = [];
    private readonly HashSet<PointerButton> _buttons = [];
    private readonly Queue<ActionStep> _pending = [];
    private nint _mouseHook, _keyboardHook;
    private long _lastEvent, _lastMove;
    private ScreenPoint? _lastPosition;
    private int _eventCount;
    private bool _dispatchPending, _disposed;
    public event Action<ActionStep>? Captured;
    public event Action<string>? Error;
    public bool IsRecording { get; private set; }

    public RecorderService(Func<bool>? shouldIgnore = null)
    { _shouldIgnore = shouldIgnore; _mouseProc = MouseCallback; _keyboardProc = KeyboardCallback; }

    public void Start()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording) return;
        DrainPending();
        _eventCount = 0; _keys.Clear(); _buttons.Clear(); _physicalKeys.Clear(); _suppressedKeys.Clear(); _pendingAlt.Clear(); _lastPosition = null;
        _lastEvent = _lastMove = Stopwatch.GetTimestamp();
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(14, _mouseProc, module, 0);
        if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装鼠标录制钩子。");
        _keyboardHook = SetWindowsHookEx(13, _keyboardProc, module, 0);
        if (_keyboardHook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            UnhookWindowsHookEx(_mouseHook); _mouseHook = 0;
            throw new Win32Exception(error, "无法安装键盘录制钩子。");
        }
        IsRecording = true;
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        StopHooks();
        BalanceHeld();
        DrainPending();
    }

    private void BalanceHeld()
    {
        // A recording ending during a drag or key hold must still produce a balanced replay.
        var first = true;
        foreach (var key in _keys.ToArray())
        { Enqueue(new() { Kind = ActionKind.KeyUp, Name = "释放按键", VirtualKey = key, UseCurrentPosition = true }, first, checkLimit: false); first = false; }
        foreach (var button in _buttons.ToArray())
        { Enqueue(new() { Kind = ActionKind.MouseUp, Name = "释放鼠标", Button = button, UseCurrentPosition = true }, first, checkLimit: false); first = false; }
        _keys.Clear(); _buttons.Clear();
        _physicalKeys.Clear(); _suppressedKeys.Clear(); _pendingAlt.Clear();
    }

    private nint MouseCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && IsRecording)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((data.Flags & 0x03) == 0 && !IgnoreMouse(data.Point) && !_physicalKeys.Any(IsWindowsKey))
                {
                    var message = (int)wParam;
                    var point = new ScreenPoint(data.Point.X, data.Point.Y);
                    var step = new ActionStep { X = point.X, Y = point.Y, UseCurrentPosition = false };
                    var capture = true;
                    switch (message)
                    {
                        case 0x0200:
                            var now = Stopwatch.GetTimestamp();
                            if (_lastPosition == point || Stopwatch.GetElapsedTime(_lastMove, now).TotalMilliseconds < 16) { capture = false; break; }
                            _lastMove = now; step.Kind = ActionKind.Move; step.Name = "移动鼠标"; break;
                        case 0x0201: capture = MouseButton(step, PointerButton.Left, true); break;
                        case 0x0202: capture = MouseButton(step, PointerButton.Left, false); break;
                        case 0x0204: capture = MouseButton(step, PointerButton.Right, true); break;
                        case 0x0205: capture = MouseButton(step, PointerButton.Right, false); break;
                        case 0x0207: capture = MouseButton(step, PointerButton.Middle, true); break;
                        case 0x0208: capture = MouseButton(step, PointerButton.Middle, false); break;
                        case 0x020A:
                            step.Kind = ActionKind.Scroll; step.Name = "滚动鼠标";
                            step.WheelDelta = (short)(data.MouseData >> 16); capture = step.WheelDelta != 0; break;
                        default: capture = false; break;
                    }
                    if (capture)
                    {
                        FlushPendingAlt();
                        if (IsRecording) { _lastPosition = point; Enqueue(step); }
                    }
                }
                else if ((data.Flags & 0x03) == 0)
                {
                    // A release over this app still closes a drag that began outside it.
                    var released = (int)wParam switch { 0x0202 => PointerButton.Left, 0x0205 => PointerButton.Right, 0x0208 => PointerButton.Middle, _ => (PointerButton?)null };
                    if (released is { } button && _buttons.Remove(button))
                        Enqueue(new() { Kind = ActionKind.MouseUp, Name = "释放鼠标", Button = button, UseCurrentPosition = true });
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private bool MouseButton(ActionStep step, PointerButton button, bool down)
    {
        var changed = down ? _buttons.Add(button) : _buttons.Remove(button);
        step.Kind = down ? ActionKind.MouseDown : ActionKind.MouseUp;
        step.Name = down ? "按下鼠标" : "释放鼠标"; step.Button = button;
        return changed;
    }

    private nint KeyboardCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && IsRecording)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = (int)data.VirtualKey;
                if ((data.Flags & 0x12) == 0 && key is >= 1 and <= 254)
                {
                    var down = (int)wParam is 0x0100 or 0x0104;
                    var up = (int)wParam is 0x0101 or 0x0105;
                    if (down || up) HandleKey(key, down);
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static bool IsWindowsKey(int key) => key is 0x5B or 0x5C;
    private static bool IsAltKey(int key) => key is 0x12 or 0xA4 or 0xA5;
    private static ActionStep KeyStep(int key, bool down) => new()
    { Kind = down ? ActionKind.KeyDown : ActionKind.KeyUp, Name = down ? "按下键盘" : "释放键盘", VirtualKey = key, UseCurrentPosition = true };

    private void HandleKey(int key, bool down)
    {
        if (down) _physicalKeys.Add(key); else _physicalKeys.Remove(key);
        if (IsWindowsKey(key) || _physicalKeys.Any(IsWindowsKey))
        {
            if (down) _suppressedKeys.Add(key); else _suppressedKeys.Remove(key);
            SuppressAlt();
            if (!down && _keys.Remove(key)) Enqueue(KeyStep(key, false));
            return;
        }
        if (down && _physicalKeys.Any(IsAltKey) && key is 0x09 or 0x1B or 0x73)
        { SuppressAlt(); _suppressedKeys.Add(key); return; }
        if (_suppressedKeys.Contains(key))
        {
            if (!down) { _suppressedKeys.Remove(key); if (_keys.Remove(key)) Enqueue(KeyStep(key, false)); }
            return;
        }
        if (key is 0x71 or 0x72 or 0x73 or 0x75 or 0x76) return;
        if (IgnoreKeyboard())
        {
            if (!down)
            {
                _pendingAlt.Remove(key);
                // Balance an external hold even if focus changed to this app before its release.
                if (_keys.Remove(key)) Enqueue(KeyStep(key, false));
            }
            return;
        }
        if (down && IsAltKey(key))
        {
            if (!_keys.Contains(key)) _pendingAlt.TryAdd(key, Stopwatch.GetTimestamp());
            return;
        }
        FlushPendingAlt();
        if (IsRecording && (down && _keys.Add(key) || !down && _keys.Remove(key))) Enqueue(KeyStep(key, down));
    }

    private void FlushPendingAlt()
    {
        var pending = _pendingAlt.OrderBy(entry => entry.Value).ToArray();
        _pendingAlt.Clear();
        foreach (var (key, at) in pending)
            if (IsRecording && _keys.Add(key)) Enqueue(KeyStep(key, true), timestamp: at);
    }

    private void SuppressAlt()
    {
        foreach (var key in _physicalKeys.Where(IsAltKey).ToArray())
        {
            _pendingAlt.Remove(key); _suppressedKeys.Add(key);
            if (_keys.Remove(key)) Enqueue(KeyStep(key, false));
        }
    }

    private bool IgnoreMouse(POINT point) => _shouldIgnore?.Invoke() == true || BelongsToThisProcess(WindowFromPoint(point));
    private bool IgnoreKeyboard() => _shouldIgnore?.Invoke() == true || BelongsToThisProcess(GetForegroundWindow());
    private bool BelongsToThisProcess(nint hwnd)
    { GetWindowThreadProcessId(hwnd, out var process); return process == _processId; }

    private void Enqueue(ActionStep step, bool timed = true, long? timestamp = null, bool checkLimit = true)
    {
        var now = timestamp ?? Stopwatch.GetTimestamp();
        step.DelayMs = timed ? (int)Math.Clamp(Math.Round(Stopwatch.GetElapsedTime(_lastEvent, now).TotalMilliseconds), 0, PlanValidator.MaxDelayMs) : 0;
        step.IntervalMs = 0; step.Count = 1;
        _lastEvent = now;
        if (_eventCount >= MaxEvents) return;
        _pending.Enqueue(step); _eventCount++;
        if (!_dispatchPending)
        {
            _dispatchPending = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainPending));
        }
        // Reserve enough slots for balancing every currently held button and key on stop.
        if (checkLimit && _eventCount + _keys.Count + _buttons.Count >= MaxEvents - 1)
        {
            StopHooks();
            BalanceHeld();
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                DrainPending();
                Error?.Invoke($"录制已达到 {MaxEvents:N0} 个事件上限，已自动停止。");
            }));
        }
    }

    private void DrainPending()
    {
        _dispatchPending = false;
        while (_pending.TryDequeue(out var step))
            try { Captured?.Invoke(step); } catch (Exception ex) { Error?.Invoke("录制数据处理失败：" + ex.Message); }
    }

    private void Fail(string message)
    {
        StopHooks();
        BalanceHeld();
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { DrainPending(); Error?.Invoke("录制失败：" + message); }));
    }

    private void StopHooks()
    {
        IsRecording = false;
        if (_mouseHook != 0) { UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
        if (_keyboardHook != 0) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
    }

    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT Point; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
}
