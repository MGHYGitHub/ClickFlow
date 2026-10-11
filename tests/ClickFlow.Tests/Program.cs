using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClickFlow.Core;

// No desktop input, external packages, network, or test framework required.
// Each execution test uses a thread-safe fake. A failing test sets exit code 1.
var tests = new (string Name, Func<Task> Run)[]
{
    ("long countdown cancels promptly without input", CancelCountdown),
    ("long step delay cancels promptly without input", CancelStepDelay),
    ("pause suppresses input until resume", PauseAndResume),
    ("pausing a countdown preserves remaining time", PausePreservesCountdown),
    ("stopping a paused run never restores held input", StopWhilePaused),
    ("an active run rejects a second start", RejectConcurrentStart),
    ("a running plan is independent of source edits", SnapshotPlan),
    ("driver failures always release held input", DriverFailureCleanup),
    ("progress observer failures cannot break cleanup", ObserverFailureCleanup),
    ("corner fail-safe stops during a wait", CornerFailSafe),
    ("finite repeats complete at the requested count", FiniteRepeats),
    ("infinite repeats stop on cancellation", InfiniteRepeats),
    ("disabled steps produce no input", DisabledSteps),
    ("wait steps produce no input and remain cancellable", WaitSteps),
    ("engine can run again after completion", RestartAfterCompletion),
    ("invalid plans are rejected before input", ValidateInvalidPlans),
    ("hotkeys normalize names and modifier ordering", ValidHotkeys),
    ("hotkeys reject unknown, duplicate and reserved keys", InvalidHotkeys),
    ("workflow JSON round-trips every persisted setting", WorkflowRoundTrip),
    ("saving invalid data preserves the existing file", InvalidSavePreservesFile),
    ("workflow save keeps the previous valid version", SaveBackup),
    ("unknown schema, duplicate and unknown fields are rejected", StrictJson),
    ("Python keyboard actions and post-action delays are preserved", ImportPythonActions),
    ("Python seconds and legacy tuple settings convert correctly", ImportPythonSettings),
    ("Java point timing, Base64 names and wheel direction are preserved", ImportJavaActions),
    ("Java repeated hotkeys preserve a pointer move before every send", ImportJavaHotkeyRepeats),
    ("Java native key codes become Windows virtual keys", ImportJavaRecording),
    ("older Java point formats remain importable", ImportOldJava),
    ("Python signed random delay cannot silently change meaning", RejectPythonRandomDelay),
    ("unsupported legacy actions are rejected without modifying source", RejectUnsupportedLegacy)
};

var failures = 0;
var all = Stopwatch.StartNew();
foreach (var (name, run) in tests)
{
    var timer = Stopwatch.StartNew();
    try { await run(); Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed in {all.Elapsed.TotalSeconds:F2}s. Real desktop input was never used.");
return failures == 0 ? 0 : 1;

static ActionStep Click(int count = 1, int interval = 20) => new()
{
    Name = "test click", Kind = ActionKind.Click, X = 125, Y = -50,
    DelayMs = 0, IntervalMs = interval, Count = count
};

static ExecutionPlan Plan(ActionStep? step = null, int repeat = 1, int delay = 0) => new(
    [step ?? Click()], new RunOptions { StartDelayMs = delay, CycleDelayMs = 10, RepeatCount = repeat, CornerFailSafe = true });

static async Task CancelCountdown()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var events = Observe(engine); var task = engine.RunAsync(Plan(delay: 60_000));
    await Until(() => events.Any(p => p.State == EngineState.Countdown), "countdown was not reported");
    await CancelAndAssert(engine, task);
    Equal(0, input.ExecutionCount); Equal(1, input.ReleaseCount);
    Equal(EngineState.Stopped, events.Last().State);
}

static async Task CancelStepDelay()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var step = Click(); step.DelayMs = 60_000;
    var events = Observe(engine); var task = engine.RunAsync(Plan(step));
    await Until(() => events.Any(p => p.State == EngineState.Running), "step wait was not entered");
    await CancelAndAssert(engine, task);
    Equal(0, input.ExecutionCount); Equal(1, input.ReleaseCount);
}

static async Task PauseAndResume()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var events = Observe(engine); var task = engine.RunAsync(Plan(Click(interval: 30), repeat: 0));
    try
    {
        await Until(() => input.ExecutionCount >= 2, "execution did not begin");
        engine.TogglePause();
        await Until(() => events.Any(p => p.State == EngineState.Paused), "pause was not acknowledged");
        var pausedCount = input.ExecutionCount;
        await Task.Delay(180);
        Equal(pausedCount, input.ExecutionCount, "input occurred during an acknowledged pause");
        True(engine.IsPaused, "paused flag was lost");
        engine.TogglePause();
        await Until(() => input.ExecutionCount > pausedCount, "execution did not resume");
    }
    finally { engine.Stop(); await Finish(task); }
    Equal(1, input.ReleaseCount);
    True(input.Lifecycle.SequenceEqual(new[] { "suspend", "resume", "release" }), "pause/resume lifecycle order was incorrect");
}

static async Task PausePreservesCountdown()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var pauseIssued = 0;
    engine.Progress += p => { if (p.State == EngineState.Countdown && Interlocked.Exchange(ref pauseIssued, 1) == 0) engine.TogglePause(); };
    var events = Observe(engine); var task = engine.RunAsync(Plan(delay: 350));
    try
    {
        await Until(() => events.Any(p => p.State == EngineState.Paused), "countdown did not pause");
        await Task.Delay(400); Equal(0, input.ExecutionCount);
        var remaining = Stopwatch.StartNew(); engine.TogglePause();
        await Finish(task);
        // Large tolerance: the full 350 ms should remain, never expire during the 400 ms pause.
        True(remaining.ElapsedMilliseconds >= 240, "paused wall time consumed the countdown");
        Equal(1, input.ExecutionCount);
    }
    finally { engine.Stop(); await Finish(task); }
}

static async Task StopWhilePaused()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var events = Observe(engine); var task = engine.RunAsync(Plan(delay: 60_000));
    engine.TogglePause();
    try { await Until(() => events.Any(p => p.State == EngineState.Paused), "pause was not acknowledged"); }
    finally { engine.Stop(); await Finish(task); }
    Equal(0, input.ExecutionCount);
    True(input.Lifecycle.SequenceEqual(new[] { "suspend", "release" }), "stopping a paused run restored held inputs");
}

static async Task RejectConcurrentStart()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var first = engine.RunAsync(Plan(delay: 60_000));
    try { Throws<InvalidOperationException>(() => engine.RunAsync(Plan())); True(engine.IsActive, "first run should remain active"); }
    finally { engine.Stop(); await Finish(first); }
    Equal(0, input.ExecutionCount); Equal(1, input.ReleaseCount);
}

static async Task SnapshotPlan()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var step = Click(2); var plan = Plan(step, delay: 100);
    var task = engine.RunAsync(plan);
    step.X = 900; step.Y = 800; step.Count = 15; step.Kind = ActionKind.Scroll; step.Enabled = false;
    plan.Options.RepeatCount = 7; plan.Options.StartDelayMs = 60_000; plan.Options.RandomOffset = 500;
    await Finish(task);
    Equal(2, input.ExecutionCount);
    foreach (var sent in input.Executions)
    {
        Equal(125, sent.Step.X); Equal(-50, sent.Step.Y); Equal(ActionKind.Click, sent.Step.Kind);
        Equal(0, sent.OffsetX); Equal(0, sent.OffsetY);
    }
}

static async Task DriverFailureCleanup()
{
    var input = new FakeDriver { ThrowOnExecute = true }; using var engine = new AutomationEngine(input);
    var events = Observe(engine); await Finish(engine.RunAsync(Plan()));
    Equal(1, input.ReleaseCount); True(!engine.IsActive, "fault left engine active");
    Equal(EngineState.Faulted, events.Last().State);
    True(events.Last().Message.Contains("injected", StringComparison.Ordinal), "fault detail was lost");
}

static async Task ObserverFailureCleanup()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    engine.Progress += _ => throw new InvalidOperationException("broken observer");
    var events = Observe(engine); await Finish(engine.RunAsync(Plan(Click(2))));
    Equal(2, input.ExecutionCount); Equal(1, input.ReleaseCount); Equal(EngineState.Completed, events.Last().State);
}

static async Task CornerFailSafe()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var events = Observe(engine); var task = engine.RunAsync(Plan(delay: 60_000));
    await Until(() => events.Any(), "countdown did not begin");
    input.AtCorner = true;
    await Finish(task, 1000);
    Equal(0, input.ExecutionCount); Equal(1, input.ReleaseCount); Equal(EngineState.Stopped, events.Last().State);
    True(events.Last().Message.Contains("角落", StringComparison.Ordinal), "fail-safe reason was lost");
}

static async Task FiniteRepeats()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var events = Observe(engine); await Finish(engine.RunAsync(Plan(Click(3), repeat: 2)));
    Equal(6, input.ExecutionCount); Equal(6L, events.Last().ActionsCompleted); Equal(2L, events.Last().Cycle);
    Equal(EngineState.Completed, events.Last().State); Equal(1, input.ReleaseCount);
}

static async Task InfiniteRepeats()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var events = Observe(engine); var task = engine.RunAsync(Plan(repeat: 0));
    try { await Until(() => input.ExecutionCount >= 4, "infinite run did not repeat"); }
    finally { engine.Stop(); await Finish(task); }
    Equal(EngineState.Stopped, events.Last().State); Equal(1, input.ReleaseCount);
    var endedCount = input.ExecutionCount; await Task.Delay(70); Equal(endedCount, input.ExecutionCount);
}

static async Task DisabledSteps()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var disabled = Click(20); disabled.Enabled = false;
    var enabled = Click(2); enabled.Name = "enabled";
    var plan = Plan() with { Steps = new[] { disabled, enabled } };
    await Finish(engine.RunAsync(plan));
    Equal(2, input.ExecutionCount); True(input.Executions.All(x => x.Step.Name == "enabled"), "disabled step ran");
}

static async Task WaitSteps()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    var step = Click(); step.Kind = ActionKind.Wait; step.IntervalMs = 60_000;
    var events = Observe(engine); var task = engine.RunAsync(Plan(step));
    await Until(() => events.Any(p => p.State == EngineState.Running && p.StepIndex == 0), "wait step was not reached");
    await CancelAndAssert(engine, task); Equal(0, input.ExecutionCount); Equal(1, input.ReleaseCount);
}

static async Task RestartAfterCompletion()
{
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    await Finish(engine.RunAsync(Plan())); await Finish(engine.RunAsync(Plan()));
    Equal(2, input.ExecutionCount); Equal(2, input.ReleaseCount); True(!engine.IsActive, "completed engine remained active");
}

static Task ValidateInvalidPlans()
{
    var cases = new List<ExecutionPlan>
    {
        Plan() with { Steps = Array.Empty<ActionStep>() },
        Plan() with { Steps = new ActionStep[] { null! } },
        Plan(repeat: -1), Plan(repeat: 1_000_001), Plan(delay: -1), Plan(delay: PlanValidator.MaxDelayMs + 1)
    };
    foreach (var change in new Action<ActionStep>[]
    {
        x => x.Count = 0, x => x.Count = 1_000_001, x => x.DelayMs = -1, x => x.IntervalMs = -1,
        x => x.X = 1_000_001, x => x.Y = -1_000_001, x => x.Kind = (ActionKind)999,
        x => x.Button = (PointerButton)999, x => x.Enabled = false,
        x => { x.Kind = ActionKind.Scroll; x.WheelDelta = 0; },
        x => { x.Kind = ActionKind.KeyDown; x.VirtualKey = 0; },
        x => { x.Kind = ActionKind.KeyUp; x.VirtualKey = 255; },
        x => { x.Kind = ActionKind.Hotkey; x.Keys = "Ctrl+Unknown"; }
    }) { var step = Click(); change(step); cases.Add(Plan(step)); }
    foreach (var change in new Action<RunOptions>[]
    {
        x => x.RandomOffset = -1, x => x.RandomOffset = 1001,
        x => x.RandomDelayMs = -1, x => x.RandomDelayMs = 60_001,
        x => x.CycleDelayMs = -1
    }) { var plan = Plan(); change(plan.Options); cases.Add(plan); }
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    foreach (var plan in cases)
    {
        True(PlanValidator.Validate(plan) is not null, "invalid plan passed validation");
        Throws<InvalidOperationException>(() => engine.RunAsync(plan));
    }
    True(PlanValidator.Validate(Plan()) is null, "valid plan was rejected"); Equal(0, input.ExecutionCount);
    return Task.CompletedTask;
}

static Task ValidHotkeys()
{
    foreach (var (text, expected) in new (string, int[])[]
    {
        (" ctrl + c ", [0x11, 0x43]), ("V+Shift+Control", [0x10, 0x11, 0x56]),
        ("Alt+F12", [0x12, 0x7B]), ("Win+Left", [0x5B, 0x25]),
        ("Numpad9", [0x69]), ("Ctrl+Plus", [0x11, 0xBB]), ("F24", [0x87])
    })
    {
        True(HotkeyParser.TryParse(text, out var actual, out var error), error ?? "hotkey rejected");
        True(expected.SequenceEqual(actual), $"incorrect virtual keys for {text}");
    }
    return Task.CompletedTask;
}

static Task InvalidHotkeys()
{
    foreach (var text in new string?[] { null, "", " ", "Ctrl++A", "Ctrl+Ctrl+C", "Ctrl+Control", "F25", "F0", "Unknown", "Ctrl+F2", "F3", "F4", "F6", "F7", "A+B+C+D+E+G+H", new string('A', 129) })
    {
        True(!HotkeyParser.TryParse(text, out _, out var error), $"bad hotkey accepted: {text}");
        True(!string.IsNullOrWhiteSpace(error), "invalid hotkey needs a helpful error");
    }
    return Task.CompletedTask;
}

static WorkflowDocument Document() => new()
{
    Name = "中文流程 — example", Theme = "Dark",
    Quick = new QuickOptions { Button = PointerButton.Middle, Count = 0, DoubleClick = true, IntervalMs = 37, UseCurrentPosition = false, X = -500, Y = 800 },
    Options = new RunOptions { RepeatCount = 4, StartDelayMs = 850, CycleDelayMs = 120, RandomDelayMs = 13, RandomOffset = 7, CornerFailSafe = false },
    Steps = [new() { Name = "复制", Kind = ActionKind.Hotkey, Keys = "Ctrl+C", Count = 3, DelayMs = 321, IntervalMs = 17, X = -42, Y = 90 }],
    Recording = [new() { Name = "按下 A", Kind = ActionKind.KeyDown, VirtualKey = 65, DelayMs = 27 }, new() { Name = "释放 A", Kind = ActionKind.KeyUp, VirtualKey = 65, DelayMs = 39 }]
};

static Task WorkflowRoundTrip()
{
    using var files = new TemporaryFiles(); var path = files.PathFor("workflow.clickflow.json");
    var original = Document(); WorkflowStore.Save(path, original); var loaded = WorkflowStore.Load(path);
    Equal(WorkflowStore.Serialize(original), WorkflowStore.Serialize(loaded));
    Equal(-500, loaded.Quick.X); Equal(ActionKind.KeyDown, loaded.Recording[0].Kind); Equal(65, loaded.Recording[1].VirtualKey);
    return Task.CompletedTask;
}

static Task InvalidSavePreservesFile()
{
    using var files = new TemporaryFiles(); var path = files.PathFor("workflow.json");
    var doc = Document(); WorkflowStore.Save(path, doc); var good = File.ReadAllText(path);
    doc.Steps[0].Count = -1; Throws<InvalidDataException>(() => WorkflowStore.Save(path, doc));
    Equal(good, File.ReadAllText(path));
    return Task.CompletedTask;
}

static Task SaveBackup()
{
    using var files = new TemporaryFiles(); var path = files.PathFor("workflow.json");
    var doc = Document(); WorkflowStore.Save(path, doc); var previous = File.ReadAllText(path);
    doc.Name = "updated"; WorkflowStore.Save(path, doc);
    Equal(previous, File.ReadAllText(path + ".bak")); Equal("updated", WorkflowStore.Load(path).Name);
    return Task.CompletedTask;
}

static Task StrictJson()
{
    using var files = new TemporaryFiles(); var path = files.PathFor("invalid.json");
    var valid = WorkflowStore.Serialize(Document());
    foreach (var json in new[]
    {
        valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal),
        valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal),
        valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"surprise\": 42", StringComparison.Ordinal),
        valid.Replace("\"kind\": \"hotkey\"", "\"kind\": 4", StringComparison.Ordinal),
        "{}", "[]", "{\"schemaVersion\":1,\"steps\":null}"
    })
    {
        File.WriteAllText(path, json);
        ThrowsAny(() => WorkflowStore.Load(path), typeof(InvalidDataException), typeof(JsonException));
    }
    return Task.CompletedTask;
}

// Fixtures mirror AutoClicker_2.5.py add_click_point/run_click_task and
// AutoClickerApp.java saveConfiguration; no legacy program is executed.
static Task ImportPythonActions()
{
    using var files = new TemporaryFiles();
    var doc = ImportFixture(files, "python.json", """
        [{"x":-50,"y":40,"name":"点位","button":"right","delay":0.4,"click_count":3,"click_interval":0.05,"action_type":"click","keys":""},
         {"x":0,"y":0,"name":"保存","button":"keyboard","delay":99,"click_count":2,"click_interval":0.9,"action_type":"keyboard","keys":"ctrl+s"}]
        """);
    Equal(3, doc.Steps.Count);
    var click = doc.Steps[0]; Equal(-50, click.X); Equal(PointerButton.Right, click.Button); Equal(3, click.Count); Equal(50, click.IntervalMs); Equal(0, click.DelayMs);
    var wait = doc.Steps[1]; Equal(ActionKind.Wait, wait.Kind); Equal(400, wait.IntervalMs); Equal(0, wait.DelayMs);
    var keyboard = doc.Steps[2]; Equal(ActionKind.Hotkey, keyboard.Kind); Equal("ctrl+s", keyboard.Keys); Equal(2, keyboard.Count); Equal(0, keyboard.IntervalMs); Equal(0, keyboard.DelayMs);
    Equal(1000, doc.Options.CycleDelayMs);
    return Task.CompletedTask;
}

static Task ImportPythonSettings()
{
    using var files = new TemporaryFiles();
    var doc = ImportFixture(files, "settings.json", """
        {"points":[[1,-2,"测试"]],"settings":{"loop_count":4,"countdown":2.5,"random_offset":3,"random_delay":0,"theme":"dark","base_delay":0.2,"click_count":7,"default_button":"middle"}}
        """);
    Equal(4, doc.Options.RepeatCount); Equal(2500, doc.Options.StartDelayMs); Equal(3, doc.Options.RandomOffset); Equal(0, doc.Options.RandomDelayMs);
    Equal("Dark", doc.Theme); Equal(1, doc.Steps.Count); Equal("测试", doc.Steps[0].Name); Equal(-2, doc.Steps[0].Y);
    Equal(7, doc.Steps[0].Count); Equal(200, doc.Steps[0].IntervalMs); Equal(PointerButton.Middle, doc.Steps[0].Button);
    return Task.CompletedTask;
}

static Task ImportJavaActions()
{
    using var files = new TemporaryFiles();
    var doc = ImportFixture(files, "java.properties", $"""
        version=1
        points.count=2
        point.0={Base64Url("滚动")},-20,30,200,50,2,WHEEL,3,
        point.1={Base64Url("保存")},1,2,300,40,1,HOTKEY,1,{Base64Url("Ctrl+S")}
        recording.count=0
        task.cycleLimit=3
        """);
    Equal(3, doc.Steps.Count); Equal("滚动", doc.Steps[0].Name); Equal(-20, doc.Steps[0].X);
    Equal(ActionKind.Scroll, doc.Steps[0].Kind); Equal(-360, doc.Steps[0].WheelDelta); Equal(200, doc.Steps[0].DelayMs); Equal(50, doc.Steps[0].IntervalMs);
    Equal(ActionKind.Move, doc.Steps[1].Kind); Equal(300, doc.Steps[1].DelayMs); Equal(1, doc.Steps[1].X); Equal(2, doc.Steps[1].Y);
    Equal(ActionKind.Hotkey, doc.Steps[2].Kind); Equal("Ctrl+S", doc.Steps[2].Keys); Equal(3, doc.Options.RepeatCount);
    Equal(0, doc.Options.StartDelayMs); Equal(0, doc.Options.CycleDelayMs);
    return Task.CompletedTask;
}

static async Task ImportJavaHotkeyRepeats()
{
    using var files = new TemporaryFiles();
    var doc = ImportFixture(files, "hotkey-repeats.properties", $"""
        version=1
        points.count=1
        point.0={Base64Url("重复保存")},-10,25,30,40,3,HOTKEY,1,{Base64Url("Ctrl+S")}
        recording.count=0
        task.cycleLimit=1
        """);
    Equal(6, doc.Steps.Count);
    for (var i = 0; i < 3; i++)
    {
        var move = doc.Steps[i * 2]; var key = doc.Steps[i * 2 + 1];
        Equal(ActionKind.Move, move.Kind); Equal(-10, move.X); Equal(25, move.Y);
        Equal(i == 0 ? 30 : 40, move.DelayMs); Equal(1, move.Count);
        Equal(ActionKind.Hotkey, key.Kind); Equal("Ctrl+S", key.Keys); Equal(0, key.DelayMs); Equal(1, key.Count);
    }
    var input = new FakeDriver(); using var engine = new AutomationEngine(input);
    await Finish(engine.RunAsync(new(doc.Steps, doc.Options)));
    True(input.Executions.Select(x => x.Step.Kind).SequenceEqual(new[] { ActionKind.Move, ActionKind.Hotkey, ActionKind.Move, ActionKind.Hotkey, ActionKind.Move, ActionKind.Hotkey }), "imported playback changed move/key ordering");
}

static Task RejectPythonRandomDelay()
{
    using var files = new TemporaryFiles();
    foreach (var value in new[] { "0.04", "0.000001", "\"0.04\"" })
    {
        var original = "{\"points\":[[1,2,\"a\"]],\"settings\":{\"random_delay\":" + value + "}}";
        var path = files.PathFor("signed-random.json"); File.WriteAllText(path, original);
        Throws<InvalidDataException>(() => LegacyImporter.Import(path)); Equal(original, File.ReadAllText(path));
    }
    return Task.CompletedTask;
}

static Task ImportJavaRecording()
{
    using var files = new TemporaryFiles();
    var doc = ImportFixture(files, "recording.properties", """
        version=1
        points.count=0
        recording.count=5
        recording.0=PRESS,5,6,1,10,0,0
        recording.1=RELEASE,5,6,1,20,0,0
        recording.2=KEY_PRESS,0,0,0,30,30,0
        recording.3=KEY_RELEASE,0,0,0,40,30,0
        recording.4=WHEEL,5,6,0,50,0,-2
        task.cycleLimit=1
        """);
    Equal(0, doc.Steps.Count); Equal(5, doc.Recording.Count);
    Equal(ActionKind.MouseDown, doc.Recording[0].Kind); Equal(PointerButton.Left, doc.Recording[0].Button);
    Equal(ActionKind.MouseUp, doc.Recording[1].Kind); Equal(20, doc.Recording[1].DelayMs);
    Equal(ActionKind.KeyDown, doc.Recording[2].Kind); Equal(65, doc.Recording[2].VirtualKey);
    Equal(ActionKind.KeyUp, doc.Recording[3].Kind); Equal(65, doc.Recording[3].VirtualKey);
    Equal(240, doc.Recording[4].WheelDelta);
    return Task.CompletedTask;
}

static Task ImportOldJava()
{
    using var files = new TemporaryFiles();
    var doc = ImportFixture(files, "old.properties", $"""
        points.count: 2
        point.0={Base64Url("legacy")},1,2,3,40,5
        point.1=6,7,8,90,10
        recording.count=0
        """);
    Equal("legacy", doc.Steps[0].Name); Equal(3, doc.Steps[0].DelayMs); Equal(5, doc.Steps[0].Count);
    Equal(6, doc.Steps[1].X); Equal(8, doc.Steps[1].DelayMs); Equal(10, doc.Steps[1].Count);
    return Task.CompletedTask;
}

static Task RejectUnsupportedLegacy()
{
    using var files = new TemporaryFiles();
    foreach (var (name, content) in new[]
    {
        ("unknown.properties", "points.count=0\nrecording.count=1\nrecording.0=KEY_PRESS,0,0,0,0,65535,0"),
        ("sequential.json", """[{"action_type":"keyboard","keys":"ctrl+a, ctrl+c"}]"""),
        ("zero.json", """{"points":[[1,2,"a"]],"settings":{"loop_count":0}}"""),
        ("unknown.json", """{"points":[],"settings":{"auto_switch_window":true}}""")
    })
    {
        var path = files.PathFor(name); File.WriteAllText(path, content, new UTF8Encoding(false));
        Throws<InvalidDataException>(() => LegacyImporter.Import(path)); Equal(content, File.ReadAllText(path));
    }
    return Task.CompletedTask;
}

static WorkflowDocument ImportFixture(TemporaryFiles files, string name, string content)
{
    var path = files.PathFor(name); File.WriteAllText(path, content, new UTF8Encoding(false)); return LegacyImporter.Import(path);
}
static string Base64Url(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Replace('+', '-').Replace('/', '_');

static ConcurrentQueue<RunProgress> Observe(AutomationEngine engine)
{
    var events = new ConcurrentQueue<RunProgress>(); engine.Progress += events.Enqueue; return events;
}

static async Task CancelAndAssert(AutomationEngine engine, Task task)
{
    var timer = Stopwatch.StartNew(); engine.Stop(); await Finish(task, 1000);
    True(timer.ElapsedMilliseconds < 750, $"cancellation took {timer.ElapsedMilliseconds} ms");
    True(!engine.IsActive, "cancelled engine remained active");
}

static async Task Until(Func<bool> predicate, string failure, int timeoutMs = 2000)
{
    var timer = Stopwatch.StartNew();
    while (!predicate()) { if (timer.ElapsedMilliseconds >= timeoutMs) throw new Exception(failure); await Task.Delay(5); }
}

static async Task Finish(Task task, int timeoutMs = 3000) => await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
static void True(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual, string? message = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message ?? $"Expected {expected}, got {actual}.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
}
static void ThrowsAny(Action action, params Type[] expected)
{
    try { action(); } catch (Exception ex) when (expected.Any(t => t.IsInstanceOfType(ex))) { return; }
    throw new Exception("Expected a validation exception.");
}

sealed record ExecutedAction(ActionStep Step, int OffsetX, int OffsetY);
sealed class FakeDriver : IInputDriver, IInputSuspension
{
    private readonly ConcurrentQueue<ExecutedAction> _executions = new();
    private readonly ConcurrentQueue<string> _lifecycle = new();
    private int _releaseCount, _atCorner;
    public bool ThrowOnExecute { get; init; }
    public bool AtCorner { get => Volatile.Read(ref _atCorner) != 0; set => Volatile.Write(ref _atCorner, value ? 1 : 0); }
    public int ExecutionCount => _executions.Count;
    public int ReleaseCount => Volatile.Read(ref _releaseCount);
    public ExecutedAction[] Executions => _executions.ToArray();
    public string[] Lifecycle => _lifecycle.ToArray();
    public ScreenPoint GetCursorPosition() => new(125, -50);
    public bool IsAtFailSafeCorner() => AtCorner;
    public void Execute(ActionStep step, int offsetX, int offsetY)
    {
        if (ThrowOnExecute) throw new InvalidOperationException("injected input failure");
        _executions.Enqueue(new(step.Clone(), offsetX, offsetY));
    }
    public void ReleaseAll() { _lifecycle.Enqueue("release"); Interlocked.Increment(ref _releaseCount); }
    public void Suspend() => _lifecycle.Enqueue("suspend");
    public void Resume() => _lifecycle.Enqueue("resume");
}

sealed class TemporaryFiles : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClickFlow.Tests", Guid.NewGuid().ToString("N"));
    public TemporaryFiles() => Directory.CreateDirectory(_directory);
    public string PathFor(string name) => Path.Combine(_directory, name);
    public void Dispose()
    {
        // Only this test's uniquely-created temporary directory is removed.
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
