using MetalForge.Core.Configuration;

namespace MetalForge.Core;

/// <summary>
/// Core 层的服务注册入口。App 层只调用本方法，不直接 new 具体实现，
/// 便于测试中替换（例如注入假的资源目录）。
/// </summary>
public static class MetalForgeCoreServices
{
    /// <summary>
    /// 解析应用运行所需的默认目录布局。
    /// </summary>
    /// <param name="portable">便携模式：用户配置与工具都放程序目录，不写系统用户目录。</param>
    /// <param name="assetsRootOverride">测试用：显式指定内置资源目录。</param>
    /// <param name="userRootOverride">测试用：显式指定用户目录。</param>
    public static AssetOptions CreateDefaultAssetOptions(
        bool portable = false,
        string? assetsRootOverride = null,
        string? userRootOverride = null)
    {
        var applicationDirectory = AppContext.BaseDirectory;

        var builtInAssets = assetsRootOverride
            ?? ResolveBuiltInAssetsDirectory(applicationDirectory);

        var userDirectory = userRootOverride
            ?? (portable
                ? Path.Combine(applicationDirectory, "userdata")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MetalForge"));

        return new AssetOptions
        {
            BuiltInAssetsDirectory = builtInAssets,
            UserDirectory = userDirectory,
            Portable = portable,
        };
    }

    /// <summary>
    /// 定位内置 assets 目录。
    /// 发布布局：&lt;程序目录&gt;\assets；
    /// 开发布局：从输出目录向上找到仓库根（含 MetalForge.sln）再拼 assets。
    /// </summary>
    public static string ResolveBuiltInAssetsDirectory(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var direct = Path.Combine(startDirectory, "assets");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "assets");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(current.FullName, "MetalForge.sln")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        // 找不到也返回预期路径：调用方会给出"资源缺失"诊断，而不是崩溃。
        return direct;
    }
}
