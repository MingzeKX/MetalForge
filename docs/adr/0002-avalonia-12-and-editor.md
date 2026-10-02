# ADR-0002：UI 采用 Avalonia 12.1.3 + Avalonia.AvaloniaEdit 12.0.0

- **状态**：已接受
- **日期**：2026-02
- **相关**：DEPENDENCIES.md §2

## 背景

MetalForge 是 Windows 优先的桌面 IDE，需要：多标签代码编辑器、深度可定制主题、
高性能长列表（构建日志、串口输出）、跨平台可能性（部分 OSDev 用户在 Linux/macOS 上开发）。
技术栈由项目要求指定为 C# + .NET 10 + Avalonia + AvaloniaEdit + CommunityToolkit.Mvvm。

## 决策

采用 **Avalonia 12.1.3**（`Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` /
`Avalonia.Fonts.Inter` 整组同版本），编辑器采用 **`Avalonia.AvaloniaEdit` 12.0.0**。

关键细节（实测得出，不是猜的）：

1. **包名不能弄错**：`AvaloniaEdit` 这个包停在 **0.10.12**（已停更），
   与 Avalonia 12 配套的是 **`Avalonia.AvaloniaEdit`**（12.0.0）。两者不可混用。
2. **编译绑定必须写 `x:DataType`**：开启 `AvaloniaUseCompiledBindingsByDefault` 后，
   缺少 `x:DataType` 会直接编译失败（`AVLN2100`）。这是好事——绑定错误在编译期暴露。
   已在最小验证程序里确认该行为。
3. **AvaloniaEdit 内置高亮只有 21 种**（实测枚举）：`C#`、`C++`、`XML`、`Json`、`MarkDown` 等，
   **不含 NASM、GAS、链接脚本、Makefile、EDK2 INF/DEC/DSC**。因此自研 `.xshd` 高亮是必需项（DESIGN.md F-13），
   不是"锦上添花"。
4. **不引入 `Avalonia.Diagnostics`**：最新稳定版为 11.3.22，停留在 11.x，
   与 12.1.3 并存有类型冲突风险。Avalonia 12 已内置开发者工具。
5. **不引入 `FluentAvalonia`**：只有 1.1.0-beta1，无稳定版；本项目需要的是"由 JSON 驱动的极简主题"，
   自研 `ThemeResourceService`（约 120 行）比适配第三方主题库更可控。

## 验证方式

在 `%TEMP%` 建立最小 Avalonia 12.1.3 应用（`App.axaml` + `MainWindow.axaml` 编译绑定 +
`[ObservableProperty]`/`[RelayCommand]` + AvaloniaEdit `TextEditor` + `HighlightingManager`），
`dotnet build` 成功（0 warning 0 error），`dotnet run` 可执行到代码路径。
正式工程在此基础上搭建。

## 后果

**正面**
- 编译绑定把绑定错误前移到编译期，配合 `TreatWarningsAsErrors=true` 效果明显。
- 主题系统完全由资源键驱动，配色改 JSON 即可生效（已实测热重载）。
- 未来移植到 Linux/macOS 的成本主要在外围工具链调用，而非 UI 层。

**负面 / 风险**
- Avalonia 12 生态较新，部分第三方控件仍停留在 11.x（已遇到 `Avalonia.Diagnostics`）。
  缓解：优先自研小而可控的控件，不依赖生态补全。
- AvaloniaEdit 12 的 API 与 11.x 有差异，查阅资料时需注意版本。

## 备选方案

| 方案 | 否决原因 |
|---|---|
| WPF | 不跨平台；且 .NET 10 上 WPF 的演进节奏慢于 Avalonia |
| .NET MAUI | 移动优先，桌面 IDE 场景（多窗口、复杂布局、编辑器）支持弱 |
| Avalonia 11.x | 12 已稳定发布（12.1.3），新项目没有理由从旧版本起步 |
| 自研编辑器控件 | 文本渲染、虚拟化、选区、输入法、撤销栈的成本远超收益 |
