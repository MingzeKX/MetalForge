# MetalForge — 设计文档（DESIGN.md）

> 版本：0.1.0（规划稿） · 状态：**待用户确认** · 最后更新：2026-02（首次撰写）
> 本文档是 MetalForge 的唯一设计真相源。任何核心决策变更必须先改本文档并取得确认。
> 变更历史见 `CHANGELOG.md`，决策理由见 `docs/adr/`。

---

## 目录

1. [项目愿景](#1-项目愿景)
2. [用户画像与场景](#2-用户画像与场景)
3. [范围边界](#3-范围边界)
4. [架构支持矩阵](#4-架构支持矩阵)
5. [引导方式矩阵](#5-引导方式矩阵)
6. [工具链矩阵](#6-工具链矩阵)
7. [功能清单与优先级](#7-功能清单与优先级)
8. [技术选型与理由](#8-技术选型与理由)
9. [目录结构](#9-目录结构)
10. [关键模块接口设计](#10-关键模块接口设计)
11. [配置系统设计](#11-配置系统设计)
12. [进程与外部工具纪律](#12-进程与外部工具纪律)
13. [工具链获取策略](#13-工具链获取策略)
14. [运行与调试链路](#14-运行与调试链路)
15. [AI Agent 设计](#15-ai-agent-设计)
16. [国际化与可访问性](#16-国际化与可访问性)
17. [性能预算](#17-性能预算)
18. [里程碑拆解](#18-里程碑拆解)
19. [测试与自我验证策略](#19-测试与自我验证策略)
20. [已知风险与对策](#20-已知风险与对策)
21. [附录 A：事实核查记录](#附录-a事实核查记录)
22. [附录 B：已确认的决策（原开放问题）](#附录-b已确认的决策原开放问题)
23. [附录 C：M0 实际产出与偏差](#附录-cm0-实际产出与偏差)

---

## 1. 项目愿景

**MetalForge 让"写操作系统"像"写普通 C 程序"一样可控、可迭代、可交付。**

今天的 OSDev 真实体验是：手工拼装交叉工具链、在 Makefile 与链接脚本之间反复试错、把 QEMU 命令行长年复制粘贴、靠 `printf` 和 `-d int` 猜测三重错误、把镜像写到 U 盘时提心吊胆。这些摩擦与"操作系统"本身的难度无关，纯粹是工具链断裂造成的。

MetalForge 把这些断裂点接成一条闭环：

```
新建项目向导 → 架构/引导方式选择 → 自动生成工具链与链接脚本
   → 编辑（clangd 智能 + 汇编/链接脚本高亮）
   → 构建（统一产物到 build/<arch>/<profile>/）
   → 验证（ELF/PE/Multiboot/ESP 结构自检）
   → 运行（QEMU 多架构 + 串口实时抓取）
   → 调试（GDB MI 源码级单步 + 寄存器/内存视图）
   → 烧录（OpenOCD / U 盘；带安全护栏）
   → 交付（ISO / img / .efi / .uf2 / .hex，一键打包）
```

**设计立场**：MetalForge 是"编排者"而不是"替代者"。它不重新发明编译器、引导程序或模拟器，它把这些成熟工具的**配置、调用、观测**统一到一个 IDE 里，并让每一步失败都有可读的诊断。

**非目标**：不做自己的 C 编译器；不做自己的 CPU 模拟器；不做云端 IDE；不做 Git 替代品。

---

## 2. 用户画像与场景

设计任何功能时，至少从以下五类用户角度审视；冲突时显式记录权衡（见各功能条目的"权衡"标记）。

| # | 画像 | 核心诉求 | 不能忍受 | 对应功能 |
|---|---|---|---|---|
| P1 | **OSDev 初学者** | 模板即用、人话报错、一键跑起来 | 一串英文链接错误、要自己找 OVMF 路径 | 新建项目向导、诊断翻译层、工具缺失引导页 |
| P2 | **固件/引导工程师** | 精确控制编译/链接参数、EDK2 与 U-Boot 兼容、可复现 | IDE 偷改参数、隐藏命令行、不可复现 | 构建命令行可查看/可覆盖、EDK2 工作流、原始命令透出 |
| P3 | **内核开发者** | 多架构、链接脚本控制、GDB 源码级调试、符号与 map | 调试器只能看汇编、符号丢失 | GDB MI 调试器、map/System.map 解析、链接脚本编辑 |
| P4 | **嵌入式开发者** | Cortex-M/ESP32、OpenOCD、SWD 烧录、UF2/HEX、串口监视 | 只能玩 x86、烧录工具要另装 | OpenOCD 集成、UF2/HEX 转换器、串口监视器 |
| P5 | **IDE 重度用户** | 快捷键、可定制布局、vim 模式、快速切换 | 卡顿、不可配置、布局写死 | 命令面板、JSON 布局预设、键盘映射表 |

**关键冲突与裁决**（记录而非默默选择）：

- **C1 自动化 vs 精确控制（P1 vs P2）**：向导自动生成参数，但**所有生成物都是仓库内的可见文本文件**（`build.config.json`、链接脚本、CMakeLists），不藏在 IDE 数据库里；IDE 从不静默追加命令行参数，任何注入都在"构建命令行"面板可见。裁决：**显式优于魔法**。
- **C2 极简 UI vs 信息密度（品牌要求 vs P5）**：默认极简，但提供"紧凑/舒适"两档密度，且每个面板可停靠可隐藏；密度是主题 JSON 的一个参数，不是硬编码。
- **C3 一键烧录 vs 数据安全（P4 vs 现实）**：提供一键烧录，但**写物理设备必须双重确认 + 卷标白名单 + 容量上限护栏**，且默认只允许写入"可移动且非系统卷"。宁可多两步，不可毁数据。

---

## 3. 范围边界

**做**：项目模板与向导、多架构构建编排、产物验证、QEMU 运行、GDB/OpenOCD 调试、串口/终端、clangd 语言智能、语法高亮扩展、配置化元信息（品牌/主题/布局）、AI Agent 辅助（人工确认后写入）、工具链探测与获取引导。

**本期不做**（记录理由，避免"看起来不完整"）：

| 不做 | 理由 |
|---|---|
| 自研编译器/汇编器 | 已有 GCC/Clang/NASM，重造无收益 |
| 自研全系统模拟器 | QEMU 覆盖 8 种目标架构，自研不可能追平 |
| Git 图形客户端 | 外部工具已足够；IDE 只做"文件变更感知" |
| 完整 FAT32/exFAT 写入实现（M1–M3） | 复杂度高；优先 ESP 小镜像与 raw 镜像，FAT 支持排到 M6 |
| 插件市场/第三方扩展宿主 | 安全与稳定性成本高，本期用"配置 + 脚本钩子"替代 |
| 内核符号级性能分析（perf 类似物） | 依赖目标机探针，超出 IDE 编排范畴 |

---

## 4. 架构支持矩阵

每种架构对应一份 `ToolchainProfile`（工具链）与一份 `RunProfile`（QEMU 参数）。所有内容由 `assets/toolchains/*.json` 驱动，可被用户覆盖。

| 架构 ID | 显示名 | 目标三元组 | 默认工具链 | QEMU 系统模拟 | 典型产物 | 优先级 |
|---|---|---|---|---|---|---|
| `x86_64` | x86_64 (AMD/Intel) | `x86_64-elf` | GCC 交叉 / LLVM | `qemu-system-x86_64` | `.elf` `.bin` `.iso` `.efi` | P0 |
| `i686` | i686 (32-bit x86) | `i686-elf` | GCC 交叉 / LLVM | `qemu-system-i386` | `.elf` `.bin` `.iso` | P0 |
| `aarch64` | AArch64 (ARM64) | `aarch64-elf` | GCC 交叉 / LLVM | `qemu-system-aarch64` | `.elf` `.bin` `.img` | P0 |
| `arm32` | ARM32 (Cortex-A) | `arm-none-eabi` | GCC 交叉 / LLVM | `qemu-system-arm` | `.elf` `.bin` `.img` | P1 |
| `riscv64` | RISC-V rv64 | `riscv64-elf` | GCC 交叉 / LLVM | `qemu-system-riscv64` | `.elf` `.bin` | P1 |
| `riscv32` | RISC-V rv32 | `riscv32-elf` | GCC 交叉 / LLVM | `qemu-system-riscv32` | `.elf` `.bin` | P2 |
| `cortex_m` | Cortex-M (M0–M33) | `arm-none-eabi` | GCC 交叉 / LLVM | `qemu-system-arm -M <board>` | `.elf` `.hex` `.uf2` | P1 |
| `esp32` | ESP32 / ESP32-S3 / C3 | `xtensa-esp32-elf` / `riscv32-esp-elf` | ESP-IDF 工具链 | `qemu-system-xtensa` / `qemu-system-riscv32` | `.elf` `.bin` `.uf2` | P2 |

**架构特有能力开关**（由 profile 声明，UI 据此显隐）：

- `x86_64` / `i686`：red zone 开关、`-mno-red-zone`、long mode / protected mode 链接脚本模板、Multiboot2 头注入、ELF 程序头对齐到 0x1000。
- `aarch64` / `arm32`：异常级别（EL1–EL3）/ 特权模式、MMU 页表初始化模板、`-mgeneral-regs-only` 选项、`-march=armv8-a` 等。
- `riscv64` / `riscv32`：`-mcmodel=medany`、OpenSBI 作为 `-bios`、M/S 态模板。
- `cortex_m`：`-mcpu=cortex-m4 -mthumb`、向量表与 `_estack` 链接脚本模板、linker `--gc-sections`、SVD 寄存器视图（M4）。
- `esp32`：二级引导加载程序、分区表 CSV、`esptool` 烧录、Wi-Fi/BLE 蓝牙栈配置标记为需要 IDF 组件。

---

## 5. 引导方式矩阵

| 引导 ID | 说明 | 固件/依赖 | 产物 | 关键生成物 | QEMU 参数要点 |
|---|---|---|---|---|---|
| `uefi` | UEFI 应用/内核（PE32+） | OVMF (`OVMF_CODE.fd` + `OVMF_VARS.fd`) | `.efi` + `bootable.iso` 或 ESP `disk.img` | `.inf`/`CMakeLists`、启动脚本 `startup.nsh`、可选的 GRUB/BOOTX64 布局 | `-drive if=pflash,format=raw,readonly=on,file=OVMF_CODE.fd -drive if=pflash,format=raw,file=<vars copy>` |
| `bios_mbr` | 传统 BIOS，512 字节 MBR | SeaBIOS（QEMU 内置） | `disk.img`（含 MBR + 内核） | 引导扇区汇编模板、`-fno-pic` 构建参数 | `-drive format=raw,file=disk.img` |
| `multiboot` | Multiboot 1 | GRUB 2 (`grub-mkrescue`) + xorriso | `bootable.iso` | `multiboot` 头汇编/`grub.cfg` | `-cdrom bootable.iso` |
| `multiboot2` | Multiboot 2 | GRUB 2 + xorriso | `bootable.iso` | `.multiboot_header` 段 + `grub.cfg` | 同上 |
| `u_boot` | U-Boot 引导 | 交叉 U-Boot 构建产物 / QEMU 内置 | `.bin`/`.img` + FIT 镜像 | `boot.scr`、`uEnv.txt`、可选的 `.its` FIT 源 | `-kernel u-boot` / `-bios u-boot.bin` |
| `coreboot` | coreboot payload | coreboot 构建产物 | `coreboot.rom` | payload 类型（ELF/`-b` flat binary）配置 | `-bios coreboot.rom` |
| `bare_metal` | 直接裸机（无引导程序） | 无（或平台 ROM） | `.bin`/`.elf` | 加载地址、入口点、链接脚本 | `-kernel`/`-device loader,file=...,addr=...` |
| `edk2_module` | EDK2 模块（DXE/PEIM/UEFI Driver） | EDK2 源码树 + `edksetup` | `.efi`（在 EDK2 树内构建） | `.inf`、`*.dsc` 片段、`*.dec` | 同 `uefi` |
| `openocd_flash` | 嵌入式烧录（非"引导"但同属交付路径） | OpenOCD + 探针 | `.hex`/`.bin`/`.uf2` | OpenOCD `cfg` 脚本、烧录命令 | 不适用（真实硬件或 QEMU `-s -S`） |

**向导与矩阵的映射**：用户选"裸机 Hello World / Multiboot 内核 / UEFI 应用 / UEFI 内核 / ARM 裸机 / Cortex-M 固件 / EDK2 模块 / U-Boot 骨架"共 8 个模板，每个模板声明 `(arch, boot)` 组合与生成的工具链参数集合。组合非法时向导禁用该项并解释原因（例如 `cortex_m` + `uefi` 不成立）。

**关键 EFI 编译选项**（写入模板与校验器，作为"已知正确配置"基线）：

```
-mno-red-zone -ffreestanding -fshort-wchar -fno-stack-protector -nostdlib
-Wl,-dll -shared -Wl,--subsystem,10 -Wl,-e,efi_main
```

校验器（M2）会检查最终 `.efi`：PE32+ 机器类型、Subsystem=10 (EFI Application)、入口点存在、镜像对齐。

---

## 6. 工具链矩阵

| 工具 | 用途 | Windows 获取方式 | 探测方式 | 最低版本 |
|---|---|---|---|---|
| `x86_64-elf-gcc` (binutils+gcc) | x86_64 内核 | 预构建二进制包（见 §13） | `--version` 解析 | GCC 13 |
| `i686-elf-gcc` | i686 内核 | 同上 | `--version` | GCC 13 |
| `aarch64-elf-gcc` | ARM64 | 同上 | `--version` | GCC 13 |
| `arm-none-eabi-gcc` | ARM32 / Cortex-M | ARM 官方 GNU Toolchain（预构建，可再分发） | `--version` | GCC 12 |
| `riscv64-elf-gcc` / `riscv32-elf-gcc` | RISC-V | 预构建二进制包 | `--version` | GCC 13 |
| `clang` / `lld` / `llvm-*` | LLVM 路径（含 `--target=` 交叉） | LLVM 官方 Windows 安装包 | `--version` | LLVM 17 |
| `x86_64-w64-mingw32-gcc` | EFI 应用（PE32+） | MSYS2 / WinLibs | `--version` | GCC 12 |
| `nasm` | x86 汇编 | 官方 zip | `-v` | 2.16 |
| `qemu-system-*` | 运行/调试 | QEMU 官方 Windows 安装包 | `--version` | QEMU 8.0 |
| `gdb` / `gdb-multiarch` | 源码级调试 | 随交叉工具链或独立 | `--version`，`set architecture` 探测 | GDB 13 |
| `openocd` | 烧录/调试探针 | 官方/社区 Windows 构建（必要时自编译） | `--version` | 0.12 |
| `cmake` / `ninja` | 构建系统 | 官方 zip / Kitware | `--version` | CMake 3.28 |
| `xorriso` | ISO 生成 | 自编译或 MSYS2（见 §13） | `--version` | 1.5 |
| `grub-mkrescue` | Multiboot ISO | 随 GRUB；Windows 上可能不可得 → 备选：自建 ISO 写入器 | `--version` | GRUB 2.06 |
| `iasl` | ACPI 表编译（EDK2） | ACPICA 源码自编译 | `-v` | 2023+ |
| `clangd` / `clang-format` | 语言智能 / 格式化 | LLVM 包 | `--version` | 17 |
| `esptool` / `idf.py` | ESP32 烧录与构建 | ESP-IDF 安装器 / pip | `version` | ESP-IDF 5.x |
| `dd`（等价实现） | 写设备镜像 | 内置实现（不依赖外部 dd） | — | — |

**Windows 策略（硬约束）**：**不在 Windows 上从源码自举编译交叉工具链**（耗时数小时、易失败）。改为：使用官方/社区预构建二进制，或用 **GitHub Actions 的 Linux runner 构建后作为发布产物分发**。这条策略的具体流水线写在 `docs/toolchain-build.md`（M2 产出）。唯一例外：`xorriso`/`iasl`/`openocd` 这类小型工具，若找不到可信 Windows 预构建，则在 CI 编译或本地经 MSYS2 编译，产物纳入 `tools/` 并由构建流程引用。

**探测与优先级**（`IToolLocator`）：1) 项目配置显式路径 → 2) 用户配置（`%APPDATA%\MetalForge\toolchains.json`）→ 3) MetalForge 托管目录 `%LOCALAPPDATA%\MetalForge\tools\` → 4) `PATH` → 5) 常见安装位置（注册表/默认路径白名单）。命中即记录来源与版本；冲突时在"工具链健康"面板展示全部候选供用户切换。

---

## 7. 功能清单与优先级

优先级定义：**P0 = 没有它 IDE 不成立；P1 = 目标用户的核心工作流；P2 = 显著增值；P3 = 有余力再做。**

### 7.1 用户原始需求项

| ID | 功能 | 优先级 | 里程碑 |
|---|---|---|---|
| F-01 | 经典 IDE 布局（菜单/工具栏/项目树/多标签编辑器/属性面板/底部输出/状态栏） | P0 | M1 |
| F-02 | 主题 JSON 驱动（低饱和、统一圆角、靠间距与明度分层） | P0 | M1 |
| F-03 | 布局预设 JSON + 多标签编辑器（AvaloniaEdit） | P0 | M1 |
| F-04 | 项目系统：`.mfp` 项目文件 + `metalforge.json` 配置 + 文件树 | P0 | M1 |
| F-05 | 构建编排：GCC/Clang/NASM/objcopy/CMake 调用链，产物到 `build/<arch>/<profile>/` | P0 | M2 |
| F-06 | 构建诊断解析（GCC/Clang/NASM/LD/CMake 错误 → 可点击跳转 + 人话解释） | P0 | M2 |
| F-07 | 产物验证器（ELF/PE/Multiboot/ESP 结构自检） | P1 | M2 |
| F-08 | QEMU 一键运行 + 多架构参数模板 + 串口实时抓取 | P0 | M2 |
| F-09 | 终端面板 + 串口监视器（ANSI、搜索、保存、发送） | P1 | M2 |
| F-10 | GDB 集成（MI2）+ QEMU gdbstub 源码级调试（断点/单步/栈/变量/寄存器/内存） | P0 | M3 |
| F-11 | OpenOCD + SWD/JTAG 烧录、`.hex`/`.uf2` 转换、真实硬件写入护栏 | P1 | M3 |
| F-12 | clangd LSP 集成（补全、诊断、跳转、`switchSourceHeader`）+ `compile_commands.json` | P0 | M3 |
| F-13 | 自研语法高亮：NASM/GAS/LD script/Makefile/EDK2 INF-DEC-DSC/DeviceTree | P1 | M3 |
| F-14 | 新建项目向导（8 模板）× 架构 × 引导方式 | P0 | M4 |
| F-15 | GNU-EFI / EDK2 可选框架集成 | P1 | M4 |
| F-16 | AI Agent 面板（多后端、密钥进凭据管理器、输出需确认才写入） | P1 | M5 |
| F-17 | 元信息配置化（关于/版本/图标/启动画面/主题/布局/许可/更新日志/贡献者） | P0 | M1 |
| F-18 | 三级配置覆盖（内置→用户→项目）+ 热重载 + JSON Schema + 错误回退 | P0 | M1 |
| F-19 | 更新检查 + 签名验证 + 从 changelog 读取（可关闭、不静默下载） | P2 | M6 |
| F-20 | i18n（默认中文，预留英文）+ 可访问性（键盘全覆盖、高对比度、屏幕阅读器） | P1 | M1/M6 |
| F-21 | 自定义快捷键 + 命令面板 | P1 | M5 |
| F-22 | 设置界面（编辑器、构建、运行、外观、快捷键、工具链、AI） | P1 | M5 |

### 7.2 我判断"你缺少"并补上的功能

| ID | 功能 | 为什么缺它不行 | 优先级 | 里程碑 |
|---|---|---|---|---|
| G-01 | **工具链健康面板 + 缺失工具引导安装**（版本、路径、来源、一键打开下载页、SHA256 校验、环境变量一键注入） | 实测本机 gcc/cmake/qemu/nasm 全缺。没有这个，新手在第一步就死掉 | P0 | M1/M2 |
| G-02 | **QEMU 固件解析器**：扫描 QEMU 安装目录与常见路径，自动定位 OVMF/BIOS/U-Boot 固件并生成 `-drive if=pflash` 模板 | OVMF 路径是新手第一道墙；手工路径极易写错 | P0 | M2 |
| G-03 | **ESP/磁盘镜像构建器**：默认自己实现的最小 FAT16/FAT32 写入（不依赖 `mkfs.fat`），把 `.efi` 写入 `\EFI\BOOT\BOOTX64.EFI` 并生成 `disk.img` | Windows 上无 loop mount、无 `mtools`，否则 UEFI 磁盘镜像做不出来 | P1 | M6 |
| G-04 | **产物结构校验器**（ELF 机器类型/入口/段对齐、PE32+ Subsystem=10、Multiboot magic 搜索、ISO El Torito 引导目录、UF2 block 校验、HEX 校验和） | 把"启动后黑屏"从玄学变成可诊断 | P1 | M2 |
| G-05 | **构建环境快照与可复现导出**（工具链版本 + 全部参数 + 哈希 → `build/repro.json`；一键导出"复现报告"） | P2 画像最痛的点；也让 bug 报告可用 | P2 | M6 |
| G-06 | **镜像安全写入护栏**：物理磁盘枚举、卷标/容量白名单、双确认、写前 SHA256、写后回读校验、拒绝系统卷 | 一次误写就是用户数据全灭 | P1 | M3 |
| G-07 | **QEMU monitor (QMP) 集成**：暂停/继续/复位、寄存器、内存 dump、设备树、屏幕截图 | GDB 断点之外的"运行时观测"是 OSDev 日常 | P1 | M3 |
| G-08 | **十六进制查看器 + 二进制差异**（看 MBR、引导扇区、PE 头、固件） | 验证与教学都必需 | P2 | M6 |
| G-09 | **串口/终端 ANSI 与 UTF-8 正确解码**（含 `\r` 覆盖、退格、颜色、行缓冲），支持导出日志与时间戳 | 内核日志是主要调试通道，乱码会毁掉整个体验 | P0 | M2 |
| G-10 | **链接脚本与符号工具**：`.map`/`System.map`/`nm` 输出解析、段布局可视化、符号搜索与反汇编（objdump） | P3 画像核心诉求，也是"内核为何崩溃"的主要线索 | P1 | M3 |
| G-11 | **脚本钩子**（`preBuild`/`postBuild`/`preRun` 等，项目配置声明，带超时与输出捕获） | 覆盖 IDE 想不到的定制需求，避免为每个需求加功能 | P2 | M4 |
| G-12 | **ITM/SWO 与 semihosting 支持**（Cortex-M）+ **GDB 半主机 stdout 抓取** | 嵌入式没有串口时的唯一输出通道 | P2 | M6 |
| G-13 | **构建/运行输出限流与结构化过滤**（保留最近 N 行、正则过滤、按级别染色、导出） | 长构建日志会让 UI 卡死 | P0 | M2 |
| G-14 | **崩溃/异常面板**：统一异常收集、可复制诊断包（日志 + 配置 + 工具链清单 + 版本） | 用户报 bug 时不用来回追问 | P1 | M5 |
| G-15 | **结构化的"下一步提示"**：构建失败/运行黑屏时给出候选原因清单（可点击执行修复动作） | 把新手从 0 拉到 1 的关键 | P1 | M5 |
| G-16 | **只读模式与安全阀**：外部工具执行前显示完整命令行，可复制；危险动作（写盘、擦除 Flash）默认需要显式确认 | 信任基础 | P1 | M3 |
| G-17 | **AI Agent 的"补丁预览-确认-应用"三道闸** + 上下文选择器（选文件/选报错/选符号） | Agent 边界要求"必须确认"，需要真正好用的确认 UI（差量视图） | P1 | M5 |
| G-18 | **启动画面与品牌资源热替换**（改 JSON 与图片即可换品牌，无需重编译） | 元信息配置化的硬要求，也便于二次分发 | P0 | M1 |

### 7.3 明确降级/推迟

- **EDK2 完整构建集成**（`edksetup` + BaseTools 全流程）在 M4 只做"打开已有 EDK2 树 + 生成 `.inf`/`.dsc` 片段 + 调用其构建脚本"，不做 BaseTools 自举。
- **U-Boot 从源码构建**不在 IDE 内做，只做"骨架生成 + 外部产物引用 + QEMU 运行"。
- **ESP-IDF 深度集成**（组件管理器、menuconfig GUI）排到 M6 之后；M6 先做工具链探测、`idf.py build` 调用、`esptool` 烧录。

---

## 8. 技术选型与理由

| 层 | 选择 | 理由 | 备选与为何不用 |
|---|---|---|---|
| 运行时 | .NET 10 (LTS，支持至 2028-11) | LTS、性能、Windows 一等公民；实测 SDK 10.0.401 已装 | .NET 8：支持窗口更短 |
| 语言 | C# 14 | 与 .NET 10 配套；`field` 关键字、扩展成员简化 ViewModel 与配置层 | — |
| UI | Avalonia 12.1.3 | 跨平台、XAML 编译绑定、成熟主题系统；实测可编译运行 | WPF（不跨平台）、MAUI（桌面编辑体验弱） |
| 编辑器 | Avalonia.AvaloniaEdit 12.0.0 | 与 Avalonia 12 对齐；实测 21 种内置高亮且支持导入 `.xshd` | 旧的 `AvaloniaEdit` 0.10.12（已停更，实测弃用） |
| MVVM | CommunityToolkit.Mvvm 8.4.2（源生成器） | 实测可用；`[ObservableProperty]`/`[RelayCommand]` 消除样板 | ReactiveUI（重量级、学习曲线陡） |
| DI/宿主 | Microsoft.Extensions.DependencyInjection + Logging 10.0.12 | 标准、可测试；不引入完整 Hosting 以减少启动开销 | Microsoft.Extensions.Hosting（启动耗时更高） |
| JSON | System.Text.Json (10.0.12) | 内置、源生成友好、性能好 | Newtonsoft.Json（额外依赖，无必要） |
| JSON Schema | JsonSchema.Net 9.4.0 | 校验用户配置、错误定位好 | NJsonSchema（偏代码生成） |
| 测试 | xUnit v3 (4.0.1) + Microsoft.NET.Test.Sdk 18.10.1 | 新项目、并行、原生 .NET 10 | NUnit/MSTest：无优势 |
| LSP 客户端 | **自研**（`System.Text.Json` + stdio JSON-RPC） | 只需求 clangd 子集，自研可控、可加 OSDev 专有扩展 | OmniSharp LSP 库：抽象层厚、与 clangd 特性对齐差 |
| DAP/调试 | **自研 GDB/MI2 客户端** | 原生 MI2 覆盖面最广，QEMU/OpenOCD 都是 GDB stub | DAP 需中间适配器（如 cppdbg），环节更多 |
| 日志/诊断 | 自研 `Diagnostics`（结构化） | 需把 GCC 输出映射到 UI 与"下一步提示" | 通用日志库不解决诊断解析 |

**依赖准入规则**（写入 `DEPENDENCIES.md`）：>5MB 或影响架构需先确认；半年未更新且 issue 堆积则替换；许可证必须是 MIT/Apache-2.0/BSD 系；所有版本锁定在 `Directory.Packages.props`（中央包管理）。

---

## 9. 目录结构

```
MetalForge/
├─ MetalForge.sln
├─ Directory.Build.props            # 统一 TFM/语言版本/分析器/警告级别
├─ Directory.Packages.props         # 中央包版本管理（唯一版本真相源）
├─ nuget.config
├─ .editorconfig                    # 命名与风格强制（含 §命名规范）
├─ .gitignore  .gitattributes
├─ README.md  CHANGELOG.md  CONTRIBUTING.md  DEPENDENCIES.md  DESIGN.md
├─ docs/
│  ├─ adr/                          # 架构决策记录（0001-...）
│  ├─ toolchain-build.md            # CI 构建交叉工具链的流水线说明
│  └─ manual-verification/          # 每个里程碑的手动验证步骤
├─ assets/                          # 全部"非代码"元信息（随应用发布，可覆盖）
│  ├─ branding/{app,about,licenses,changelog,contributors,update}.json + logo/*.svg + splash/*
│  ├─ themes/{light,dark,high-contrast}.theme.json + schema/theme.schema.json
│  ├─ layouts/{default,minimal,debug,compact}.layout.json + schema/layout.schema.json
│  ├─ targets/{architectures,boot-methods,templates,toolchains,boards,qemu}.json
│  ├─ highlight/*.xshd               # NASM/GAS/LD/Makefile/INF/DEC/DSC/DTS...
│  └─ locales/{zh-Hans,en}.json
├─ src/
│  ├─ MetalForge.Core/              # 纯逻辑，禁止引用任何 UI 类型（无 Avalonia 引用）
│  │  ├─ Abstractions/              # IProcessRunner, IToolLocator, IBuildEngine ...
│  │  ├─ Projects/                  # 项目模型、metalforge.json、文件树
│  │  ├─ Toolchains/                # 探测、版本解析、profile
│  │  ├─ Build/                     # 构建图、命令行生成、诊断解析
│  │  ├─ Artifacts/                 # ELF/PE/Multiboot/ISO/UF2/HEX 解析与校验
│  │  ├─ Emulation/                 # QEMU 参数生成、QMP、固件解析、串口抓取
│  │  ├─ Debugging/                 # GDB/MI2、OpenOCD
│  │  ├─ LanguageServices/          # LSP 客户端、clangd 会话、compile_commands
│  │  ├─ Diagnostics/               # 结构化诊断 + 下一步提示引擎
│  │  ├─ Configuration/             # 三级配置、schema 校验、热重载
│  │  ├─ Imaging/                   # raw/ESP/FAT、UF2/HEX 转换、安全写盘
│  │  └─ Ai/                        # 后端抽象、提示模板、补丁建议模型
│  ├─ MetalForge.Templates/         # 模板定义 + 模板文件（作为嵌入资源）
│  │  └─ Content/<template-id>/**
│  └─ MetalForge.App/               # Avalonia 应用（唯一含 UI 的项目）
│     ├─ Program.cs  App.axaml(.cs)
│     ├─ Views/  ViewModels/  Controls/  Converters/  Services/(UI 侧适配)
│     ├─ Assets/                    # 编译进程序集的图标/字体（仅必需）
│     └─ app.manifest
├─ tests/
│  ├─ MetalForge.Core.Tests/        # 单元测试（含真实进程调用测试，带超时）
│  ├─ MetalForge.Templates.Tests/   # 模板快照测试（生成结果与金样对比）
│  └─ fixtures/                     # 测试用二进制：kernel.elf/efi/iso/uf2 等
└─ build/                           # 全部产物（git 忽略），按 arch/profile 隔离
   └─ <arch>/<profile>/{obj,bin,iso,log}/
```

**规则**：源码目录永远干净；一切产物进 `build/`；`assets/` 只放配置与资源，不放代码。

---

## 10. 关键模块接口设计

> 以下为**签名级设计**，实现时以本文档为准。所有异步方法以 `Async` 结尾，全部接受 `CancellationToken`。

### 10.1 进程执行（质量底线所在）

```csharp
namespace MetalForge.Core.Abstractions;

public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];   // 结构化，绝不拼字符串
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan? Timeout { get; init; }
    public string? StandardInput { get; init; }
    public bool KillProcessTreeOnExit { get; init; } = true;
}

public sealed record ProcessOutputLine(DateTimeOffset Timestamp, bool IsError, string Text);

public sealed record ProcessResult(int ExitCode, bool TimedOut, bool Canceled, TimeSpan Duration,
                                   IReadOnlyList<ProcessOutputLine> Lines, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Canceled;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutputLine>? progress, CancellationToken ct);
    IProcessHandle StartInteractive(ProcessRequest request);   // 长驻：QEMU/GDB/clangd/OpenOCD
}

public interface IProcessHandle : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    event EventHandler<ProcessOutputLine>? Output;
    event EventHandler<int>? Exited;
    StreamWriter StandardInput { get; }   // 交互式 stdio（GDB MI / clangd 使用）
    Task WriteLineAsync(string line, CancellationToken ct);
    Task<int> WaitForExitAsync(CancellationToken ct);
    void Kill(bool entireTree = true);
}
```

**实现纪律（逐条对应质量底线）**：

1. `OutputDataReceived` / `ErrorDataReceived` **必须**异步订阅，杜绝 stdout/stderr 缓冲填满导致死锁。
2. 一律使用 `ProcessStartInfo.ArgumentList`，不手工加引号；路径含空格由运行时处理。
3. 所有子进程标准流设为 UTF-8（`StandardOutputEncoding = new UTF8Encoding(false)`），并对 `\r` 与 ANSI 序列做显式处理（G-09）。
4. **取消语义**：先 `CloseMainWindow`/发 SIGINT（可用时）→ 宽限期 → Windows **Job Object**（`CreateJobObject` + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）终止整棵进程树，避免 QEMU 派生的子进程残留。
5. **句柄与事件**：所有 `Process` 与 `JobObject` 句柄在 `DisposeAsync` 中释放；事件处理器统一解绑，防止长会话泄漏。
6. **异常不吞**：`ProcessRunner` 内部捕获异常时一律转换为结构化诊断（`Diagnostic` 记录 `Exception`），由 UI 呈现，绝不 `catch {}` 后静默继续。
7. **超时**：`Timeout` 到期 → 标记 `TimedOut` 并走终止流程；不依赖 `WaitForExit(ms)` 单点判断（需配合输出排空等待）。
8. **启动失败**（`Win32Exception`）映射为 `ToolchainNotReady` 诊断，触发 G-01 引导。

### 10.2 工具定位

```csharp
namespace MetalForge.Core.Toolchains;

public enum ToolSource { ProjectConfig, UserConfig, ManagedDirectory, Path, KnownLocation, NotFound }

public sealed record ToolInstance(string ToolId, string ExecutablePath, Version? Version, ToolSource Source,
                                  string? ArchitectureTag, IReadOnlyDictionary<string,string> Metadata);

public interface IToolLocator
{
    Task<ToolInstance?> LocateAsync(string toolId, ToolQuery query, CancellationToken ct);
    Task<IReadOnlyList<ToolInstance>> EnumerateAllAsync(string toolId, ToolQuery query, CancellationToken ct);
    Task<ToolHealthReport> CheckHealthAsync(ToolchainProfile profile, CancellationToken ct);
}
```

`ToolHealthReport` 是 G-01 的数据源：每个工具的 `Found/Version/Path/Source/Requirement/CandidateDownloads` 与总体 `Ready|Degraded|Blocked`。

### 10.3 构建引擎

```csharp
namespace MetalForge.Core.Build;

public interface IBuildEngine
{
    Task<BuildResult> BuildAsync(BuildRequest request, IProgress<BuildEvent> progress, CancellationToken ct);
    Task CleanAsync(CleanLevel level, BuildRequest request, CancellationToken ct);
}

public sealed record BuildRequest(string ProjectPath, string Configuration /*Debug|Release*/, string ArchitectureId,
                                  IReadOnlyList<string> Targets, bool DryRun, bool VerboseCommands);

public sealed record BuildResult(bool Succeeded, IReadOnlyList<Artifact> Artifacts,
                                 IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> ExecutedCommands,
                                 TimeSpan Duration);
```

- `Diagnostic` 包含 `Severity`、`Code`、`Message`、`File`、`Line`、`Column`、`RawLine`、`Explanation`（人话）、`Fixes`（可执行动作集合）。解析器覆盖 GCC/Clang/NASM/LD/CMake/`idf.py`。
- **`DryRun` 输出完整命令行**（P2 画像要求）。所有命令记入 `build/<arch>/<profile>/log/commands.jsonl`。
- 清理分级：`CleanOutputs`（build 产物）/ `CleanIntermediate`（obj）/ `CleanAll`（整个 build 目录）/ `CleanGeneratedFromTemplates`（仅模板重生成文件，带二次确认）。

### 10.4 产物模型与校验

```csharp
namespace MetalForge.Core.Artifacts;

public enum ArtifactKind { Elf, Pe, FlatBinary, Iso, DiskImage, Uf2, IntelHex, Map }

public sealed record Artifact(ArtifactKind Kind, string Path, string ArchitectureId, long SizeBytes, string Sha256);

public interface IArtifactInspector
{
    Task<ArtifactReport> InspectAsync(Artifact artifact, CancellationToken ct);
}

public sealed record ArtifactReport(Artifact Artifact, IReadOnlyList<ArtifactFinding> Findings, object? Details);
public sealed record ArtifactFinding(FindingSeverity Severity, string Code, string Message, string? Hint);
```

校验规则示例：`ELF.machine` 与目标架构匹配；入口点落在可执行段内；`p_type=PT_LOAD` 对齐满足分页要求；PE32+ 且 `Subsystem=10`；Multiboot magic 在头部 8KB 内；ISO 存在 El Torito 引导目录；UF2 每个 block 的 `magicStart0/1`、家族 ID、块数与文件大小一致；HEX 校验和与地址递增合法。

### 10.5 运行与模拟

```csharp
namespace MetalForge.Core.Emulation;

public interface IEmulatorService
{
    Task<RunSession> StartAsync(RunRequest request, CancellationToken ct);
    Task<IReadOnlyList<FirmwareCandidate>> DiscoverFirmwareAsync(string architectureId, CancellationToken ct);
    Task<QmpClient> ConnectMonitorAsync(RunSession session, CancellationToken ct);
}

public sealed record RunRequest(string ProjectPath, string ArchitectureId, string BootMethodId,
                                Artifact PrimaryArtifact, RunOptions Options);
public sealed record RunSession(string Id, string QemuExecutable, IReadOnlyList<string> Arguments,
                                int? GdbPort, int? MonitorPort, IProcessHandle Process);
```

- **`FirmwareCandidate`（G-02）**：扫描 QEMU 安装目录、`%ProgramFiles%`、`PATH`、用户自定义目录，识别 `OVMF_CODE*.fd`/`OVMF_VARS*.fd`/`edk2-*`/`u-boot*`/`bios-256k.bin` 等，返回带来源与"是否只读"标记的候选列表，并自动生成对应的 QEMU 参数（含 VARS 副本策略，避免污染原固件）。
- **串口抓取（G-09）**：优先 `-chardev socket`/`-serial stdio` 的行式读取，输出经 ANSI 解析器与 UTF-8 解码器进入环形缓冲（默认 50k 行、可配置），并保留"原始字节"以便保存与二进制诊断。

### 10.6 调试

```csharp
namespace MetalForge.Core.Debugging;

public interface IDebugSession : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);                 // gdb -q -i=mi2 + target remote
    Task SetBreakpointAsync(BreakpointLocation location, CancellationToken ct);
    Task ContinueAsync(CancellationToken ct);
    Task StepAsync(StepKind kind, CancellationToken ct);
    Task<IReadOnlyList<StackFrame>> GetStackTraceAsync(CancellationToken ct);
    Task<IReadOnlyList<Variable>> GetVariablesAsync(int frameId, CancellationToken ct);
    Task<MemoryBlock> ReadMemoryAsync(ulong address, int length, CancellationToken ct);
    event EventHandler<DebugStoppedEventArgs>? Stopped;
    event EventHandler<DebugOutputEventArgs>? Output;
}
```

- MI2 解析器（`*stopped`/`=breakpoint-created`/`^done`/`^error`/`~`/`@`）自研，独立可测。
- 反汇编视图用 `objdump` 或 `-data-disassemble`；寄存器视图用 `info registers`（含 Cortex-M 的 SVD 可选增强，M6）。
- **符号解析（G-10）**：解析 `.map`/`nm` 输出 → 符号库（地址、大小、段、类型、来源文件），支撑符号搜索、栈回溯人性化、链接布局视图。

### 10.7 语言服务（clangd）

```csharp
namespace MetalForge.Core.LanguageServices;

public interface ILanguageClient : IAsyncDisposable
{
    Task InitializeAsync(string projectRoot, CancellationToken ct);
    Task<CompletionList> CompleteAsync(string filePath, Position position, CancellationToken ct);
    Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct);
    Task<Location?> GoToDefinitionAsync(string filePath, Position position, CancellationToken ct);
    Task<Location?> SwitchSourceHeaderAsync(string filePath, CancellationToken ct);   // clangd 扩展
    Task FormatDocumentAsync(string filePath, CancellationToken ct);                  // clang-format
}
```

- LSP 走 stdio JSON-RPC（`Content-Length` 帧）；请求/通知/取消（`$/cancelRequest`）完整实现。
- `clangd` 的编译数据库优先来自项目构建；无 CMake 时由 MetalForge 生成 `compile_commands.json`（从构建图导出，含 `-mno-red-zone`、`-ffreestanding` 等 freestanding 参数，否则 clangd 会报满屏假错误）。
- **降级策略**：clangd 不可用 → 保留语法高亮 + 符号索引（基于 `nm`/ripgrep 式自研搜索），并在 UI 明确提示"智能功能不可用，点击获取 clangd"。

### 10.8 AI Agent

```csharp
namespace MetalForge.Core.Ai;

public interface IAiBackend
{
    string Id { get; }                       // openai | anthropic | deepseek | local(ollama)
    IAsyncEnumerable<AiChunk> StreamAsync(AiRequest request, CancellationToken ct);
}

public interface IAiAgentService
{
    Task<AiSuggestion> ProposeAsync(AiTaskRequest request, CancellationToken ct);   // 只产出建议
    Task<string> ApplyAsync(AiSuggestion suggestion, ApplyMode mode, CancellationToken ct); // 用户确认后写入
}
```

**边界硬约束**（代码层面强制，不只靠提示词）：`IAiAgentService` 没有构建/运行/提交能力；文件写入必须经过 `ApplyAsync`，其入参是**已确认的建议对象**；密钥通过 `ISecretStore`（Windows Credential Manager，`CredMan` P/Invoke）读写，配置文件里只存密钥**引用名**。

---

## 11. 配置系统设计

### 11.1 三级覆盖

```
内置资源（程序集嵌入 assets/）  ←  用户目录 %APPDATA%\MetalForge\  ←  项目目录 <project>/.metalforge/
```

- 合并规则：对象**深合并**，数组**整体替换**（可预测、易解释），`null` 表示"删除该项"。
- 每次解析保留 **来源追踪**（`ConfigOrigin`），UI 可显示"此值来自项目配置"（P5 画像刚需）。
- **热重载**：`FileSystemWatcher` + 300ms 防抖 + 原子替换（先构建新快照，再切换引用），失败时保留旧快照并提示。
- **校验失败回退**：JSON Schema 校验失败 → 拒绝该层、回退到上一层、弹出可定位的错误提示（文件、行、路径、期望）。启动时若内置资源损坏，回退到编译期硬编码的**最小默认值**并记录严重诊断（唯一允许的硬编码后备）。

### 11.2 配置文件清单

| 文件 | 驱动内容 | Schema |
|---|---|---|
| `assets/branding/app.json` | 应用名、厂商、版本、官网、图标路径、启动画面、窗口默认尺寸 | `app.schema.json` |
| `assets/branding/about.json` | 关于对话框：标语、描述、构建信息、致谢、链接分组 | `about.schema.json` |
| `assets/branding/licenses.json` | 许可协议全文（分节） | `licenses.schema.json` |
| `assets/branding/changelog.json` | 更新日志（版本、日期、分类条目） | `changelog.schema.json` |
| `assets/branding/contributors.json` | 贡献者名单（名字、角色、链接、头像） | `contributors.schema.json` |
| `assets/branding/update.json` | 更新源 URL、渠道、签名公钥、是否默认检查 | `update.schema.json` |
| `assets/themes/*.theme.json` | 全部颜色、圆角、间距、字号、密度、对比度 | `theme.schema.json` |
| `assets/layouts/*.layout.json` | 面板树、初始尺寸、默认标签页、工具栏项 | `layout.schema.json` |
| `assets/targets/architectures.json` | 架构矩阵 | `architectures.schema.json` |
| `assets/targets/boot-methods.json` | 引导方式矩阵（含 QEMU 参数模板） | `boot-methods.schema.json` |
| `assets/targets/templates.json` | 新项目向导条目与参数 | `templates.schema.json` |
| `assets/targets/toolchains.json` | 工具链 profile、下载候选、SHA256 | `toolchains.schema.json` |
| `assets/targets/boards.json` | 开发板（QEMU machine、OpenOCD cfg、Flash 布局） | `boards.schema.json` |
| `assets/targets/qemu.json` | QEMU 参数字典与固件模式 | `qemu.schema.json` |
| `assets/locales/*.json` | 界面文案 | `locale.schema.json` |
| `<project>/metalforge.json` | 项目：架构、引导、目标、源文件、参数覆盖、脚本钩子、运行配置 | `project.schema.json` |

**主题示例（示意，实际字段在 M1 落地）**：

```json
{
  "$schema": "../../assets/themes/schema/theme.schema.json",
  "id": "metalforge-dark",
  "displayName": "MetalForge Dark",
  "variant": "dark",
  "palette": {
    "background": "#16181C", "surface": "#1C1F24", "surfaceRaised": "#22262C",
    "foreground": "#D7DAE0", "foregroundMuted": "#8A9099",
    "accent": "#7AA2C8", "border": "#2A2F36",
    "success": "#7FA88A", "warning": "#C8A87A", "danger": "#C88A8A"
  },
  "metrics": { "cornerRadius": 6, "spacingUnit": 4, "fontSizeBase": 13, "density": "comfortable" },
  "editor": { "fontFamily": "Cascadia Mono, Consolas, monospace", "fontSize": 13, "lineHeight": 1.5 }
}
```

**约束**：C# 中不得出现颜色/尺寸字面量；所有视觉值经 `IThemeService` 查询。CI 增加一个 lint 测试，扫描 `src/MetalForge.App/**/*.axaml` 与 `.cs` 中的颜色字面量并失败。

---

## 12. 进程与外部工具纪律

统一入口：只有 `MetalForge.Core.Processes.ProcessRunner` 允许触碰 `System.Diagnostics.Process`；其余代码通过 `IProcessRunner`。CI 用架构测试（reflection 扫描）强制此约束。

| 风险 | 纪律 |
|---|---|
| 死锁 | 异步读 stdout/stderr；禁用 `WaitForExit()` 无参阻塞等待 |
| 路径空格/转义 | `ArgumentList`；绝不 `Arguments = "..."` 字符串拼接 |
| 编码 | 子进程标准流强制 UTF-8；串口按字节流 + 显式解码器 |
| 跨线程 UI | UI 更新一律经 `Dispatcher.UIThread.Post/InvokeAsync`；ViewModel 不直接触碰控件 |
| 句柄泄漏 | 所有句柄 `DisposeAsync`；长驻会话在窗口关闭/项目切换时强制回收 |
| 异常吞没 | 禁止空 `catch`；`catch` 必转诊断或重抛；分析器规则 + 代码评审清单 |
| 进程树残留 | Windows Job Object 终止整棵树（.NET 10 相关改进见 ADR-0004） |
| 输出爆炸 | 环形缓冲 + 批量刷新（≥16ms 合并一次 UI 更新），保证 <100ms 可见延迟 |

---

## 13. 工具链获取策略

**分级策略**（`docs/toolchain-build.md` 详细化）：

1. **优先探测本机**（§6 优先级链），命中即用，显示来源与版本。
2. **引导用户获取**（G-01）：给出官方下载页、校验值、建议安装目录、安装后"重新探测"按钮；对可解压的 zip 型工具（NASM、CMake、Ninja）提供**应用内下载 + SHA256 校验 + 解压到 `%LOCALAPPDATA%\MetalForge\tools\`**（明确告知、需用户点击、带进度与取消）。
3. **CI 构建分发**：`xorriso`、`iasl`、`openocd` 等小型工具，用 GitHub Actions（Ubuntu）交叉构建出 Windows 产物，作为 Release 附件；本地构建脚本 `scripts/build-tools.ps1` 走 MSYS2 路线作为备选。
4. **绝不**：静默下载、静默执行安装程序、修改系统级 PATH（只改进程环境或在用户配置中记录）。

**工具获取必须验证**：下载后校验 SHA256；解压后运行 `--version` 确认；失败则回滚并报告，不留半成品。

---

## 14. 运行与调试链路

```
构建产物 ──┬─→ 产物校验（G-04）──失败→ 诊断 + 下一步提示（不启动 QEMU）
          └─→ 通过 → QEMU 参数生成（含固件解析 G-02）→ 启动
                        ├─ 串口/终端标签页（G-09）
                        ├─ QMP 监视器（G-07）
                        └─ GDB 会话（用户可 -s -S 挂起）→ MI2 → 源码级调试 UI
嵌入式路径：OpenOCD（GDB server + telnet）→ 同一 IDebugSession 实现
烧录路径：UF2/HEX 转换 → 安全护栏（G-06）→ 写盘/探针
```

**黑屏/挂起的"下一步提示"示例**（G-15）：未找到引导签名 → "产物缺少 Multiboot 头，可能未链接 `boot.S`"；`-kernel` 加载后无输出 → "检查串口初始化（COM1 0x3F8）或改用 `-serial stdio`"；UEFI 直接返回固件 → "`efi_main` 未返回 `EFI_STATUS` 或未调用 `ExitBootServices`"。

---

## 15. AI Agent 设计

- **后端**：OpenAI 兼容 REST、Anthropic Messages API、DeepSeek、本地 Ollama（可选）。统一为 `IAiBackend`，流式输出（SSE / chunked）。
- **能力**（受边界约束）：生成引导/启动汇编骨架、解释固件与链接错误、生成/解释链接脚本、把硬件手册要点转成寄存器定义草案、解释 `.map` 与符号表。
- **工作流（G-17）**：请求 → 流式预览（可停止）→ **补丁视图（并排 diff）** → 用户按文件/按 hunk 接受 → 写入（自动生成"由 AI 生成"标记注释，可追溯）。
- **上下文选择器**：当前文件、选区、构建诊断、符号表、QEMU 串口尾部 N 行、项目配置——用户勾选才进入上下文，并在发送前显示"将发送什么"。
- **密钥**：Windows Credential Manager（`ISecretStore`），配置只存引用名；UI 显示"已保存于凭据管理器"。
- **禁止项（代码强制）**：自动执行构建产物、自动运行 QEMU、自动 `git commit`、自动写文件。

---

## 16. 国际化与可访问性

- 文案全部走 `assets/locales/*.json`（默认 `zh-Hans`，预留 `en`）；C# 与 XAML 中不得出现用户可见字符串字面量；CI lint 检查（`ViewModels/Views` 目录禁止中文字符串字面量，白名单：日志与技术标识）。
- 语言切换即时生效（无需重启），首选项持久化到用户配置。
- 键盘：所有功能可达；`Ctrl+Shift+P` 命令面板；快捷键映射表存 `assets/layouts/keybindings.json` 并可由用户覆盖；vim 模式（M5 规划：AvaloniaEdit 的输入处理器扩展）。
- 高对比度主题、字号缩放（80%–200%）、屏幕阅读器：所有交互控件提供 `AutomationProperties.Name`，编辑器提供语义化行文本（分页读取）。

---

## 17. 性能预算

| 指标 | 目标 | 保障手段 | 验证方式 |
|---|---|---|---|
| 冷启动 | < 2s | 延迟初始化非首屏服务（clangd/QEMU 探测延后）；DI 精简；避免 Hosting | 启动计时埋点，写入 `--profile-startup` 报告 |
| 打开 10MB 文件 | < 1s | 编辑器仅装载可视区（AvaloniaEdit 自身行虚拟化）；大文件 >4MB 时禁用语法高亮并提示 | 单元/手动测试 fixture |
| 构建输出刷新延迟 | < 100ms | 批量合并 UI 更新（16–50ms），不在每行触发布局 | 压测：10k 行/秒 输出 |
| 空项目内存 | < 500MB | 避免加载全部模板/高亮资源；图片按需解码；编辑器文档上限 | `--profile-memory` 采样 |

超预算即视为缺陷（不做"以后优化"）。

---

## 18. 里程碑拆解

**每个里程碑结束必须停下来交给用户 review。** 每个原子单元完成即提交（Conventional Commits）。

### M0 — 规划与仓库基建（已完成）
产出：`DESIGN.md`、`DEPENDENCIES.md`、`docs/adr/0001-0006`、`.gitignore`、`Directory.Build.props`、`Directory.Packages.props`、解决方案骨架（可 `dotnet build` 通过的空分层工程）、首次提交 + `v0.0.1` tag。
验收：`dotnet build` 成功；分层约束（Core 不引用 Avalonia）有测试强制。

### M1 — 外壳与配置系统（已完成，验收见 `docs/manual-verification/M1.md`）
产出：经典 IDE 布局、主题/布局 JSON 驱动、三级配置 + Schema 校验 + 热重载、关于/许可/更新日志/贡献者视图、启动画面、窗口图标与几何记忆、项目系统（`metalforge.json` + 文件树）、目标矩阵（架构 × 引导方式）、代码编辑器（AvaloniaEdit）、i18n、G-01 工具链健康面板。
**提前完成**：M3 计划的自研 XSHD 高亮（NASM/GAS/LD/Makefile/DTS/INF/DEC/DSC/GRUB）已实现，且配色由主题生成。
验收：改主题 JSON 立即换肤；改 `app.json` 立即改标题与关于信息；无硬编码颜色（lint 通过）；打开项目、双击文件、编辑器正确显示内容与高亮；窗口位置被记住。

### M2 — 构建与运行闭环（第一个"真的能跑"的里程碑）
产出：构建计划（`BuildPlan`：语言检测 + 架构必需选项 + 工具链选择）、CMake 工程与工具链文件生成、构建引擎（CMake/Ninja/Make/GCC/Clang/NASM）+ 诊断解析、产物校验器（ELF/PE）、ISO9660 + El Torito 自研实现、QEMU 参数生成 + 固件解析、串口终端、构建/运行面板、脚本钩子。
验收：**真实构建并运行一个 x86_64 Multiboot "Hello" 与一个 UEFI `.efi`（需用户机器具备工具链；缺失时走 G-01 引导）**，串口输出可见。
注意：本机实测**没有任何 OSDev 工具链**，因此本里程碑的验收必须包含
"工具链缺失时给出可执行的下一步"这一路径，而不仅是成功路径。

### 18.1 M2 构建系统设计

#### 分层与职责

| 组件 | 位置 | 职责 |
|---|---|---|
| `BuildPlan` | Core/Build | 由项目配置 + 目标矩阵推出**完整构建方案**：编译/汇编/链接命令、输出路径、必需选项 |
| `ToolchainResolver` | Core/Build | 由目标架构的三元组在已探测工具中选出实际可执行文件；缺失时报出需要哪一个 |
| `CMakeProjectWriter` | Core/Build | 生成 `CMakeLists.txt` 与 `cmake/<triple>.toolchain.cmake` |
| `BuildDiagnosticParser` | Core/Build | 把 GCC/Clang/Ninja/MSVC 输出解析为 `Diagnostic`（文件、行、列、级别、代码） |
| `IBuildEngine` | Core/Build | 执行构建；产出结构化结果（诊断、产物路径、耗时、退出码） |
| `ImageBuilder` | Core/Build/Images | ISO9660 + El Torito 生成、EFI 系统分区布局、flat binary 提取 |
| `RunPlan` | Core/Build | QEMU 参数生成（由引导方式的模板 + 架构 + 项目运行配置合成） |

#### 为什么生成 CMake 而不是自己拼编译命令

两种做法都能用，取舍如下：

- **自己拼命令行**：控制力最强，但对每个目标都要维护一份"编译 → 汇编 → 链接"的流程，
  且用户无法用熟悉的工具做增量构建、无法接入 IDE 的 CMake 支持。
- **生成 CMake 工程**：与主流 OSDev 工作流一致（用户能直接 `cmake --build` 复现），
  增量构建、依赖跟踪、多配置由 CMake 负责。

**决定：生成 CMake 工程**，但把"必需编译选项从哪来"留在我们这边 ——
架构矩阵里的 `requiredCompilerFlags` 是 OSDev 特有的知识（`-mno-red-zone`、
`-mgeneral-regs-only`、`-mcmodel=medany`），必须在生成的工程里显式带上。
生成的工程里会写明每个选项的用途，用户改起来才有依据。

#### 诊断：构建输出必须变成可点击的问题

原始构建输出不是给人看的。`BuildDiagnosticParser` 把常见格式解析成结构化的
`Diagnostic`（复用配置系统的类型），界面据此提供"双击跳到出错行"。

需要支持的格式（都来自真实工具）：

| 工具 | 格式 |
|---|---|
| GCC / Clang | `path:line:col: error: message [-Wflag]` |
| GCC（无列） | `path:line: error: message` |
| Ninja | `FAILED: <target>` 以及其后紧跟的工具输出 |
| ld / lld | `path:(.text+0x1f): undefined reference to 'x'` |
| NASM | `path:line: error: message` |
| MSVC（EDK2 场景） | `path(line) : error C1234: message` |

解析必须**容错**：看不懂的行原样进入输出面板，而不是被丢弃。
一个把"看不懂的行"丢掉的解析器比没有解析器更糟。


### M3 — 调试与语言智能
产出：clangd LSP（补全/诊断/跳转/switchSourceHeader/格式化）、自研 `.xshd` 高亮（NASM/GAS/LD/Makefile/INF/DEC/DSC/DTS）、GDB MI2 调试器（QEMU gdbstub）、QMP 监视器、符号/map 工具、OpenOCD 集成 + UF2/HEX + 安全写盘护栏。
验收：在 QEMU 里对内核下断点、单步、看栈与寄存器、读内存；clangd 在 freestanding 项目里不产生假错误。

### M4 — 向导、模板与框架集成
产出：8 个模板 + 向导 UI（架构 × 引导方式联动）、模板快照测试、GNU-EFI/EDK2 骨架、U-Boot 骨架、脚本钩子完善。
验收：从零生成 8 个模板项目，全部能构建（具备工具链时）；至少 2 个能一键 QEMU 运行。

### M5 — AI Agent、命令面板、设置与外部编辑器体验
产出：AI 面板 + 补丁确认流、凭据管理器密钥存储、命令面板、快捷键自定义、设置界面、崩溃诊断包（G-14）。
验收：AI 建议可预览可拒绝；密钥不落配置文件（手动验证 + 测试）。

### M6 — 打磨、交付与可访问性
产出：ESP/FAT 镜像构建器、i18n 英文、高对比度、屏幕阅读器支持、更新检查 + 签名验证、可复现报告（G-05）、十六进制查看器（G-08）、ITM/SWO 与 semihosting（G-12）、性能达标验证、README/CONTRIBUTING。
验收：性能预算全部达标；安装包产出；文档完整。

---

## 19. 测试与自我验证策略

| 层 | 手段 |
|---|---|
| 单元测试 | 纯逻辑：参数生成、各解析器（GCC/PE/ELF/Multiboot/MI2/LSP 帧/ANSI）、配置合并与 Schema 校验 |
| 集成测试 | **真实调用进程**：`ProcessRunner` 对真实 `cmd`/`dotnet` 的超时与取消；工具探测对真实安装目录；带 5s 超时避免挂 CI |
| 金样测试 | 模板生成结果与 `tests/fixtures/expected/**` 逐文件对比 |
| 契约测试 | clangd/QEMU/GDB 的协议帧解析用录制样本（`tests/fixtures/protocol/*.jsonl`） |
| 手动验证 | `docs/manual-verification/Mn.md`，每里程碑一份 checklist，逐条打勾并记录环境 |
| 构建自证 | 每里程碑必须真实 `dotnet build` + `dotnet test` + 至少一次真实 UI 启动截图 |

**原则**：不确定的 API 先写最小验证程序跑通再落地（本次会话已用该法核实 Avalonia 12.1.3 + AvaloniaEdit 12.0.0 + 编译绑定 + `[ObservableProperty]` 可构建，"我写完了"不等于"它能工作"）。

---

## 20. 已知风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | 用户机器无任何 OSDev 工具链（**实测确认**：gcc/cmake/qemu/nasm 全缺） | 里程碑验收无法端到端 | G-01 引导 + 应用内安全下载小工具；验收分两级："引擎逻辑"用伪造/录制输出测，"真链路"在工具可用时验证；必要时申请用户授权安装工具 |
| R2 | Windows 上 `grub-mkrescue`/`xorriso` 难以获取 | Multiboot ISO 交付受阻 | 自研最小 ISO9660+El Torito 写入器（可控、无外部依赖）；`xorriso` 作为可选高保真路径 |
| R3 | Windows 无 loop mount / `mtools` | UEFI 磁盘镜像（ESP）难做 | 自研最小 FAT16/32 写入器（G-03）；EDK2 的 `mkfat` 思路参考 |
| R4 | 交叉工具链预构建来源不稳定 | 长期可维护性 | 记录多个候选源 + SHA256；CI 自建流水线作为兜底；所有获取路径集中在 `toolchains.json`，改配置不改代码 |
| R5 | OVMF 变体多、路径各异 | 一键运行失败率高 | G-02 多路径解析 + 变体识别（`OVMF_CODE.secboot.fd` 等）+ 失败时给出确切期望路径与获取指引 |
| R6 | QEMU/GDB/clangd 版本差异导致参数或协议不兼容 | 运行/调试/智能功能间歇失败 | 启动即探测版本并记录；按版本选择参数模板；协议层容错（未知字段忽略）；"诊断包"收集版本信息 |
| R7 | Avalonia 12 与 AvaloniaEdit 12 生态较新，可能有坑 | 开发阻塞 | 已完成最小验证；关键 UI 先用小原型验证再大规模写；锁版本并记录已知问题到 ADR |
| R8 | 长驻进程（QEMU/GDB/clangd/OpenOCD）泄漏或残留 | 用户机器被拖慢 | Job Object + 会话注册表 + 关闭钩子 + `DisposeAsync`；测试覆盖"强制杀进程树" |
| R9 | 自研解析器（ELF/PE/FAT/ISO/MI2/LSP）出错 | 误报/漏报 | 每个解析器配真实 fixture 与边界用例；解析失败**降级为警告并显示原始信息**，绝不崩溃 |
| R10 | 范围过大导致半成品 | 交付失败 | 已排优先级与里程碑；M2 结束必须能端到端跑通一个真实项目，否则不加新功能 |
| R11 | 写物理磁盘造成数据损毁 | 不可逆损失 | G-06 护栏 + 默认禁用 + 双确认 + 回读校验 |
| R12 | 依赖停滞（如某个高亮/终端库半年未更新） | 维护成本 | 依赖准入规则 + `DEPENDENCIES.md` 复审节奏（每里程碑一次） |

---

## 附录 B：已确认的决策（原开放问题）

> 2026-02 已由用户确认，全部按建议执行。以下为**生效决策**，不再是待议项。

| # | 决策 | 生效内容 | 影响 |
|---|---|---|---|
| Q1 | **工具链获取方式** | 先实现 G-01 引导安装（探测 + 应用内安全下载 + SHA256 校验）；**M2 端到端验收前再单独请求用户授权**下载最小集合（QEMU + NASM + CMake + Ninja，约数百 MB） | 决定 M2 能否端到端验收；在此之前 M2 的引擎逻辑用录制样本与伪造工具验证 |
| Q2 | **Multiboot ISO 生成路径** | 默认走**自研最小 ISO9660 + El Torito 写入器**（不依赖 xorriso/GRUB）；`xorriso` + `grub-mkrescue` 作为可选"高保真"路径保留 | 少一个外部依赖、产物完全可控；ADR-0008 需在 M2 落地时补齐 |
| Q3 | **仓库公开性** | **暂不公开**，先本地 Git；等 M2 跑通后再决定（创建公开仓库属"必须先问"事项） | CONTRIBUTING.md 推迟到仓库公开前；CI 暂用本地脚本 |
| Q4 | **AI 后端默认** | 抽象层支持 OpenAI 兼容 / Anthropic / DeepSeek / 本地 Ollama；默认预置 **DeepSeek + 本地 Ollama**，其余留空待填；密钥一律存 Windows 凭据管理器 | M5 默认配置；ADR-0013 需在 M5 落地时补齐 |
| Q5 | **vim 模式** | M5 提供**最简实现**（模态输入 + 常用操作：`h/j/k/l`、`i/a/o`、`dd/yy/p`、`/` 搜索、`:` 命令行子集），不做 vimscript 与插件 | M5 工作量可控 |
| Q6 | **便携模式** | **支持**。命令行 `--portable` 或程序目录存在 `portable.marker` 时，用户配置与工具链都放程序目录，不写 `%APPDATA%` | M1 配置系统的路径解析分支（`AssetOptions.Portable` 已预留） |

---

## 附录 A：事实核查记录

本次会话中**实际执行**的核实（非记忆）：

1. `dotnet --version` → `10.0.401`；`dotnet --list-runtimes` → `Microsoft.NETCore.App 10.0.12`。
2. nuget.org flat-container 查询实测最新稳定版：`Avalonia 12.1.3`、`Avalonia.AvaloniaEdit 12.0.0`（旧包 `AvaloniaEdit` 停在 `0.10.12`，**弃用**）、`CommunityToolkit.Mvvm 8.4.2`、`Microsoft.Extensions.* 10.0.12`、`JsonSchema.Net 9.4.0`、`xunit.v3 4.0.1`、`Microsoft.NET.Test.Sdk 18.10.1`。
3. 在 `%TEMP%` 建立最小 Avalonia 12.1.3 应用（`App.axaml` + `MainWindow.axaml` 编译绑定 `x:DataType` + `[ObservableProperty]`/`[RelayCommand]` + AvaloniaEdit `TextEditor` + `HighlightingManager`）→ `dotnet build` **成功，0 warning 0 error**，`dotnet run` 可执行到代码路径。
4. 运行时枚举 AvaloniaEdit 高亮定义 → 共 21 种（含 C++/C#/XML/Json），**不含 NASM/GAS/LD script/Makefile** → 确认必须自研 `.xshd`（F-13）。
5. 本机 `PATH` 探测：`cmake`/`ninja`/`qemu-system-x86_64`/`gcc`/`clang`/`nasm`/`gh`/`pnpm` **均不存在** → R1 为实证风险，G-01 为 P0。
6. 文档站点（`docs.avaloniaui.net`、`v11.docs.avaloniaui.net`、`raw.githubusercontent.com`）本次网络不可达 → Avalonia 12 破坏性变更清单**尚未从官方文档核实**，处理方式：以实测编译为准，M1 开始时再补文档核查（列入 ADR 待办）。
7. 应用启动后实测屏幕几何（应用自身日志）：`bounds 2256x1504, work 0,0 2256x1432, screenScaling 1.5`；
   而截图进程读到真实帧缓冲为 `1280x720`。两个独立来源（Avalonia 平台 API 与 Win32 `GetSystemMetrics`）
   在应用进程内一致，仅截图进程不同 → 判定为当前会话显示环境特性，非应用缺陷；应用侧仍保留帧缓冲钳制作为保险。

---

## 附录 D：M0 实际产出与偏差

规划与实际之间的差异必须记录，否则文档会退化成愿望清单。

### D.1 已完成且经真实验证

| 项 | 验证方式 |
|---|---|
| 4 个工程分层骨架（Core / Templates / App / Tests） | `dotnet build` 通过，**0 警告 0 错误**（`TreatWarningsAsErrors=true` 已开启） |
| 三级配置 + JSON Schema 校验 + 热重载 | 73 个测试全绿（0.44s）；`scripts/Verify-HotReload.ps1` 端到端验证通过 |
| 品牌/主题/布局配置化 | 截图确认界面文字与配色均来自 `assets/`；新增主题文件即生效 |
| 架构约束机器强制 | `ArchitectureTests` + `NoHardCodedVisualsTests` 会因违规而失败（曾真实抓出 Core 内硬编码调色板） |
| 应用真实启动并渲染 | `scripts/Capture-AppWindow.ps1` 产出截图 `build/screenshots/m0-shell.png` |

### D.2 与原规划的偏差

| 偏差 | 原因 | 处置 |
|---|---|---|
| 解决方案文件为 `.slnx` 而非 `.sln` | .NET 10 SDK 的 `dotnet new sln` 默认生成 XML 格式 | 接受；`TestPaths` 与脚本已适配 `.slnx` |
| 自研 JSON Schema 校验器替代 JsonSchema.Net | 后者携带 OSMFEULA（对年收入 ≥ 10k 美元的商业使用收维护费） | 已接受，见 ADR-0006 |
| 测试入口改为直接运行测试程序集 | `dotnet test` 在 .NET 10 + xunit v3 下报"零个测试"，官方模板同样复现 | 已接受，见 ADR-0007 |
| 新增 `FramebufferProbe`（Win32 `GetSystemMetrics`） | 实测环境报告屏幕 2256×1504 而真实帧缓冲 1280×720 | 已接受；作为"OS 报告不可信"的保险，非关键路径 |
| M0 未交付 `CONTRIBUTING.md` | 仓库暂不公开（决策 Q3），该文档在公开前撰写更合适 | 推迟到仓库公开前 |

### D.3 已识别但推迟到后续里程碑

| 项 | 目标里程碑 |
|---|---|
| Avalonia 12 破坏性变更官方文档核查（M0 时网络不可达） | M1 开始 |
| 窗口图标接线（当前仅 exe 图标生效） | M1 |
| 启动画面（`SplashBranding` 配置已就绪） | M1 |
| 窗口尺寸/位置记忆（配置项已就绪） | M1 |
| i18n 资源文件与语言切换 | M1（骨架）→ M6（英文完整） |
| 工具链健康面板 | M1（只读探测）→ M2（下载引导） |
| 外部进程统一入口 `IProcessRunner` + Job Object | M2 |
