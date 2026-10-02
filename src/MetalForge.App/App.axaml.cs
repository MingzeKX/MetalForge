using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MetalForge.App.Services;
using MetalForge.App.ViewModels;
using MetalForge.App.Views;
using MetalForge.Core.Configuration;
using MetalForge.Core.Layout;
using MetalForge.Core.Localization;
using MetalForge.Core.Toolchains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MetalForge.App;

public partial class App : Application
{
    private IConfigurationService _configuration = null!;
    private ILocalizationService _localization = null!;
    private IToolLocator _toolLocator = null!;
    private ThemeResourceService _themeResources = null!;
    private ShellViewModel _shell = null!;
    private ILogger<App> _logger = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 顺序是硬要求：配置 → 主题资源 → 本地化 → 布局/内容工厂 → 窗口。
        // 视图通过 DynamicResource 引用主题、通过服务查询文案，缺任何一步都会
        // 表现为"界面渲染出来了但内容是空的或颜色不对"。
        _configuration = Program.Services.GetRequiredService<IConfigurationService>();
        _localization = Program.Services.GetRequiredService<ILocalizationService>();
        _toolLocator = Program.Services.GetRequiredService<IToolLocator>();
        _themeResources = Program.Services.GetRequiredService<ThemeResourceService>();
        _logger = Program.Services.GetRequiredService<ILogger<App>>();

        LoadConfiguration();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _shell = Program.Services.GetRequiredService<ShellViewModel>();
            _shell.Rebuild();

            desktop.MainWindow = CreateMainWindow(_shell);
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void LoadConfiguration()
    {
        try
        {
            _configuration.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 配置不可读时必须仍能启动：使用运行时兜底值，并把原因记进日志。
            ApplicationLog.ConfigurationLoadFailed(_logger, exception);
        }

        (Program.Services.GetRequiredService<LocalizationService>()).Reload();

        var configuration = _configuration.Current;
        _themeResources.Apply(this, configuration.Theme);

        foreach (var diagnostic in configuration.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        foreach (var diagnostic in Program.Services.GetRequiredService<LocalizationService>().Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        ApplicationLog.ApplicationStarted(_logger, configuration.Branding.Name, configuration.Branding.Version);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var runtimeVersion = Environment.Version.ToString(3);
            var operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            ApplicationLog.RuntimeEnvironment(_logger, runtimeVersion, operatingSystem);
        }

        _configuration.StartWatching();
        _configuration.ConfigurationChanged += OnConfigurationChanged;
    }

    private void LogDiagnostic(Core.Diagnostics.Diagnostic diagnostic)
    {
        switch (diagnostic.Severity)
        {
            case Core.Diagnostics.DiagnosticSeverity.Critical:
            case Core.Diagnostics.DiagnosticSeverity.Error:
                ApplicationLog.ConfigurationError(_logger, diagnostic.Severity, diagnostic.Code, diagnostic.Message);
                break;
            case Core.Diagnostics.DiagnosticSeverity.Warning:
                ApplicationLog.ConfigurationWarning(_logger, diagnostic.Severity, diagnostic.Code, diagnostic.Message);
                break;
            default:
                ApplicationLog.ConfigurationDiagnostic(_logger, diagnostic.Severity, diagnostic.Code, diagnostic.Message);
                break;
        }
    }

    private void OnConfigurationChanged(object? sender, MetalForgeConfiguration configuration)
    {
        // 文件监视器在后台线程触发；UI 更新必须切回 UI 线程。
        Dispatcher.UIThread.Post(() =>
        {
            _themeResources.Apply(this, configuration.Theme);

            foreach (var diagnostic in configuration.Diagnostics)
            {
                LogDiagnostic(diagnostic);
            }

            ApplicationLog.ConfigurationReloaded(
                _logger,
                configuration.Theme.Id,
                configuration.AvailableThemes.Count,
                configuration.Diagnostics.Count);

            // 布局与菜单都要按新配置重建（用户可能改了布局预设）。
            _shell.Rebuild();
        });
    }

    private MainWindow CreateMainWindow(ShellViewModel shell)
    {
        var branding = _configuration.Current.Branding;
        var window = new MainWindow
        {
            DataContext = shell,
            MinWidth = branding.Window.MinimumWidth,
            MinHeight = branding.Window.MinimumHeight,
        };

        ApplyInitialSize(window, branding.Window);
        window.Opened += (_, _) => ClampToScreen(window);

        // 启动后异步探测一次工具链：不阻塞首屏，也不在无工具时让界面显示"未知"。
        _ = ProbeToolchainAsync(window);

        return window;
    }

    private async Task ProbeToolchainAsync(Window window)
    {
        try
        {
            var report = await _toolLocator.CheckHealthAsync(CancellationToken.None).ConfigureAwait(true);
            _configuration.SetToolHealth(report);
            _shell.Rebuild();

            // CA1873：实参在日志禁用时也会求值，因此先算好再传。
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var readiness = report.Readiness.ToString();
                var toolCount = report.Tools.Count;
                var missingCount = report.MissingRequiredTools().Count;
                ApplicationLog.ToolchainProbed(_logger, readiness, toolCount, missingCount, report.Duration.TotalMilliseconds);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 探测失败不影响界面可用性：状态栏会显示"缺少工具链"，详情在健康面板里。
            ApplicationLog.ToolchainProbeFailed(_logger, exception);
        }
    }

    /// <summary>
    /// 按屏幕工作区裁剪初始尺寸与位置。
    ///
    /// 为什么必须做这件事：品牌配置里的尺寸是逻辑像素，而屏幕容量要用"物理像素 ÷ 缩放比"换算。
    /// 在 150% 缩放的笔记本屏幕上，1440x900 的逻辑窗口会占 2160x1350 物理像素，
    /// 结果右侧面板与底部状态栏直接落在可视区域之外——用户看到的是"界面缺了一块"。
    /// 本项目第一次截图正是如此：右侧属性面板与状态栏整块不可见。
    /// </summary>
    private void ApplyInitialSize(MainWindow window, WindowBranding brandingWindow)
    {
        var width = brandingWindow.DefaultWidth;
        var height = brandingWindow.DefaultHeight;

        try
        {
            var screens = window.Screens;
            var screen = screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
            if (screen is not null)
            {
                var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

                // Bounds 是物理像素，先换算成逻辑像素再比较。
                var logicalBoundsWidth = (int)(screen.Bounds.Width / scaling);
                var logicalBoundsHeight = (int)(screen.Bounds.Height / scaling);

                // 留出边距，避免窗口贴合屏幕边缘导致边框被任务栏或外框吃掉。
                const int edgeMargin = 48;
                var availableWidth = Math.Max(logicalBoundsWidth - edgeMargin, brandingWindow.MinimumWidth);
                var availableHeight = Math.Max(logicalBoundsHeight - edgeMargin, brandingWindow.MinimumHeight);

                width = Math.Min(width, availableWidth);
                height = Math.Min(height, availableHeight);
            }
        }
        catch (InvalidOperationException exception)
        {
            // 平台未提供屏幕信息（例如无头环境）：使用配置值，并记录原因而不是静默跳过。
            ApplicationLog.ScreenInformationUnavailable(_logger, exception);
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (brandingWindow.StartMaximized)
        {
            window.WindowState = WindowState.Maximized;
            return;
        }

        window.Width = Math.Max(width, brandingWindow.MinimumWidth);
        window.Height = Math.Max(height, brandingWindow.MinimumHeight);
    }

    /// <summary>
    /// 窗口打开后二次钳制：把真实尺寸与位置收敛到屏幕工作区内。
    /// 只在确实越界时改动，避免破坏用户（或未来"记住窗口位置"功能）的意图。
    ///
    /// 同时参考 Win32 报告的真实帧缓冲尺寸：远程桌面、虚拟显示、部分多屏组合下，
    /// 平台报告的屏幕尺寸可能大于实际帧缓冲（本机实测 2256x1504 vs 1280x720），
    /// 此时任何基于"报告尺寸"的判断都会把窗口摆到屏幕之外。
    /// </summary>
    private void ClampToScreen(MainWindow window)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            return;
        }

        try
        {
            var screens = window.Screens;
            var screen = screens.ScreenFromWindow(window)
                         ?? screens.Primary
                         ?? (screens.All.Count > 0 ? screens.All[0] : null);
            if (screen is null)
            {
                return;
            }

            var density = window.RenderScaling <= 0 ? screen.Scaling : window.RenderScaling;
            if (density <= 0)
            {
                density = 1.0;
            }

            var workArea = screen.WorkingArea;
            var workAreaLeft = workArea.X / density;
            var workAreaTop = workArea.Y / density;
            var workAreaWidth = workArea.Width / density;
            var workAreaHeight = workArea.Height / density;

            // 真实帧缓冲上限（物理像素 -> 逻辑像素）。取不到时为 0，表示不参与约束。
            var framebuffer = FramebufferProbe.TryGetFramebufferSize();
            if (framebuffer is { } fb)
            {
                var framebufferLogicalWidth = fb.Width / density;
                var framebufferLogicalHeight = fb.Height / density;

                if (workAreaLeft + workAreaWidth > framebufferLogicalWidth)
                {
                    workAreaWidth = Math.Max(framebufferLogicalWidth - workAreaLeft, window.MinWidth);
                }

                if (workAreaTop + workAreaHeight > framebufferLogicalHeight)
                {
                    workAreaHeight = Math.Max(framebufferLogicalHeight - workAreaTop, window.MinHeight);
                }
            }

            var width = window.Width;
            var height = window.Height;
            var sizeChanged = false;

            if (width > workAreaWidth || height > workAreaHeight)
            {
                width = Math.Min(width, workAreaWidth);
                height = Math.Min(height, workAreaHeight);
                sizeChanged = true;
            }

            var position = window.Position;
            var positionX = Math.Min(Math.Max(position.X, workAreaLeft), Math.Max(workAreaLeft + workAreaWidth - width, workAreaLeft));
            var positionY = Math.Min(Math.Max(position.Y, workAreaTop), Math.Max(workAreaTop + workAreaHeight - height, workAreaTop));
            var positionChanged = Math.Abs(positionX - position.X) > 0.5 || Math.Abs(positionY - position.Y) > 0.5;

            if (sizeChanged)
            {
                window.Width = width;
                window.Height = height;
            }

            if (positionChanged)
            {
                window.Position = new PixelPoint((int)positionX, (int)positionY);
            }

            if (sizeChanged || positionChanged)
            {
                ApplicationLog.WindowClampedToScreen(
                    _logger,
                    width,
                    height,
                    (int)workAreaWidth,
                    (int)workAreaHeight);
            }
        }
        catch (InvalidOperationException exception)
        {
            ApplicationLog.ScreenInformationUnavailable(_logger, exception);
        }
    }

    /// <summary>
    /// 诊断用：把屏幕与窗口的几何信息写进日志。
    /// 高 DPI 下的"物理像素 vs 逻辑像素"是本项目踩过坑的地方，
    /// 与其猜，不如在真实环境里读出来。
    /// </summary>
    private void LogWindowGeometry(MainWindow window, string stage)
    {
        try
        {
            var screens = window.Screens;
            var screen = screens.ScreenFromWindow(window) ?? screens.Primary;
            if (screen is null)
            {
                return;
            }

            ApplicationLog.WindowGeometry(
                _logger,
                stage,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.WorkingArea.X,
                screen.WorkingArea.Y,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height,
                screen.Scaling,
                window.RenderScaling,
                window.Width,
                window.Height,
                window.Position.X,
                window.Position.Y);
        }
        catch (InvalidOperationException exception)
        {
            ApplicationLog.ScreenInformationUnavailable(_logger, exception);
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _configuration.ConfigurationChanged -= OnConfigurationChanged;
        _configuration.Dispose();
        _localization.Dispose();
        _toolLocator.Dispose();
    }
}
