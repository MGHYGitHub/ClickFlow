using System.Diagnostics;

namespace ClickFlow.Core;

/// <summary>Optional support for releasing a held drag/key during pause and restoring it on resume.</summary>
public interface IInputSuspension
{
    void Suspend();
    void Resume();
}

/// <summary>One serialized run. Every wait is cancellable and excludes paused time.</summary>
public sealed class AutomationEngine(IInputDriver input) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private bool _paused, _disposed;
    private long _completed, _cycle;
    private int _stepIndex = -1;
    private long _lastProgressTick;
    private readonly Stopwatch _elapsed = new();
    public event Action<RunProgress>? Progress;
    public bool IsActive { get { lock (_gate) return _stop is not null; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }

    public Task RunAsync(ExecutionPlan plan)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop is not null) throw new InvalidOperationException("已有任务正在运行，请先停止当前任务。");
            var error = PlanValidator.Validate(plan);
            if (error is not null) throw new InvalidOperationException(error);
            // Editing the UI or source document must never alter a running task.
            var snapshot = new ExecutionPlan(plan.Steps.Select(s => s.Clone()).ToArray(), plan.Options.Clone());
            _stop = new CancellationTokenSource(); _paused = false;
            var source = _stop;
            return Task.Run(() => RunCoreAsync(snapshot, source));
        }
    }

    public void TogglePause()
    {
        lock (_gate) { if (_stop is null || _stop.IsCancellationRequested) return; _paused = !_paused; }
    }

    public void Stop()
    {
        lock (_gate) { _stop?.Cancel(); _paused = false; }
    }

    private async Task RunCoreAsync(ExecutionPlan plan, CancellationTokenSource source)
    {
        var token = source.Token;
        var options = plan.Options;
        var state = EngineState.Completed;
        var message = "任务已完成";
        _completed = 0; _cycle = 0; _stepIndex = -1; _lastProgressTick = 0; _elapsed.Restart();
        try
        {
            if (options.StartDelayMs > 0)
                await WaitAsync(options.StartDelayMs, options, token, EngineState.Countdown, "即将开始");
            for (_cycle = 1; options.RepeatCount == 0 || _cycle <= options.RepeatCount; _cycle++)
            {
                var cycleStarted = Stopwatch.GetTimestamp();
                for (_stepIndex = 0; _stepIndex < plan.Steps.Count; _stepIndex++)
                {
                    var step = plan.Steps[_stepIndex];
                    if (!step.Enabled) continue;
                    await WaitAsync(step.DelayMs, options, token, EngineState.Running, $"等待 · {step.Name}");
                    for (var repeat = 0; repeat < step.Count; repeat++)
                    {
                        await ReadyAsync(options, token);
                        if (step.Kind == ActionKind.Wait)
                            await WaitAsync(step.IntervalMs, options, token, EngineState.Running, step.Name);
                        else
                        {
                            var offset = options.RandomOffset;
                            input.Execute(step, offset == 0 ? 0 : Random.Shared.Next(-offset, offset + 1), offset == 0 ? 0 : Random.Shared.Next(-offset, offset + 1));
                        }
                        _completed++;
                        Report(EngineState.Running, step.Name);
                        // Recorded single events have no extra interval: their DelayMs already preserves timing.
                        var duration = repeat < step.Count - 1 && step.Kind != ActionKind.Wait ? Math.Max(10, step.IntervalMs) : 0;
                        if (repeat < step.Count - 1 && step.Kind == ActionKind.Wait && step.IntervalMs < 10) duration = 10 - step.IntervalMs;
                        if (options.RandomDelayMs > 0) duration += Random.Shared.Next(options.RandomDelayMs + 1);
                        if (duration > 0) await WaitAsync(duration, options, token, EngineState.Running, step.Name);
                        else if (_completed % 100 == 0) await Task.Yield();
                    }
                }
                if (options.RepeatCount == 0 || _cycle < options.RepeatCount)
                {
                    // Guard only a genuinely empty/zero-delay cycle; do not add 10 ms to normal playback.
                    var minimum = (int)Math.Max(0, 10 - Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds);
                    await WaitAsync(Math.Max(minimum, options.CycleDelayMs), options, token, EngineState.Running, "循环间隔");
                }
            }
            if (options.RepeatCount > 0) _cycle = options.RepeatCount;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { state = EngineState.Stopped; message = "任务已停止"; }
        catch (FailSafeException) { state = EngineState.Stopped; message = "鼠标移至屏幕角落，已紧急停止"; }
        catch (Exception ex) { state = EngineState.Faulted; message = ex.Message; }
        finally
        {
            try { input.ReleaseAll(); }
            catch (Exception ex) { state = EngineState.Faulted; message = "释放输入失败：" + ex.Message; }
            _elapsed.Stop();
            lock (_gate)
            {
                _paused = false; _stop = null; source.Dispose();
                Report(state, message, true);
            }
        }
    }

    private async Task ReadyAsync(RunOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckCorner(options);
        var suspended = false;
        while (IsPaused)
        {
            _elapsed.Stop();
            if (!suspended && input is IInputSuspension suspension) { suspension.Suspend(); suspended = true; }
            Report(EngineState.Paused, "已暂停 · F4 继续");
            await Task.Delay(20, token);
            CheckCorner(options);
        }
        token.ThrowIfCancellationRequested();
        if (suspended && input is IInputSuspension restorable)
        {
            lock (_gate) { token.ThrowIfCancellationRequested(); restorable.Resume(); }
        }
        _elapsed.Start();
    }

    private async Task WaitAsync(int milliseconds, RunOptions options, CancellationToken token, EngineState state, string message)
    {
        double remaining = milliseconds;
        do
        {
            await ReadyAsync(options, token);
            Report(state, state == EngineState.Countdown ? $"{message} · {Math.Ceiling(remaining / 1000):0} 秒" : message);
            if (remaining <= 0) break;
            var slice = Math.Min(25, remaining);
            var started = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromMilliseconds(slice), token);
            // Never charge a pause against a countdown, interval or recorded delay.
            if (!IsPaused) remaining -= Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        } while (remaining > 0);
        await ReadyAsync(options, token);
    }

    private void CheckCorner(RunOptions options)
    {
        if (options.CornerFailSafe && input.IsAtFailSafeCorner()) throw new FailSafeException();
    }

    private void Report(EngineState state, string message, bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && _lastProgressTick != 0 && Stopwatch.GetElapsedTime(_lastProgressTick, now).TotalMilliseconds < 50) return;
        _lastProgressTick = now;
        var progress = new RunProgress(state, _completed, _cycle, _stepIndex, message, _elapsed.Elapsed);
        // Observers cannot interrupt input cleanup or corrupt engine state.
        if (Progress is { } observers)
            foreach (Action<RunProgress> observer in observers.GetInvocationList()) { try { observer(progress); } catch { } }
    }

    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _stop?.Cancel(); _paused = false; } }
    private sealed class FailSafeException : Exception;
}
