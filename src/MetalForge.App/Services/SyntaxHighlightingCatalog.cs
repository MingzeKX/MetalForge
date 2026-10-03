using System.Text;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Theming;

namespace MetalForge.App.Services;

/// <summary>
/// 自研语法高亮定义的注册表。
///
/// 两件事必须同时成立，因此这个类的结构是现在这样：
///
/// 1) **必须有自研定义**。AvaloniaEdit 内置高亮只有 21 种（实测枚举），含 C/C++/C#/XML/JSON，
///    但不含 NASM、GAS 汇编、链接脚本、Makefile、EDK2 INF/DEC/DSC、Device Tree ——
///    而这些恰好是 OSDev 日常。没有它们，打开 boot.S 或 linker.ld 就是一片没有层次的文字。
///
/// 2) **颜色必须来自主题**。XSHD 只接受具体颜色值，不支持主题令牌；
///    若把颜色写进模板，用户换主题时编辑器会保持另一套配色，
///    而项目纪律（NoHardcodedVisualsTests）也禁止代码里出现颜色字面量。
///    因此做法是"以主题的 syntax 配色渲染 XSHD 模板"。
/// </summary>
public sealed class SyntaxHighlightingCatalog
{
    /// <summary>高亮定义名与匹配规则。无扩展名的固定文件名（Makefile、grub.cfg）走全名匹配。</summary>
    private static readonly (string DefinitionName, string[] Patterns)[] _filePatterns =
    [
        ("NASM", [".asm", ".nasm", ".inc"]),
        ("GNU Assembler", [".s", ".S"]),
        ("Linker Script", [".ld", ".lds", ".ldscript"]),
        ("Makefile", ["Makefile", ".mk"]),
        ("Device Tree", [".dts", ".dtsi"]),
        ("EDK2 Module", [".inf", ".dec", ".dsc"]),
        ("GRUB Config", ["grub.cfg"]),
    ];

    /// <summary>内置通用语言：直接交给 AvaloniaEdit 的定义。</summary>
    private static readonly Dictionary<string, string> _builtInByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".c"] = "C++",
        [".h"] = "C++",
        [".cc"] = "C++",
        [".cpp"] = "C++",
        [".hpp"] = "C++",
        [".cs"] = "C#",
        [".json"] = "Json",
        [".xml"] = "XML",
        [".xshd"] = "XML",
        [".md"] = "MarkDown",
        [".ps1"] = "PowerShell",
        [".psm1"] = "PowerShell",
    };

    private readonly ThemeSyntaxColors _syntax;
    private readonly Dictionary<string, IHighlightingDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Diagnostic> _diagnostics = [];

    public SyntaxHighlightingCatalog(ThemeSyntaxColors syntax)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        _syntax = syntax;
    }

    /// <summary>加载过程中产生的诊断（XSHD 渲染或解析失败时不会抛异常）。</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>已成功注册的定义名。</summary>
    public IReadOnlyList<string> RegisteredNames => [.. _definitions.Keys];

    /// <summary>注册全部自研高亮定义。重复调用无副作用。</summary>
    public void RegisterAll()
    {
        foreach (var (name, template) in EnumerateTemplates())
        {
            if (_definitions.ContainsKey(name))
            {
                continue;
            }

            var xshd = RenderTemplate(name, template, _syntax);

            try
            {
                using var reader = new StringReader(xshd);
                using var xmlReader = System.Xml.XmlReader.Create(reader);
                _definitions[name] = HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException or ArgumentException)
            {
                // 高亮定义出错不应该影响打开文件：记录诊断并继续注册其余定义。
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "MFHL001",
                    $"语法高亮定义 '{name}' 无法加载：{exception.Message}",
                    Hint: "该语言将退化为纯文本显示；其余高亮不受影响。",
                    Exception: exception));
            }
        }
    }

    /// <summary>按文件路径选择高亮定义；没有匹配时返回 null（纯文本）。</summary>
    public IHighlightingDefinition? FindForFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var fileName = Path.GetFileName(filePath);

        foreach (var (definitionName, patterns) in _filePatterns)
        {
            if (!_definitions.TryGetValue(definitionName, out var definition))
            {
                continue;
            }

            foreach (var pattern in patterns)
            {
                var isPlainName = !pattern.StartsWith('.');
                var matches = isPlainName
                    ? string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase)
                    : fileName.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);

                if (matches)
                {
                    return definition;
                }
            }
        }

        var extension = Path.GetExtension(filePath);
        return _builtInByExtension.TryGetValue(extension, out var builtInName)
            ? HighlightingManager.Instance.GetDefinition(builtInName)
            : null;
    }

    /// <summary>该扩展名是否有专用高亮（用于界面提示）。</summary>
    public bool HasDedicatedHighlighting(string? filePath) => FindForFile(filePath) is not null;

    /// <summary>
    /// 把模板里的 <c>{Role}</c> 占位符替换为当前主题的颜色。
    /// 缺失角色**不猜测、不回退到写死的颜色**：直接报诊断并跳过该定义，
    /// 这样"主题漏配"会立刻可见，而不是悄悄用上另一套配色。
    /// </summary>
    private string RenderTemplate(string name, string template, ThemeSyntaxColors syntax)
    {
        var builder = new StringBuilder(template.Length + 256);
        var missing = new List<string>();

        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);
            var role = template[(open + 1)..close];
            var color = syntax[role];

            if (color is null)
            {
                missing.Add(role);
                builder.Append("INVALID");
            }
            else
            {
                builder.Append(color);
            }

            index = close + 1;
        }

        if (missing.Count > 0)
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFHL002",
                $"定义 '{name}' 需要主题中缺失的语法配色角色：{string.Join(", ", missing.Distinct(StringComparer.Ordinal))}",
                Hint: "在 assets/themes/*.theme.json 的 syntax 段补上这些角色。"));
        }

        return builder.ToString();
    }

    /// <summary>全部模板：名称 → 含 <c>{Role}</c> 占位符的 XSHD 文本。</summary>
    private static IEnumerable<(string Name, string Template)> EnumerateTemplates()
    {
        yield return ("NASM", NasmTemplate);
        yield return ("GNU Assembler", GasTemplate);
        yield return ("Linker Script", LinkerScriptTemplate);
        yield return ("Makefile", MakefileTemplate);
        yield return ("Device Tree", DeviceTreeTemplate);
        yield return ("EDK2 Module", Edk2Template);
        yield return ("GRUB Config", GrubConfigTemplate);
    }

    // ---------------------------------------------------------------------
    // XSHD 模板。
    // 每个 Color 的 foreground 都是 {Role} 占位符，由主题填充。
    // 规则按"读 OSDev 源码时需要区分的层次"设计：
    // 注释 / 字符串 / 数字 / 指令 / 寄存器 / 指示符 / 标签 / 函数 / 变量 / 预处理。
    // ---------------------------------------------------------------------

    private const string NasmTemplate = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="NASM" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="{Comment}" />
          <Color name="String" foreground="{String}" />
          <Color name="Number" foreground="{Number}" />
          <Color name="Instruction" foreground="{Instruction}" fontWeight="bold" />
          <Color name="Register" foreground="{Register}" />
          <Color name="Directive" foreground="{Directive}" />
          <Color name="Label" foreground="{Label}" fontWeight="bold" />
          <Color name="Preprocessor" foreground="{Preprocessor}" />
          <RuleSet>
            <Span color="Comment" begin=";" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Span color="String" begin="'" end="'" />
            <Span color="Preprocessor" begin="%" end="$" />
            <Rule color="Label">^\s*[A-Za-z_.$?][A-Za-z0-9_.$?]*:</Rule>
            <Keywords color="Instruction">
              <Word>mov</Word><Word>movzx</Word><Word>movsx</Word><Word>lea</Word>
              <Word>push</Word><Word>pop</Word><Word>add</Word><Word>sub</Word>
              <Word>mul</Word><Word>imul</Word><Word>div</Word><Word>idiv</Word>
              <Word>and</Word><Word>or</Word><Word>xor</Word><Word>not</Word><Word>neg</Word>
              <Word>shl</Word><Word>shr</Word><Word>sal</Word><Word>sar</Word>
              <Word>rol</Word><Word>ror</Word>
              <Word>cmp</Word><Word>test</Word>
              <Word>jmp</Word><Word>je</Word><Word>jne</Word><Word>jz</Word><Word>jnz</Word>
              <Word>jg</Word><Word>jge</Word><Word>jl</Word><Word>jle</Word>
              <Word>ja</Word><Word>jae</Word><Word>jb</Word><Word>jbe</Word>
              <Word>call</Word><Word>ret</Word><Word>retf</Word><Word>iret</Word><Word>iretq</Word>
              <Word>int</Word><Word>syscall</Word><Word>sysret</Word><Word>hlt</Word><Word>nop</Word>
              <Word>cli</Word><Word>sti</Word><Word>cld</Word><Word>std</Word>
              <Word>in</Word><Word>out</Word><Word>insb</Word><Word>outsb</Word>
              <Word>lgdt</Word><Word>lidt</Word><Word>ltr</Word><Word>lldt</Word>
              <Word>wrmsr</Word><Word>rdmsr</Word><Word>cpuid</Word><Word>rdtsc</Word>
              <Word>invlpg</Word><Word>wbinvd</Word><Word>swapgs</Word>
              <Word>rep</Word><Word>repe</Word><Word>repne</Word><Word>movsb</Word><Word>movsq</Word>
              <Word>stosb</Word><Word>stosq</Word><Word>lodsb</Word><Word>lodsl</Word>
              <Word>loop</Word><Word>enter</Word><Word>leave</Word>
              <Word>db</Word><Word>dw</Word><Word>dd</Word><Word>dq</Word><Word>dt</Word>
              <Word>resb</Word><Word>resw</Word><Word>resd</Word><Word>resq</Word>
              <Word>times</Word><Word>equ</Word>
            </Keywords>
            <Keywords color="Register">
              <Word>al</Word><Word>ah</Word><Word>ax</Word><Word>eax</Word><Word>rax</Word>
              <Word>bl</Word><Word>bh</Word><Word>bx</Word><Word>ebx</Word><Word>rbx</Word>
              <Word>cl</Word><Word>ch</Word><Word>cx</Word><Word>ecx</Word><Word>rcx</Word>
              <Word>dl</Word><Word>dh</Word><Word>dx</Word><Word>edx</Word><Word>rdx</Word>
              <Word>si</Word><Word>esi</Word><Word>rsi</Word><Word>di</Word><Word>edi</Word><Word>rdi</Word>
              <Word>bp</Word><Word>ebp</Word><Word>rbp</Word><Word>sp</Word><Word>esp</Word><Word>rsp</Word>
              <Word>r8</Word><Word>r9</Word><Word>r10</Word><Word>r11</Word>
              <Word>r12</Word><Word>r13</Word><Word>r14</Word><Word>r15</Word>
              <Word>cs</Word><Word>ds</Word><Word>es</Word><Word>fs</Word><Word>gs</Word><Word>ss</Word>
              <Word>cr0</Word><Word>cr2</Word><Word>cr3</Word><Word>cr4</Word><Word>cr8</Word>
              <Word>eflags</Word><Word>rflags</Word>
            </Keywords>
            <Keywords color="Directive">
              <Word>section</Word><Word>segment</Word><Word>global</Word><Word>extern</Word>
              <Word>bits</Word><Word>default</Word><Word>org</Word><Word>align</Word><Word>alignb</Word>
              <Word>cpu</Word><Word>use16</Word><Word>use32</Word><Word>use64</Word>
              <Word>absolute</Word><Word>common</Word><Word>static</Word>
            </Keywords>
            <Rule color="Number">\b0[xX][0-9a-fA-F]+\b|\b\d+\b|\$[0-9a-fA-F]+</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string GasTemplate = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="GNU Assembler" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="{Comment}" />
          <Color name="String" foreground="{String}" />
          <Color name="Number" foreground="{Number}" />
          <Color name="Instruction" foreground="{Instruction}" fontWeight="bold" />
          <Color name="Register" foreground="{Register}" />
          <Color name="Directive" foreground="{Directive}" />
          <Color name="Label" foreground="{Label}" fontWeight="bold" />
          <RuleSet>
            <Span color="Comment" begin="/\*" end="\*/" />
            <Span color="Comment" begin="#" />
            <Span color="Comment" begin="//" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Rule color="Label">^\s*[A-Za-z_.$][A-Za-z0-9_.$]*:</Rule>
            <Rule color="Register">%[a-z][a-z0-9]*</Rule>
            <Rule color="Directive">^\s*\.[A-Za-z_][A-Za-z0-9_]*</Rule>
            <Keywords color="Instruction">
              <Word>mov</Word><Word>movq</Word><Word>movl</Word><Word>movw</Word><Word>movb</Word>
              <Word>movz</Word><Word>movs</Word><Word>lea</Word><Word>leaq</Word>
              <Word>push</Word><Word>pop</Word><Word>pushq</Word><Word>popq</Word>
              <Word>add</Word><Word>sub</Word><Word>imul</Word><Word>mul</Word><Word>idiv</Word><Word>div</Word>
              <Word>and</Word><Word>or</Word><Word>xor</Word><Word>not</Word><Word>neg</Word>
              <Word>shl</Word><Word>shr</Word><Word>sar</Word>
              <Word>cmp</Word><Word>test</Word>
              <Word>jmp</Word><Word>je</Word><Word>jne</Word><Word>jz</Word><Word>jnz</Word>
              <Word>jg</Word><Word>jge</Word><Word>jl</Word><Word>jle</Word>
              <Word>call</Word><Word>callq</Word><Word>ret</Word><Word>retq</Word><Word>iretq</Word>
              <Word>int</Word><Word>syscall</Word><Word>hlt</Word><Word>nop</Word>
              <Word>cli</Word><Word>sti</Word><Word>cld</Word><Word>std</Word>
              <Word>in</Word><Word>out</Word>
              <Word>lgdt</Word><Word>lidt</Word><Word>wrmsr</Word><Word>rdmsr</Word><Word>cpuid</Word>
              <Word>invlpg</Word><Word>swapgs</Word>
              <Word>rep</Word><Word>movsb</Word><Word>movsq</Word><Word>stosb</Word><Word>stosq</Word>
              <Word>mrs</Word><Word>msr</Word><Word>eret</Word><Word>wfi</Word><Word>wfe</Word><Word>dsb</Word><Word>isb</Word>
              <Word>svc</Word><Word>hvc</Word><Word>smc</Word>
              <Word>ldr</Word><Word>str</Word><Word>ldp</Word><Word>stp</Word><Word>adr</Word><Word>adrp</Word>
              <Word>b</Word><Word>bl</Word><Word>br</Word><Word>blr</Word><Word>cbz</Word><Word>cbnz</Word>
              <Word>csrr</Word><Word>csrw</Word><Word>csrrw</Word><Word>csrrs</Word><Word>csrrc</Word>
              <Word>ecall</Word><Word>ebreak</Word><Word>mret</Word><Word>sret</Word><Word>sfence</Word>
            </Keywords>
            <Rule color="Number">\b0[xX][0-9a-fA-F]+\b|\b\d+\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string LinkerScriptTemplate = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="Linker Script" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="{Comment}" />
          <Color name="String" foreground="{String}" />
          <Color name="Number" foreground="{Number}" />
          <Color name="Keyword" foreground="{Keyword}" fontWeight="bold" />
          <Color name="Function" foreground="{Function}" />
          <Color name="Variable" foreground="{Variable}" />
          <RuleSet>
            <Span color="Comment" begin="/\*" end="\*/" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Keywords color="Keyword">
              <Word>SECTIONS</Word><Word>MEMORY</Word><Word>ENTRY</Word><Word>OUTPUT_FORMAT</Word>
              <Word>OUTPUT_ARCH</Word><Word>GROUP</Word><Word>INPUT</Word><Word>SEARCH_DIR</Word>
              <Word>PROVIDE</Word><Word>PROVIDE_HIDDEN</Word><Word>ASSERT</Word><Word>INCLUDE</Word>
              <Word>STARTUP</Word><Word>PHDRS</Word><Word>VERSION</Word><Word>OVERLAY</Word>
              <Word>AT</Word><Word>SUBALIGN</Word><Word>REGION</Word>
              <Word>ORIGIN</Word><Word>LENGTH</Word><Word>KEEP</Word><Word>SORT</Word>
            </Keywords>
            <Keywords color="Function">
              <Word>ALIGN</Word><Word>ADDR</Word><Word>LOADADDR</Word><Word>SIZEOF</Word>
              <Word>MAX</Word><Word>MIN</Word><Word>ABSOLUTE</Word><Word>DEFINED</Word>
              <Word>DATA_SEGMENT_ALIGN</Word><Word>DATA_SEGMENT_RELRO_END</Word>
            </Keywords>
            <Rule color="Variable">\b__[A-Za-z0-9_]+\b</Rule>
            <Rule color="Number">\b0[xX][0-9a-fA-F]+\b|\b\d+[KMG]?\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string MakefileTemplate = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="Makefile" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="{Comment}" />
          <Color name="String" foreground="{String}" />
          <Color name="Target" foreground="{Keyword}" fontWeight="bold" />
          <Color name="Variable" foreground="{Variable}" />
          <Color name="Directive" foreground="{Directive}" />
          <Color name="Automatic" foreground="{Label}" />
          <RuleSet>
            <Span color="Comment" begin="#" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Rule color="Automatic">\$[@&lt;^?*+]|\$\$</Rule>
            <Rule color="Variable">\$\([A-Za-z_][A-Za-z0-9_]*\)|\$\{[A-Za-z_][A-Za-z0-9_]*\}</Rule>
            <Rule color="Target">^[A-Za-z0-9_./%$-]+(?=\s*:(?!=))</Rule>
            <Rule color="Directive">^\s*\.(PHONY|SUFFIXES|DEFAULT|PRECIOUS|INTERMEDIATE|SECONDARY|DELETE_ON_ERROR|IGNORE|SILENT|EXPORT|UNEXPORT|NOTPARALLEL|ONESHELL|POSIX)\b</Rule>
            <Rule color="Variable">^[A-Za-z_][A-Za-z0-9_]*\s*(?=[:+?]?=)</Rule>
            <Rule color="Directive">^\s*(ifeq|ifneq|ifdef|ifndef|else|endif|include|-include|define|endef|export|unexport|override|vpath)\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string DeviceTreeTemplate = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="Device Tree" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="{Comment}" />
          <Color name="String" foreground="{String}" />
          <Color name="Directive" foreground="{Directive}" />
          <Color name="Node" foreground="{Keyword}" fontWeight="bold" />
          <Color name="Property" foreground="{Variable}" />
          <Color name="Number" foreground="{Number}" />
          <RuleSet>
            <Span color="Comment" begin="//" />
            <Span color="Comment" begin="/\*" end="\*/" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Rule color="Directive">^/\w[\w-]*/</Rule>
            <Rule color="Node">^[\t ]*[A-Za-z0-9_,@-]+(?=\s*\{)</Rule>
            <Rule color="Property">^[\t ]*[A-Za-z0-9_#,-]+(?=\s*=)</Rule>
            <Rule color="Number">&lt;[^&gt;]*&gt;|\[\s*[0-9a-fA-F\s]*\]</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string Edk2Template = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="EDK2 Module" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="{Comment}" />
          <Color name="String" foreground="{String}" />
          <Color name="Section" foreground="{Keyword}" fontWeight="bold" />
          <Color name="Key" foreground="{Variable}" />
          <RuleSet>
            <Span color="Comment" begin="#" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Rule color="Section">^\s*\[[A-Za-z0-9_.,\s]+\]</Rule>
            <Rule color="Key">^\s*[A-Za-z_][A-Za-z0-9_.]*\s*(?==)</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string GrubConfigTemplate = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="GRUB Config" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="{Comment}" />
          <Color name="String" foreground="{String}" />
          <Color name="Command" foreground="{Keyword}" fontWeight="bold" />
          <Color name="Variable" foreground="{Variable}" />
          <Color name="Number" foreground="{Number}" />
          <RuleSet>
            <Span color="Comment" begin="#" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Span color="String" begin="'" end="'" />
            <Rule color="Variable">\$[A-Za-z_][A-Za-z0-9_]*|\$\{[^}]*\}</Rule>
            <Keywords color="Command">
              <Word>menuentry</Word><Word>submenu</Word><Word>set</Word><Word>unset</Word>
              <Word>insmod</Word><Word>linux</Word><Word>linux16</Word><Word>initrd</Word>
              <Word>multiboot</Word><Word>multiboot2</Word><Word>module</Word><Word>module2</Word>
              <Word>boot</Word><Word>chainloader</Word><Word>root</Word><Word>loopback</Word>
              <Word>if</Word><Word>then</Word><Word>else</Word><Word>fi</Word>
              <Word>for</Word><Word>while</Word><Word>do</Word><Word>done</Word>
              <Word>function</Word><Word>source</Word><Word>terminal_output</Word>
            </Keywords>
            <Rule color="Number">\b0[xX][0-9a-fA-F]+\b|\b\d+\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
