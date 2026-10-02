using CommunityToolkit.Mvvm.ComponentModel;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 命令面板/菜单/工具栏共用的一个可点击条目。
///
/// 设计取舍：<see cref="Command"/> 与 <see cref="IsEnabled"/> 都来自界面注册表，
/// 未实现的命令会**显式禁用**并给出说明，而不是点了没反应或弹出"未实现"。
/// 一个看起来能用、实际不工作的按钮比一个禁用的按钮更糟。
/// </summary>
public sealed partial class CommandItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private string? _disabledReason;

    [ObservableProperty]
    private string? _gesture;

    /// <summary>命令标识，例如 <c>view.theme.metalforge-light</c>。</summary>
    public string CommandId { get; init; } = string.Empty;

    /// <summary>执行体；null 表示尚未实现。</summary>
    public Action? Execute { get; init; }

    /// <summary>是否为分隔符（工具栏用）。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>是否有子菜单项。</summary>
    public IReadOnlyList<CommandItemViewModel> Children { get; init; } = [];

    public bool HasChildren => Children.Count > 0;
}
