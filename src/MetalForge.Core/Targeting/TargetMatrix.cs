using System.Text.Json.Nodes;

namespace MetalForge.Core.Targeting;

/// <summary>
/// 一种目标架构的配置。字段与 <c>assets/targets/architectures.json</c> 对应。
/// </summary>
public sealed record ArchitectureDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>GCC/LLVM 的目标三元组，例如 <c>x86_64-elf</c>。</summary>
    public required string TargetTriple { get; init; }

    /// <summary>同一架构的其他可用三元组（例如 EFI 场景用 mingw）。</summary>
    public IReadOnlyList<string> AlternativeTargetTriples { get; init; } = [];

    /// <summary>首选工具链 id，对应 <c>tools.json</c> 里的分组或工具链 profile。</summary>
    public required string PreferredToolchainId { get; init; }

    public IReadOnlyList<string> AlternativeToolchainIds { get; init; } = [];

    public required int Bitness { get; init; }

    /// <summary><c>little</c> 或 <c>big</c>。</summary>
    public required string Endianness { get; init; }

    public required string DefaultBootMethodId { get; init; }

    /// <summary>该架构支持的引导方式 id；必须都能在引导矩阵里找到。</summary>
    public IReadOnlyList<string> SupportedBootMethodIds { get; init; } = [];

    public string? DefaultArtifactKind { get; init; }

    public required string QemuSystemExecutable { get; init; }

    public string? QemuMachine { get; init; }

    public string? QemuCpu { get; init; }

    /// <summary>必需编译选项。这些是"写错就会在运行时炸"的参数，因此直接给出。</summary>
    public IReadOnlyList<string> RequiredCompilerFlags { get; init; } = [];

    /// <summary>ABI 备忘（面向用户，解释为什么需要某些参数）。</summary>
    public string? AbiNotes { get; init; }
}

/// <summary>
/// 一种引导方式的配置。字段与 <c>assets/targets/boot-methods.json</c> 对应。
/// </summary>
public sealed record BootMethodDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>产物类型：efi / iso / diskImage / elf / flatBinary / firmware。</summary>
    public required string ArtifactKind { get; init; }

    public bool RequiresFirmware { get; init; }

    public string? FirmwareKind { get; init; }

    public IReadOnlyList<string> RequiredTools { get; init; } = [];

    public IReadOnlyList<string> SupportedArchitectureIds { get; init; } = [];

    public string? LinkerScriptTemplate { get; init; }

    /// <summary>QEMU 参数模板；占位符见 <see cref="QemuArgumentPlaceholders"/>。</summary>
    public IReadOnlyList<string> QemuArguments { get; init; } = [];

    public string? Notes { get; init; }

    /// <summary>参数模板里支持的占位符。</summary>
    public static IReadOnlyList<string> QemuArgumentPlaceholders { get; } =
    [
        "{artifact}", "{firmwareCode}", "{firmwareVarsCopy}", "{firmwareRom}", "{ubootBinary}",
    ];
}

/// <summary>架构与引导方式的组合是否可用。</summary>
/// <param name="IsValid">是否可用。</param>
/// <param name="Reason">不可用时的原因（面向用户）。</param>
public readonly record struct TargetCombination(bool IsValid, string? Reason)
{
    public static TargetCombination Valid { get; } = new(true, null);

    public static TargetCombination Invalid(string reason) => new(false, reason);
}

/// <summary>
/// 目标矩阵：架构 × 引导方式。它是"新建项目向导"的依据，
/// 也是构建参数生成的输入。
/// </summary>
public sealed class TargetMatrix
{
    private readonly Dictionary<string, ArchitectureDefinition> _architectures;
    private readonly Dictionary<string, BootMethodDefinition> _bootMethods;

    public TargetMatrix(IReadOnlyList<ArchitectureDefinition> architectures, IReadOnlyList<BootMethodDefinition> bootMethods)
    {
        ArgumentNullException.ThrowIfNull(architectures);
        ArgumentNullException.ThrowIfNull(bootMethods);

        Architectures = architectures;
        BootMethods = bootMethods;

        _architectures = architectures.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        _bootMethods = bootMethods.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ArchitectureDefinition> Architectures { get; }

    public IReadOnlyList<BootMethodDefinition> BootMethods { get; }

    public ArchitectureDefinition? FindArchitecture(string? id)
        => id is not null && _architectures.TryGetValue(id, out var architecture) ? architecture : null;

    public BootMethodDefinition? FindBootMethod(string? id)
        => id is not null && _bootMethods.TryGetValue(id, out var method) ? method : null;

    /// <summary>
    /// 判断某个架构 + 引导方式组合是否成立。
    /// 两个矩阵是分开维护的，因此"架构声称支持某引导方式"与"该引导方式声称支持该架构"
    /// 可能不一致 —— 这里要求**双方都认可**，避免向导里出现选了就报错的组合。
    /// </summary>
    public TargetCombination Evaluate(string? architectureId, string? bootMethodId)
    {
        var architecture = FindArchitecture(architectureId);
        if (architecture is null)
        {
            return TargetCombination.Invalid($"未知架构：{architectureId ?? "(空)"}");
        }

        var bootMethod = FindBootMethod(bootMethodId);
        if (bootMethod is null)
        {
            return TargetCombination.Invalid($"未知引导方式：{bootMethodId ?? "(空)"}");
        }

        if (!architecture.SupportedBootMethodIds.Contains(bootMethod.Id, StringComparer.OrdinalIgnoreCase))
        {
            return TargetCombination.Invalid(
                $"架构 {architecture.DisplayName} 不支持引导方式 {bootMethod.DisplayName}。");
        }

        if (!bootMethod.SupportedArchitectureIds.Contains(architecture.Id, StringComparer.OrdinalIgnoreCase))
        {
            return TargetCombination.Invalid(
                $"引导方式 {bootMethod.DisplayName} 未声明支持架构 {architecture.DisplayName}；"
                + "两个矩阵不一致，请检查 assets/targets/ 下的配置。");
        }

        return TargetCombination.Valid;
    }

    /// <summary>某个架构可用的引导方式（经过双向确认）。</summary>
    public IReadOnlyList<BootMethodDefinition> AvailableBootMethods(ArchitectureDefinition architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);

        return
        [
            .. BootMethods.Where(method =>
                architecture.SupportedBootMethodIds.Contains(method.Id, StringComparer.OrdinalIgnoreCase)
                && method.SupportedArchitectureIds.Contains(architecture.Id, StringComparer.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>
    /// 一致性校验：把"配置里写错但不会立刻报错"的问题找出来。
    /// 在加载时执行一次，结果作为诊断呈现给用户。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var architecture in Architectures)
        {
            if (!_bootMethods.ContainsKey(architecture.DefaultBootMethodId))
            {
                problems.Add(
                    $"架构 '{architecture.Id}' 的 defaultBootMethodId='{architecture.DefaultBootMethodId}' "
                    + "在引导方式矩阵中不存在。");
            }

            foreach (var bootMethodId in architecture.SupportedBootMethodIds)
            {
                if (!_bootMethods.TryGetValue(bootMethodId, out var method))
                {
                    problems.Add($"架构 '{architecture.Id}' 声明支持未知引导方式 '{bootMethodId}'。");
                    continue;
                }

                if (!method.SupportedArchitectureIds.Contains(architecture.Id, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"不一致：架构 '{architecture.Id}' 声明支持 '{bootMethodId}'，"
                        + $"但 '{bootMethodId}' 未声明支持 '{architecture.Id}'。");
                }
            }
        }

        foreach (var method in BootMethods)
        {
            foreach (var architectureId in method.SupportedArchitectureIds)
            {
                if (!_architectures.TryGetValue(architectureId, out var architecture))
                {
                    problems.Add($"引导方式 '{method.Id}' 声明支持未知架构 '{architectureId}'。");
                    continue;
                }

                if (!architecture.SupportedBootMethodIds.Contains(method.Id, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"不一致：引导方式 '{method.Id}' 声明支持 '{architectureId}'，"
                        + $"但 '{architectureId}' 未声明支持 '{method.Id}'。");
                }
            }
        }

        return problems;
    }
}

/// <summary>
/// 从已解析的 JSON 读取架构与引导方式定义。
/// 与工具清单同构：字段缺失的条目被跳过（Schema 校验会给出精确位置）。
/// </summary>
public static class TargetMatrixReader
{
    /// <summary>读取架构定义。</summary>
    public static IReadOnlyList<ArchitectureDefinition> ReadArchitectures(JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root["architectures"] is not JsonArray items)
        {
            return [];
        }

        var result = new List<ArchitectureDefinition>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in items)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var id = ReadString(entry, "id");
            var displayName = ReadString(entry, "displayName");
            var targetTriple = ReadString(entry, "targetTriple");
            var toolchainId = ReadString(entry, "preferredToolchainId");
            var bootMethodId = ReadString(entry, "defaultBootMethodId");
            var qemuExecutable = ReadString(entry, "qemuSystemExecutable");

            if (id is null || displayName is null || targetTriple is null || toolchainId is null
                || bootMethodId is null || qemuExecutable is null)
            {
                continue;
            }

            if (!seen.Add(id))
            {
                continue;
            }

            result.Add(new ArchitectureDefinition
            {
                Id = id,
                DisplayName = displayName,
                Description = ReadString(entry, "description"),
                TargetTriple = targetTriple,
                AlternativeTargetTriples = ReadStringArray(entry, "alternativeTargetTriples"),
                PreferredToolchainId = toolchainId,
                AlternativeToolchainIds = ReadStringArray(entry, "alternativeToolchainIds"),
                Bitness = ReadInt(entry, "bitness", 64),
                Endianness = ReadString(entry, "endianness") ?? "little",
                DefaultBootMethodId = bootMethodId,
                SupportedBootMethodIds = ReadStringArray(entry, "supportedBootMethodIds"),
                DefaultArtifactKind = ReadString(entry, "defaultArtifactKind"),
                QemuSystemExecutable = qemuExecutable,
                QemuMachine = ReadString(entry, "qemuMachine"),
                QemuCpu = ReadString(entry, "qemuCpu"),
                RequiredCompilerFlags = ReadStringArray(entry, "requiredCompilerFlags"),
                AbiNotes = ReadString(entry, "abiNotes"),
            });
        }

        return result;
    }

    /// <summary>读取引导方式定义。</summary>
    public static IReadOnlyList<BootMethodDefinition> ReadBootMethods(JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root["bootMethods"] is not JsonArray items)
        {
            return [];
        }

        var result = new List<BootMethodDefinition>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in items)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var id = ReadString(entry, "id");
            var displayName = ReadString(entry, "displayName");
            var description = ReadString(entry, "description");
            var artifactKind = ReadString(entry, "artifactKind");

            if (id is null || displayName is null || description is null || artifactKind is null)
            {
                continue;
            }

            if (!seen.Add(id))
            {
                continue;
            }

            result.Add(new BootMethodDefinition
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                ArtifactKind = artifactKind,
                RequiresFirmware = entry["requiresFirmware"] is JsonValue required && required.TryGetValue(out bool value) && value,
                FirmwareKind = ReadString(entry, "firmwareKind"),
                RequiredTools = ReadStringArray(entry, "requiredTools"),
                SupportedArchitectureIds = ReadStringArray(entry, "supportedArchitectureIds"),
                LinkerScriptTemplate = ReadString(entry, "linkerScriptTemplate"),
                QemuArguments = ReadStringArray(entry, "qemuArguments"),
                Notes = ReadString(entry, "notes"),
            });
        }

        return result;
    }

    private static string? ReadString(JsonObject node, string propertyName)
        => node[propertyName] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static int ReadInt(JsonObject node, string propertyName, int fallback)
        => node[propertyName] is JsonValue value && value.TryGetValue(out int number) ? number : fallback;

    private static IReadOnlyList<string> ReadStringArray(JsonObject node, string propertyName)
    {
        if (node[propertyName] is not JsonArray array)
        {
            return [];
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }
}
