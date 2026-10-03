using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MetalForge.App.ViewModels;

namespace MetalForge.App.Views;

/// <summary>
/// 项目浏览器面板。
///
/// 这里处理两件 XAML 表达不了的事：
///   1) 双击文件 → 用编辑器打开。TreeView 的条目双击没有现成的命令可绑，
///      而"双击 = 打开"是文件管理器的通用约定。
///   2) 展开根节点。<c>TreeViewItem</c> 有自己的 <c>IsExpanded</c> 属性（默认 false），
///      它不会自动跟随 ViewModel；不处理的话用户看到的是"项目树只有一行且收起"。
///      用样式绑定 <c>IsExpanded</c> 不行：TreeViewItem 的 DataContext 与
///      x:DataType 不是同一个类型，编译期绑定会失败。
/// </summary>
public partial class ProjectExplorerView : UserControl
{
    public ProjectExplorerView()
    {
        InitializeComponent();
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);

        // 展开需要 TreeViewItem 已经生成，因此排到布局完成之后。
        AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(ExpandRootNodes, DispatcherPriority.Loaded);
    }

    /// <summary>展开第一层节点（项目根），让用户一打开项目就看到内容。</summary>
    private void ExpandRootNodes()
    {
        if (fileTree is null)
        {
            return;
        }

        foreach (var item in fileTree.GetRealizedContainers())
        {
            if (item is TreeViewItem treeViewItem)
            {
                treeViewItem.IsExpanded = true;
            }
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is not ProjectExplorerViewModel viewModel)
        {
            return;
        }

        if (args.Source is not Control source)
        {
            return;
        }

        // 从被点击的元素向上找到承载节点数据的控件。
        var node = FindNode(source);
        if (node is null)
        {
            return;
        }

        viewModel.ActivateNode(node);
        args.Handled = true;
    }

    private static ProjectTreeNodeViewModel? FindNode(Control source)
    {
        for (var current = source; current is not null; current = current.Parent as Control)
        {
            if (current.DataContext is ProjectTreeNodeViewModel node)
            {
                return node;
            }
        }

        return null;
    }
}
