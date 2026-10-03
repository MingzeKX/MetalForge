using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MetalForge.App.ViewModels;
using MetalForge.Core.Configuration;
using MetalForge.Core.Workspace;

namespace MetalForge.App.Views;

public partial class MainWindow : Window
{
    private readonly IWindowStateStore? _windowStateStore;
    private readonly WindowBranding? _windowBranding;
    private ShellViewModel? _shell;
    private bool _geometryRestored;

    /// <summary>
    /// 窗口最大化之前记录的"普通"几何信息。
    ///
    /// 为什么需要手动记录：Avalonia 12 的 <c>Window</c> 没有 WPF 的 <c>RestoreBounds</c>。
    /// 若在最大化状态下直接读 <c>Width/Height</c> 并存起来，下次取消最大化时窗口会占满屏幕 ——
    /// 这是"记住位置"功能最典型的实现错误。
    /// </summary>
    private Rect? _lastNormalBounds;

    /// <summary>无参构造仅用于 XAML 设计器；运行时不走它。</summary>
    public MainWindow()
    {
        InitializeComponent();
        WireUp();
    }

    public MainWindow(IWindowStateStore windowStateStore, WindowBranding windowBranding, string? iconPath)
    {
        ArgumentNullException.ThrowIfNull(windowStateStore);
        ArgumentNullException.ThrowIfNull(windowBranding);

        InitializeComponent();
        WireUp();

        _windowStateStore = windowStateStore;
        _windowBranding = windowBranding;

        ApplyIcon(iconPath);
    }

    private void WireUp()
    {
        // 菜单条用 Button + Flyout 而不是 Menu 控件（原因见 MainWindow.axaml 注释）。
        // Button 不会自动打开自己的 Flyout，因此这里统一处理一次，
        // 而不是给每个菜单项挂事件处理器。
        AddHandler(Button.ClickEvent, OnAnyButtonClick, RoutingStrategies.Bubble);

        DataContextChanged += OnDataContextChanged;

        // 在窗口由普通变为最大化/最小化之前，记下当时的几何信息。
        // 用 PropertyChanged 而不是 OnPropertyChanged 重写：需要在状态真正改变之前取值。
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != WindowStateProperty)
        {
            return;
        }

        var previous = args.OldValue is WindowState oldState ? oldState : WindowState.Normal;
        var current = args.NewValue is WindowState newState ? newState : WindowState.Normal;

        // 只有在"离开 Normal"时才记录；从最大化恢复为 Normal 时不覆盖，
        // 因为此时的 Width/Height 就是还原后的值，会在下次保存时自然取到。
        if (previous == WindowState.Normal && current != WindowState.Normal)
        {
            CaptureNormalBounds();
        }
    }

    private void CaptureNormalBounds()
    {
        var width = Width;
        var height = Height;

        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            return;
        }

        _lastNormalBounds = new Rect(Position.X, Position.Y, width, height);
    }

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_shell is not null)
        {
            _shell.FileOpenRequested -= OnFileOpenRequested;
            _shell.ProjectOpenRequested -= OnProjectOpenRequested;
        }

        _shell = DataContext as ShellViewModel;

        if (_shell is not null)
        {
            _shell.FileOpenRequested += OnFileOpenRequested;
            _shell.ProjectOpenRequested += OnProjectOpenRequested;
        }
    }

    /// <summary>
    /// 设置窗口图标。
    /// 图标文件缺失时只记录一行日志：没有图标的窗口仍然能用，
    /// 而因为图标读不到就崩溃是不可接受的。
    /// </summary>
    private void ApplyIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            Console.WriteLine($"[shell] 窗口图标不可用：{iconPath ?? "(未配置)"}");
            return;
        }

        try
        {
            using var stream = File.OpenRead(iconPath);
            Icon = new WindowIcon(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"[shell] 读取窗口图标失败：{exception.Message}");
        }
    }

    /// <summary>弹出文件选择对话框，并把选中的文件交给编辑器。</summary>
    private async void OnFileOpenRequested(object? sender, EventArgs args)
    {
        if (_shell is null)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开文件",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("OSDev 源文件")
                    {
                        Patterns = ["*.c", "*.h", "*.cpp", "*.hpp", "*.S", "*.s", "*.asm", "*.nasm",
                                    "*.ld", "*.lds", "*.mk", "Makefile", "*.dts", "*.dtsi",
                                    "*.inf", "*.dec", "*.dsc", "*.cfg", "*.json", "*.md"],
                    },
                    FilePickerFileTypes.All,
                ],
            });

            if (files.Count == 0)
            {
                return;
            }

            if (files[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                _shell.OpenInEditor(path);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // 平台不支持文件选择器（例如无头环境）：不能让异常冒到 UI 线程导致进程退出。
            Console.WriteLine($"[shell] 文件选择器不可用：{exception.Message}");
        }
    }

    /// <summary>弹出目录选择对话框，并把选中的目录当作项目打开。</summary>
    private async void OnProjectOpenRequested(object? sender, EventArgs args)
    {
        if (_shell is null)
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "打开项目（选择包含 metalforge.json 的目录）",
                AllowMultiple = false,
            });

            if (folders.Count == 0)
            {
                return;
            }

            if (folders[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                _shell.OpenProject(path);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            Console.WriteLine($"[shell] 目录选择器不可用：{exception.Message}");
        }
    }

    /// <summary>
    /// 点击带 Flyout 的按钮即打开它；已打开则收起（再次点击关闭，符合桌面惯例）。
    /// 不带 Flyout 的按钮（工具栏按钮）由它们自己的 Command 处理，这里直接返回。
    /// </summary>
    private void OnAnyButtonClick(object? sender, RoutedEventArgs args)
    {
        if (args.Source is not Button { Flyout: { } flyout } button)
        {
            return;
        }

        if (flyout.IsOpen)
        {
            flyout.Hide();
            return;
        }

        flyout.ShowAt(button);
    }

    /// <summary>
    /// 窗口打开后：恢复记住的几何信息，并做一次布局自检。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        RestoreGeometry();

        // 等一次布局 pass 完成后再测量，否则拿到的是初始值。
        Dispatcher.UIThread.Post(ReportLayoutMetrics, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 恢复上次的窗口位置与尺寸。
    ///
    /// 安全性检查是必须的：显示器被拔掉、分辨率变化、远程会话分辨率不同时，
    /// 记住的坐标可能落在屏幕之外，表现为"应用启动了但看不到窗口"。
    /// 因此除了钳制尺寸，还要确认位置仍落在某个屏幕的工作区内。
    /// </summary>
    private void RestoreGeometry()
    {
        if (_geometryRestored || _windowStateStore is null || _windowBranding is null)
        {
            return;
        }

        _geometryRestored = true;

        if (!_windowBranding.RememberSizeAndPosition)
        {
            if (_windowBranding.StartMaximized)
            {
                WindowState = WindowState.Maximized;
            }

            return;
        }

        var geometry = _windowStateStore.Load();

        if (geometry is null)
        {
            if (_windowBranding.StartMaximized)
            {
                WindowState = WindowState.Maximized;
            }

            return;
        }

        if (geometry.IsMaximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        if (!geometry.HasUsableBounds)
        {
            return;
        }

        var screens = Screens;
        var width = geometry.Width!.Value;
        var height = geometry.Height!.Value;
        var x = geometry.X!.Value;
        var y = geometry.Y!.Value;

        // 位置必须与某个屏幕的工作区有足够重叠，否则丢弃位置、只保留尺寸。
        var isVisible = screens.All.Any(screen =>
        {
            var workArea = screen.WorkingArea;
            var overlapX = Math.Min(workArea.Right, x + width) - Math.Max(workArea.X, x);
            var overlapY = Math.Min(workArea.Bottom, y + height) - Math.Max(workArea.Y, y);
            return overlapX > 80 && overlapY > 40;
        });

        if (isVisible)
        {
            Position = new PixelPoint(x, y);
        }

        Width = width;
        Height = height;
    }

    /// <summary>窗口关闭前记住几何信息。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        SaveGeometry();
    }

    private void SaveGeometry()
    {
        if (_windowStateStore is null || _windowBranding is null || !_windowBranding.RememberSizeAndPosition)
        {
            return;
        }

        // 最大化时要用"最大化之前的"尺寸；直接记 Width/Height 会把最大化后的尺寸
        // 当成普通尺寸，导致下次取消最大化时窗口占满屏幕。
        var isMaximized = WindowState == WindowState.Maximized;

        Rect bounds;
        if (isMaximized && _lastNormalBounds is { } remembered)
        {
            bounds = remembered;
        }
        else
        {
            var width = Width;
            var height = Height;

            if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
            {
                bounds = _lastNormalBounds ?? default;
            }
            else
            {
                bounds = new Rect(Position.X, Position.Y, width, height);
            }
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        _windowStateStore.Save(new WindowGeometry
        {
            X = (int)Math.Round(bounds.X),
            Y = (int)Math.Round(bounds.Y),
            Width = (int)Math.Round(bounds.Width),
            Height = (int)Math.Round(bounds.Height),
            IsMaximized = isMaximized,
        });
    }

    private void ReportLayoutMetrics()
    {
        if (layoutHost.Content is not Control root)
        {
            Console.WriteLine("[layout] layoutHost has no content");
            return;
        }

        Console.WriteLine($"[layout] root={root.GetType().Name} size={root.Bounds.Width:0.#}x{root.Bounds.Height:0.#}");
        DumpNode(root, depth: 1, maxDepth: 4);
    }

    /// <summary>递归打印布局树中每个容器的子项尺寸，直到找到分栏的实际像素值。</summary>
    private static void DumpNode(Control control, int depth, int maxDepth)
    {
        if (depth > maxDepth || control is not Panel panel)
        {
            return;
        }

        var indent = new string(' ', depth * 2);

        foreach (var child in panel.Children)
        {
            if (child is not Control childControl)
            {
                continue;
            }

            var bounds = childControl.Bounds;
            var share = panel.Bounds.Width > 0 ? bounds.Width / panel.Bounds.Width : 0;
            Console.WriteLine(
                $"[layout] {indent}{childControl.GetType().Name} " +
                $"width={bounds.Width:0.#} height={bounds.Height:0.#} x={bounds.X:0.#} share={share:P0}");

            DumpNode(childControl, depth + 1, maxDepth);
        }
    }
}
