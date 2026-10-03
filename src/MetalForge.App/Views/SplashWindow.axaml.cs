using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MetalForge.Core.Configuration;
using MetalForge.Core.Localization;

namespace MetalForge.App.Views;

/// <summary>启动画面的数据：名称、说明、版本与当前状态行。</summary>
public sealed partial class SplashViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appName = "MetalForge";

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private bool _showVersion = true;

    public static SplashViewModel Create(AppBranding branding, ILocalizationService localization, string statusKey)
    {
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(localization);

        return new SplashViewModel
        {
            AppName = branding.Name,
            Description = branding.Description ?? string.Empty,
            VersionText = $"v{branding.Version}",
            ShowVersion = branding.Splash.ShowVersion,
            StatusText = localization[statusKey],
        };
    }
}

/// <summary>
/// 启动画面窗口。
///
/// 它是无边框 + 透明背景的窗口，圆角与配色来自主题样式类（<c>splash-*</c>），
/// 因此这里不出现任何颜色或尺寸字面量。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>无参构造供 XAML 设计器使用。</summary>
    public SplashWindow()
    {
        InitializeComponent();
    }

    public SplashWindow(SplashViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }

    /// <summary>更新状态行（在启动流程的各个阶段调用）。</summary>
    public void SetStatus(string statusText)
    {
        if (DataContext is SplashViewModel viewModel)
        {
            viewModel.StatusText = statusText;
        }
    }
}
