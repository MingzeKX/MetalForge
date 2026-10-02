using CommunityToolkit.Mvvm.ComponentModel;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 尚未实现的标签页的占位 ViewModel。
///
/// 为什么要有它，而不是留空：一个空白面板会让人怀疑"是不是坏了"。
/// 明确写出"这个标签页是什么、计划在哪个里程碑实现"，既诚实又有用。
/// </summary>
public sealed partial class PlaceholderViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _plannedMilestone = string.Empty;

    /// <summary>标签页 id，便于用户对照布局配置。</summary>
    [ObservableProperty]
    private string _note = string.Empty;
}
