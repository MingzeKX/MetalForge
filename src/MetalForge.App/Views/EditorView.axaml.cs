using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using MetalForge.App.ViewModels;

namespace MetalForge.App.Views;

/// <summary>
/// 代码编辑器视图。
///
/// 视图负责把 ViewModel 与 AvaloniaEdit 控件接起来（包括语法高亮与光标位置回传）；
/// ViewModel 不引用控件，因此可以在布局重建之间存活而不触发
/// "控件已有视觉父节点"的崩溃。
/// </summary>
public partial class EditorView : UserControl
{
    private TextEditor? _editor;
    private EditorDocumentViewModel? _boundDocument;
    private bool _documentApplied;

    public EditorView()
    {
        InitializeComponent();

        _editor = this.FindControl<TextEditor>("textEditor");
        if (_editor is not null)
        {
            _editor.TextChanged += OnTextChanged;
            _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        }

        DataContextChanged += OnDataContextChanged;

        // 文档内容必须在控件附着到视觉树、且 TextEditor 模板已应用之后才能写入。
        //
        // 为什么：DataContext 在宿主创建控件时就设置了，那一刻 TextEditor 还是"空壳"，
        // 它的模板尚未应用，因此我们写入的 Document 会被模板初始化时创建的文档覆盖 ——
        // 表现为"标签页标题、语言、光标位置都对，但正文一片空白"（本项目真实踩过）。
        AttachedToVisualTree += (_, _) => ApplyDocumentIfReady();
    }

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_editor is null)
        {
            return;
        }

        if (_boundDocument is not null)
        {
            _boundDocument.PropertyChanged -= OnDocumentPropertyChanged;
        }

        _boundDocument = DataContext as EditorDocumentViewModel;
        _documentApplied = false;

        if (_boundDocument is null)
        {
            return;
        }

        _boundDocument.PropertyChanged += OnDocumentPropertyChanged;
        ApplyDocumentIfReady();
    }

    /// <summary>模板就绪后再写入文档；未就绪时留待 AttachedToVisualTree 处理。</summary>
    private void ApplyDocumentIfReady()
    {
        if (_documentApplied || _editor is null || _boundDocument is null)
        {
            return;
        }

        // TextArea 为 null 说明模板还没应用，此时写入会被覆盖。
        if (_editor.TextArea is null)
        {
            return;
        }

        _documentApplied = true;
        ApplyDocument(_boundDocument);


    }

    private void ApplyDocument(EditorDocumentViewModel document)
    {
        if (_editor is null)
        {
            return;
        }

        // Document 每次重建，避免把旧文档的撤销栈带进来。
        _editor.Document = new TextDocument(document.Content);
        _editor.SyntaxHighlighting = document.Highlighting;
        _editor.IsReadOnly = document.IsReadOnly;

        // 大文件关闭语法高亮：性能预算要求 10MB 文件 1 秒内打开，
        // 逐行高亮是最主要的开销来源。
        if (document.Content.Length > 2 * 1024 * 1024)
        {
            _editor.SyntaxHighlighting = null;
        }
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_editor is null || _boundDocument is null)
        {
            return;
        }

        // 只处理"内容被外部改动"。
        // 不能无条件同步 Content：用户每敲一个字都会触发 TextChanged，
        // 若再把 Content 写回控件，光标会被重置到开头。
        if (args.PropertyName == nameof(EditorDocumentViewModel.Content)
            && _editor.Document.Text != _boundDocument.Content)
        {
            var caretOffset = _editor.CaretOffset;
            _editor.Document.Text = _boundDocument.Content;
            _editor.CaretOffset = Math.Min(caretOffset, _editor.Document.TextLength);
        }
        else if (args.PropertyName == nameof(EditorDocumentViewModel.IsReadOnly))
        {
            _editor.IsReadOnly = _boundDocument.IsReadOnly;
        }
    }

    private void OnTextChanged(object? sender, EventArgs args)
    {
        if (_editor is null || _boundDocument is null)
        {
            return;
        }

        _boundDocument.Content = _editor.Document.Text;
        _boundDocument.IsDirty = true;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs args)
    {
        if (_editor is null || _boundDocument is null)
        {
            return;
        }

        var caret = _editor.TextArea.Caret;
        _boundDocument.CaretLine = caret.Line;
        _boundDocument.CaretColumn = caret.Column;
    }
}
