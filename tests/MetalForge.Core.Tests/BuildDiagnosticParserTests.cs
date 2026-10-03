using MetalForge.Core.Build;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Tests;

/// <summary>
/// 构建诊断解析的测试。
///
/// 所有测试输入都取自真实工具的**真实输出格式**（不是编造的近似值）：
/// 解析器一旦与实际输出不符就毫无价值，而这类偏差只有对照真实样本才能发现。
///
/// 另一条硬要求是容错：识别不出的行必须原样保留。
/// 一个丢掉"看不懂的行"的解析器比没有解析器更糟 —— 用户会以为构建没有输出。
/// </summary>
public sealed class BuildDiagnosticParserTests
{
    private const string ProjectRoot = @"C:\projects\my-os";

    [Fact]
    public void ParsesGccErrorWithColumn()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "src/kernel/main.c:42:17: error: 'kmain' undeclared here (not in a function)",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic!.Severity);
        Assert.Equal(Path.Combine(ProjectRoot, "src", "kernel", "main.c"), diagnostic.Source);
        Assert.Equal(42, diagnostic.Line);
        Assert.Equal(17, diagnostic.Column);
        Assert.Contains("undeclared", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesGccDiagnosticWithoutColumn()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "src/boot/boot.S:8: error: junk at end of line",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(8, diagnostic!.Line);
        Assert.Null(diagnostic.Column);
    }

    [Fact]
    public void ExtractsWarningFlagAsCodeAndRemovesItFromMessage()
    {
        // [-Wunused-variable] 放进代码字段后就不该再出现在消息里：重复显示是噪音。
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "src/main.c:9:12: warning: unused variable 'x' [-Wunused-variable]",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic!.Severity);
        Assert.Equal("-Wunused-variable", diagnostic.Code);
        Assert.DoesNotContain("-Wunused-variable", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("unused variable", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesFatalErrorAsErrorSeverity()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "src/main.c:1:10: fatal error: stdio.h: No such file or directory",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic!.Severity);
    }

    [Fact]
    public void ParsesNoteAsInfo()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "src/main.c:3:5: note: declared here",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic!.Severity);
    }

    [Fact]
    public void ParsesAbsolutePathsWithoutPrefixingProjectRoot()
    {
        // 链接器常输出完整路径。再次拼接会得到 C:\projects\my-os\C:\... 这种坏路径。
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            @"C:\toolchain\x86_64-elf\lib\crt0.o:1:5: error: broken",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(@"C:\toolchain\x86_64-elf\lib\crt0.o", diagnostic!.Source);
    }

    [Fact]
    public void ParsesLinkerUndefinedReference()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "src/kernel/main.o:(.text+0x1f): undefined reference to `kmain'",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic!.Severity);
        Assert.Equal("MFBUILD-LINK", diagnostic.Code);
        Assert.Contains("undefined reference", diagnostic.Message, StringComparison.Ordinal);

        // 链接错误最常见的成因是"忘了把某个目标文件加进来"，提示要指向这个方向。
        Assert.NotNull(diagnostic.Hint);
    }

    [Fact]
    public void ParsesLinkerCannotFind()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "ld: cannot find -lc",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Contains("cannot find", diagnostic!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesNinjaFailedTarget()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "FAILED: CMakeFiles/kernel.dir/src/main.c.obj",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal("MFBUILD-NINJA", diagnostic!.Code);
        Assert.Contains("main.c.obj", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(diagnostic.Line);
    }

    [Fact]
    public void ParsesNasmError()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "src/boot/boot.asm:24: error: parser: instruction expected",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(24, diagnostic!.Line);
        Assert.Contains("instruction expected", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesMsvcErrorForEdk2Scenario()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            @"MdePkg\Library\BaseLib\String.c(120) : error C2143: syntax error : missing ';'",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic!.Severity);
        Assert.Equal("C2143", diagnostic.Code);
        Assert.Equal(120, diagnostic.Line);
        Assert.EndsWith("String.c", diagnostic.Source!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesCMakeConfigureError()
    {
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            "CMake Error at CMakeLists.txt:12 (add_executable):",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic!.Severity);
        Assert.Equal(12, diagnostic.Line);
        Assert.Contains("配置阶段", diagnostic.Hint!, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 容错
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("Consolidate compiler generated dependencies of target kernel")]
    [InlineData("[ 50%] Building C object CMakeFiles/kernel.dir/src/main.c.obj")]
    [InlineData("[100%] Linking C executable kernel.elf")]
    [InlineData("make[2]: Entering directory '/build'")]
    [InlineData("ninja: build stopped: subcommand failed.")]
    [InlineData("-- Configuring done (0.4s)")]
    public void NonDiagnosticLinesAreNotMistakenForDiagnostics(string line)
    {
        Assert.Null(BuildDiagnosticParser.TryParseLine(line, ProjectRoot));
    }

    [Fact]
    public void KeepsEveryLineIncludingUnrecognizedOnes()
    {
        // 这是最重要的一条：解析器不得丢弃任何输出行。
        string[] output =
        [
            "[ 25%] Building C object src/main.c.obj",
            "src/main.c:9:12: warning: unused variable 'x' [-Wunused-variable]",
            "some line from a tool we do not know",
            "src/main.c:42:17: error: 'kmain' undeclared here",
            "[100%] Linking C executable kernel.elf",
        ];

        var result = BuildDiagnosticParser.Parse(output, ProjectRoot);

        Assert.Equal(output.Length, result.Lines.Count);
        Assert.Equal(output, result.Lines.Select(line => line.Text).ToArray());
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.True(result.HasErrors);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void GradesRecognizedLinesForColoring()
    {
        string[] output =
        [
            "[ 25%] Building C object src/main.c.obj",
            "src/main.c:9:12: warning: unused variable 'x'",
            "src/main.c:42:17: error: boom",
        ];

        var result = BuildDiagnosticParser.Parse(output, ProjectRoot);

        Assert.Equal(DiagnosticSeverity.Info, result.Lines[0].Severity);
        Assert.Equal(DiagnosticSeverity.Warning, result.Lines[1].Severity);
        Assert.Equal(DiagnosticSeverity.Error, result.Lines[2].Severity);
    }

    [Fact]
    public void DeduplicatesRepeatedDiagnosticsButKeepsBothOutputLines()
    {
        // Ninja 会回显失败命令的输出，因此同一诊断可能出现两次。
        // 问题面板不该出现重复条目，但输出面板必须保留原文。
        const string duplicate = "src/main.c:42:17: error: 'kmain' undeclared here";
        string[] output = [duplicate, duplicate];

        var result = BuildDiagnosticParser.Parse(output, ProjectRoot);

        Assert.Single(result.Diagnostics);
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public void HandlesEmptyAndBlankLines()
    {
        string[] output = ["", "   ", "src/main.c:1:1: error: x"];

        var result = BuildDiagnosticParser.Parse(output, ProjectRoot);

        Assert.Equal(3, result.Lines.Count);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void WithoutProjectRootKeepsRelativePathAsIs()
    {
        // 没有项目根时不能瞎拼路径：保留相对路径比编出一个错误路径好。
        var diagnostic = BuildDiagnosticParser.TryParseLine("src/main.c:1:1: error: x");

        Assert.NotNull(diagnostic);
        Assert.Equal("src/main.c", diagnostic!.Source);
    }

    [Fact]
    public void ParsesColonContainingWindowsPathCorrectly()
    {
        // C:\... 里的冒号会干扰"以冒号分列"的天真实现。
        var diagnostic = BuildDiagnosticParser.TryParseLine(
            @"C:\proj\src\main.c:15:3: error: expected ';'",
            ProjectRoot);

        Assert.NotNull(diagnostic);
        Assert.Equal(@"C:\proj\src\main.c", diagnostic!.Source);
        Assert.Equal(15, diagnostic.Line);
        Assert.Equal(3, diagnostic.Column);
    }

    [Fact]
    public void EmptyOutputProducesEmptyResult()
    {
        var result = BuildDiagnosticParser.Parse([], ProjectRoot);

        Assert.Empty(result.Lines);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }
}
