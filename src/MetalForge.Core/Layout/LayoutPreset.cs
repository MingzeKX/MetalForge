namespace MetalForge.Core.Layout;

/// <summary>面板树节点类型。与 <c>assets/layouts/schema/layout.schema.json</c> 的 <c>oneOf</c> 对应。</summary>
public enum LayoutNodeKind
{
    /// <summary>分割容器，包含两个及以上子节点。</summary>
    Split,

    /// <summary>工具面板（可停靠、可折叠），例如项目浏览器、属性面板、底部标签页。</summary>
    Panel,

    /// <summary>文档区域（多标签编辑器）。</summary>
    Document,
}

/// <summary>分割方向。</summary>
public enum LayoutOrientation
{
    /// <summary>水平排列（子节点左右并排）。</summary>
    Horizontal,

    /// <summary>垂直排列（子节点上下堆叠）。</summary>
    Vertical,
}

/// <summary>
/// 面板树节点。
///
/// 设计取舍：不用多态类型层级（Split/Content 各自一个类 + JSON 多态解析），
/// 而是单一节点类型 + <see cref="Kind"/> 判别式。
/// 理由：布局树只有三种节点、字段很少；多态解析需要自定义 converter 且
/// 反序列化失败时的诊断更难定位。单一类型让"配置写错"永远表现为可读的校验错误。
/// </summary>
public sealed record LayoutNode
{
    public LayoutNodeKind Kind { get; init; }

    /// <summary>分割方向；仅 <see cref="LayoutNodeKind.Split"/> 有意义。</summary>
    public LayoutOrientation Orientation { get; init; } = LayoutOrientation.Horizontal;

    /// <summary>子节点；仅 Split 有意义。</summary>
    public IReadOnlyList<LayoutNode> Children { get; init; } = [];

    /// <summary>各子节点占比；仅 Split 有意义。为空或数量不匹配时按等分处理。</summary>
    public IReadOnlyList<double> Proportions { get; init; } = [];

    /// <summary>节点标识；Panel 必填。</summary>
    public string? Id { get; init; }

    /// <summary>显示标题；Panel 使用。</summary>
    public string? Title { get; init; }

    /// <summary>标签页 id 列表；Panel 使用，第一项为默认选中。</summary>
    public IReadOnlyList<string> Tabs { get; init; } = [];

    /// <summary>启动时打开的文档标签页；Document 使用（例如 welcome）。</summary>
    public IReadOnlyList<string> InitialTabs { get; init; } = [];

    public bool Visible { get; init; } = true;

    public bool Collapsible { get; init; }

    /// <summary>停靠时的建议像素尺寸。</summary>
    public double? Size { get; init; }

    public double? MinimumSize { get; init; }

    /// <summary>按子节点数量归一化占比；缺失或非法时等分。</summary>
    public IReadOnlyList<double> EffectiveProportions()
    {
        if (Children.Count == 0)
        {
            return [];
        }

        if (Proportions.Count != Children.Count)
        {
            var even = 1.0 / Children.Count;
            return [.. Enumerable.Repeat(even, Children.Count)];
        }

        var total = Proportions.Sum();
        if (total <= 0)
        {
            var even = 1.0 / Children.Count;
            return [.. Enumerable.Repeat(even, Children.Count)];
        }

        // 归一化：允许作者写 1/2/1 这类相对权重，而不必自己凑成 1.0。
        return [.. Proportions.Select(p => p / total)];
    }

    /// <summary>深度优先遍历自身与全部后代。</summary>
    public IEnumerable<LayoutNode> DescendantsAndSelf()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }
}

/// <summary>窗口初始几何。</summary>
public sealed record LayoutWindow
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool Maximized { get; init; }
}

/// <summary>工具栏定义。</summary>
public sealed record LayoutToolbar
{
    public bool Visible { get; init; } = true;

    /// <summary>命令 id 列表，<c>separator</c> 表示分隔符。</summary>
    public IReadOnlyList<string> Items { get; init; } = [];
}

/// <summary>状态栏定义。</summary>
public sealed record LayoutStatusBar
{
    public bool Visible { get; init; } = true;
    public IReadOnlyList<string> Items { get; init; } = [];
}

/// <summary>
/// 一个布局预设。字段名与 <c>assets/layouts/*.layout.json</c> 一一对应。
/// </summary>
public sealed record LayoutPreset
{
    public string Id { get; init; } = "default";
    public string DisplayName { get; init; } = "默认布局";
    public string? Description { get; init; }
    public LayoutWindow Window { get; init; } = new();
    public bool MenuBar { get; init; } = true;
    public LayoutToolbar Toolbar { get; init; } = new();
    public LayoutStatusBar StatusBar { get; init; } = new();
    public LayoutNode Root { get; init; } = new() { Kind = LayoutNodeKind.Document };

    /// <summary>查找指定 id 的面板节点。</summary>
    public LayoutNode? FindPanel(string panelId)
        => Root.DescendantsAndSelf().FirstOrDefault(
            node => node.Kind == LayoutNodeKind.Panel
                 && string.Equals(node.Id, panelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>枚举全部面板节点（按树序）。</summary>
    public IEnumerable<LayoutNode> Panels()
        => Root.DescendantsAndSelf().Where(node => node.Kind == LayoutNodeKind.Panel);

    /// <summary>全部被引用的标签页 id（按出现顺序去重）。</summary>
    public IReadOnlyList<string> AllTabIds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var node in Root.DescendantsAndSelf())
        {
            foreach (var tab in node.Tabs.Concat(node.InitialTabs))
            {
                if (seen.Add(tab))
                {
                    result.Add(tab);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 结构性校验。构造正确但内容自相矛盾的布局（例如占比数量与子节点数量不符、
    /// 分割节点只有一个子节点）在这里被发现，而不是在 UI 布局时崩溃。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        ValidateNode(Root, "root", problems);
        return problems;
    }

    private static void ValidateNode(LayoutNode node, string path, List<string> problems)
    {
        switch (node.Kind)
        {
            case LayoutNodeKind.Split:
                if (node.Children.Count < 2)
                {
                    problems.Add($"{path}: 分割节点至少需要 2 个子节点，实际 {node.Children.Count} 个。");
                }

                if (node.Proportions.Count > 0 && node.Proportions.Count != node.Children.Count)
                {
                    problems.Add($"{path}: proportions 数量（{node.Proportions.Count}）与子节点数量（{node.Children.Count}）不一致。");
                }

                if (node.Proportions.Any(p => p <= 0))
                {
                    problems.Add($"{path}: proportions 必须全部为正数。");
                }

                for (var index = 0; index < node.Children.Count; index++)
                {
                    ValidateNode(node.Children[index], $"{path}.children[{index}]", problems);
                }

                break;

            case LayoutNodeKind.Panel:
                if (string.IsNullOrWhiteSpace(node.Id))
                {
                    problems.Add($"{path}: 面板节点缺少 id。");
                }

                if (node.MinimumSize is { } minimum && node.Size is { } size && minimum > size)
                {
                    problems.Add($"{path}: minimumSize（{minimum}）大于 size（{size}），面板无法达到建议尺寸。");
                }

                break;

            case LayoutNodeKind.Document:
                break;

            default:
                problems.Add($"{path}: 未知节点类型 {node.Kind}。");
                break;
        }
    }
}
