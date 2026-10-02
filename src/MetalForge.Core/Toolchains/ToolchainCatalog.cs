using System.Text.Json.Nodes;

namespace MetalForge.Core.Toolchains;

/// <summary>
/// 从 <c>assets/targets/tools.json</c> 读取工具清单。
///
/// 与主题/布局加载同构但更简单：工具清单是一个整体，不做逐项合并
/// （合并"部分工具定义"会产生"某个工具的字段一半来自内置、一半来自用户"这种
/// 难以解释的状态；用户要改就整条覆盖）。三层覆盖通过 <see cref="ReadRequirements"/> 的
/// 调用方在文件层面完成。
/// </summary>
public static class ToolchainCatalog
{
    /// <summary>从已合并的 JSON 根节点读取工具定义。结构损坏的条目被跳过而不是抛异常。</summary>
    public static IReadOnlyList<ToolRequirement> ReadRequirements(JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root["tools"] is not JsonArray tools)
        {
            return [];
        }

        var requirements = new List<ToolRequirement>(tools.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in tools)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var toolId = ReadString(entry, "toolId");
            var displayName = ReadString(entry, "displayName");
            var purpose = ReadString(entry, "purpose");
            var executableName = ReadString(entry, "executableName");

            // 四个必填字段缺一不可：缺了就无法探测也无法向用户解释，跳过比报错更合适
            // （Schema 校验已经会给出精确的错误位置）。
            if (toolId is null || displayName is null || purpose is null || executableName is null)
            {
                continue;
            }

            if (!seenIds.Add(toolId))
            {
                continue;
            }

            requirements.Add(new ToolRequirement
            {
                ToolId = toolId,
                DisplayName = displayName,
                Purpose = purpose,
                ExecutableName = executableName,
                VersionArguments = ReadStringArray(entry, "versionArguments", ["--version"]),
                VersionPattern = ReadString(entry, "versionPattern"),
                MinimumVersion = ReadString(entry, "minimumVersion"),
                KnownLocations = ReadStringArray(entry, "knownLocations", []),
                AcquisitionHint = ReadString(entry, "acquisitionHint"),
                DownloadUrl = ReadString(entry, "downloadUrl"),
                Category = ReadString(entry, "category") ?? "general",
                Required = entry["required"] is JsonValue requiredValue && requiredValue.TryGetValue(out bool required) && required,
            });
        }

        return requirements;
    }

    private static string? ReadString(JsonObject node, string propertyName)
        => node[propertyName] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonObject node, string propertyName, IReadOnlyList<string> fallback)
    {
        if (node[propertyName] is not JsonArray array)
        {
            return fallback;
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values.Count == 0 ? fallback : values;
    }
}
