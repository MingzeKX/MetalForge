using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 空状态：面板/标签页在当前条件下确实没有内容可显示时，告诉用户"这里是什么、
/// 什么时候会有内容、现在能做什么"。
///
/// 与"占位视图"的区别在于**它给出可执行的下一步**。
/// "该标签页尚未实现"对用户没有任何帮助；"打开一个项目后这里会列出源文件，
/// 现在可以按 Ctrl+Shift+O 打开项目"才是。
///
/// 这也是空状态与占位话术的分界线：任何一处的文案都应当通过
/// "用户看完知道该做什么吗"这个检验。
/// </summary>
public sealed partial class EmptyStateViewModel : ObservableObject
{
    /// <summary>标题（面板名）。</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>这个面板用来做什么。</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>当前为什么是空的。</summary>
    [ObservableProperty]
    private string _reason = string.Empty;

    /// <summary>建议的下一步。</summary>
    [ObservableProperty]
    private string _suggestion = string.Empty;

    /// <summary>动作按钮的文案；为空时不显示按钮。</summary>
    [ObservableProperty]
    private string _actionLabel = string.Empty;

    /// <summary>动作；为 null 时按钮不显示。</summary>
    [ObservableProperty]
    private Action? _action;

    public bool HasAction => Action is not null && ActionLabel.Length > 0;

    partial void OnActionChanged(Action? value) => OnPropertyChanged(nameof(HasAction));

    partial void OnActionLabelChanged(string value) => OnPropertyChanged(nameof(HasAction));

    [RelayCommand]
    private void Invoke() => Action?.Invoke();
}
