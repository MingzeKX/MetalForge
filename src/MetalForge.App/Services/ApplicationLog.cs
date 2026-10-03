using MetalForge.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MetalForge.App.Services;

/// <summary>
/// 源生成的日志方法。
///
/// 为什么不用 <c>logger.LogInformation(...)</c>：
/// CA1848 要求使用 LoggerMessage 委托以避免装箱与字符串格式化开销，
/// CA1873 进一步指出"即使日志级别被禁用，实参仍会被求值"。
/// 用 [LoggerMessage] 源生成器一次解决两个问题，同时让日志字段结构化。
/// </summary>
internal static partial class ApplicationLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "MetalForge 启动：{BrandName} {Version}")]
    public static partial void ApplicationStarted(ILogger logger, string brandName, string version);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "运行环境：.NET {RuntimeVersion}，{OperatingSystem}")]
    public static partial void RuntimeEnvironment(ILogger logger, string runtimeVersion, string operatingSystem);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "配置诊断 [{Severity}] {Code}: {Message}")]
    public static partial void ConfigurationDiagnostic(ILogger logger, DiagnosticSeverity severity, string code, string message);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning, Message = "配置诊断 [{Severity}] {Code}: {Message}")]
    public static partial void ConfigurationWarning(ILogger logger, DiagnosticSeverity severity, string code, string message);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Error, Message = "配置诊断 [{Severity}] {Code}: {Message}")]
    public static partial void ConfigurationError(ILogger logger, DiagnosticSeverity severity, string code, string message);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "配置加载失败，将使用内置兜底默认值启动。")]
    public static partial void ConfigurationLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "配置已重新加载：主题 {ThemeId}，可用主题 {ThemeCount} 套，诊断 {DiagnosticCount} 条。")]
    public static partial void ConfigurationReloaded(ILogger logger, string themeId, int themeCount, int diagnosticCount);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Debug, Message = "无法获取屏幕工作区，使用配置的窗口尺寸。")]
    public static partial void ScreenInformationUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "窗口已按屏幕容量调整：{Width}x{Height}（可用 {AvailableWidth}x{AvailableHeight}）。")]
    public static partial void WindowClampedToScreen(ILogger logger, double width, double height, int availableWidth, int availableHeight);

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "工具链探测完成：{Readiness}，共 {ToolCount} 项，缺失必需 {MissingCount} 项，耗时 {ElapsedMilliseconds:F0} ms。")]
    public static partial void ToolchainProbed(ILogger logger, string readiness, int toolCount, int missingCount, double elapsedMilliseconds);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "工具链探测失败，界面将显示为未检测到工具链。")]
    public static partial void ToolchainProbeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Error, Message = "工具清单为空：assets/targets/tools.json 无法读取或没有有效条目，工具链健康检查将无内容可显示。")]
    public static partial void ToolchainCatalogEmpty(ILogger logger);

    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning, Message = "语法高亮定义加载失败：{Diagnostic}")]
    public static partial void SyntaxHighlightingFailed(ILogger logger, string diagnostic);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Information,
        Message = "窗口几何[{Stage}]：bounds {BoundsWidth}x{BoundsHeight}，work {WorkX},{WorkY} {WorkWidth}x{WorkHeight}，screenScaling {ScreenScaling}，renderScaling {RenderScaling}，window {WindowWidth}x{WindowHeight} @ {WindowX},{WindowY}")]
    public static partial void WindowGeometry(
        ILogger logger,
        string stage,
        int boundsWidth,
        int boundsHeight,
        int workX,
        int workY,
        int workWidth,
        int workHeight,
        double screenScaling,
        double renderScaling,
        double windowWidth,
        double windowHeight,
        int windowX,
        int windowY);
}
