using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MetalForge.App.ViewModels;

namespace MetalForge.App.Views;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();

        // 菜单条用 Button + Flyout 而不是 Menu 控件（原因见 MainWindow.axaml 注释）。
        // Button 不会自动打开自己的 Flyout，因此这里统一处理一次，
        // 而不是给每个菜单项挂事件处理器。
        AddHandler(Button.ClickEvent, OnAnyButtonClick, RoutingStrategies.Bubble);

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_shell is not null)
        {
            _shell.FileOpenRequested -= OnFileOpenRequested;
        }

        _shell = DataContext as ShellViewModel;

        if (_shell is not null)
        {
            _shell.FileOpenRequested += OnFileOpenRequested;
        }
    }

    /// <summary>
    /// 弹出文件选择对话框，并把选中的文件交给编辑器。
    /// 对话框与窗口属于 UI 层，因此由视图处理；ViewModel 只发出请求事件。
    /// </summary>
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
    /// 布局自检：把布局区里每个栏位的实际宽度写进日志。
    ///
    /// 为什么需要它：布局比例来自 JSON，但"比例是否真的生效"在截图上很难判断
    /// （本项目两次凭截图误判比例）。把实测值打出来，就不必靠肉眼估算像素。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 等一次布局 pass 完成后再测量，否则拿到的是初始值。
        Dispatcher.UIThread.Post(ReportLayoutMetrics, DispatcherPriority.Loaded);
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
