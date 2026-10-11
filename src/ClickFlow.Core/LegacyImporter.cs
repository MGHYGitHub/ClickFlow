using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClickFlow.Core;

/// <summary>Imports the two original applications without executing any input.</summary>
public static class LegacyImporter
{
    public static WorkflowDocument Import(string path)
    {
        if (new FileInfo(path).Length > WorkflowStore.MaximumFileBytes)
            throw new InvalidDataException("旧版配置超过 16 MB，无法导入。");
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
        WorkflowDocument result;
        if (text.TrimStart().StartsWith('[') || text.TrimStart().StartsWith('{'))
            result = ImportPython(text);
        else
            result = ImportJava(File.ReadAllText(path, Encoding.Latin1));
        WorkflowStore.Validate(result);
        // Empty libraries can be imported, but every imported action must be executable.
        foreach (var steps in new[] { result.Steps, result.Recording })
            if (steps.Any(step => step.Enabled) && PlanValidator.Validate(new(steps, result.Options)) is { } error)
                throw new InvalidDataException($"旧版配置无法安全转换：{error}");
        return result;
    }

    private static WorkflowDocument ImportPython(string text)
    {
        using var json = JsonDocument.Parse(text);
        WorkflowStore.RejectDuplicateProperties(json.RootElement);
        var root = json.RootElement;
        var points = root;
        JsonElement settings = default;
        var document = new WorkflowDocument { Name = "Python 旧版导入", Options = new() { CycleDelayMs = 1000 } };
        if (root.ValueKind == JsonValueKind.Object)
        {
            CheckFields(root, ["points", "click_points", "settings", "name"], "配置");
            if (root.TryGetProperty("points", out points) && root.TryGetProperty("click_points", out _))
                throw new InvalidDataException("points 和 click_points 不能同时出现。");
            if (points.ValueKind == JsonValueKind.Undefined && !root.TryGetProperty("click_points", out points))
                throw new InvalidDataException("Python 配置缺少 points/click_points 数组。");
            if (root.TryGetProperty("name", out var name)) document.Name = ReadString(name, "name");
            root.TryGetProperty("settings", out settings);
        }
        if (points.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Python 点位配置必须是数组。");
        if (points.GetArrayLength() > PlanValidator.MaxSteps) throw new InvalidDataException("旧版点位数量过多。");
        if (settings.ValueKind != JsonValueKind.Undefined)
        {
            if (settings.ValueKind != JsonValueKind.Object) throw new InvalidDataException("settings 必须是对象。");
            CheckFields(settings, ["loop_count", "loop", "countdown", "random_offset", "random_offset_px", "random_delay", "base_delay", "click_count", "default_button", "theme"], "settings");
            document.Options.RepeatCount = Int(settings, "loop_count", Int(settings, "loop", 1));
            if (document.Options.RepeatCount < 1)
                throw new InvalidDataException("Python 旧版循环次数必须大于 0；为避免把旧版的零次执行误转成无限循环，导入已取消。");
            document.Options.StartDelayMs = Seconds(settings, "countdown", 3);
            document.Options.RandomOffset = Int(settings, "random_offset_px", Int(settings, "random_offset", 0));
            document.Options.RandomDelayMs = Seconds(settings, "random_delay", 0);
            if (settings.TryGetProperty("random_delay", out var legacyRandomDelay) &&
                double.Parse(legacyRandomDelay.ToString(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("旧版 random_delay 为点击间隔正负随机偏移，新版为动作后附加随机等待，无法等价迁移。请在旧配置副本中将其设为 0，导入后重新设置随机等待。");
            document.Theme = String(settings, "theme", "light").ToLowerInvariant() switch
            { "light" => "Light", "dark" => "Dark", _ => throw new InvalidDataException("旧版主题必须是 light 或 dark。") };
        }
        var defaultDelay = Seconds(settings, "base_delay", 1);
        var defaultCount = Int(settings, "click_count", 1);
        var defaultButton = String(settings, "default_button", "left");
        document.Quick.Count = defaultCount;
        document.Quick.IntervalMs = defaultDelay;
        document.Quick.Button = ParseButton(defaultButton);
        var index = 0;
        foreach (var point in points.EnumerateArray())
        {
            index++;
            var step = new ActionStep { DelayMs = 0, IntervalMs = defaultDelay, Count = defaultCount, Name = $"点位 {index}", Keys = "" };
            var postDelay = defaultDelay;
            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 3)
            {
                step.X = ReadInt(point[0], "x"); step.Y = ReadInt(point[1], "y");
                step.Name = ReadString(point[2], "name"); step.Button = ParseButton(defaultButton);
            }
            else if (point.ValueKind == JsonValueKind.Object)
            {
                CheckFields(point, ["x", "y", "name", "button", "delay", "click_count", "click_interval", "keys", "action_type"], $"点位 {index}");
                step.X = Int(point, "x", 0); step.Y = Int(point, "y", 0);
                step.Name = String(point, "name", step.Name);
                step.Count = Int(point, "click_count", defaultCount);
                step.IntervalMs = Seconds(point, "click_interval", defaultDelay / 1000d);
                postDelay = Seconds(point, "delay", defaultDelay / 1000d);
                var button = String(point, "button", defaultButton);
                var action = String(point, "action_type", button == "keyboard" ? "keyboard" : "click");
                if (action == "keyboard")
                {
                    step.Kind = ActionKind.Hotkey;
                    step.Keys = String(point, "keys", "");
                    if (!HotkeyParser.TryParse(step.Keys, out _, out var error))
                        throw new InvalidDataException($"点位 {index} 的键盘组合不能转换：{error} 请将顺序组合拆成多个步骤。");
                    // The original keyboard branch did not apply click_interval.
                    step.IntervalMs = 0;
                }
                else if (action == "click") step.Button = ParseButton(button);
                else throw new InvalidDataException($"点位 {index} 的 action_type“{action}”不受支持。");
            }
            else throw new InvalidDataException($"点位 {index} 格式无效，应为三元素数组或对象。");
            document.Steps.Add(step);
            // Python delay was AFTER the step and was ignored on the final point.
            if (index < points.GetArrayLength() && postDelay > 0)
                document.Steps.Add(new() { Name = $"等待 · {step.Name}之后", Kind = ActionKind.Wait, DelayMs = 0, IntervalMs = postDelay });
        }
        return document;
    }

    private static WorkflowDocument ImportJava(string text)
    {
        var p = ReadProperties(text);
        if (!p.ContainsKey("points.count") && !p.ContainsKey("recording.count"))
            throw new InvalidDataException("这不是已识别的 Java 配置：缺少 points.count/recording.count。");
        if (Get(p, "version", "1") != "1") throw new InvalidDataException("不支持的 Java 配置版本。");
        var document = new WorkflowDocument
        {
            Name = "Java 旧版导入",
            Options = new() { RepeatCount = PropertyInt(p, "task.cycleLimit", 0), StartDelayMs = 0, CycleDelayMs = 0 },
            Quick = new() { IntervalMs = PropertyInt(p, "default.intervalMs", 100), Count = PropertyInt(p, "default.clicks", 1) }
        };
        // default.startDelayMs controls future point creation in Java, not task startup.
        // Existing points carry their own delay. Keep that template value in an inert disabled row.
        var templateDelay = PropertyInt(p, "default.startDelayMs", 0);
        if (templateDelay < 0) throw new InvalidDataException("default.startDelayMs 不能为负数。");
        var pointCount = PropertyInt(p, "points.count", 0);
        var recordingCount = PropertyInt(p, "recording.count", 0);
        if (pointCount is < 0 or > PlanValidator.MaxSteps || recordingCount is < 0 or > PlanValidator.MaxSteps)
            throw new InvalidDataException("Java 点位/录制数量超出允许范围。");
        var allowed = new HashSet<string>(["version", "default.startDelayMs", "default.intervalMs", "default.clicks", "task.cycleLimit", "points.count", "recording.count"]);
        for (var i = 0; i < pointCount; i++)
        {
            var key = $"point.{i}"; allowed.Add(key);
            var parts = Required(p, key).Split(',');
            if (parts.Length is not (5 or 6 or 9)) throw new InvalidDataException($"{key} 应为 5、6 或 9 个字段。");
            var shift = parts.Length == 5 ? 0 : 1;
            var step = new ActionStep
            {
                Name = shift == 0 ? $"点位 {i + 1}" : DecodeBase64(parts[0], key),
                X = Number(parts[shift], key), Y = Number(parts[shift + 1], key),
                DelayMs = Number(parts[shift + 2], key), IntervalMs = Number(parts[shift + 3], key), Count = Number(parts[shift + 4], key), Keys = ""
            };
            if (parts.Length == 9)
            {
                step.Kind = parts[6] switch
                { "LEFT_CLICK" or "RIGHT_CLICK" => ActionKind.Click, "WHEEL" => ActionKind.Scroll, "HOTKEY" => ActionKind.Hotkey, _ => throw new InvalidDataException($"{key} 动作类型不受支持：{parts[6]}。") };
                step.Button = parts[6] == "RIGHT_CLICK" ? PointerButton.Right : PointerButton.Left;
                step.WheelDelta = WheelDelta(Number(parts[7], key));
                step.Keys = DecodeBase64(parts[8], key);
                if (step.Kind == ActionKind.Hotkey && !HotkeyParser.TryParse(step.Keys, out _, out var error))
                    throw new InvalidDataException($"{key} 的组合键不能转换：{error}");
            }
            if (step.Kind == ActionKind.Hotkey)
            {
                // Java Robot moved the pointer before EACH hotkey repetition. Keep those moves
                // explicit so migration never relies on a hotkey driver's incidental behavior.
                if (step.Count < 1 || (long)document.Steps.Count + (long)step.Count * 2 > PlanValidator.MaxSteps)
                    throw new InvalidDataException($"{key} 展开移动和组合键后超过 {PlanValidator.MaxSteps} 个动作，无法完整迁移。");
                if (step.DelayMs < 0 || step.IntervalMs < 0)
                    throw new InvalidDataException($"{key} 延迟不能为负数。");
                for (var repeat = 0; repeat < step.Count; repeat++)
                {
                    document.Steps.Add(new() { Name = $"定位 · {step.Name}", Kind = ActionKind.Move, X = step.X, Y = step.Y, DelayMs = repeat == 0 ? step.DelayMs : Math.Max(10, step.IntervalMs), IntervalMs = 0 });
                    var hotkey = step.Clone(); hotkey.Count = 1; hotkey.DelayMs = 0; hotkey.IntervalMs = 0;
                    document.Steps.Add(hotkey);
                }
            }
            else document.Steps.Add(step);
        }
        for (var i = 0; i < recordingCount; i++)
        {
            var key = $"recording.{i}"; allowed.Add(key);
            var parts = Required(p, key).Split(',');
            if (parts.Length is < 5 or > 7) throw new InvalidDataException($"{key} 应为 5 至 7 个字段。");
            var step = new ActionStep
            {
                Name = $"录制 {i + 1} · {parts[0]}", X = Number(parts[1], key), Y = Number(parts[2], key),
                DelayMs = Number(parts[4], key), IntervalMs = 0, Keys = "",
                Kind = parts[0] switch
                { "MOVE" => ActionKind.Move, "PRESS" => ActionKind.MouseDown, "RELEASE" => ActionKind.MouseUp, "WHEEL" => ActionKind.Scroll, "KEY_PRESS" => ActionKind.KeyDown, "KEY_RELEASE" => ActionKind.KeyUp, _ => throw new InvalidDataException($"{key} 动作类型未知：{parts[0]}。") }
            };
            if (step.Kind is ActionKind.MouseDown or ActionKind.MouseUp)
                step.Button = Number(parts[3], key) switch
                { 1 => PointerButton.Left, 2 => PointerButton.Middle, 3 => PointerButton.Right, _ => throw new InvalidDataException($"{key} 使用不支持的鼠标侧键，无法安全迁移。") };
            if (step.Kind is ActionKind.KeyDown or ActionKind.KeyUp)
                step.VirtualKey = parts.Length >= 6 ? NativeKeyToVirtualKey(Number(parts[5], key)) : throw new InvalidDataException($"{key} 缺少原生键码。");
            if (step.Kind == ActionKind.Scroll)
                step.WheelDelta = parts.Length == 7 ? WheelDelta(Number(parts[6], key)) : throw new InvalidDataException($"{key} 缺少滚轮量。");
            document.Recording.Add(step);
        }
        foreach (var key in p.Keys)
            if (!allowed.Contains(key)) throw new InvalidDataException($"无法保留未知 Java 配置字段“{key}”；导入已取消，原文件未修改。");
        if (templateDelay > 0)
            document.Steps.Add(new() { Name = "旧版新增点位默认等待（仅供参考）", Kind = ActionKind.Wait, Enabled = false, DelayMs = 0, IntervalMs = templateDelay });
        return document;
    }

    // Verified against jnativehook 2.2.2 NativeKeyEvent.java. These are NOT Windows VK values.
    private static int NativeKeyToVirtualKey(int code)
    {
        int[] letters = [0x1e, 0x30, 0x2e, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1f, 0x14, 0x16, 0x2f, 0x11, 0x2d, 0x15, 0x2c];
        var letter = Array.IndexOf(letters, code);
        if (letter >= 0) return 0x41 + letter;
        if (code is >= 0x02 and <= 0x0A) return 0x31 + code - 0x02;
        if (code == 0x0B) return 0x30;
        var vk = code switch
        {
            >= 0x3B and <= 0x44 => 0x70 + code - 0x3B, 0x57 => 0x7A, 0x58 => 0x7B,
            >= 0x5B and <= 0x5D => 0x7C + code - 0x5B, >= 0x63 and <= 0x6B => 0x7F + code - 0x63,
            0x01 => 0x1B, 0x0E => 0x08, 0x0F => 0x09, 0x1C => 0x0D, 0x39 => 0x20,
            0x2A => 0x10, 0x1D => 0x11, 0x38 => 0x12, 0x0E5B => 0x5B, 0x0E5D => 0x5D,
            0x3A => 0x14, 0x45 => 0x90, 0x46 => 0x91, 0x0E37 => 0x2C, 0x0E45 => 0x13,
            0x0E52 => 0x2D, 0x0E53 => 0x2E, 0x0E47 => 0x24, 0x0E4F => 0x23, 0x0E49 => 0x21, 0x0E51 => 0x22,
            0xE048 => 0x26, 0xE04B => 0x25, 0xE04C => 0x0C, 0xE04D => 0x27, 0xE050 => 0x28,
            0x0C => 0xBD, 0x0D => 0xBB, 0x1A => 0xDB, 0x1B => 0xDD, 0x2B => 0xDC,
            0x27 => 0xBA, 0x28 => 0xDE, 0x29 => 0xC0, 0x33 => 0xBC, 0x34 => 0xBE, 0x35 => 0xBF,
            0xE022 => 0xB3, 0xE024 => 0xB2, 0xE010 => 0xB1, 0xE019 => 0xB0,
            0xE020 => 0xAD, 0xE030 => 0xAF, 0xE02E => 0xAE,
            _ => throw new InvalidDataException($"JNativeHook 键码 0x{code:X} 无可靠的 Windows 映射，导入已取消。")
        };
        if (vk is 0x71 or 0x72 or 0x73 or 0x75 or 0x76)
            throw new InvalidDataException("旧录制包含本程序控制键 F2/F3/F4/F6/F7，无法安全回放，请先在旧配置副本中移除对应事件。");
        return vk;
    }

    private static PointerButton ParseButton(string value) => value.ToLowerInvariant() switch
    { "left" => PointerButton.Left, "right" => PointerButton.Right, "middle" => PointerButton.Middle, _ => throw new InvalidDataException($"不支持的鼠标键：{value}。") };
    private static int WheelDelta(int steps)
    { try { return checked(-120 * steps); } catch (OverflowException) { throw new InvalidDataException("滚轮量超出范围。"); } }
    private static string String(JsonElement item, string key, string fallback) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? ReadString(value, key) : fallback;
    private static int Int(JsonElement item, string key, int fallback) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? ReadInt(value, key) : fallback;
    private static int ReadInt(JsonElement value, string key) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : value.ValueKind == JsonValueKind.String ? Number(value.GetString()!, key) : throw new InvalidDataException($"{key} 必须为整数。");
    private static string ReadString(JsonElement value, string key) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException($"{key} 必须为字符串。");
    private static int Seconds(JsonElement item, string key, double fallback)
    {
        var seconds = fallback;
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) seconds = number;
            else if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) seconds = number;
            else throw new InvalidDataException($"{key} 必须为秒数。");
        }
        if (!double.IsFinite(seconds) || seconds < 0 || seconds * 1000 > PlanValidator.MaxDelayMs) throw new InvalidDataException($"{key} 应为 0 至 86400 秒。");
        return checked((int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
    }
    private static void CheckFields(JsonElement item, string[] allowed, string context)
    {
        foreach (var property in item.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw new InvalidDataException($"无法保留 {context} 中的未知字段“{property.Name}”；导入已取消，原文件未修改。");
    }
    private static string DecodeBase64(string text, string key)
    {
        try
        {
            text = text.Replace('-', '+').Replace('_', '/');
            text = text.PadRight((text.Length + 3) / 4 * 4, '=');
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text));
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException) { throw new InvalidDataException($"{key} 的 Base64Url 文本无效。", e); }
    }
    private static int Number(string text, string key) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException($"{key} 包含无效整数“{text}”。");
    private static string Get(Dictionary<string, string> p, string key, string fallback) => p.GetValueOrDefault(key, fallback);
    private static string Required(Dictionary<string, string> p, string key) => p.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"配置缺少 {key}。");
    private static int PropertyInt(Dictionary<string, string> p, string key, int fallback) => p.TryGetValue(key, out var value) ? Number(value, key) : fallback;

    private static Dictionary<string, string> ReadProperties(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        string? physical;
        var logical = new StringBuilder();
        while ((physical = reader.ReadLine()) is not null)
        {
            var line = physical.TrimStart(' ', '\t', '\f');
            if (logical.Length == 0 && (line.Length == 0 || line[0] is '#' or '!')) continue;
            logical.Append(line);
            var slashes = 0;
            for (var i = logical.Length - 1; i >= 0 && logical[i] == '\\'; i--) slashes++;
            if (slashes % 2 == 1) { logical.Length--; continue; }
            ParseLine(logical.ToString()); logical.Clear();
        }
        if (logical.Length > 0) ParseLine(logical.ToString());
        return result;

        void ParseLine(string line)
        {
            var separator = 0;
            for (; separator < line.Length; separator++)
            {
                if (line[separator] == '\\') { separator++; continue; }
                if (line[separator] is '=' or ':' or ' ' or '\t' or '\f') break;
            }
            var valueAt = separator;
            while (valueAt < line.Length && line[valueAt] is ' ' or '\t' or '\f') valueAt++;
            if (valueAt < line.Length && line[valueAt] is '=' or ':') valueAt++;
            while (valueAt < line.Length && line[valueAt] is ' ' or '\t' or '\f') valueAt++;
            var key = Unescape(line[..Math.Min(separator, line.Length)]);
            if (!result.TryAdd(key, Unescape(line[valueAt..]))) throw new InvalidDataException($"Java 配置字段重复：{key}。");
        }
    }

    private static string Unescape(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\') { result.Append(c); continue; }
            if (++i >= text.Length) throw new InvalidDataException("Java 配置存在不完整转义。");
            c = text[i];
            if (c == 'u')
            {
                if (i + 4 >= text.Length || !ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    throw new InvalidDataException("Java 配置存在无效 Unicode 转义。");
                result.Append((char)code); i += 4;
            }
            else result.Append(c switch { 't' => '\t', 'n' => '\n', 'r' => '\r', 'f' => '\f', _ => c });
        }
        return result.ToString();
    }
}
