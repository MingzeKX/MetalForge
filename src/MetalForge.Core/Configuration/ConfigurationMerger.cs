using System.Text.Json;
using System.Text.Json.Nodes;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Configuration;

/// <summary>配置来源层级。数值越大优先级越高。</summary>
public enum ConfigLayerKind
{
    /// <summary>程序集内置（随发行版发布，用户不可改）。</summary>
    BuiltIn = 0,

    /// <summary>用户目录（<c>%APPDATA%\MetalForge\</c> 或便携模式下的程序目录）。</summary>
    User = 1,

    /// <summary>项目目录（<c>&lt;project&gt;/.metalforge/</c>）。</summary>
    Project = 2,
}

/// <summary>一个配置层的元信息。</summary>
/// <param name="Kind">层级。</param>
/// <param name="DisplayName">面向用户的名字，用于"此值来自何处"的提示。</param>
/// <param name="RootDirectory">该层的根目录；null 表示内置层。</param>
public sealed record ConfigLayer(ConfigLayerKind Kind, string DisplayName, string? RootDirectory)
{
    public static ConfigLayer BuiltIn { get; } = new(ConfigLayerKind.BuiltIn, "内置默认值", null);
}

/// <summary>配置中某个最终值的来源，用于 UI 展示"此值来自项目配置"。</summary>
/// <param name="Location">JSON Pointer。</param>
/// <param name="Layer">生效层级。</param>
public readonly record struct ConfigOrigin(JsonPointer Location, ConfigLayerKind Layer);

/// <summary>一次配置解析的完整结果：合并后的文档 + 来源追踪 + 全部诊断。</summary>
public sealed class ResolvedConfiguration
{
    public ResolvedConfiguration(JsonNode? root, IReadOnlyDictionary<string, ConfigOrigin> origins, IReadOnlyList<Diagnostic> diagnostics)
    {
        Root = root;
        Origins = origins;
        Diagnostics = diagnostics;
    }

    /// <summary>合并后的配置树；全部层都缺失时为 null。</summary>
    public JsonNode? Root { get; }

    /// <summary>路径（JSON Pointer 字符串）到来源的映射。</summary>
    public IReadOnlyDictionary<string, ConfigOrigin> Origins { get; }

    /// <summary>解析过程中的全部诊断（含被拒绝的层的错误）。</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Error);

    /// <summary>查询某个字段最终来自哪一层。<paramref name="jsonPointer"/> 形如 <c>/palette/accent</c>。</summary>
    public ConfigLayerKind? OriginOf(string jsonPointer) => Origins.TryGetValue(jsonPointer, out var origin) ? origin.Layer : null;

    /// <summary>把合并结果反序列化为强类型模型；失败时返回 null 并输出诊断。</summary>
    public T? Bind<T>(JsonSerializerOptions options, out Diagnostic? failure)
        where T : class
    {
        failure = null;
        if (Root is null)
        {
            return null;
        }

        try
        {
            return Root.Deserialize<T>(options);
        }
        catch (JsonException exception)
        {
            failure = new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG120",
                $"配置结构与预期不符：{exception.Message}",
                Source: null,
                Location: exception.Path is null ? null : new JsonPointer("/" + exception.Path.Replace('.', '/')),
                Hint: "对照 assets/ 下的 JSON Schema 修正字段名与类型。",
                Exception: exception);
            return null;
        }
    }
}

/// <summary>
/// 三级配置合并器。
/// 语义（刻意保持简单可解释）：
///   - 对象：深合并，逐字段覆盖；
///   - 数组：整体替换（不做元素级合并，避免"看起来生效了一半"）；
///   - <c>null</c>：删除该键，回落到下一层或默认值；
///   - 每个叶子记录最终来源层级，供 UI 提示。
/// </summary>
public static class ConfigurationMerger
{
    /// <summary>按给定顺序（低优先级在前）合并多个文档。</summary>
    public static ResolvedConfiguration Merge(IReadOnlyList<(ConfigLayer Layer, JsonNode? Document)> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        JsonNode? merged = null;
        var origins = new Dictionary<string, ConfigOrigin>(StringComparer.Ordinal);

        foreach (var (layer, document) in layers)
        {
            if (document is null)
            {
                continue;
            }

            if (merged is null)
            {
                merged = document.DeepClone();
                RecordOrigins(merged, layer.Kind, JsonPointer.Root, origins);
                continue;
            }

            ApplyLayer(merged, document, layer.Kind, JsonPointer.Root, origins);
        }

        // 被 null 删除的键需要连同其来源一起清理，否则查询会给出过期来源。
        RemoveOrphanOrigins(merged, JsonPointer.Root, origins);

        return new ResolvedConfiguration(merged, origins, []);
    }

    private static void ApplyLayer(JsonNode target, JsonNode overlay, ConfigLayerKind kind, JsonPointer pointer, Dictionary<string, ConfigOrigin> origins)
    {
        if (target is not JsonObject targetObject || overlay is not JsonObject overlayObject)
        {
            return;
        }

        foreach (var pair in overlayObject.ToList())
        {
            var childPointer = pointer.Append(pair.Key);

            if (pair.Value is null)
            {
                targetObject.Remove(pair.Key);
                origins.Remove(childPointer.Value);
                continue;
            }

            if (targetObject.TryGetPropertyValue(pair.Key, out var existing) && existing is JsonObject existingObject && pair.Value is JsonObject overlayChild)
            {
                ApplyLayer(existingObject, overlayChild, kind, childPointer, origins);
                origins[childPointer.Value] = new ConfigOrigin(childPointer, kind);
                continue;
            }

            targetObject[pair.Key] = pair.Value.DeepClone();
            RecordOrigins(targetObject[pair.Key], kind, childPointer, origins);
        }
    }

    private static void RecordOrigins(JsonNode? node, ConfigLayerKind kind, JsonPointer pointer, Dictionary<string, ConfigOrigin> origins)
    {
        origins[pointer.Value] = new ConfigOrigin(pointer, kind);

        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var pair in jsonObject)
                {
                    RecordOrigins(pair.Value, kind, pointer.Append(pair.Key), origins);
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    RecordOrigins(jsonArray[index], kind, pointer.AppendIndex(index), origins);
                }

                break;
        }
    }

    private static void RemoveOrphanOrigins(JsonNode? node, JsonPointer pointer, Dictionary<string, ConfigOrigin> origins)
    {
        if (node is null)
        {
            origins.Remove(pointer.Value);
            return;
        }

        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var pair in jsonObject.ToList())
                {
                    RemoveOrphanOrigins(pair.Value, pointer.Append(pair.Key), origins);
                }

                break;

            case JsonArray jsonArray:
                for (var index = jsonArray.Count - 1; index >= 0; index--)
                {
                    RemoveOrphanOrigins(jsonArray[index], pointer.AppendIndex(index), origins);
                }

                break;
        }
    }
}
