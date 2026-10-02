# 更新日志

本项目更新日志同时以两种形式维护：

- 本文件（Markdown，面向开发者与仓库读者）
- `assets/branding/changelog.json`（结构化，供应用内"更新日志"对话框与更新检查读取）

两者内容保持一致；应用内展示的版本以 JSON 为准。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

---

## [未发布]

### 计划中（M1 — 外壳与配置系统）

- 经典 IDE 布局骨架：菜单栏、工具栏、项目浏览器、多标签编辑器、属性面板、底部标签页、状态栏
- 布局由 `assets/layouts/*.layout.json` 驱动，可切换预设
- 启动画面（`assets/branding/splash/splash.json` 驱动）
- 窗口尺寸与位置记忆
- 窗口图标接线（当前仅 exe 图标生效）
- 工具链健康面板（只读探测）
- i18n 骨架（`assets/locales/`）
- 补齐 Avalonia 12 破坏性变更的官方文档核查（M0 时网络不可达）

---

## [0.0.1] — 2026-02

**里程碑 M0：规划与仓库基建。**
本版本没有面向用户的 OSDev 功能；它建立的是"后续所有功能都能被验证"的地基。

### 新增

#### 设计文档
- `DESIGN.md`：项目愿景、五类用户画像与冲突裁决、架构支持矩阵（8 种架构）、
  引导方式矩阵（9 种）、工具链矩阵、功能清单（P0–P3 优先级）、
  模块接口设计（C# 签名级）、配置系统设计、进程纪律、里程碑 M0–M6、
  风险登记表（12 项）与开放问题（6 项）
- `DEPENDENCIES.md`：依赖清单、许可证、健康度复审规则、已实测的 SDK 级问题
- `docs/adr/`：7 条架构决策记录 + 6 条待决策占位
- `docs/manual-verification/M0.md`：带真实输出的验收记录

#### 分层工程
- `MetalForge.Core`（纯逻辑，禁止引用任何 UI 类型）
- `MetalForge.Templates`（模板宿主）
- `MetalForge.App`（Avalonia 桌面应用）
- 中央包版本管理（`Directory.Packages.props`）+ 统一构建属性（`Directory.Build.props`）
- 全部产物重定向到 `build/`，源码树保持干净

#### 配置系统（Core）
- 三级覆盖：内置 `assets/` → 用户目录 → 项目目录 `.metalforge/`
- 合并语义：对象深合并、数组整体替换、`null` 删除键（刻意保持可预测）
- 每个叶子记录来源层级（`ConfigOrigin`），UI 可显示"此值来自项目配置"
- 热重载：`FileSystemWatcher` + 300ms 防抖 + 原子快照替换
- 任何一层失败都不阻断启动：忽略该层、产出带**行号列号**与 **JSON Pointer** 的诊断、继续运行

#### JSON Schema 校验（Core）
- 自研受限子集校验器（Draft 2020-12 的 20 余个关键字），约 500 行，20 个单元测试
- 未知字段报告可用字段列表（针对最常见的拼写错误）
- 区分"用户配置错误"与"内置 Schema 缺陷"（后者报 Warning 而非 Error）
- 决策理由见 `docs/adr/0006-json-schema-validation.md`（规避 JsonSchema.Net 的 OSMFEULA 条款）

#### 品牌与主题配置化
- `assets/branding/`：应用信息、关于对话框、许可协议、更新日志、贡献者、更新检查
  （共 8 个 JSON + 6 个 JSON Schema）
- `assets/themes/`：深色与浅色两套完整主题 + Schema；新增主题只需放一个 JSON 文件
- `assets/layouts/`：默认布局与调试布局预设 + Schema
- `assets/branding/logo/`：SVG 品牌图形、单色版、7 尺寸 ICO
- Core 的主题模型**不含任何颜色字面量**，`assets/themes/*.theme.json` 是配色唯一真相源

#### 应用外壳（App）
- Avalonia 12.1.3 + Avalonia.AvaloniaEdit 12.0.0 + CommunityToolkit.Mvvm 8.4.2
- 依赖注入装配、源生成日志（`[LoggerMessage]`，满足 CA1848/CA1873）
- 主题资源服务：`ThemeDefinition` → Avalonia 资源键（`Mf*`），视图只引用资源键
- 工具栏 + 三栏骨架 + 状态栏 + 欢迎页（全部文字来自配置）
- 启动时按屏幕容量钳制窗口尺寸与位置（高 DPI 与多屏环境安全）

#### 工程纪律与质量门禁
- `.editorconfig` 把命名规范变成编译期诊断（接口 `I` 前缀、`Async` 后缀、`_camelCase` 私有字段）
- `TreatWarningsAsErrors=true`（含 .NET 分析器），当前构建 0 警告 0 错误
- `ArchitectureTests`：反射强制 Core 不引用 UI 程序集、接口命名、异步命名、私有字段命名
- `NoHardCodedVisualsTests`：扫描颜色字面量与硬编码品牌名，白名单仅保留必要转换层
- 纯 ASCII 脚本纪律 + `Invoke-RepoScript.ps1` 自举（规避 Windows PowerShell 5.1 的 ANSI 解码陷阱）

#### 脚本
- `scripts/Build.ps1`、`scripts/Test.ps1`（绕过 `dotnet test` 的已知问题）
- `scripts/New-BrandingIcon.ps1`（从品牌配色重新生成多尺寸 ICO，带回读自检）
- `scripts/Capture-AppWindow.ps1`（启动应用、按客户区截图、强制回收进程）
- `scripts/Verify-HotReload.ps1`（端到端验证配置热重载与错误降级）

### 修复

M0 期间发现并修复的真实缺陷（均记录于 `docs/manual-verification/M0.md`）：

- **图标生成写出损坏文件**：`BinaryWriter.Dispose()` 会连带关闭底层 `MemoryStream`，
  在释放后调用 `ToArray()` 导致只写出 118 字节目录头、帧数据全部丢失。
  改为显式索引写入单个 `byte[]`，并在写完后回读自检
- **PowerShell 脚本编码崩溃**：无 BOM 的 UTF-8 `.ps1` 被 Windows PowerShell 5.1 按 ANSI(GBK)
  解码，中文注释导致语法错误。固定为"脚本保持纯 ASCII + 需要中文时经 pwsh 自举"
- **截图坐标错位**：`GetWindowRect` 返回物理像素，直接喂给 `CopyFromScreen` 在 150% 缩放下
  截出偏移且被裁切的画面。改用 `GetClientRect` + `ClientToScreen` 并按真实帧缓冲尺寸裁剪
- **窗口超出屏幕**：默认 1440x900 逻辑像素在 150% 缩放下占 2160x1350 物理像素，
  右侧面板与状态栏整块落在可视区域之外。默认尺寸调整为 1280x720，
  并新增窗口打开后的二次钳制（含 Win32 真实帧缓冲探测）
- **`$schema` 被误报为未知字段**：校验器未把 `$schema` 视为注解关键字
- **`Enumerate` 返回不一致的路径形式**：跨层级返回的键随目录前缀变化，改为以文件名去重
- **窗口标题重复**：无文档时产生 `MetalForge — MetalForge`，改为直接使用应用名
- **Core 内硬编码调色板**：架构测试抓出后，`ThemePalette` 改为全部可空、无默认颜色值

### 已知问题

- 使用 `dotnet test` 会报告"运行了零个测试"（退出码 5）。官方 `dotnet new xunit3` 模板在同一
  SDK 组合下表现相同，确认为工具链缺口。请使用 `scripts/Test.ps1`。
  详见 `docs/adr/0007-test-runner.md`
- Avalonia 12 破坏性变更清单尚未从官方文档核实（M0 期间文档站点网络不可达）。
  当前以实测编译行为为准，M1 开始时补齐
- 本机缺少全部 OSDev 外部工具（cmake/ninja/qemu/gcc/clang/nasm），
  M2 的端到端验收需要先解决工具获取（见 `DESIGN.md` G-01 与开放问题 Q1）
