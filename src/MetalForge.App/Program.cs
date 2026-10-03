using Avalonia;
using Avalonia.Controls;
using MetalForge.App.Services;
using MetalForge.App.ViewModels;
using MetalForge.Core;
using MetalForge.Core.Configuration;
using MetalForge.Core.Layout;
using MetalForge.Core.Localization;
using MetalForge.Core.Processes;
using MetalForge.Core.Toolchains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MetalForge.App;

/// <summary>
/// 应用装配点。职责边界：
///   - 构造服务容器（Core 提供服务，App 负责接线）；
///   - 加载配置并把主题翻译成 Avalonia 资源；
///   - 创建主窗口。
/// 业务逻辑不写在这里。
/// </summary>
internal static class Program
{
    /// <summary>服务容器；在 <see cref="Main"/> 中构建。</summary>
    internal static ServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        Services = BuildServices(args);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Services.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static ServiceProvider BuildServices(string[] args)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var portable = args.Contains("--portable", StringComparer.OrdinalIgnoreCase);
        var assetOptions = MetalForgeCoreServices.CreateDefaultAssetOptions(portable);

        services.AddSingleton(assetOptions);
        services.AddSingleton(provider => new AssetResolver(provider.GetRequiredService<AssetOptions>()));

        // 配置与本地化：两者都从 assets/ 读取，且都需要把诊断暴露给界面。
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<IConfigurationService>(provider => provider.GetRequiredService<ConfigurationService>());
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ILocalizationService>(provider => provider.GetRequiredService<LocalizationService>());

        services.AddSingleton<ThemeResourceService>();

        // 进程执行与工具链探测。
        services.AddSingleton<IProcessRunner>(_ => new ProcessRunner());
        services.AddSingleton<IToolLocator>(provider => CreateToolLocator(provider, assetOptions));

        // 界面：ViewModels 与标签页工厂。
        services.AddSingleton<WelcomeViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // 语法高亮目录：通用语言用 AvaloniaEdit 的内置定义，
        // NASM/GAS/链接脚本/Makefile 等 OSDev 常用格式由本项目提供（内置里没有）。
        // 模板里的颜色角色由当前主题填充 —— 因此它的构造依赖配置服务。
        //
        // 注册时顺带跑一次自检并把结果填进工具链面板：
        // 高亮失效不会报错，只会静默变成一片单色文字，因此必须能被看见。
        services.AddSingleton(provider =>
        {
            var theme = provider.GetRequiredService<IConfigurationService>().Current.Theme;
            var catalog = new SyntaxHighlightingCatalog(theme.Syntax);
            catalog.RegisterAll();

            var logger = provider.GetRequiredService<ILogger<SyntaxHighlightingCatalog>>();
            foreach (var diagnostic in catalog.Diagnostics)
            {
                ApplicationLog.SyntaxHighlightingFailed(logger, diagnostic.ToString());
            }

            var health = provider.GetRequiredService<ToolchainHealthViewModel>();
            health.SetHighlightStatuses(catalog.SelfTest());

            return catalog;
        });

        services.AddSingleton<EditorViewModel>(provider => new EditorViewModel(
            provider.GetRequiredService<ILocalizationService>(),
            filePath => provider.GetRequiredService<SyntaxHighlightingCatalog>().FindForFile(filePath)));

        services.AddSingleton<ToolchainHealthViewModel>(provider => new ToolchainHealthViewModel(
            provider.GetRequiredService<IToolLocator>(),
            key => provider.GetRequiredService<ILocalizationService>()[key],
            (key, arguments) => provider.GetRequiredService<ILocalizationService>().Format(key, arguments)));

        services.AddSingleton<TabContentFactory>(provider => new TabContentFactory(
            key => provider.GetRequiredService<ILocalizationService>()[key],
            provider.GetRequiredService<WelcomeViewModel>,
            provider.GetRequiredService<ToolchainHealthViewModel>,
            provider.GetRequiredService<AboutViewModel>,
            provider.GetRequiredService<SettingsViewModel>,
            provider.GetRequiredService<EditorViewModel>));

        // 文档区：它是"已打开的文件"的唯一真相源，因此必须在布局构建器之前注册。
        services.AddSingleton<DocumentAreaViewModel>(provider => new DocumentAreaViewModel(
            provider.GetRequiredService<WelcomeViewModel>,
            provider.GetRequiredService<ToolchainHealthViewModel>,
            provider.GetRequiredService<AboutViewModel>,
            provider.GetRequiredService<SettingsViewModel>,
            () => provider.GetRequiredService<ShellViewModel>().ActiveEditorDocument,
            key => provider.GetRequiredService<ILocalizationService>()[key]));

        services.AddSingleton(provider =>
        {
            var factory = provider.GetRequiredService<TabContentFactory>();
            var documentArea = provider.GetRequiredService<DocumentAreaViewModel>();

            var builder = new LayoutControlBuilder(
                factory,
                () => new Views.DocumentAreaView { DataContext = documentArea });

            return (Func<LayoutPreset, Control>)(preset => builder.Build(preset.Root));
        });

        services.AddSingleton<ShellViewModel>(provider => new ShellViewModel(
            provider.GetRequiredService<IConfigurationService>(),
            provider.GetRequiredService<ILocalizationService>(),
            provider.GetRequiredService<Func<LayoutPreset, Control>>(),
            provider.GetRequiredService<EditorViewModel>(),
            provider.GetRequiredService<DocumentAreaViewModel>()));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// 构造工具链探测器：读取 assets/targets/tools.json，并让探测结果可被界面订阅。
    /// 探测本身不在启动路径上同步执行（见 App.ProbeToolchainAsync）。
    /// </summary>
    private static ToolLocator CreateToolLocator(IServiceProvider provider, AssetOptions assetOptions)
    {
        var resolver = provider.GetRequiredService<AssetResolver>();
        var (configuration, diagnostics) = resolver.ResolveValidated("targets/tools.json", "targets/schema/tools.schema.json");
        var logger = provider.GetRequiredService<ILogger<ToolLocator>>();

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity >= Core.Diagnostics.DiagnosticSeverity.Warning)
            {
                ApplicationLog.ConfigurationWarning(logger, diagnostic.Severity, diagnostic.Code, diagnostic.Message);
            }
        }

        var requirements = configuration.Root is null
            ? []
            : ToolchainCatalog.ReadRequirements(configuration.Root);

        if (requirements.Count == 0)
        {
            ApplicationLog.ToolchainCatalogEmpty(logger);
        }

        var searchOptions = new ToolSearchOptions
        {
            UserDirectory = assetOptions.UserDirectory,
            ManagedToolsDirectory = Path.Combine(
                Path.GetDirectoryName(assetOptions.UserDirectory) ?? assetOptions.UserDirectory,
                "tools"),
            SearchSystemPath = true,
            SearchKnownLocations = true,
            ProbeVersions = true,
        };

        return new ToolLocator(
            requirements,
            provider.GetRequiredService<IProcessRunner>(),
            searchOptions,
            diagnostic => ApplicationLog.ConfigurationWarning(logger, diagnostic.Severity, diagnostic.Code, diagnostic.Message));
    }
}
