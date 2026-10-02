using System.Runtime.InteropServices;

namespace MetalForge.App.Services;

/// <summary>
/// 探测真实帧缓冲尺寸（物理像素）。
///
/// 为什么需要它：平台 API 报告的屏幕尺寸并不总是可信。
/// 本机实测（远程/虚拟显示 + 150% 缩放）报告为 2256x1504，而真实帧缓冲只有 1280x720；
/// 任何完全信任报告值的窗口定位都会把界面摆到屏幕之外，用户看到的是"面板消失了"。
/// <see cref="System.Windows.Forms"/> 不在依赖里，因此直接调用 user32。
///
/// 非 Windows 平台返回 null，调用方据此退化为"只使用平台报告值"。
/// </summary>
internal static class FramebufferProbe
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    /// <summary>返回真实帧缓冲尺寸；无法获取时返回 null。</summary>
    public static (int Width, int Height)? TryGetFramebufferSize()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var width = GetSystemMetrics(SmCxScreen);
            var height = GetSystemMetrics(SmCyScreen);

            return width > 0 && height > 0 ? (width, height) : null;
        }
        catch (DllNotFoundException)
        {
            // 理论上不会发生（已判断平台）；真发生了也不能让窗口定位失败。
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    // 用 DllImport 而不是 LibraryImport：后者要求 AllowUnsafeBlocks，
    // 而为了读两个整数就把整个应用标记为允许 unsafe 代码，代价不成比例。
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
