using System.ComponentModel;
using System.Runtime.InteropServices;
using ClickFlow.Core;

namespace ClickFlow.Desktop.Services;

public sealed class InputDriver : IInputDriver, IInputSuspension
{
    private readonly object _gate = new();
    private readonly HashSet<int> _keysDown = [];
    private readonly HashSet<PointerButton> _buttonsDown = [];
    private bool _suspended;
    public ScreenPoint GetCursorPosition()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取鼠标位置。");
        return new(point.X, point.Y);
    }

    public bool IsAtFailSafeCorner()
    {
        var point = GetCursorPosition();
        var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var r = info.Monitor;
        return (point.X <= r.Left + 1 || point.X >= r.Right - 2) && (point.Y <= r.Top + 1 || point.Y >= r.Bottom - 2);
    }

    public void Execute(ActionStep step, int offsetX, int offsetY)
    {
        lock (_gate)
        {
            var pointerAction = step.Kind is ActionKind.Click or ActionKind.DoubleClick or ActionKind.Move or ActionKind.Scroll or ActionKind.MouseDown or ActionKind.MouseUp;
            if (pointerAction && !step.UseCurrentPosition)
                MoveTo((long)step.X + offsetX, (long)step.Y + offsetY);
            switch (step.Kind)
            {
                case ActionKind.Click: Click(step.Button, false); break;
                case ActionKind.DoubleClick: Click(step.Button, true); break;
                case ActionKind.Move: break;
                case ActionKind.Scroll: Send(Mouse(0x0800, unchecked((uint)step.WheelDelta))); break;
                case ActionKind.Hotkey: SendHotkey(step.Keys); break;
                case ActionKind.MouseDown: SetButton(step.Button, true); break;
                case ActionKind.MouseUp: SetButton(step.Button, false); break;
                case ActionKind.KeyDown: SetKey(step.VirtualKey, true); break;
                case ActionKind.KeyUp: SetKey(step.VirtualKey, false); break;
                case ActionKind.Wait: break;
                default: throw new InvalidOperationException("不支持的输入动作。");
            }
        }
    }

    private void MoveTo(long x, long y)
    {
        // PerMonitorV2 awareness in the manifest makes these physical pixels, including negative coordinates.
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("无法读取虚拟桌面尺寸。");
        if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue ||
            MonitorFromPoint(new POINT { X = (int)x, Y = (int)y }, 0) == 0)
            throw new InvalidOperationException($"目标坐标 ({x}, {y}) 不在任何显示器内，请重新捕获位置或减小随机偏移。");
        // Aim at each physical pixel's center in the 0..65535 absolute-coordinate space.
        var normalizedX = (int)(((x - left) * 65536 + 32768) / width);
        var normalizedY = (int)(((y - top) * 65536 + 32768) / height);
        var input = Mouse(0x0001 | 0x8000 | 0x4000);
        input.Data.Mouse.X = normalizedX; input.Data.Mouse.Y = normalizedY;
        Send(input);
    }

    private void Click(PointerButton button, bool twice)
    {
        _buttonsDown.Add(button);
        var down = Mouse(ButtonFlag(button, true)); var up = Mouse(ButtonFlag(button, false));
        // Keep the button tracked after partial SendInput so finally cleanup can release it.
        Send(twice ? [down, up, down, up] : [down, up]);
        _buttonsDown.Remove(button);
    }

    private void SetButton(PointerButton button, bool down)
    {
        if (down) _buttonsDown.Add(button);
        Send(Mouse(ButtonFlag(button, down)));
        if (!down) _buttonsDown.Remove(button);
    }

    private void SetKey(int key, bool down)
    {
        if (down) _keysDown.Add(key);
        Send(Keyboard(key, down));
        if (!down) _keysDown.Remove(key);
    }

    private void SendHotkey(string keys)
    {
        if (!HotkeyParser.TryParse(keys, out var parsed, out var error)) throw new InvalidOperationException(error);
        var newlyHeld = parsed.Where(key => !_keysDown.Contains(key)).ToArray();
        try { foreach (var key in newlyHeld) SetKey(key, true); }
        finally
        {
            Exception? releaseError = null;
            foreach (var key in newlyHeld.Reverse())
                try { SetKey(key, false); } catch (Exception ex) { releaseError ??= ex; }
            if (releaseError is not null) throw releaseError;
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            Exception? firstError = null;
            foreach (var key in _keysDown.ToArray().Reverse())
                try { SetKey(key, false); } catch (Exception ex) { firstError ??= ex; }
            foreach (var button in _buttonsDown.ToArray())
                try { SetButton(button, false); } catch (Exception ex) { firstError ??= ex; }
            _suspended = false;
            if (firstError is not null) throw firstError;
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (_suspended) return;
            _suspended = true;
            // Retain the logical holds while releasing their physical state.
            foreach (var key in _keysDown.Reverse()) Send(Keyboard(key, false));
            foreach (var button in _buttonsDown) Send(Mouse(ButtonFlag(button, false)));
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_suspended) return;
            foreach (var key in _keysDown.OrderBy(key => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5 ? 0 : 1)) Send(Keyboard(key, true));
            foreach (var button in _buttonsDown) Send(Mouse(ButtonFlag(button, true)));
            _suspended = false;
        }
    }

    private static uint ButtonFlag(PointerButton button, bool down) => button switch
    {
        PointerButton.Left => down ? 0x0002u : 0x0004u,
        PointerButton.Right => down ? 0x0008u : 0x0010u,
        PointerButton.Middle => down ? 0x0020u : 0x0040u,
        _ => throw new InvalidOperationException("无效的鼠标按键。")
    };
    private static INPUT Mouse(uint flags, uint data = 0) => new() { Type = 0, Data = new INPUTUNION { Mouse = new MOUSEINPUT { Flags = flags, MouseData = data } } };
    private static INPUT Keyboard(int key, bool down) => new()
    {
        Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = checked((ushort)key),
            Flags = (down ? 0u : 0x0002u) | (IsExtendedKey(key) ? 0x0001u : 0u) } }
    };
    private static bool IsExtendedKey(int key) => key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;
    private static void Send(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows 仅接受了 {sent}/{inputs.Length} 个输入事件。目标程序可能以更高权限运行，或当前桌面不可交互。");
    }

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    // Include all native union members. MOUSEINPUT is 32 bytes on x64; INPUT is 40 bytes.
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint Message; public ushort ParamL, ParamH; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
}
