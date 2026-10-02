using System.Reflection;
using MetalForge.Core.Configuration;

namespace MetalForge.Core.Tests;

/// <summary>
/// 架构约束测试。DESIGN.md 第九节与分层原则不是靠自觉维持的，这里把它变成会失败的测试。
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly CoreAssembly = typeof(MetalForgeCoreServices).Assembly;

    [Fact]
    public void Core_DoesNotReferenceAnyUiAssembly()
    {
        var referenced = CoreAssembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        var uiReferences = referenced
            .Where(name => name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("System.Windows.Forms", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            uiReferences.Length == 0,
            $"MetalForge.Core 不得引用 UI 程序集，实际引用了：{string.Join(", ", uiReferences)}");
    }

    [Fact]
    public void Core_TypesAreNullOrPublic_NoAccidentalInternalLeaks()
    {
        // 只是防止误把公共类型标成 internal 导致 App 层无法使用；
        // 具体可见性由代码评审决定，这里不做过度约束。
        var exported = CoreAssembly.GetExportedTypes();

        Assert.Contains(exported, type => type == typeof(IConfigurationService));
        Assert.Contains(exported, type => type == typeof(ConfigurationService));
        Assert.Contains(exported, type => type == typeof(AssetOptions));
    }

    [Fact]
    public void AllInterfaces_ArePrefixedWithI()
    {
        var offenders = CoreAssembly
            .GetExportedTypes()
            .Where(type => type.IsInterface && !type.Name.StartsWith('I'))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.True(offenders.Length == 0, $"接口命名必须以 I 开头：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void AllAsyncMethods_AreSuffixedWithAsync()
    {
        var offenders = new List<string>();

        foreach (var type in CoreAssembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var returnsTaskLike = typeof(Task).IsAssignableFrom(method.ReturnType)
                                   || method.ReturnType.IsGenericType
                                      && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)
                                   || method.ReturnType == typeof(ValueTask);

                if (returnsTaskLike && !method.Name.EndsWith("Async", StringComparison.Ordinal))
                {
                    offenders.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"返回 Task/ValueTask 的公共方法必须以 Async 结尾：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void PrivateFields_UseUnderscoreCamelCase()
    {
        var offenders = new List<string>();

        foreach (var type in CoreAssembly.GetExportedTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                // 编译器生成的字段（如集合初始化的缓存）不参与约束
                if (field.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
                {
                    continue;
                }

                if (field.Name.StartsWith('<') || field.Name.Contains("k__BackingField", StringComparison.Ordinal))
                {
                    continue;
                }

                // const 是编译期常量，按 .NET 惯例使用 PascalCase（例如 AppAssetPath），
                // 与"实例字段用 _camelCase"并不冲突。
                if (field.IsLiteral)
                {
                    continue;
                }

                if (!field.Name.StartsWith('_'))
                {
                    offenders.Add($"{type.Name}.{field.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"私有字段必须使用 _camelCase：{string.Join(", ", offenders)}");
    }
}
