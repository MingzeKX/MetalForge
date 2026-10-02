using Avalonia;
using MetalForge.App.Services;
using MetalForge.App.ViewModels;
using MetalForge.App.Views;
using MetalForge.Core;
using MetalForge.Core.Configuration;
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
        // 命令行解析（--portable / --assets <dir> / --profile-startup）在 M1 落地，
        // 当前先保证最小可用路径：构建服务容器 -> 启动 UI。
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
        services.AddSingleton(MetalForgeCoreServices.CreateDefaultAssetOptions(portable));
        services.AddSingleton<AssetResolver>(provider =>
            new AssetResolver(provider.GetRequiredService<AssetOptions>()));
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<IConfigurationService>(provider => provider.GetRequiredService<ConfigurationService>());
        services.AddSingleton<ThemeResourceService>();
        services.AddSingleton<WelcomeViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
