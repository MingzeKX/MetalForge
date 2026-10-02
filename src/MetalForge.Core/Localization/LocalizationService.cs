using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Localization;

/// <summary>本地化服务的查询接口。</summary>
public interface ILocalizationService
{
    /// <summary>当前生效的语言代码。</summary>
    string CurrentLanguage { get; }

    /// <summary>全部可用语言（含面向用户的名称）。</summary>
    IReadOnlyList<(string LanguageCode, string DisplayName)> AvailableLanguages { get; }

    /// <summary>语言切换后触发。</summary>
    event EventHandler<string>? LanguageChanged;

    /// <summary>当前语言中可用的全部文案键（点号路径，已合并默认语言回退）。</summary>
    IReadOnlyList<string> Keys();

    /// <summary>
    /// 取文案。找不到时返回 <paramref name="fallback"/>，再找不到返回键本身
    /// （返回键而不是空串：界面上显示 <c>menu.file.new</c> 能立刻指出缺失的翻译，
    /// 显示空白则只会让人以为界面坏了）。
    /// </summary>
    string this[string key] { get; }

    /// <summary>带占位符的文案，例如 <c>status.errors</c> + <c>{count}</c>。</summary>
    string Format(string key, params object?[] arguments);

    /// <summary>尝试取文案；缺失返回 false。</summary>
    bool TryGet(string key, out string value);

    /// <summary>切换语言；语言不存在时返回 false 并保持当前语言。</summary>
    bool TrySetLanguage(string languageCode);
}

/// <summary>本地化服务加载选项。</summary>
/// <param name="LocaleDirectory">相对 <c>assets/</c> 的目录。</param>
/// <param name="SearchPattern">文件匹配模式。</param>
/// <param name="DefaultLanguage">用户未选择时的默认语言。</param>
/// <param name="ExcludedFileSuffixes">
/// 需要跳过的文件名后缀。默认跳过 <c>.schema.json</c>：
/// 语言目录里同时放着 JSON Schema，它本身不是语言文件，
/// 不排除的话每次加载都会报一条"缺少 language 字段"的假错误。
/// </param>
public sealed record LocalizationOptions(
    string LocaleDirectory = "locales",
    string SearchPattern = "*.json",
    string DefaultLanguage = "zh-Hans",
    IReadOnlyList<string>? ExcludedFileSuffixes = null)
{
    /// <summary>实际生效的排除后缀。</summary>
    public IReadOnlyList<string> EffectiveExcludedSuffixes { get; } =
        ExcludedFileSuffixes ?? [".schema.json"];
}

/// <summary>
/// 本地化服务默认实现。
///
/// 回退链：当前语言 → 默认语言 → 键名本身。
/// 语言文件缺失或损坏只产生诊断，不影响界面运行（"缺失翻译"不是崩溃理由）。
///
/// 实现 <see cref="IDisposable"/> 是为了统一服务生命周期约定：
/// 容器释放时所有服务都被显式释放，调用方不必记住"哪个服务需要释放"。
/// </summary>
public sealed class LocalizationService : ILocalizationService, IDisposable
{
    private readonly AssetResolver _resolver;
    private readonly LocalizationOptions _options;
    private readonly Dictionary<string, LocalizationCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Diagnostic> _diagnostics = [];

    private LocalizationCatalog? _active;
    private LocalizationCatalog? _fallback;
    private bool _disposed;

    public LocalizationService(AssetResolver resolver, LocalizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _options = options ?? new LocalizationOptions();
    }

    public string CurrentLanguage => _active?.LanguageCode ?? _options.DefaultLanguage;

    public IReadOnlyList<(string LanguageCode, string DisplayName)> AvailableLanguages =>
    [
        .. _catalogs.Values
            .OrderBy(catalog => catalog.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .Select(catalog => (catalog.LanguageCode, catalog.DisplayName))
    ];

    /// <summary>本次加载产生的全部诊断（语言文件缺失、损坏等）。</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public event EventHandler<string>? LanguageChanged;

    /// <summary>当前生效的查找表（含默认语言补齐）。供诊断与"检查翻译完整性"使用。</summary>
    public LocalizationCatalog? ActiveCatalog => _active;

    public IReadOnlyList<string> Keys() => _active is null ? [] : [.. _active.Keys()];

    public string this[string key] => TryGet(key, out var value) ? value : key;

    public string Format(string key, params object?[] arguments)
    {
        var template = this[key];
        if (arguments.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException exception)
        {
            // 翻译文件里的占位符写错（例如 {0} 写成 {o}）不应该让界面崩掉。
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFLOC002",
                $"文案 '{key}' 的占位符与调用不匹配：{exception.Message}",
                Hint: "检查语言文件中该条目的大括号占位符数量与编号。",
                Exception: exception));
            return template;
        }
    }

    public bool TryGet(string key, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (_active?.Find(key) is { } active)
        {
            value = active;
            return true;
        }

        if (_fallback?.Find(key) is { } fallback)
        {
            value = fallback;
            return true;
        }

        return false;
    }

    public bool TrySetLanguage(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return false;
        }

        if (!_catalogs.TryGetValue(languageCode, out var catalog))
        {
            return false;
        }

        if (ReferenceEquals(catalog, _active))
        {
            return true;
        }

        _active = catalog;
        LanguageChanged?.Invoke(this, catalog.LanguageCode);
        return true;
    }

    /// <summary>从磁盘重新加载全部语言文件，保持当前语言选择（若仍可用）。</summary>
    public void Reload(string? preferredLanguage = null)
    {
        var previousLanguage = preferredLanguage ?? CurrentLanguage;

        _catalogs.Clear();
        _diagnostics.Clear();

        foreach (var fileName in _resolver.Enumerate(_options.LocaleDirectory, _options.SearchPattern))
        {
            if (_options.EffectiveExcludedSuffixes.Any(suffix =>
                    fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var (catalog, diagnostics) = LocalizationCatalog.LoadOne(_resolver, $"{_options.LocaleDirectory}/{fileName}");
            _diagnostics.AddRange(diagnostics);

            if (catalog is null)
            {
                continue;
            }

            _catalogs[catalog.LanguageCode] = catalog;
        }

        if (_catalogs.Count == 0)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFLOC003",
                $"未找到任何语言文件（目录 {_options.LocaleDirectory}）。界面将显示文案键名。",
                Hint: "确认 assets/locales/ 目录存在且包含 .json 语言文件。"));
            _active = null;
            _fallback = null;
            return;
        }

        _fallback = _catalogs.TryGetValue(_options.DefaultLanguage, out var defaultCatalog)
            ? defaultCatalog
            : _catalogs.Values.First();

        _active = _catalogs.TryGetValue(previousLanguage, out var preferred)
            ? preferred
            : _fallback;

        // 用默认语言补齐当前语言缺失的键，避免切换语言后出现大片键名。
        if (!ReferenceEquals(_active, _fallback))
        {
            _active = _active.WithFallback(_fallback);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _catalogs.Clear();
        _diagnostics.Clear();
        _active = null;
        _fallback = null;
        LanguageChanged = null;
    }
}
