# MetalForge 依赖清单（DEPENDENCIES.md）

> 版本核查日期：2026-02（全部通过 `api.nuget.org` flat-container 接口实测，非凭记忆）
> 规则：任何新依赖必须先在此登记；>5MB 或影响架构的依赖需先取得用户确认；
> 半年未更新且 issue 堆积的库必须替换。版本唯一真相源是 `Directory.Packages.props`。

## 1. 运行时与工具链（非 NuGet）

| 组件 | 版本要求 | 本机实测 | 说明 |
|---|---|---|---|
| .NET SDK | 10.0.x（LTS，支持至 2028-11） | **10.0.401** ✅ | `global.json` 固定 `10.0.401` + `rollForward: latestFeature` |
| .NET 运行时 | 10.0.x | **10.0.12** ✅ | `Microsoft.NETCore.App 10.0.12` |
| C# | 14.0 | ✅ | `Directory.Build.props` 的 `LangVersion` |
| Git | 任意近期版本 | **2.54.0.windows.1** ✅ | — |
| GitHub CLI (`gh`) | 任意 | ❌ 未安装 | 创建公开仓库前需安装；当前为本地 Git |

**未安装但后续里程碑需要的外部工具**（M2 起逐个处理，见 DESIGN.md §13 与 G-01）：
`cmake`、`ninja`、`qemu-system-*`、`gcc`（含交叉工具链）、`clang`/`clangd`、`nasm`、
`gdb`、`openocd`、`xorriso`、`iasl`。

## 2. 直接依赖（NuGet）

| 包 | 版本 | 许可证 | 用途 | 引入里程碑 | 备选与结论 |
|---|---|---|---|---|---|
| **Avalonia** | 12.1.3 | MIT ✅ | UI 框架核心 | M0 | WPF（不跨平台）、MAUI（桌面编辑体验弱）。选 Avalonia |
| **Avalonia.Desktop** | 12.1.3 | MIT ✅ | 桌面平台后端 | M0 | — |
| **Avalonia.Themes.Fluent** | 12.1.3 | MIT ✅ | 基础主题（其上叠加自定义主题 JSON） | M0 | — |
| **Avalonia.Fonts.Inter** | 12.1.3 | MIT ✅ | 内置字体，避免依赖系统字体 | M0 | — |
| **Avalonia.AvaloniaEdit** | 12.0.0 | MIT ✅ | 代码编辑器控件 | M0 | ⚠️ **必须用这个包名**；旧包 `AvaloniaEdit` 停在 0.10.12 已停更，两者不可混用 |
| **CommunityToolkit.Mvvm** | 8.4.2 | MIT ✅ | `[ObservableProperty]`/`[RelayCommand]` 源生成器 | M0 | ReactiveUI（重量级、学习曲线陡） |
| **Microsoft.Extensions.DependencyInjection** | 10.0.12 | MIT ✅ | 服务容器 | M0 | 不引入完整 `Hosting`：启动开销更大，本项目不需要其生命周期语义 |
| **Microsoft.Extensions.Logging** | 10.0.12 | MIT ✅ | 日志抽象（配合源生成 LoggerMessage） | M0 | — |
| **Microsoft.Extensions.Logging.Console** | 10.0.12 | MIT ✅ | 控制台日志输出（诊断用） | M0 | — |
| **xunit.v3.mtp-v2** | 4.0.1 | Apache-2.0 ✅ | 测试框架（Microsoft.Testing.Platform 版本） | M0 | xunit 2.x（旧）、NUnit/MSTest（无优势） |

### 2.1 刻意不引入的依赖（决策记录）

| 包 | 最新版 | 不引入的原因 |
|---|---|---|
| **JsonSchema.Net** | 9.4.0 | ⚠️ **许可证风险**：NuGet 包装载的是 `OSMFEULA.txt`（Open Source Maintenance Fee Agreement）。包本身是 MIT，但该协议对**年收入 ≥ 10,000 美元**且用于营利活动的用户收取月度维护费。引入即产生许可义务与法务复核成本。改为自研受限子集校验器（约 500 行，完全可测）。见 `docs/adr/ADR-0006` |
| **NJsonSchema** | 11.6.1 | MIT，但体量大且偏向 C# 类型生成，本项目只需要"校验 + 定位错误" |
| **System.CommandLine** | 2.0.12 | 稳定版刚发布；M0 命令行参数极少，用简单解析即可。M2 若参数变复杂再评估 |
| **OmniSharp LSP 客户端** | — | 抽象层厚，与 clangd 扩展（`switchSourceHeader`）对齐差。改为自研 stdio JSON-RPC 客户端 |
| **FluentAvalonia** | 仅 1.1.0-beta1 | 无稳定版；本项目需要的是"可配置的极简主题"，自己实现更可控 |
| **Avalonia.Diagnostics** | 11.3.22 | 停留在 11.x，与 Avalonia 12.1.3 不匹配，存在类型冲突风险。Avalonia 12 内置开发者工具 |

## 3. 依赖健康度复审节奏

每个里程碑结束时复审一次：

- [ ] 各包是否有新稳定版？升级是否有破坏性变更？
- [ ] 最近一次发布时间是否在 6 个月内？
- [ ] 开放 issue 是否在合理范围（无长期无人处理的崩溃类问题）？
- [ ] 许可证是否发生变化（尤其上游引入类似 OSMFEULA 的条款）？

## 4. 已知的 SDK 级问题（实测，非猜测）

| 问题 | 现象 | 规避方式 |
|---|---|---|
| `dotnet test` 与 Microsoft.Testing.Platform | 在 .NET 10 SDK 10.0.401 + xunit.v3 4.0.1 下，`dotnet test` 报 **"运行了零个测试"（退出码 5）**，即使已按官方要求配置 `global.json` 的 `test.runner` 与 `TestingPlatformDotnetTestSupport`。同一程序集直接执行则全部测试正常 | **统一使用 `scripts/Test.ps1`**（直接执行测试程序集）。已在 `docs/manual-verification/M0.md` 记录复现步骤 |
| `xunit.v3` 官方模板 TFM | `dotnet new xunit3` 生成 `net8.0` | 本项目目标 `net10.0`，已在 csproj 显式覆盖 |
| Windows PowerShell 5.1 脚本编码 | 无 BOM 的 UTF-8 `.ps1` 会被按 ANSI(GBK) 解码，中文注释导致语法错误 | 脚本一律纯 ASCII；需要中文的脚本经 `scripts/Invoke-RepoScript.ps1` 在 pwsh 下运行 |
| 高 DPI 截图 | `CopyFromScreen` 的坐标空间与 `GetWindowRect` 的物理像素不一致，会截出偏移/裁切画面 | 改用 `GetClientRect` + `ClientToScreen`，并按真实帧缓冲尺寸裁剪。见 `scripts/Capture-AppWindow.ps1` |

## 5. 体积预算

| 项 | 当前 | 预算 | 备注 |
|---|---|---|---|
| NuGet 依赖包总大小 | 约 47 MB（Avalonia 12 全家桶为主） | < 120 MB | 含 .NET 运行时自包含发布后另行统计 |
| 发布产物体积 | 待测（M6） | < 150 MB（自包含） | 若超标则评估裁剪（Avalonia 支持 trimming） |
