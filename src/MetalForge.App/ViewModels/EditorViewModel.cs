using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using CommunityToolkit.Mvvm.ComponentModel;
using MetalForge.Core.Localization;
using MetalForge.Core.Theming;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 一个打开的文件在编辑器里的状态。
///
/// 设计取舍：这个 ViewModel 只描述"文档是什么、处于什么状态"，
/// 不持有 AvaloniaEdit 的控件实例。视图负责把两者接起来。
/// 原因：控件属于视觉树（一个实例只能有一个父节点），而 ViewModel 需要
/// 在布局重建之间存活 —— 本项目已经因为缓存控件崩溃过一次。
/// </summary>
public sealed partial class EditorDocumentViewModel : ObservableObject
{
    private readonly Func<string, string> _localize;
    private readonly Func<string, object?[], string> _format;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private string _languageName = string.Empty;

    [ObservableProperty]
    private int _caretLine = 1;

    [ObservableProperty]
    private int _caretColumn = 1;

    public EditorDocumentViewModel(
        string? filePath,
        string content,
        IHighlightingDefinition? highlighting,
        Func<string, string> localize,
        Func<string, object?[], string>? format = null)
    {
        ArgumentNullException.ThrowIfNull(localize);

        _localize = localize;
        _format = format ?? ((key, _) => localize(key));
        FilePath = filePath;
        Highlighting = highlighting;
        _content = content;
        LanguageName = highlighting?.Name ?? localize("editor.plainText");
        Title = filePath is null ? localize("editor.untitled") : Path.GetFileName(filePath);
    }

    /// <summary>文件绝对路径；null 表示未命名的新文档。</summary>
    public string? FilePath { get; }

    /// <summary>标题（文件名或"未命名"）。</summary>
    public string Title { get; private set; }

    /// <summary>语法高亮定义；null 表示纯文本。</summary>
    public IHighlightingDefinition? Highlighting { get; }

    /// <summary>标题 + 未保存标记。</summary>
    public string DisplayTitle => IsDirty ? Title + " •" : Title;

    /// <summary>光标位置文本，供状态栏显示。</summary>
    public string CaretText => $"行 {CaretLine}，列 {CaretColumn}";

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));

    partial void OnCaretLineChanged(int value) => OnPropertyChanged(nameof(CaretText));

    partial void OnCaretColumnChanged(int value) => OnPropertyChanged(nameof(CaretText));

    /// <summary>
    /// 从磁盘读取文件并创建文档。文件不存在或不可读时返回带说明的文档，
    /// 而不是抛异常 —— 打开失败应该表现为"编辑器里写着原因"，不是崩溃。
    /// </summary>
    public static EditorDocumentViewModel Load(
        string filePath,
        Func<string, IHighlightingDefinition?> highlightingResolver,
        Func<string, string> localize,
        Func<string, object?[], string>? format = null,
        long sizeLimitBytes = 10 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(highlightingResolver);
        ArgumentNullException.ThrowIfNull(localize);

        var resolveFormat = format ?? ((key, _) => localize(key));
        var highlighting = highlightingResolver(filePath);

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return new EditorDocumentViewModel(filePath, string.Empty, highlighting, localize, resolveFormat)
                {
                    StatusMessage = localize("editor.fileMissing"),
                    IsReadOnly = true,
                };
            }

            // 性能预算要求"打开 10MB 文件 < 1 秒"。更大的文件直接拒绝加载全文，
            // 明确告诉用户原因，而不是让界面卡死几十秒。
            if (info.Length > sizeLimitBytes)
            {
                return new EditorDocumentViewModel(filePath, string.Empty, highlighting, localize, resolveFormat)
                {
                    StatusMessage = resolveFormat(
                        "editor.fileTooLarge",
                        [info.Length / (1024.0 * 1024.0), sizeLimitBytes / (1024.0 * 1024.0)]),
                    IsReadOnly = true,
                };
            }

            var text = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            return new EditorDocumentViewModel(filePath, text, highlighting, localize, resolveFormat);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return new EditorDocumentViewModel(filePath, string.Empty, highlighting, localize, resolveFormat)
            {
                StatusMessage = exception.Message,
                IsReadOnly = true,
            };
        }
    }
}

/// <summary>
/// 编辑器标签页的 ViewModel：管理当前打开的文档集合。
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Func<string, IHighlightingDefinition?> _highlightingResolver;

    [ObservableProperty]
    private EditorDocumentViewModel? _activeDocument;

    public EditorViewModel(ILocalizationService localization, Func<string, IHighlightingDefinition?> highlightingResolver)
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(highlightingResolver);

        _localization = localization;
        _highlightingResolver = highlightingResolver;
    }

    /// <summary>已打开的文档。</summary>
    public System.Collections.ObjectModel.ObservableCollection<EditorDocumentViewModel> Documents { get; } = [];

    /// <summary>打开文件；已打开则聚焦既有文档。</summary>
    public EditorDocumentViewModel Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var normalized = Path.GetFullPath(filePath);
        var existing = Documents.FirstOrDefault(
            document => document.FilePath is not null
                     && string.Equals(Path.GetFullPath(document.FilePath), normalized, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActiveDocument = existing;
            return existing;
        }

        var document = EditorDocumentViewModel.Load(
            normalized,
            _highlightingResolver,
            key => _localization[key],
            (key, arguments) => _localization.Format(key, arguments));

        Documents.Add(document);
        ActiveDocument = document;
        return document;
    }

    /// <summary>关闭文档。</summary>
    public void Close(EditorDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var index = Documents.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        Documents.RemoveAt(index);
        ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
    }

    /// <summary>
    /// 保存文档。路径为空或只读时不动磁盘，返回 false。
    /// 不访问实例状态，因此是静态的（文档自带路径与内容）。
    /// </summary>
    public static bool Save(EditorDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.FilePath is null || document.IsReadOnly)
        {
            return false;
        }

        try
        {
            File.WriteAllText(document.FilePath, document.Content, new System.Text.UTF8Encoding(false));
            document.IsDirty = false;
            document.StatusMessage = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 保存失败必须让用户看到原因，而不是静默丢失编辑内容。
            document.StatusMessage = exception.Message;
            return false;
        }
    }
}
