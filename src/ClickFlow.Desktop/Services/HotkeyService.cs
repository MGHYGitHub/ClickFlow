using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClickFlow.Desktop.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly Window _window;
    private HwndSource? _source;
    private nint _handle;
    private readonly List<int> _registered = [];
    private readonly List<string> _failures = [];
    private bool _disposed;
    public event Action<int>? Pressed;
    public IReadOnlyList<string> Failures => _failures.AsReadOnly();

    public HotkeyService(Window window)
    {
        _window = window;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
        if (new WindowInteropHelper(window).Handle != 0) Register();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Register();
    private void OnClosed(object? sender, EventArgs e) => Dispose();
    private void Register()
    {
        if (_disposed || _source is not null) return;
        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        if (_source is null) { _failures.Add("无法获取窗口句柄，全局快捷键不可用。"); return; }
        _source.AddHook(WindowProc);
        foreach (var vk in new[] { 0x71, 0x72, 0x73, 0x75, 0x76 })
        {
            if (RegisterHotKey(_handle, vk, 0x4000, (uint)vk)) _registered.Add(vk);
            else _failures.Add($"F{vk - 0x6F} 注册失败，可能被其他程序占用（错误 {Marshal.GetLastWin32Error()}）。");
        }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && _registered.Contains((int)wParam))
        { handled = true; Pressed?.Invoke((int)wParam); }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized; _window.Closed -= OnClosed;
        foreach (var id in _registered) UnregisterHotKey(_handle, id);
        _registered.Clear();
        _source?.RemoveHook(WindowProc); _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
