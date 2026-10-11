using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ClickFlow.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public enum ActionKind { Click, DoubleClick, Move, Scroll, Hotkey, Wait, MouseDown, MouseUp, KeyDown, KeyUp }
public enum PointerButton { Left, Right, Middle }
public sealed class ActionStep : ObservableObject
{
    private string _name = "点击"; private ActionKind _kind; private PointerButton _button; private int _x, _y, _delay = 200, _interval = 100, _count = 1, _wheel = -120, _virtualKey; private bool _enabled = true, _useCurrent; private string _keys = "Ctrl+C";
    public string Name { get => _name; set => Set(ref _name, value); }
    public ActionKind Kind { get => _kind; set => Set(ref _kind, value); }
    public PointerButton Button { get => _button; set => Set(ref _button, value); }
    public int X { get => _x; set => Set(ref _x, value); }
    public int Y { get => _y; set => Set(ref _y, value); }
    public int DelayMs { get => _delay; set => Set(ref _delay, value); }
    public int IntervalMs { get => _interval; set => Set(ref _interval, value); }
    public int Count { get => _count; set => Set(ref _count, value); }
    public int WheelDelta { get => _wheel; set => Set(ref _wheel, value); }
    public int VirtualKey { get => _virtualKey; set => Set(ref _virtualKey, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool UseCurrentPosition { get => _useCurrent; set => Set(ref _useCurrent, value); }
    public string Keys { get => _keys; set => Set(ref _keys, value); }
    public ActionStep Clone() => new() { Name=Name, Kind=Kind, Button=Button, X=X, Y=Y, DelayMs=DelayMs, IntervalMs=IntervalMs, Count=Count, WheelDelta=WheelDelta, VirtualKey=VirtualKey, Enabled=Enabled, UseCurrentPosition=UseCurrentPosition, Keys=Keys };
}
public sealed class RunOptions
{
    public int RepeatCount { get; set; } = 1;
    public int StartDelayMs { get; set; } = 3000;
    public int CycleDelayMs { get; set; } = 100;
    public int RandomOffset { get; set; }
    public int RandomDelayMs { get; set; }
    public bool CornerFailSafe { get; set; } = true;
    public RunOptions Clone() => (RunOptions)MemberwiseClone();
}
public sealed class QuickOptions
{
    public int IntervalMs { get; set; } = 100;
    public int Count { get; set; } = 100;
    public bool UseCurrentPosition { get; set; } = true;
    public bool DoubleClick { get; set; }
    public PointerButton Button { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}
public sealed class WorkflowDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "我的工作流";
    public string Theme { get; set; } = "Light";
    public List<ActionStep> Steps { get; set; } = [];
    public List<ActionStep> Recording { get; set; } = [];
    public QuickOptions Quick { get; set; } = new();
    public RunOptions Options { get; set; } = new();
}
public sealed record ExecutionPlan(IReadOnlyList<ActionStep> Steps, RunOptions Options);
public enum EngineState { Idle, Countdown, Running, Paused, Completed, Stopped, Faulted }
public sealed record RunProgress(EngineState State, long ActionsCompleted, long Cycle, int StepIndex, string Message, TimeSpan Elapsed);
public readonly record struct ScreenPoint(int X, int Y);
public interface IInputDriver
{
    ScreenPoint GetCursorPosition();
    bool IsAtFailSafeCorner();
    void Execute(ActionStep step, int offsetX, int offsetY);
    void ReleaseAll();
}
