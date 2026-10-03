using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MetalForge.Core.Layout;

namespace MetalForge.App.Services;

/// <summary>
/// 面板内容的工厂。App 层据此把布局里的标签页 id 变成真实控件。
///
/// 返回 null 表示该标签页尚未实现：调用方会生成一个明确的占位视图，
/// 而不是留出空白（空白面板是"东西坏了吗"的经典来源）。
/// </summary>
public interface ITabContentFactory
{
    /// <summary>为一个标签页创建内容控件。</summary>
    Control? CreateContent(TabDescriptor descriptor);

    /// <summary>标题（已本地化）。</summary>
    string GetTitle(TabDescriptor descriptor);
}

/// <summary>
/// 把 <see cref="LayoutPreset"/> 变成 Avalonia 控件树。
///
/// 之所以要"构建器"而不是把布局写死在 XAML 里：
/// 项目要求布局可由 JSON 预设驱动（DESIGN.md F-03），而 XAML 无法表达
/// "布局文件说三栏、比例 0.2/0.62/0.18"这种由数据决定的嵌套结构。
///
/// 视觉参数（背景、圆角、间距、字号）全部通过样式类引用主题资源，
/// 构建器本身不写任何颜色或尺寸字面量。
/// </summary>
public sealed class LayoutControlBuilder
{
    /// <summary>面板容器使用的样式类名（在 Styles.axaml 中定义）。</summary>
    public const string PanelChromeClass = "panel-chrome";

    /// <summary>面板标题使用的样式类名。</summary>
    public const string PanelTitleClass = "panel-title";

    /// <summary>占位内容使用的样式类名（未实现的标签页）。</summary>
    public const string PlaceholderClass = "panel-placeholder";

    private readonly ITabContentFactory _contentFactory;

    public LayoutControlBuilder(ITabContentFactory contentFactory)
    {
        ArgumentNullException.ThrowIfNull(contentFactory);
        _contentFactory = contentFactory;
    }

    /// <summary>构建整棵布局树。根节点的比例设置无效（它没有父容器）。</summary>
    public Control Build(LayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return BuildNode(root);
    }

    private Control BuildNode(LayoutNode node) => node.Kind switch
    {
        LayoutNodeKind.Split => BuildSplit(node),
        LayoutNodeKind.Panel => BuildPanel(node),
        LayoutNodeKind.Document => BuildDocumentArea(node),
        _ => BuildUnsupported(node),
    };

    private Control BuildSplit(LayoutNode node)
    {
        var orientation = node.Orientation == LayoutOrientation.Vertical
            ? Orientation.Vertical
            : Orientation.Horizontal;

        var grid = new Grid
        {
            // 需要在子控件之间插入 GridSplitter，因此列/行数是子节点数的两倍减一。
            RowDefinitions = new RowDefinitions(),
            ColumnDefinitions = new ColumnDefinitions(),
        };

        var proportions = node.EffectiveProportions();
        var children = node.Children;

        // 诊断：比例是"布局配置驱动"的核心。比例没生效时界面会悄悄变成另一副样子，
        // 而截图上很难判断"到底是不是 20%"。这里把实际用于构建的值记录下来。
        System.Diagnostics.Trace.WriteLine(
            $"[layout] {orientation} split: {children.Count} children, proportions=" +
            $"[{string.Join(", ", proportions.Select(p => p.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}]");

        for (var index = 0; index < children.Count; index++)
        {
            var proportion = index < proportions.Count ? proportions[index] : 1.0 / children.Count;

            if (index > 0)
            {
                AddSplitter(grid, orientation);
            }

            AddContentDefinition(grid, orientation, proportion);

            var child = BuildNode(children[index]);
            var slot = index * 2;

            if (orientation == Orientation.Horizontal)
            {
                Grid.SetColumn(child, slot);
            }
            else
            {
                Grid.SetRow(child, slot);
            }

            grid.Children.Add(child);
        }

        return grid;
    }

    private static void AddSplitter(Grid grid, Orientation orientation)
    {
        var splitter = new GridSplitter
        {
            ResizeDirection = orientation == Orientation.Horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            // 透明的"抓取带"：视觉上靠间距分区，不靠分割线（符合项目的极简 UI 要求）。
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        splitter.Classes.Add("layout-splitter");

        var slot = grid.Children.Count;

        if (orientation == Orientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6, GridUnitType.Pixel)));
            Grid.SetColumn(splitter, slot);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(6, GridUnitType.Pixel)));
            Grid.SetRow(splitter, slot);
        }

        grid.Children.Add(splitter);
    }

    private static void AddContentDefinition(Grid grid, Orientation orientation, double proportion)
    {
        var length = new GridLength(Math.Max(proportion, 0.0001), GridUnitType.Star);

        if (orientation == Orientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(length));
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(length));
        }
    }

    private Control BuildPanel(LayoutNode node)
    {
        var tabs = new TabControl
        {
            Padding = default,
            // 顶部留出标题，底部是 TabControl 自带的标签条。
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        tabs.Classes.Add("tool-panel");

        if (node.Tabs.Count == 0)
        {
            tabs.Items.Add(CreatePlaceholder(node.Title ?? node.Id ?? "panel"));
        }
        else
        {
            foreach (var tabId in node.Tabs)
            {
                tabs.Items.Add(CreateTabItem(tabId));
            }
        }

        var container = new Border
        {
            Child = tabs,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        container.Classes.Add(PanelChromeClass);

        if (!node.Visible)
        {
            container.IsVisible = false;
        }

        return container;
    }

    private Control BuildDocumentArea(LayoutNode node)
    {
        var tabs = new TabControl
        {
            Padding = default,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        tabs.Classes.Add("document-area");

        foreach (var tabId in node.InitialTabs)
        {
            tabs.Items.Add(CreateTabItem(tabId));
        }

        if (tabs.Items.Count == 0)
        {
            tabs.Items.Add(CreatePlaceholder(null));
        }

        return tabs;
    }

    private TabItem CreateTabItem(string tabId)
    {
        var descriptor = TabCatalog.Find(tabId);

        if (descriptor is null)
        {
            // 未登记的 id 在 Core 层已有测试拦截；这里仍要给出可见的说明，
            // 因为用户可能编辑了布局文件。
            return new TabItem
            {
                Header = tabId,
                Content = CreateMessage($"未登记的标签页：{tabId}"),
            };
        }

        var content = _contentFactory.CreateContent(descriptor);

        return new TabItem
        {
            Header = _contentFactory.GetTitle(descriptor),
            Content = content ?? CreateMessage($"{_contentFactory.GetTitle(descriptor)}（尚未实现）"),
        };
    }

    private static Control CreatePlaceholder(string? title)
        => CreateMessage(string.IsNullOrWhiteSpace(title) ? "（此面板暂无内容）" : $"{title}（尚未实现）");

    private static Control CreateMessage(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        text.Classes.Add(PlaceholderClass);

        return new Border { Child = text, Padding = new Thickness(12) };
    }

    private static Control BuildUnsupported(LayoutNode node)
        => CreateMessage($"不支持的节点类型：{node.Kind}");
}
