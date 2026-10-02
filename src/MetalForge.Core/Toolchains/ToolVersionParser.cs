using System.Text.RegularExpressions;

namespace MetalForge.Core.Toolchains;

/// <summary>
/// 从工具的版本输出中解析版本号。
///
/// 现实情况：外部工具的版本输出格式五花八门 ——
/// <c>cmake version 3.28.1</c>、<c>NASM version 2.16.01 compiled on ...</c>、
/// <c>GNU ld (GNU Binutils) 2.41</c>、<c>QEMU emulator version 8.1.2 (v8.1.2-dirty)</c>。
/// 因此解析规则是"配置驱动 + 显式失败"，而不是把某个工具的格式硬编码进代码。
///
/// 解析不出确切版本时返回 null 且**不猜测**：版本未知比版本错误更安全，
/// 上层据此把状态标为"已找到但版本未知"，而不是误判为满足要求。
/// </summary>
public static class ToolVersionParser
{
    /// <summary>内置的宽松匹配：第一个形如 <c>1.2.3</c> 或 <c>1.2</c> 的数字序列。</summary>
    private static readonly Regex _fallbackPattern = new(
        @"(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 按 <paramref name="pattern"/> 解析版本；pattern 为 null 时使用内置宽松匹配。
    /// </summary>
    /// <param name="output">工具输出的全文（标准输出与标准错误都会传入）。</param>
    /// <param name="pattern">配置里的正则，需含命名组 <c>major</c>（可选 <c>minor</c>、<c>patch</c>）。</param>
    public static (Version? Version, string? FailureReason) Parse(string? output, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (null, "工具没有输出任何内容。");
        }

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            return ParseWithPattern(output, pattern);
        }

        var match = _fallbackPattern.Match(output);
        return match.Success
            ? (BuildVersion(match), null)
            : (null, "输出中找不到形如 x.y.z 的版本号。");
    }

    private static (Version?, string?) ParseWithPattern(string output, string pattern)
    {
        Regex expression;
        try
        {
            expression = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException exception)
        {
            // 配置写坏正则：这是内置资源的缺陷，必须能被看见，而不是静默降级。
            return (null, $"版本匹配正则不合法：{exception.Message}");
        }

        var match = expression.Match(output);
        if (!match.Success)
        {
            return (null, $"输出不匹配配置的版本正则：{pattern}");
        }

        if (!match.Groups["major"].Success)
        {
            return (null, "版本正则缺少必需的命名捕获组 'major'。");
        }

        return (BuildVersion(match), null);
    }

    private static Version BuildVersion(Match match)
    {
        var major = int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = ParseGroup(match, "minor");
        var patch = ParseGroup(match, "patch");
        var build = ParseGroup(match, "build");

        // Version 的四个分量都必须非负；缺失的按 0 处理。
        return new Version(
            major,
            Math.Max(minor, 0),
            Math.Max(patch, 0),
            Math.Max(build, 0));
    }

    private static int ParseGroup(Match match, string name)
        => match.Groups[name].Success
            ? int.Parse(match.Groups[name].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

    /// <summary>
    /// 判定"找到的版本是否满足最低要求"。
    /// 版本未知时返回 <c>Unknown</c>，由上层决定如何呈现（不猜、不放行也不误报为过旧）。
    /// </summary>
    public static VersionSatisfaction Satisfies(Version? found, Version? minimum)
    {
        if (minimum is null)
        {
            return VersionSatisfaction.NoRequirement;
        }

        if (found is null)
        {
            return VersionSatisfaction.Unknown;
        }

        // 只比较已指定的分量：2.41 与 2.41.0 视为相同。
        var foundNormalized = Normalize(found);
        var minimumNormalized = Normalize(minimum);

        return foundNormalized >= minimumNormalized
            ? VersionSatisfaction.Satisfied
            : VersionSatisfaction.TooOld;
    }

    private static Version Normalize(Version version) => new(
        version.Major,
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0));
}

/// <summary>版本是否满足要求的判定结果。</summary>
public enum VersionSatisfaction
{
    /// <summary>配置里没有最低版本要求。</summary>
    NoRequirement,

    /// <summary>满足要求。</summary>
    Satisfied,

    /// <summary>低于要求。</summary>
    TooOld,

    /// <summary>版本无法解析，无法判定。</summary>
    Unknown,
}
