using System.Runtime.CompilerServices;

namespace MetalForge.Core.Tests;

/// <summary>
/// 测试用路径解析。从测试程序集所在目录向上找到仓库根（含 MetalForge.slnx），
/// 这样测试可以直接读取真实的 <c>assets/</c> 资源，而不是复制一份副本
/// （副本会与真实资源漂移，测出来的结论没有意义）。
/// </summary>
internal static class TestPaths
{
    /// <summary>解决方案文件名。.NET 10 SDK 默认生成 .slnx（XML 格式），仓库使用该格式。</summary>
    private const string SolutionFileName = "MetalForge.slnx";

    private static readonly Lazy<string> LazyRepositoryRoot = new(() => FindRepositoryRoot());

    public static string RepositoryRoot => LazyRepositoryRoot.Value;

    public static string AssetsDirectory => Path.Combine(RepositoryRoot, "assets");

    public static bool AssetsAvailable => Directory.Exists(AssetsDirectory);

    public static string Asset(params string[] segments)
        => Path.Combine([AssetsDirectory, .. segments]);

    private static string FindRepositoryRoot([CallerFilePath] string callerFilePath = "")
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        // 兜底：使用源文件位置（编译期常量，始终指向仓库）。
        var sourceDirectory = Path.GetDirectoryName(callerFilePath);
        while (sourceDirectory is not null)
        {
            if (File.Exists(Path.Combine(sourceDirectory, SolutionFileName)))
            {
                return sourceDirectory;
            }

            sourceDirectory = Path.GetDirectoryName(sourceDirectory);
        }

        throw new InvalidOperationException(
            $"找不到仓库根目录（未找到 {SolutionFileName}）。测试需要读取真实的 assets/ 资源。");
    }
}
