using Avalonia.Controls;
using Avalonia.Threading;
using MetalForge.App.ViewModels;

namespace MetalForge.App.Views;

/// <summary>
/// 多标签文档区。标签条由 XAML 绑定，内容控件由 <see cref="DocumentAreaViewModel.CreateContent"/>
/// 按当前选中项创建。
///
/// 两个必须遵守的时序/所有权约束（都是本项目真实踩过的坑）：
///   1) 内容必须在控件附着到视觉树**之后**才能赋值。DataContext 在构造阶段就会被设置，
///      那一刻 documentContentHost 还不存在，直接写入会被丢掉，界面表现为空白。
///   2) 每次必须创建新的内容控件。Avalonia 的控件只能有一个父节点，
///      复用会抛 "already has a visual parent"。
/// </summary>
public partial class DocumentAreaView : UserControl
{
    private DocumentAreaViewModel? _viewModel;

    public DocumentAreaView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => ScheduleApply();
    }

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DocumentAreaViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ScheduleApply();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DocumentAreaViewModel.ActiveTab))
        {
            ScheduleApply();
        }
    }

    /// <summary>排到布局完成后执行，确保内容宿主已存在且已完成一次布局。</summary>
    private void ScheduleApply()
        => Dispatcher.UIThread.Post(ApplyActiveTab, DispatcherPriority.Loaded);

    private void ApplyActiveTab()
    {
        if (_viewModel is null || documentContentHost is null)
        {
            return;
        }

        var tab = _viewModel.ActiveTab;
        var content = tab is null ? null : _viewModel.CreateContent(tab);
        documentContentHost.Content = content;


    }
}
