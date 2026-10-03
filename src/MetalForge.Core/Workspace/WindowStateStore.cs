using System.Text.Json;
using System.Text.Json.Serialization;
using MetalForge.Core.Configuration;

namespace MetalForge.Core.Workspace;

/// <summary>
/// 记住的窗口几何信息。
/// 字段用可空类型：首次启动时没有任何值，而"没有值"与"值为 0"必须能区分开
/// （0 是一个非法的窗口坐标，不应该被当成有效位置）。
/// </summary>
public sealed record WindowGeometry
{
    public int? X { get; init; }

    public int? Y { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public bool IsMaximized { get; init; }

    /// <summary>
    /// 位置与尺寸是否都有效（可以安全地应用）。
    /// 这是计算属性，不参与序列化 —— 写进文件只会给"用户偏好"多出一个会误导人的字段
    /// （它看起来像设置，实际上永远由其他字段推导）。
    /// </summary>
    [JsonIgnore]
    public bool HasUsableBounds =>
        X is not null && Y is not null && Width is > 200 && Height is > 150;

    /// <summary>校验：负坐标在多显示器布局下合法，但极端值说明屏幕配置已变。</summary>
    public WindowGeometry Clamp(int minimumWidth, int minimumHeight, int maximumWidth, int maximumHeight) => this with
    {
        Width = Width is { } width ? Math.Clamp(width, minimumWidth, maximumWidth) : null,
        Height = Height is { } height ? Math.Clamp(height, minimumHeight, maximumHeight) : null,
    };
}

/// <summary>
/// 窗口几何信息的持久化。
///
/// 为什么单独一个文件（而不是写回 <c>assets/branding/app.json</c>）：
/// 用户目录下的配置是"用户偏好"，程序不该改写随应用分发的默认品牌文件。
/// 记不住的后果很具体：每次启动都要重新摆窗口，这是重度用户最先抱怨的点。
/// </summary>
public interface IWindowStateStore
{
    /// <summary>读取上次记住的几何信息；没有或损坏时返回 null。</summary>
    WindowGeometry? Load();

    /// <summary>保存几何信息。失败不应影响正在关闭的应用。</summary>
    void Save(WindowGeometry geometry);
}

/// <summary>把窗口几何信息存到用户配置目录的 <c>window.json</c>。</summary>
public sealed class WindowStateStore : IWindowStateStore
{
    private const string FileName = "window.json";

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;

    public WindowStateStore(AssetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _directory = resolver.UserDirectory;
    }

    /// <summary>实际使用的文件路径（测试与诊断用）。</summary>
    public string FilePath => Path.Combine(_directory, FileName);

    public WindowGeometry? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var text = File.ReadAllText(FilePath, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<WindowGeometry>(text, _options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 记住窗口位置是"锦上添花"。文件损坏或不可读时静默回到默认几何，
            // 绝不因为在启动路径上抛异常而阻止应用打开。
            return null;
        }
    }

    public void Save(WindowGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(geometry, _options);
            File.WriteAllText(FilePath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 保存失败只影响下次启动的位置，不值得打断用户关闭应用。
        }
    }
}
