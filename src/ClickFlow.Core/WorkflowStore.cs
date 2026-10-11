using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickFlow.Core;

/// <summary>Versioned, strict JSON storage. Validation finishes before the destination is touched.</summary>
public static class WorkflowStore
{
    internal const long MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static WorkflowDocument Load(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("配置文件超过 16 MB，无法加载。");
        var json = File.ReadAllText(path, Encoding.UTF8);
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
            !parsed.RootElement.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var value) || value != 1)
            throw new InvalidDataException("不支持的配置版本；旧版配置请使用“导入旧版”。");
        RejectDuplicateProperties(parsed.RootElement);
        var document = JsonSerializer.Deserialize<WorkflowDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("配置内容为空。");
        Validate(document);
        return document;
    }

    public static string Serialize(WorkflowDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static void Save(string path, WorkflowDocument document)
    {
        // Serialize first: invalid edits must never overwrite the last known valid file.
        var json = Serialize(document);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            throw new InvalidDataException("配置内容超过 16 MB，无法保存。");
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Both operations are on the same volume. Never fall back to delete + copy.
            if (File.Exists(destination)) File.Replace(temporary, destination, destination + ".bak");
            else File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Preserve the primary exception; only an uncommitted temp remains. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Validate(WorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1) throw new InvalidDataException("仅支持 schemaVersion=1 的配置。");
        if (document.Name is null || document.Theme is not ("Light" or "Dark"))
            throw new InvalidDataException("配置名称或主题无效，主题应为 Light 或 Dark。");
        if (document.Options is null || document.Quick is null || document.Steps is null || document.Recording is null)
            throw new InvalidDataException("配置缺少 options、quick、steps 或 recording。");
        var options = document.Options;
        if (options.RepeatCount is < 0 or > 1_000_000 || options.StartDelayMs is < 0 or > PlanValidator.MaxDelayMs ||
            options.CycleDelayMs is < 0 or > PlanValidator.MaxDelayMs || options.RandomOffset is < 0 or > 1000 || options.RandomDelayMs is < 0 or > 60_000)
            throw new InvalidDataException("运行设置中的次数、延迟或随机范围超出支持范围。");
        if (document.Quick.IntervalMs is < 0 or > PlanValidator.MaxDelayMs || document.Quick.Count is < 0 or > 1_000_000 || !Enum.IsDefined(document.Quick.Button))
            throw new InvalidDataException("快速连点设置无效。");
        ValidateSteps(document.Steps, "步骤");
        ValidateSteps(document.Recording, "录制");
    }

    private static void ValidateSteps(List<ActionStep> steps, string label)
    {
        if (steps.Count > PlanValidator.MaxSteps) throw new InvalidDataException($"{label}数量超过 {PlanValidator.MaxSteps}。");
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step is null || step.Name is null || step.Keys is null ||
                !Enum.IsDefined(step.Kind) || !Enum.IsDefined(step.Button) ||
                step.DelayMs is < 0 or > PlanValidator.MaxDelayMs || step.IntervalMs is < 0 or > PlanValidator.MaxDelayMs || step.Count is < 1 or > 1_000_000)
                throw new InvalidDataException($"{label} {i + 1} 包含无效动作、次数或延迟。");
            if (step.Kind == ActionKind.Hotkey && string.IsNullOrWhiteSpace(step.Keys))
                throw new InvalidDataException($"{label} {i + 1} 的组合键不能为空。");
            if (step.Kind is ActionKind.KeyDown or ActionKind.KeyUp && step.VirtualKey is < 1 or > 254)
                throw new InvalidDataException($"{label} {i + 1} 的 Windows 虚拟键码无效。");
            if (step.Kind is ActionKind.KeyDown or ActionKind.KeyUp && step.VirtualKey is 0x71 or 0x72 or 0x73 or 0x75 or 0x76)
                throw new InvalidDataException($"{label} {i + 1} 使用了应用保留控制键。");
            if (step.Kind == ActionKind.Hotkey && !HotkeyParser.TryParse(step.Keys, out _, out var error))
                throw new InvalidDataException($"{label} {i + 1}：{error}");
            if (step.Kind == ActionKind.Scroll && (step.WheelDelta == 0 || step.WheelDelta is < -12_000 or > 12_000))
                throw new InvalidDataException($"{label} {i + 1} 的滚轮量无效。");
        }
    }

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"JSON 字段重复：{property.Name}。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
