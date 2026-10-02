namespace MetalForge.Core.Layout;

/// <summary>
/// 面板节点在 JSON 中的形状。
///
/// 刻意与领域模型 <see cref="LayoutNode"/> 分开：
/// JSON 里的判别式字段是字符串（<c>"split"</c>/<c>"panel"</c>/<c>"document"</c>），
/// 而领域模型用枚举。分开之后：
///   - 非法取值不会在反序列化时抛异常，而是由 <see cref="LayoutPreset.Validate"/> 报成可读问题；
///   - JSON 结构调整（例如将来加 <c>"toolbar"</c>）不会牵动领域模型。
/// </summary>
public sealed record LayoutNodeJson
{
    public string? Type { get; init; }
    public string? Orientation { get; init; }
    public IReadOnlyList<LayoutNodeJson> Children { get; init; } = [];
    public IReadOnlyList<double> Proportions { get; init; } = [];
    public string? Id { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string> Tabs { get; init; } = [];
    public IReadOnlyList<string> InitialTabs { get; init; } = [];
    public bool? Visible { get; init; }
    public bool? Collapsible { get; init; }
    public double? Size { get; init; }
    public double? MinimumSize { get; init; }

    /// <summary>转换为领域模型。未知 <c>type</c> 会退化为文档节点并被 <see cref="LayoutPreset.Validate"/> 报出。</summary>
    public LayoutNode ToNode()
    {
        var kind = Type?.ToLowerInvariant() switch
        {
            "split" => LayoutNodeKind.Split,
            "panel" => LayoutNodeKind.Panel,
            "document" => LayoutNodeKind.Document,
            _ => LayoutNodeKind.Document,
        };

        var orientation = Orientation?.ToLowerInvariant() switch
        {
            "vertical" => LayoutOrientation.Vertical,
            _ => LayoutOrientation.Horizontal,
        };

        return new LayoutNode
        {
            Kind = kind,
            Orientation = orientation,
            Children = [.. Children.Select(child => child.ToNode())],
            Proportions = [.. Proportions],
            Id = Id,
            Title = Title,
            Tabs = [.. Tabs],
            InitialTabs = [.. InitialTabs],
            Visible = Visible ?? true,
            Collapsible = Collapsible ?? false,
            Size = Size,
            MinimumSize = MinimumSize,
        };
    }
}

/// <summary>布局预设的 JSON 形状。与 <c>assets/layouts/*.layout.json</c> 字段一一对应。</summary>
public sealed record LayoutPresetJson
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public LayoutWindow? Window { get; init; }
    public bool? MenuBar { get; init; }
    public LayoutToolbar? Toolbar { get; init; }
    public LayoutStatusBar? StatusBar { get; init; }
    public LayoutNodeJson? Root { get; init; }

    /// <summary>转换为领域模型。</summary>
    public LayoutPreset ToPreset()
    {
        var id = string.IsNullOrWhiteSpace(Id) ? "default" : Id;

        return new LayoutPreset
        {
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? id : DisplayName,
            Description = Description,
            Window = Window ?? new LayoutWindow(),
            MenuBar = MenuBar ?? true,
            Toolbar = Toolbar ?? new LayoutToolbar(),
            StatusBar = StatusBar ?? new LayoutStatusBar(),
            Root = Root?.ToNode() ?? new LayoutNode { Kind = LayoutNodeKind.Document },
        };
    }
}
