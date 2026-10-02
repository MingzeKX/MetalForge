using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MetalForge.App.Services;
using MetalForge.App.ViewModels;
using MetalForge.App.Views;
using MetalForge.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MetalForge.App;

public partial class App : Application
{
    private IConfigurationService _configurationService = null!;
    private ThemeResourceService _themeResources = null!;
    private ILogger<App> _logger = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 配置必须在创建任何窗口之前加载完毕：
        // 视图通过 DynamicResource 引用主题，资源不存在时会静默回退，
        // 因此"先有主题、后有窗口"是硬顺序。
        _configurationService = Program.Services.GetRequiredService<IConfigurationService>();
        _themeResources = Program.Services.GetRequiredService<ThemeResourceService>();
        _logger = Program.Services.GetRequiredService<ILogger<App>>();

        LoadConfiguration();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateMainWindow();
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void LoadConfiguration()
    {
        try
        {
            _configurationService.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 配置不可读时必须仍能启动：使用运行时兜底值，并把原因记进日志。
            ApplicationLog.ConfigurationLoadFailed(_logger, exception);
        }

        var configuration = _configurationService.Current;
        _themeResources.Apply(this, configuration.Theme);

        foreach (var diagnostic in configuration.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        ApplicationLog.ApplicationStarted(_logger, configuration.Branding.Name, configuration.Branding.Version);

        // CA1873：即使日志被禁用，实参也会被求值；这里显式守卫，避免无意义的环境字符串拼接。
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var runtimeVersion = Environment.Version.ToString(3);
            var operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            ApplicationLog.RuntimeEnvironment(_logger, runtimeVersion, operatingSystem);
        }

        _configurationService.StartWatching();
        _configurationService.ConfigurationChanged += OnConfigurationChanged;
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

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                window.Title = configuration.Branding.FormatWindowTitle();
            }
        });
    }

    private MainWindow CreateMainWindow()
    {
        var branding = _configurationService.Current.Branding;
        var welcome = Program.Services.GetRequiredService<WelcomeViewModel>();
        welcome.UpdateFrom(_configurationService.Current);

        var window = new MainWindow
        {
            Title = branding.FormatWindowTitle(),
            MinWidth = branding.Window.MinimumWidth,
            MinHeight = branding.Window.MinimumHeight,
            DataContext = welcome,
        };

        ApplyInitialSize(window, branding.Window);

        // 窗口完成首次布局后再钳制一次：只有此时 Width/Height 才是真实值。
        // 单纯依赖创建前的估算，会在小屏幕上把面板和状态栏推到屏幕之外
        // （本项目在 150% 缩放的 1280x720 屏幕上实测到了这个现象）。
        window.Opened += (_, _) =>
        {
            LogWindowGeometry(window, "opened");
            ClampToScreen(window);
            LogWindowGeometry(window, "clamped");
        };

        return window;
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
        _configurationService.ConfigurationChanged -= OnConfigurationChanged;
        _configurationService.Dispose();
    }
}
