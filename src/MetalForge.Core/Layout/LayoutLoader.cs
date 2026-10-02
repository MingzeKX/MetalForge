using System.Text.Json;
using System.Text.Json.Nodes;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Layout;

/// <summary>加载布局预设的选项。</summary>
/// <param name="LayoutDirectory">相对 <c>assets/</c> 的布局目录，默认 <c>layouts</c>。</param>
/// <param name="SchemaRelativePath">相对 <c>assets/</c> 的 Schema 路径。</param>
/// <param name="SearchPattern">文件匹配模式。</param>
public sealed record LayoutLoadOptions(
    string LayoutDirectory = "layouts",
    string SchemaRelativePath = "layouts/schema/layout.schema.json",
    string SearchPattern = "*.layout.json");

/// <summary>
/// 布局预设加载器。
/// 与主题加载同构：枚举 → 逐层合并（内置/用户/项目）→ Schema 校验 → 反序列化。
/// 单个布局文件损坏只影响它自己，其余预设仍然可用。
/// </summary>
public sealed class LayoutLoader
{
    private readonly AssetResolver _resolver;
    private readonly LayoutLoadOptions _options;

    public LayoutLoader(AssetResolver resolver, LayoutLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _options = options ?? new LayoutLoadOptions();
    }

    /// <summary>加载全部布局预设。返回值按文件名顺序稳定，便于测试与 UI 展示。</summary>
    public (IReadOnlyList<LayoutPreset> Presets, IReadOnlyList<Diagnostic> Diagnostics) LoadAll()
    {
        var presets = new List<LayoutPreset>();
        var diagnostics = new List<Diagnostic>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in _resolver.Enumerate(_options.LayoutDirectory, _options.SearchPattern))
        {
            var relativePath = $"{_options.LayoutDirectory}/{fileName}";
            var (configuration, layerDiagnostics) = _resolver.ResolveValidated(relativePath, _options.SchemaRelativePath);
            diagnostics.AddRange(layerDiagnostics);

            if (configuration.Root is null)
            {
                continue;
            }

            LayoutPresetJson? json;
            try
            {
                json = configuration.Root.Deserialize<LayoutPresetJson>(AssetOptions.SerializerOptions);
            }
            catch (JsonException exception)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "MFLAYOUT001",
                    $"布局预设结构无法解析：{exception.Message}",
                    relativePath,
                    Hint: "对照 assets/layouts/schema/layout.schema.json 检查字段名与类型。",
                    Exception: exception));
                continue;
            }

            if (json is null)
            {
                continue;
            }

            var preset = json.ToPreset();

            if (!seenIds.Add(preset.Id))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "MFLAYOUT002",
                    $"布局预设 id 重复：'{preset.Id}'，后出现的被忽略。",
                    relativePath,
                    Hint: "每个布局文件的 id 必须唯一（高优先级层的同名文件会覆盖低优先级层）。"));
                continue;
            }

            // 结构自相矛盾（占比数量不符、分割节点只有一个子节点等）在这里暴露，
            // 而不是等到 UI 布局阶段抛异常或渲染出诡异结果。
            var problems = preset.Validate();
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "MFLAYOUT003",
                        $"布局预设 '{preset.Id}' 结构不合法：{problem}",
                        relativePath,
                        Hint: "检查分割节点的 children 与 proportions 是否一一对应。"));
                }

                continue;
            }

            presets.Add(preset);
        }

        return (presets, diagnostics);
    }

    /// <summary>
    /// 校验布局引用的标签页 id 是否都有对应的界面实现。
    /// 未知 id 只报警告：用户可能在自己写的布局里用了尚未实现的标签页，
    /// 这不应该让整个布局失效。
    /// </summary>
    public static IReadOnlyList<Diagnostic> ValidateTabIds(LayoutPreset preset, IReadOnlySet<string> knownTabIds, string source)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(knownTabIds);

        return
        [
            .. preset.AllTabIds()
                .Where(tabId => !knownTabIds.Contains(tabId))
                .Select(tabId => new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "MFLAYOUT004",
                    $"布局 '{preset.Id}' 引用了未知的标签页 '{tabId}'，该标签页不会显示。",
                    source,
                    Hint: "确认标签页 id 拼写，或等待该功能在后续里程碑实现。"))
        ];
    }
}
