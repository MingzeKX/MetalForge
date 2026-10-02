using System.Text.Json;
using System.Text.Json.Nodes;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Configuration;

/// <summary>资源解析选项。</summary>
public sealed record AssetOptions
{
    /// <summary>内置资源目录（开发期为仓库 <c>assets/</c>；发布期为程序目录下的 <c>assets/</c>）。</summary>
    public required string BuiltInAssetsDirectory { get; init; }

    /// <summary>用户资源目录（<c>%APPDATA%\MetalForge</c>；便携模式下为程序目录）。</summary>
    public required string UserDirectory { get; init; }

    /// <summary>便携模式：用户层与项目层都改为相对程序目录解析（不写系统用户目录）。</summary>
    public bool Portable { get; init; }

    /// <summary>JSON 读取选项：允许注释与尾随逗号，便于非程序员手工编辑。</summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}

/// <summary>一次资源读取的结果。</summary>
/// <param name="Node">解析出的 JSON 节点；失败为 null。</param>
/// <param name="Layer">来源层。</param>
/// <param name="Path">实际读取的文件路径；文件不存在为 null。</param>
/// <param name="Diagnostics">该次读取产生的诊断。</param>
public sealed record AssetLoadResult(JsonNode? Node, ConfigLayer Layer, string? Path, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Exists => Node is not null;
}

/// <summary>
/// 资源解析器：把 <c>assets/</c> 下的相对路径解析为"内置 → 用户 → 项目"三级文档。
/// 文件语法错误不会导致启动失败：该层被拒绝，诊断被收集，其余层继续生效。
/// </summary>
public sealed class AssetResolver
{
    private readonly AssetOptions _options;
    private readonly Func<string, ConfigLayer, AssetLoadResult>? _projectLayerFactory;

    public AssetResolver(AssetOptions options)
        : this(options, projectDirectory: null)
    {
    }

    /// <summary>
    /// 构造解析器。<paramref name="projectDirectory"/> 非空时启用项目层
    /// （项目目录下的 <c>.metalforge/</c> 覆盖用户层）。
    /// </summary>
    public AssetResolver(AssetOptions options, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        ProjectDirectory = projectDirectory;

        if (!string.IsNullOrEmpty(projectDirectory))
        {
            var projectOverlayRoot = Path.Combine(projectDirectory, ".metalforge");
            _projectLayerFactory = (relativePath, layer) => ReadFile(Path.Combine(projectOverlayRoot, ToPlatformPath(relativePath)), layer);
        }
    }

    /// <summary>当前项目目录；null 表示不启用项目层。</summary>
    public string? ProjectDirectory { get; }

    public AssetOptions Options => _options;

    /// <summary>内置资源目录的绝对路径。</summary>
    public string BuiltInDirectory => _options.BuiltInAssetsDirectory;

    /// <summary>用户层资源目录的绝对路径。</summary>
    public string UserDirectory => _options.UserDirectory;

    /// <summary>
    /// 读取单个资源并完成三级合并。
    /// </summary>
    /// <param name="relativePath">相对 <c>assets/</c> 的路径，例如 <c>branding/app.json</c>。</param>
    public ResolvedConfiguration Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var diagnostics = new List<Diagnostic>();
        var layers = new List<(ConfigLayer Layer, JsonNode? Document)>();

        var builtIn = ReadFile(Path.Combine(_options.BuiltInAssetsDirectory, ToPlatformPath(relativePath)), ConfigLayer.BuiltIn);
        diagnostics.AddRange(builtIn.Diagnostics);
        layers.Add((ConfigLayer.BuiltIn, builtIn.Node));

        var userLayer = new ConfigLayer(ConfigLayerKind.User, _options.Portable ? "便携配置" : "用户配置", _options.UserDirectory);
        var user = ReadFile(Path.Combine(_options.UserDirectory, ToPlatformPath(relativePath)), userLayer);
        diagnostics.AddRange(user.Diagnostics);
        layers.Add((userLayer, user.Node));

        if (_projectLayerFactory is not null)
        {
            var projectLayer = new ConfigLayer(ConfigLayerKind.Project, "项目配置", Path.Combine(ProjectDirectory!, ".metalforge"));
            var project = _projectLayerFactory(relativePath, projectLayer);
            diagnostics.AddRange(project.Diagnostics);
            layers.Add((projectLayer, project.Node));
        }

        var merged = ConfigurationMerger.Merge(layers);
        return new ResolvedConfiguration(merged.Root, merged.Origins, [.. diagnostics, .. merged.Diagnostics]);
    }

    /// <summary>读取并校验单个资源，同时应用可选 JSON Schema。</summary>
    /// <param name="relativePath">相对 <c>assets/</c> 的路径。</param>
    /// <param name="schemaRelativePath">相对 <c>assets/</c> 的 Schema 路径；null 表示不校验。</param>
    public (ResolvedConfiguration Configuration, IReadOnlyList<Diagnostic> Diagnostics) ResolveValidated(string relativePath, string? schemaRelativePath)
    {
        var resolved = Resolve(relativePath);
        var diagnostics = new List<Diagnostic>(resolved.Diagnostics);

        if (schemaRelativePath is null || resolved.Root is null)
        {
            return (resolved, diagnostics);
        }

        var schemaPath = Path.Combine(_options.BuiltInAssetsDirectory, ToPlatformPath(schemaRelativePath));
        if (!File.Exists(schemaPath))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFCFG130",
                $"找不到 JSON Schema：{schemaRelativePath}，本次跳过校验。",
                schemaPath,
                Hint: "确认 assets/ 目录完整；Schema 缺失不会阻止程序启动。"));
            return (resolved, diagnostics);
        }

        try
        {
            using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath), AssetOptions.DocumentOptions);
            using var instanceDocument = JsonDocument.Parse(resolved.Root.ToJsonString(), AssetOptions.DocumentOptions);
            var validator = new JsonSchemaValidator(schemaDocument.RootElement);
            diagnostics.AddRange(validator.Validate(instanceDocument.RootElement, relativePath));
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG131",
                $"JSON Schema 本身无法解析：{exception.Message}",
                schemaPath,
                Hint: "这是内置 Schema 的缺陷，请上报。",
                Exception: exception));
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG132",
                $"读取 JSON Schema 失败：{exception.Message}",
                schemaPath,
                Hint: "检查文件是否被其他程序占用。",
                Exception: exception));
        }

        return (resolved, diagnostics);
    }

    /// <summary>列举某目录下所有匹配的文件（用于主题、布局等"可扩展集合"）。</summary>
    /// <param name="relativeDirectory">相对 <c>assets/</c> 的目录。</param>
    /// <param name="searchPattern">例如 <c>*.theme.json</c>。</param>
    /// <param name="includeSubdirectories">是否递归。</param>
    /// <returns>按优先级（项目 → 用户 → 内置）排序的文件名列表，同名文件只出现一次。</returns>
    public IReadOnlyList<string> Enumerate(string relativeDirectory, string searchPattern, bool includeSubdirectories = false)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateRoots())
        {
            if (root is null)
            {
                continue;
            }

            var directory = Path.Combine(root, ToPlatformPath(relativeDirectory));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, searchPattern, includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                // 以"文件名"作为身份：同名文件视为同一资源，高优先级层先枚举即胜出。
                // 返回文件名（而非相对路径）是刻意的：ResolveValidated 接收的也是
                // "themes/xxx.theme.json" 形式，调用方需要能直接把结果喂回去；
                // 相对目录前缀会随层变化，作为键是不稳定的。
                if (seen.Add(Path.GetFileName(file)))
                {
                    results.Add(Path.GetFileName(file));
                }
            }
        }

        return results;
    }

    private IEnumerable<string?> EnumerateRoots()
    {
        // 顺序即优先级：项目 > 用户 > 内置
        if (ProjectDirectory is not null)
        {
            yield return Path.Combine(ProjectDirectory, ".metalforge");
        }

        yield return _options.UserDirectory;
        yield return _options.BuiltInAssetsDirectory;
    }

    private static string ToPlatformPath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static AssetLoadResult ReadFile(string path, ConfigLayer layer)
    {
        var diagnostics = new List<Diagnostic>();

        if (!File.Exists(path))
        {
            return new AssetLoadResult(null, layer, null, diagnostics);
        }

        string text;
        try
        {
            text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG140",
                $"读取配置失败：{exception.Message}",
                path,
                Hint: "文件可能被其他程序占用；该层配置已被忽略，其余层继续生效。",
                Exception: exception));
            return new AssetLoadResult(null, layer, path, diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG141",
                $"没有权限读取配置：{exception.Message}",
                path,
                Hint: "检查文件权限；该层配置已被忽略。",
                Exception: exception));
            return new AssetLoadResult(null, layer, path, diagnostics);
        }

        try
        {
            var node = JsonNode.Parse(text, documentOptions: AssetOptions.DocumentOptions, nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = false });
            if (node is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "MFCFG142",
                    "配置内容为空。",
                    path,
                    Hint: "空文件不产生覆盖；如需使用默认值请删除该文件。"));
            }

            return new AssetLoadResult(node, layer, path, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG143",
                $"JSON 语法错误（第 {exception.LineNumber + 1} 行，第 {exception.BytePositionInLine + 1} 列）：{exception.Message}",
                path,
                Line: (int?)(exception.LineNumber + 1),
                Column: (int?)(exception.BytePositionInLine + 1),
                Hint: "常见原因：少了逗号、多了逗号、引号不配对、使用了单引号。该层配置已被忽略。",
                Exception: exception));
            return new AssetLoadResult(null, layer, path, diagnostics);
        }
    }
}
