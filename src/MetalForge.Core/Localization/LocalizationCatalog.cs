using System.Text.Json.Nodes;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Localization;

/// <summary>
/// 界面文案的查找表。
///
/// 文件结构是嵌套对象（<c>menu.file.new</c> 对应 <c>{ "menu": { "file": { "new": "..." } } }</c>），
/// 而不是扁平的点号键。理由：非程序员编辑 JSON 时，嵌套结构能自动获得编辑器的折叠与
/// 层级提示；点号键在几百条文案里极易写错前缀。
/// </summary>
public sealed class LocalizationCatalog
{
    private readonly JsonObject _root;

    private LocalizationCatalog(string languageCode, string displayName, bool rightToLeft, JsonObject root)
    {
        LanguageCode = languageCode;
        DisplayName = displayName;
        RightToLeft = rightToLeft;
        _root = root;
    }

    /// <summary>BCP-47 语言代码，例如 <c>zh-Hans</c>。</summary>
    public string LanguageCode { get; }

    /// <summary>面向用户的语言名称，例如"简体中文"。</summary>
    public string DisplayName { get; }

    /// <summary>是否从右到左排版（为阿拉伯语等预留）。</summary>
    public bool RightToLeft { get; }

    /// <summary>本语言中出现的全部键（点号路径）。</summary>
    public IEnumerable<string> Keys() => EnumerateKeys(_root, string.Empty);

    /// <summary>
    /// 按点号路径查找文案；不存在返回 null。
    ///
    /// 查找规则：逐段下钻，**一旦遇到标量就不再继续**。
    /// 这条规则解决一个真实冲突：菜单标题与菜单项天然共用路径前缀
    /// （<c>menu.file</c> 是"文件"，<c>menu.file.items.newProject</c> 是"新建项目…"）。
    /// 若把"标量"与"可继续下钻"混为一谈，<c>menu.file</c> 查询会意外拿到子对象，
    /// 而 <c>menu.file.newProject</c> 又会命中那个字符串。标量优先让语义明确。
    /// </summary>
    public string? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var segments = key.Split('.');
        JsonNode? current = _root;

        for (var index = 0; index < segments.Length; index++)
        {
            if (current is not JsonObject jsonObject || !jsonObject.TryGetPropertyValue(segments[index], out var next))
            {
                return null;
            }

            current = next;

            if (current is JsonValue intermediate)
            {
                // 标量优先：路径尚未走完却已命中叶子。
                // 只有在"这正好是最后一段"时才返回它，否则说明键名写错了。
                var isLastSegment = index == segments.Length - 1;
                return isLastSegment && intermediate.TryGetValue(out string? partial) ? partial : null;
            }
        }

        return current is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    /// <summary>
    /// 从已解析的 JSON 构建目录。缺少 <c>language</c> 视为损坏并返回 null
    /// （调用方会给出诊断，而不是静默使用一个没有标识的语言）。
    /// </summary>
    public static LocalizationCatalog? FromJson(JsonNode? node, out string? failureReason)
    {
        failureReason = null;

        if (node is not JsonObject root)
        {
            failureReason = "语言文件的根节点必须是 JSON 对象。";
            return null;
        }

        if (root["language"] is not JsonValue languageValue || !languageValue.TryGetValue(out string? language) || string.IsNullOrWhiteSpace(language))
        {
            failureReason = "语言文件缺少必填字段 'language'（例如 \"zh-Hans\"）。";
            return null;
        }

        var displayName = root["displayName"] is JsonValue displayValue && displayValue.TryGetValue(out string? name)
            ? name
            : language;

        var rightToLeft = root["rightToLeft"] is JsonValue rtlValue && rtlValue.TryGetValue(out bool rtl) && rtl;

        var strings = root["strings"] as JsonObject ?? [];
        return new LocalizationCatalog(language, displayName ?? language, rightToLeft, strings);
    }

    /// <summary>与回退语言合并：本语言缺失的键由 <paramref name="fallback"/> 补齐。</summary>
    public LocalizationCatalog WithFallback(LocalizationCatalog fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        var merged = new JsonObject();
        foreach (var pair in fallback._root)
        {
            merged[pair.Key] = pair.Value?.DeepClone();
        }

        MergeInto(merged, _root);

        return new LocalizationCatalog(LanguageCode, DisplayName, RightToLeft, merged);
    }

    private static void MergeInto(JsonObject target, JsonObject overlay)
    {
        foreach (var pair in overlay)
        {
            if (pair.Value is JsonObject overlayChild
                && target[pair.Key] is JsonObject targetChild)
            {
                MergeInto(targetChild, overlayChild);
                continue;
            }

            target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static IEnumerable<string> EnumerateKeys(JsonObject node, string prefix)
    {
        foreach (var pair in node)
        {
            var path = prefix.Length == 0 ? pair.Key : prefix + "." + pair.Key;

            if (pair.Value is JsonObject child)
            {
                foreach (var key in EnumerateKeys(child, path))
                {
                    yield return key;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    /// <summary>从 assets 层解析出的完整结果，供 <see cref="LocalizationService"/> 使用。</summary>
    internal static (LocalizationCatalog? Catalog, IReadOnlyList<Diagnostic> Diagnostics) LoadOne(AssetResolver resolver, string relativePath)
    {
        var diagnostics = new List<Diagnostic>();
        var (configuration, layerDiagnostics) = resolver.ResolveValidated(relativePath, "locales/schema/locale.schema.json");
        diagnostics.AddRange(layerDiagnostics);

        if (configuration.Root is null)
        {
            return (null, diagnostics);
        }

        var catalog = FromJson(configuration.Root, out var failureReason);
        if (catalog is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFLOC001",
                $"语言文件不可用：{failureReason}",
                relativePath,
                Hint: "语言文件至少需要 language 与 strings 两个字段。"));
        }

        return (catalog, diagnostics);
    }
}
