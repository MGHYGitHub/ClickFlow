namespace ClickFlow.Core;

public static class PlanValidator
{
    public const int MaxSteps = 50_000;
    public const int MaxDelayMs = 86_400_000;

    public static string? Validate(ExecutionPlan plan)
    {
        if (plan is null || plan.Options is null || plan.Steps is null) return "任务配置不完整。";
        if (plan.Steps.Count is 0 or > MaxSteps) return $"动作数量必须为 1–{MaxSteps:N0}。";
        var options = plan.Options;
        if (options.RepeatCount is < 0 or > 1_000_000) return "循环次数应为 0–1,000,000（0 表示无限循环）。";
        if (options.StartDelayMs is < 0 or > MaxDelayMs || options.CycleDelayMs is < 0 or > MaxDelayMs) return "启动及循环等待应在 0–24 小时之间。";
        if (options.RandomOffset is < 0 or > 1000) return "随机位置偏移应为 0–1,000 像素。";
        if (options.RandomDelayMs is < 0 or > 60_000) return "随机附加等待应为 0–60,000 毫秒。";
        var enabled = false;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var prefix = $"第 {i + 1} 个动作：";
            if (step is null) return prefix + "动作不能为空。";
            if (!Enum.IsDefined(step.Kind) || !Enum.IsDefined(step.Button)) return prefix + "动作类型或鼠标按键无效。";
            if (step.DelayMs is < 0 or > MaxDelayMs || step.IntervalMs is < 0 or > MaxDelayMs) return prefix + "等待时间应在 0–24 小时之间。";
            if (step.Count is < 1 or > 1_000_000) return prefix + "重复次数应为 1–1,000,000。";
            if (step.X is < -1_000_000 or > 1_000_000 || step.Y is < -1_000_000 or > 1_000_000) return prefix + "坐标超出支持范围。";
            if (step.Kind == ActionKind.Scroll && (step.WheelDelta == 0 || step.WheelDelta is < -12_000 or > 12_000)) return prefix + "滚轮量应为非零的 -12,000 至 12,000。";
            if (step.Kind is ActionKind.KeyDown or ActionKind.KeyUp && step.VirtualKey is < 1 or > 254) return prefix + "键盘虚拟键值应为 1–254。";
            if (step.Kind is ActionKind.KeyDown or ActionKind.KeyUp && step.VirtualKey is 0x71 or 0x72 or 0x73 or 0x75 or 0x76) return prefix + "F2 / F3 / F4 / F6 / F7 为应用控制键，不能作为回放按键。";
            if (step.Kind == ActionKind.Hotkey && !HotkeyParser.TryParse(step.Keys, out _, out var error)) return prefix + error;
            enabled |= step.Enabled;
        }
        return enabled ? null : "至少启用一个动作。";
    }
}

/// <summary>Parses portable Windows virtual-key names without depending on a UI framework.</summary>
public static class HotkeyParser
{
    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"]=0x11, ["CONTROL"]=0x11, ["SHIFT"]=0x10, ["ALT"]=0x12,
        ["WIN"]=0x5B, ["WINDOWS"]=0x5B, ["ENTER"]=0x0D, ["RETURN"]=0x0D,
        ["TAB"]=0x09, ["SPACE"]=0x20, ["ESC"]=0x1B, ["ESCAPE"]=0x1B,
        ["BACKSPACE"]=0x08, ["DELETE"]=0x2E, ["DEL"]=0x2E, ["INSERT"]=0x2D,
        ["HOME"]=0x24, ["END"]=0x23, ["PAGEUP"]=0x21, ["PAGEDOWN"]=0x22,
        ["LEFT"]=0x25, ["UP"]=0x26, ["RIGHT"]=0x27, ["DOWN"]=0x28,
        ["CAPSLOCK"]=0x14, ["NUMLOCK"]=0x90, ["SCROLLLOCK"]=0x91,
        ["PRINTSCREEN"]=0x2C, ["PAUSE"]=0x13, ["PLUS"]=0xBB, ["MINUS"]=0xBD,
        ["COMMA"]=0xBC, ["PERIOD"]=0xBE, ["SEMICOLON"]=0xBA, ["SLASH"]=0xBF,
        ["BACKSLASH"]=0xDC, ["QUOTE"]=0xDE, ["LBRACKET"]=0xDB, ["RBRACKET"]=0xDD,
        ["NUMPADADD"]=0x6B, ["NUMPADSUBTRACT"]=0x6D, ["NUMPADMULTIPLY"]=0x6A,
        ["NUMPADDIVIDE"]=0x6F, ["NUMPADDECIMAL"]=0x6E
    };

    public static bool TryParse(string? text, out int[] keys, out string? error)
    {
        keys = []; error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128) { error = "请输入有效的快捷键，例如 Ctrl+C。"; return false; }
        var parsed = new List<int>();
        foreach (var part in text.Split('+'))
        {
            var token = part.Trim().ToUpperInvariant();
            int key;
            if (Names.TryGetValue(token, out var named)) key = named;
            else if (token.Length == 1 && (token[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) key = token[0];
            else if (token.StartsWith('F') && int.TryParse(token.AsSpan(1), out var fn) && fn is >= 1 and <= 24) key = 0x70 + fn - 1;
            else if (token.StartsWith("NUMPAD") && token.Length == 7 && token[6] is >= '0' and <= '9') key = 0x60 + token[6] - '0';
            else { error = $"不支持的键名“{part}”。"; return false; }
            if (parsed.Contains(key)) { error = "快捷键不能包含重复按键。"; return false; }
            parsed.Add(key);
        }
        if (parsed.Count > 6) { error = "快捷键最多包含 6 个按键。"; return false; }
        if (parsed.Any(k => k is 0x71 or 0x72 or 0x73 or 0x75 or 0x76)) { error = "F2 / F3 / F4 / F6 / F7 为应用控制键，请选择其他按键。"; return false; }
        // Modifiers always go down before ordinary keys, regardless of input order.
        keys = parsed.OrderBy(k => k is 0x10 or 0x11 or 0x12 or 0x5B ? 0 : 1).ToArray();
        return true;
    }
}
