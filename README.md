# MetalForge

> 把写操作系统，变成写普通 C 程序。
> **当前状态：M0（规划与仓库基建）已完成，M1（外壳与配置系统）进行中。**

**仓库：https://github.com/MingzeKX/MetalForge**

```bash
git clone https://github.com/MingzeKX/MetalForge.git
```

MetalForge 是一个面向 Windows 的多架构操作系统开发 IDE，把交叉工具链、引导流程、
QEMU 运行、GDB 调试与固件烧录编排成一条闭环，让裸机开发具备普通应用开发的迭代速度。

> ⚠️ **这不是一个可用的 IDE，现在还不是。** 它目前是一个可运行的外壳：
> 配置系统、主题系统、分层架构与质量门禁已经落地并验证；
> 构建、运行、调试、模板向导等功能在 M2–M5 实现。
> 请先阅读 [`DESIGN.md`](DESIGN.md) 了解完整规划。

## 现在能做什么

- 启动一个由 JSON 完全驱动的应用外壳（品牌、主题、布局、启动参数）
- 改 JSON 即时看到界面变化（配置热重载，含错误回退与可读诊断）
- 探测本机工具链并给出健康报告与获取指引（Core 已就绪，界面在 M1）
- 跑测试（119 个，覆盖配置系统、Schema 校验、布局、i18n、进程执行与架构约束）

## 现在不能做什么

- 新建 OSDev 项目（M4）
- 编译内核、生成镜像（M2）
- 一键运行 QEMU、抓串口输出（M2）
- 断点调试、clangd 智能补全（M3）
- 烧录到真实硬件（M3）

## 快速开始

```powershell
# 需要 .NET SDK 10.0.4xx（见 global.json）
.\scripts\Build.ps1          # 构建全部工程
.\scripts\Test.ps1           # 运行测试
.\scripts\Capture-AppWindow.ps1   # 启动应用并截图（验证界面渲染）
```

**注意**：请使用 `scripts/` 下的脚本，而不是直接 `dotnet test`。
原因见 [`docs/adr/0007-test-runner.md`](docs/adr/0007-test-runner.md) —— 这是 .NET 10 SDK
与 xunit v3 组合的已知问题，官方模板同样复现。

## 换品牌 / 换配色（不需要重新编译）

| 想改什么 | 改哪个文件 |
|---|---|
| 应用名、标语、版本、官网链接、窗口尺寸、启动画面 | `assets/branding/app.json` |
| 配色、圆角、间距、字号、编辑器字体 | `assets/themes/*.theme.json` |
| 面板布局预设 | `assets/layouts/*.layout.json` |
| 关于对话框内容、致谢 | `assets/branding/about.json` |
| 许可协议 | `assets/branding/licenses.json` |
| 更新日志 | `assets/branding/changelog.json` |
| 贡献者名单 | `assets/branding/contributors.json` |

三个目录层级按优先级覆盖：**内置 `assets/` → 用户目录 → 项目目录 `.metalforge/`**。
写错了不会崩溃：坏掉的那一层被忽略，界面回退到上一层，并在日志里给出行号与修复建议。

新增一套配色主题只需往 `assets/themes/` 放一个文件：

```json
{
  "$schema": "./schema/theme.schema.json",
  "id": "my-theme",
  "displayName": "我的主题",
  "variant": "dark",
  "extends": "metalforge-dark",
  "palette": { "accent": "#C8A87A" },
  "metrics": { "cornerRadius": 2 }
}
```

## 项目结构

```
src/MetalForge.Core        纯逻辑：配置、工具链、构建、产物解析、调试协议（禁止引用 UI）
src/MetalForge.Templates   新项目模板与脚手架
src/MetalForge.App         Avalonia 桌面应用（唯一含 UI 类型的工程）
tests/                     单元测试、架构约束测试、无硬编码检查
assets/                    全部品牌/主题/布局/目标矩阵配置（JSON + Schema）
docs/adr/                  架构决策记录（"为什么不用 X"）
docs/manual-verification/  每个里程碑的手动验证清单与实际结果
scripts/                   构建、测试、验证、资源生成脚本
build/                     全部产物（git 忽略）
```

## 文档

| 文档 | 内容 |
|---|---|
| [`DESIGN.md`](DESIGN.md) | 唯一设计真相源：愿景、架构/引导/工具链矩阵、模块接口、里程碑、风险 |
| [`DEPENDENCIES.md`](DEPENDENCIES.md) | 依赖清单、许可证、健康度、已知 SDK 问题 |
| [`CHANGELOG.md`](CHANGELOG.md) | 每个里程碑的变更 |
| [`docs/adr/`](docs/adr/README.md) | 架构决策记录 |
| [`docs/manual-verification/`](docs/manual-verification/) | 里程碑验收记录（含真实输出） |

## 质量底线

这些不是口号，是被测试强制的约束：

- **构建零警告**：`TreatWarningsAsErrors=true`，包含 .NET 分析器
- **分层不可破坏**：`ArchitectureTests` 反射检查 Core 的程序集引用
- **视觉参数不可硬编码**：`NoHardCodedVisualsTests` 扫描颜色字面量并失败
- **命名规范机器强制**：`.editorconfig` 把接口 `I` 前缀、`Async` 后缀、`_camelCase` 私有字段
  变成编译期诊断
- **配置错误不崩溃**：任何一层配置解析失败都降级为诊断，应用继续启动
- **外部进程**：统一经 `IProcessRunner`，强制异步读流、参数列表化、超时与进程树终止（M2）

## 许可证

MIT，见 [`assets/branding/licenses.json`](assets/branding/licenses.json)。

第三方组件许可见同文件中的"第三方组件"一节与 [`DEPENDENCIES.md`](DEPENDENCIES.md)。
外部工具（QEMU、GCC、LLVM、NASM、OpenOCD 等）由用户自行获取，MetalForge 不分发、不修改它们。
