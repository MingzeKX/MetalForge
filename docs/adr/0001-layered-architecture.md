# ADR-0001：Core 与 App 严格分层，Core 禁止引用任何 UI 类型

- **状态**：已接受
- **日期**：2026-02
- **相关**：DESIGN.md §9 目录结构、§19 测试策略

## 背景

IDE 的绝大多数复杂度不在界面，而在"构建命令行怎么拼、产物结构对不对、GDB 回了什么、
配置合并成什么样"。这些逻辑如果和 UI 类型混在一起，就只能靠启动图形界面来验证，
测试成本高到实际上等于没有测试 —— 而本项目的核心承诺恰恰是"每一步失败都可诊断"。

## 决策

工程分为三层，依赖方向单向：

```
MetalForge.Core        纯逻辑，禁止引用 Avalonia / WPF / WinForms / 任何 UI 程序集
MetalForge.Templates   模板与脚手架，仅依赖 Core
MetalForge.App         Avalonia 桌面应用，唯一包含 UI 类型的工程，依赖 Core + Templates
```

具体约束：

1. `MetalForge.Core.csproj` 不引用任何 UI 包；显式设置 `UseWpf=false`、`UseWindowsForms=false`。
2. Core 对外暴露的类型不包含 UI 类型。颜色用 `HexColor`（自带通道值），不返回 `Avalonia.Media.Color`。
3. UI 需要的平台信息（屏幕尺寸等）由 App 层实现，通过接口注入 Core 或留在 App 层。
4. 约束由测试强制，而不是靠代码评审：
   `tests/MetalForge.Core.Tests/ArchitectureTests.cs` 反射检查 Core 的程序集引用，
   出现 `Avalonia*` / `Presentation*` / `System.Windows.Forms*` 即测试失败。

## 后果

**正面**
- 解析器、参数生成、配置合并、诊断规则全部可在毫秒级单元测试里验证（当前 73 个测试 0.5 秒内跑完）。
- 未来若要加 CLI 或语言服务器，Core 可直接复用。
- UI 重构不会波及核心逻辑。

**负面**
- 需要在边界处写转换代码（例如 `ThemeResourceService` 把 `HexColor` 变成 `SolidColorBrush`）。
- 有些"顺手就能拿到"的 UI 工具（如 `Application.Current.Resources`）在 Core 里不可用，需要显式传递。

## 备选方案

| 方案 | 否决原因 |
|---|---|
| 单一工程，按文件夹分层 | 约束无法机器强制，几个月后必然被打破；测试需要引用 UI 框架，启动开销与崩溃面都变大 |
| Core + UI 两个工程，但允许 Core 引用 Avalonia.Base | Avalonia.Base 里就有 `Color`/`Thickness`，一旦放开，边界会在几周内消失 |
| 通过 InternalsVisibleTo 共享内部类型以减少转换代码 | 会让"边界"变成建议而非约束；当前只在测试场景使用该机制 |
