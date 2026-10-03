# 更新日志

本项目更新日志同时以两种形式维护：

- 本文件（Markdown，面向开发者与仓库读者）
- `assets/branding/changelog.json`（结构化，供应用内"更新日志"对话框与更新检查读取）

两者内容保持一致；应用内展示的版本以 JSON 为准。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

---

## [未发布]

### 新增（M1 — 外壳、配置系统、编辑器、项目系统）

**外壳与配置**

- 经典 IDE 布局骨架：菜单栏、工具栏、项目浏览器、多标签编辑器、属性面板、底部标签页、状态栏
- 布局由 `assets/layouts/*.layout.json` 驱动，可切换预设（`default` / `debug`）
- 菜单栏改为点击弹出式（去掉内建展开箭头，点击行为与主流 IDE 一致）
- 工具栏按钮收紧内边距与字号，并放进横向滚动容器，避免窄窗口下被裁切
- 鼠标点击不再留下焦点环（键盘导航时仍可见）
- i18n：嵌套 JSON 文案表 + "标量优先"查找规则；回退链 当前语言 → 默认语言 → 键名
- 主题系统：`assets/themes/*.theme.json` → `Mf*` 资源键；代码中零颜色/尺寸字面量
  （由 `NoHardcodedVisualsTests` 强制）

**编辑器**

- 接入 AvaloniaEdit：打开/保存文件、行号、光标位置、只读与错误提示条
- 打开失败（不存在、无权限、过大）表现为"编辑器里写着原因"，而不是崩溃
- 性能预算：超过 10MB 拒绝加载全文并说明原因；超过 2MB 关闭语法高亮

**语法高亮**

- 自研 XSHD 定义 7 种，均不在 AvaloniaEdit 内置的 21 种之内：
  NASM、GNU 汇编（x86 / ARM / RISC-V 指令）、链接脚本、Makefile、
  Device Tree、EDK2 INF/DEC/DSC、GRUB 配置
- **颜色由主题生成**：模板使用 `{Role}` 占位符，由 `ThemeSyntaxColors` 的 11 个角色填充；
  主题漏配角色时报 `MFHL002` 诊断并跳过该定义，而不是悄悄用上另一套配色
- 语法高亮自检：对每种定义跑真实代码样本并统计命中的着色区段，
  结果显示在工具链健康面板 —— 高亮失效不会报错，只会静默变成单色文字

**项目系统**

- `metalforge.json` 项目配置（刻意放在项目根而不是 `.metalforge/` 里：
  构建配置应当一眼可见、能被评审、能脱离 IDE 复现）
- 坏输入全部转成可读诊断（`MFPROJ001`–`MFPROJ005`、`MFWS001`–`MFWS004`），
  含 JSON 语法错误的行号
- 项目浏览器：文件树按需加载、过滤 `build`/`.git`/`bin`/`obj`/`node_modules`、
  单目录条目上限、双击文件在编辑器打开
- 目标矩阵：8 种架构 × 9 种引导方式，含目标三元组、必备编译选项、
  QEMU 参数模板、固件需求；架构与引导方式必须**双向认可**才算可用组合

**启动与窗口**

- 启动画面，内容来自品牌配置与主题资源
- 窗口图标接线
- 窗口尺寸、位置与最大化状态记忆（写到用户配置目录的 `window.json`）；
  恢复时检查位置是否仍落在某个屏幕工作区内，避免"应用启动了但看不到窗口"
- 命令行：`MetalForge.exe [--project <目录>] [文件]`

**诊断与工具**

- `scripts/capture-window.mjs`：可靠的窗口截图（Node + PowerShell FFI）。
  先声明 per-monitor-v2 DPI 感知，再把窗口移到 (0,0) 后截取，因此不受遮挡、
  也不受显示缩放影响；支持按进程号附加（`ShowInTaskbar=false` 的窗口没有主窗口句柄）
- `SyntaxHighlightingCatalog.SelfTest()`：高亮规则的可观测性
- 布局自检：启动时把各栏位实测像素占比写进日志（布局比例不再靠肉眼估算）

### 修复（M1 期间实测发现）

- **编辑器一片空白，但所有日志正常**：`App.axaml` 缺少 AvaloniaEdit 的控件模板引入。
  该缺失不产生任何警告或异常 —— 控件照常测量、布局、报告可见，却一个像素都不绘制。
  已补 `<StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml" />`，
  并由 `ApplicationStyleTests` 守卫
- 文档区内容被赋给已脱离视觉树的控件实例（布局重建时新建了控件）
- `ContentControl` 不把可用空间传给内容，而 AvaloniaEdit 在无约束时报告 0 高度 → 改用 `Decorator`
- 编辑器文档在控件模板应用之前写入会被覆盖
- 启动画面与主窗口同时可见（`desktop.MainWindow` 赋值会立即显示该窗口）
- 打开文件后界面停在欢迎页（存在两套并存的"当前文档"概念）
- 标签选中下划线贴到文件名；标签关闭按钮是 Fluent 的"圆角大按钮"
- 子菜单容器可点却没有动作；工具栏出现两个"启动 QEMU"
- 项目树只显示一行且收起（`TreeViewItem` 的 `IsExpanded` 不会跟随 ViewModel）
- 应用启动即崩溃：XSHD 模板里的 `${...}` 被占位符渲染器误认为角色名，
  产出非法正则；已收紧占位符规则并让高亮注册捕获所有异常转诊断
- 语言文件里 `splash` 段误放在顶层而不是 `strings` 内

### 计划中（M2 — 构建系统）

- 工具链探测结果驱动构建（CMake / Ninja / Make）
- 构建输出与问题面板
- 产物检查器（ELF/PE 头、节表、符号）
- ISO9660 + El Torito 自研实现（Q2 决策）
- 引导安装向导（Q1 决策：先做引导安装；下载功能在 M2 前再请求授权）
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
- `ScriptEncodingTests`：强制 `scripts/*.ps1` 保持纯 ASCII（含非 ASCII 会导致
  Windows PowerShell 5.1 按 ANSI 解码并解析失败——本项目因此踩坑两次），
  并反向校验 `assets/`、`docs/` 下的 JSON/Markdown 是合法 UTF-8
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
- **`Build.ps1` 默认解决方案文件名错误**：仍指向 `MetalForge.sln`，而 SDK 生成的是 `.slnx`；
  该问题被"我已经手动构建过"掩盖，直到用脚本自身入口做干净重建才暴露。
  同时把"脚本必须纯 ASCII"变成 `ScriptEncodingTests` 强制检查

### 已知问题

- 使用 `dotnet test` 会报告"运行了零个测试"（退出码 5）。官方 `dotnet new xunit3` 模板在同一
  SDK 组合下表现相同，确认为工具链缺口。请使用 `scripts/Test.ps1`。
  详见 `docs/adr/0007-test-runner.md`
- Avalonia 12 破坏性变更清单尚未从官方文档核实（M0 期间文档站点网络不可达）。
  当前以实测编译行为为准，M1 开始时补齐
- 本机缺少全部 OSDev 外部工具（cmake/ninja/qemu/gcc/clang/nasm），
  M2 的端到端验收需要先解决工具获取（见 `DESIGN.md` G-01 与开放问题 Q1）
