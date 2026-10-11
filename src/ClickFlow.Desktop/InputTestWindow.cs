using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClickFlow.Core;
using ClickFlow.Desktop.Services;

namespace ClickFlow.Desktop;

/// <summary>A manually started native-input check, confined to this window's own receiving controls.</summary>
public sealed class InputTestWindow : Window
{
    private readonly Button _receiver, _clickTest, _keyboardTest, _stop;
    private readonly TextBox _typing, _log;
    private readonly TextBlock _counter, _result;
    private AutomationEngine? _engine;
    private Task? _run;
    private RunProgress? _lastProgress;
    private int _received;
    private bool _testingClicks, _testingKeyboard, _closing, _allowClose;
    private string? _stopReason;

    public InputTestWindow(Window owner)
    {
        Owner = owner; Resources = owner.Resources;
        Title = "输入自检 · ClickFlow"; Width = 700; Height = 690; MinWidth = 580; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FontFamily = new("Microsoft YaHei UI"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "AppBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");

        var layout = new DockPanel { Margin = new(26) }; Content = layout;
        var header = new StackPanel { Margin = new(0, 0, 0, 18) }; DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "确认每一次输入，确实送达", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var description = new TextBlock
        {
            Text = "这是独立的真实输入测试板。打开窗口不会执行任何操作；点击下方测试按钮后，仅向本板内的接收区发送输入。测试期间请保持窗口位置和焦点。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0), LineHeight = 23
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); header.Children.Add(description);

        var footer = new StackPanel { Margin = new(0, 16, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        _result = new TextBlock { Text = "尚未开始 · 当前没有发送输入", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 10) };
        footer.Children.Add(_result);
        _log = new TextBox { Height = 120, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        AutomationProperties.SetName(_log, "真实输入事件日志"); footer.Children.Add(_log);

        var body = new StackPanel(); layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var receiverContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        receiverContent.Children.Add(new TextBlock { Text = "鼠标点击接收区", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        _counter = new TextBlock { Text = "已收到 0 / 3 次左键事件", Margin = new(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        receiverContent.Children.Add(_counter);
        _receiver = new Button { Content = receiverContent, Height = 138, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 14) };
        _receiver.SetResourceReference(Control.BackgroundProperty, "AccentSoft");
        AutomationProperties.SetName(_receiver, "鼠标点击接收区");
        _receiver.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_testingClicks || e.ChangedButton != MouseButton.Left) return;
            _received++; _counter.Text = $"已收到 {_received} / 3 次左键事件";
            Log($"接收区实际收到 MouseLeftButtonDown #{_received}。");
        };
        body.Children.Add(_receiver);
        body.Children.Add(new TextBlock { Text = "键盘输入接收区", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 7) });
        _typing = new TextBox { Height = 42, FontSize = 20, MaxLength = 100, VerticalContentAlignment = VerticalAlignment.Center, Margin = new(0, 0, 0, 6) };
        InputMethod.SetIsInputMethodEnabled(_typing, false);
        AutomationProperties.SetName(_typing, "键盘输入接收区");
        _typing.TextChanged += (_, _) => { if (_testingKeyboard) Log($"文本框实际触发 TextChanged：{_typing.Text}"); };
        _typing.LostKeyboardFocus += (_, _) => { if (_testingKeyboard) Cancel("键盘接收区失去焦点，测试已取消。"); };
        body.Children.Add(_typing);
        var typingHint = new TextBlock { Text = "键盘测试发送 A、B、C，预期结果为 abc；请关闭 Caps Lock，并使用英文键盘布局。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 16) };
        typingHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); body.Children.Add(typingHint);
        var controls = new WrapPanel(); body.Children.Add(controls);
        _clickTest = new Button { Content = "测试 3 次点击", MinWidth = 145, Margin = new(0, 0, 10, 8) };
        _keyboardTest = new Button { Content = "测试键盘 ABC", MinWidth = 145, Margin = new(0, 0, 10, 8) };
        _stop = new Button { Content = "停止 · Esc", MinWidth = 115, IsEnabled = false, Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetName(_clickTest, "测试 3 次点击"); AutomationProperties.SetName(_keyboardTest, "测试键盘 ABC");
        controls.Children.Add(_clickTest); controls.Children.Add(_keyboardTest); controls.Children.Add(_stop);
        _clickTest.Click += async (_, _) => await TestAsync(false);
        _keyboardTest.Click += async (_, _) => await TestAsync(true);
        _stop.Click += (_, _) => Cancel("已由用户停止测试。");
        Deactivated += (_, _) => Cancel("测试板失去前台焦点，测试已取消。");
        LocationChanged += (_, _) => Cancel("测试板位置发生变化，测试已取消。");
        SizeChanged += (_, _) => Cancel("测试板尺寸发生变化，测试已取消。");
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancel("已按 Esc 停止测试。"); e.Handled = true; } };
        Closing += OnClosing;
        Log("测试板已打开。尚未发送鼠标或键盘输入。");
    }

    private async Task TestAsync(bool keyboard)
    {
        if (_engine?.IsActive == true || _closing) return;
        if (!IsActive) { _result.Text = "请先激活测试板。"; return; }
        if (Keyboard.Modifiers != ModifierKeys.None || keyboard && Keyboard.IsKeyToggled(Key.CapsLock))
        { _result.Text = "请松开 Ctrl / Shift / Alt / Win，并关闭 Caps Lock，再开始测试。"; return; }
        _received = 0; _counter.Text = "已收到 0 / 3 次左键事件"; _stopReason = null; _lastProgress = null;
        _typing.Text = "";
        if (keyboard) { _typing.Focus(); Keyboard.Focus(_typing); }
        else _receiver.Focus();
        UpdateLayout();
        var control = keyboard ? (FrameworkElement)_typing : _receiver;
        var bounds = ScreenBounds(control);
        var point = new ScreenPoint((int)Math.Floor(bounds.Left + bounds.Width / 2), (int)Math.Floor(bounds.Top + bounds.Height / 2));
        var driver = new GuardedInputDriver(this, control, keyboard, bounds, point);
        using var engine = new AutomationEngine(driver);
        _engine = engine;
        engine.Progress += progress => _lastProgress = progress;
        _testingKeyboard = keyboard; _testingClicks = !keyboard;
        _clickTest.IsEnabled = _keyboardTest.IsEnabled = false; _stop.IsEnabled = true;
        _result.Text = keyboard ? "正在测试真实键盘输入…" : "正在测试真实左键输入…";
        Log(keyboard ? "手动启动：仅向本板文本框发送 A、B、C。" : $"手动启动：仅向本板接收区 ({point.X}, {point.Y}) 发送 3 次左键，间隔 300 ms。");
        try
        {
            IReadOnlyList<ActionStep> steps = keyboard
                ? new[] { "A", "B", "C" }.Select(key => new ActionStep { Name = "板内键盘测试 " + key, Kind = ActionKind.Hotkey, Keys = key, DelayMs = 300, Count = 1, IntervalMs = 0 }).ToArray()
                : [new ActionStep { Name = "板内点击测试", Kind = ActionKind.Click, Button = PointerButton.Left, X = point.X, Y = point.Y, UseCurrentPosition = false, DelayMs = 300, Count = 3, IntervalMs = 300 }];
            _run = engine.RunAsync(new(steps, new RunOptions { StartDelayMs = 0, RepeatCount = 1, CycleDelayMs = 0, CornerFailSafe = false }));
            await _run;
            // Let the desktop deliver the final real input event before judging the receiving control.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (_closing) return;
            if (_lastProgress?.State == EngineState.Completed && _stopReason is null)
            {
                var passed = keyboard ? _typing.Text == "abc" : _received == 3;
                _result.Text = keyboard
                    ? (passed ? "键盘验证通过 · 文本框实际收到 abc" : $"键盘验证未通过 · 实际文本为“{_typing.Text}”，预期 abc")
                    : (passed ? "鼠标验证通过 · 实际收到 3 / 3 次左键事件" : $"鼠标验证未通过 · 实际收到 {_received} / 3 次左键事件");
                Log(_result.Text);
            }
            else
            {
                _result.Text = _stopReason ?? _lastProgress?.Message ?? "测试未完成。";
                Log(_result.Text);
            }
        }
        catch (Exception ex) { if (!_closing) { _result.Text = "测试未完成：" + ex.Message; Log(_result.Text); } }
        finally
        {
            _testingClicks = _testingKeyboard = false;
            _engine = null; _run = null;
            if (!_closing) { _clickTest.IsEnabled = _keyboardTest.IsEnabled = true; _stop.IsEnabled = false; }
        }
    }

    private void Cancel(string reason)
    {
        if (!_testingClicks && !_testingKeyboard) return;
        _stopReason ??= reason;
        _engine?.Stop();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; Cancel("测试板正在关闭，测试已取消。");
        if (_run is { } run) { try { await run; } catch { } }
        _engine?.Dispose();
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void Log(string message)
    { _log.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}"); _log.ScrollToEnd(); }

    private static Rect ScreenBounds(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    private sealed class GuardedInputDriver(InputTestWindow window, FrameworkElement target, bool keyboard, Rect originalBounds, ScreenPoint point) : IInputDriver
    {
        private readonly InputDriver _native = new();
        public ScreenPoint GetCursorPosition() => _native.GetCursorPosition();
        public bool IsAtFailSafeCorner() => false;
        public void ReleaseAll() => _native.ReleaseAll();
        public void Execute(ActionStep step, int offsetX, int offsetY)
        {
            window.Dispatcher.Invoke(() =>
            {
                if (window._closing || window._stopReason is not null || !window.IsActive || !window.IsVisible)
                    throw new InvalidOperationException("测试板不是活动窗口，已拒绝发送输入。");
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == 0 || !BelongsTo(hwnd, GetForegroundWindow()))
                    throw new InvalidOperationException("前台窗口已变化，已拒绝发送输入。");
                var bounds = ScreenBounds(target);
                if (bounds != originalBounds || !bounds.Contains(new Point(point.X, point.Y)))
                    throw new InvalidOperationException("接收区位置或尺寸已变化，已拒绝发送输入。");
                if (!BelongsTo(hwnd, WindowFromPoint(new POINT { X = point.X, Y = point.Y })))
                    throw new InvalidOperationException("接收区已被其他窗口覆盖，已拒绝发送输入。");
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
                    throw new InvalidOperationException("检测到按住的修饰键，已拒绝发送输入，请松开后重试。");
                if (offsetX != 0 || offsetY != 0) throw new InvalidOperationException("测试板不允许随机位置偏移。");
                if (keyboard)
                {
                    if (!window._typing.IsKeyboardFocusWithin || step.Kind != ActionKind.Hotkey || step.Keys is not ("A" or "B" or "C"))
                        throw new InvalidOperationException("键盘焦点不在本板文本框内，或输入不属于 ABC 测试，已拒绝发送。");
                }
                else if (step.Kind != ActionKind.Click || step.Button != PointerButton.Left || step.UseCurrentPosition || step.X != point.X || step.Y != point.Y)
                    throw new InvalidOperationException("点击不属于本板固定接收位置，已拒绝发送。");
                // Check and injection share one UI-dispatch invocation. No other WPF event can interleave them.
                _native.Execute(step, 0, 0);
            });
        }
        private static bool BelongsTo(nint owner, nint candidate) => candidate == owner || (candidate != 0 && IsChild(owner, candidate));
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    }
}
