using Avalonia.Controls;
using MetalForge.App.ViewModels;

namespace MetalForge.App.Views;

public partial class ToolchainHealthView : UserControl
{
    public ToolchainHealthView()
    {
        InitializeComponent();
    }

    /// <summary>首次显示时触发一次探测（由宿主调用，避免在构造函数里做 I/O）。</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
        => DataContext is ToolchainHealthViewModel viewModel
            ? viewModel.RefreshAsync(cancellationToken)
            : Task.CompletedTask;
}
