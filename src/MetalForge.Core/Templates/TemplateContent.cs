namespace MetalForge.Core.Templates;

/// <summary>
/// 各模板的文件内容。
///
/// 这里的内容**就是生成出来的文件的样子**：占位符用 <c>@@NAME@@</c> 形式，
/// 因此正文里的花括号、缩进、注释都不需要转义。
///
/// 每个模板都刻意生成"能编译通过并输出一行字"的完整程序，而不是一堆 TODO。
/// 初学者第一次构建就失败会直接摧毁信心；一个能跑起来的内核才是有用的起点。
/// </summary>
internal static class TemplateContent
{
    /// <summary>取某个模板在某架构下的文件列表（相对路径 → 内容）。</summary>
    public static IReadOnlyList<(string Path, string Content)> Expand(string templateId, string architectureId)
        => templateId switch
        {
            "uefi-c" => UefiFiles,
            "bare-metal-c" => BareMetalFiles,
            "multiboot2-asm" => MultibootAsmFiles,
            _ => MultibootCFiles(architectureId),
        };

    // =====================================================================
    // Multiboot 2（C）
    // =====================================================================

    private static IReadOnlyList<(string, string)> MultibootCFiles(string architectureId)
    {
        var is64 = architectureId == "x86_64";

        var bootAssembly = is64
            ? BootMultiboot64
            : BootMultiboot32;

        return
        [
            ("README.md", MultibootReadme),
            ("linker.ld", LinkerMultiboot),
            ("include/kernel.h", KernelHeader),
            ("src/boot/boot.S", bootAssembly),
            ("src/kernel/main.c", MultibootMainC),
            ("src/kernel/serial.c", SerialImplementation),
            ("src/kernel/memory.c", MemoryImplementation),
            ("Makefile", Makefile),
        ];
    }

    private const string MultibootReadme = """
        # @@PROJECT_NAME@@

        由 MetalForge 生成的多架构 OSDev 项目。

        | 项 | 值 |
        |---|---|
        | 架构 | @@ARCH_DISPLAY@@ |
        | 目标三元组 | `@@ARCH_TRIPLE@@` |
        | 引导方式 | Multiboot 2（GRUB 2） |
        | 入口 | `kernel_main` |

        ## 它现在能做什么

        构建后运行，串口会输出一行字并报告 GRUB 传入的 Multiboot 信息结构地址。
        **这不是骨架，是一个能跑起来的最小内核。**

        ## 构建

        在 MetalForge 里按 `Ctrl+Shift+B`，或手工执行：

        ```bash
        cmake -S . -B build/cmake "-DCMAKE_TOOLCHAIN_FILE=cmake/@@ARCH_TRIPLE@@.toolchain.cmake"
        cmake --build build/cmake
        ```

        ## 目录

        | 路径 | 内容 |
        |---|---|
        | `metalforge.json` | 项目配置：架构、引导方式、编译选项 |
        | `linker.ld` | 链接脚本：决定内核被加载到哪个地址、各段怎么排 |
        | `src/boot/boot.S` | 汇编入口：声明 Multiboot 头、建栈、跳到 C |
        | `src/kernel/main.c` | 内核入口 |
        | `src/kernel/serial.c` | 串口输出：裸机上最早能用的调试手段 |
        | `src/kernel/memory.c` | 内存操作：裸机没有 libc |

        ## 为什么没有用 libc

        内核运行在没有任何操作系统支持的环境里，`printf`、`malloc`、`memcpy`
        都不存在。标准库假设有系统调用可用，而这里没有。

        ## 下一步

        1. 读 `multiboot_info` 拿到 GRUB 填好的内存映射；
        2. 建立自己的 GDT 与 IDT，接管中断 —— 这样你才能拿到时钟与键盘；
        3. 实现物理内存分配器，然后才有条件谈分页。

        ## 目标架构备忘

        @@ARCH_NOTES@@
        """;

    private const string LinkerMultiboot = """
        /* 链接脚本：由 MetalForge 生成。
         *
         * 内存布局是 OSDev 的核心知识，因此这里逐段写明用途，
         * 而不是给出一堆看不出所以然的符号。
         */

        ENTRY(_start)

        SECTIONS
        {
            /* 加载地址：Multiboot 的约定是 1MB 以上。
             * 1MB 以下是实模式代码、BIOS 数据区与显存，放内核会被覆盖。 */
            . = 1M;

            .text : ALIGN(4K)
            {
                /* Multiboot 头必须落在镜像前 32KB 内，因此排在最前面。
                 * KEEP 防止链接器把它当无用段丢掉。 */
                KEEP(*(.multiboot_header))
                *(.text .text.*)
            }

            .rodata : ALIGN(4K)
            {
                *(.rodata .rodata.*)
            }

            .data : ALIGN(4K)
            {
                *(.data .data.*)
            }

            .bss : ALIGN(4K)
            {
                __bss_start = .;
                *(.bss .bss.*)
                *(COMMON)
                __bss_end = .;
            }

            __kernel_end = .;

            /* 丢弃宿主工具链塞进来的无用段，否则链接器会警告"找不到 .eh_frame 的输入"。 */
            /DISCARD/ :
            {
                *(.eh_frame)
                *(.comment)
                *(.note.*)
            }
        }
        """;

    private const string KernelHeader = """
        /* 内核公共声明。 */
        #ifndef METALFORGE_KERNEL_H
        #define METALFORGE_KERNEL_H

        #include <stdint.h>
        #include <stddef.h>

        /* 串口输出。裸机上没有 printf，这是最早能用的调试手段，
         * 也是 QEMU 里唯一能可靠看到的输出通道。 */
        void serial_initialize(void);
        void serial_write(const char *text);
        void serial_write_hex(uint64_t value);

        /* 内存操作。不用 libc：裸机没有 libc。 */
        void *memory_set(void *destination, int value, size_t count);
        void *memory_copy(void *destination, const void *source, size_t count);

        #endif /* METALFORGE_KERNEL_H */
        """;

    private const string BootMultiboot64 = """
        /* 汇编入口：由 MetalForge 生成。
         *
         * 职责只有三件事，做完就跳到 C：
         *   1) 声明 Multiboot 2 头，让 GRUB 认识这是一个可引导内核；
         *   2) 建立栈 —— C 代码运行前必须有栈；
         *   3) 按 System V ABI 把参数放进寄存器。
         */

        .set MB2_MAGIC, 0xE85250D6
        .set MB2_ARCH,  0                  /* 0 = i386 保护模式 */

        .section .multiboot_header, "a"
        .align 8
        multiboot_header_start:
            .long MB2_MAGIC
            .long MB2_ARCH
            .long multiboot_header_end - multiboot_header_start
            /* 校验和：前三个字段之和的补码，必须使总和为 0。
             * 写错时 GRUB 会直接说"这不是一个 Multiboot 内核"。 */
            .long -(MB2_MAGIC + MB2_ARCH + (multiboot_header_end - multiboot_header_start))
        /* 结束标记：类型 0、标志 0、大小 8。 */
        .short 0
        .short 0
        .long 8
        multiboot_header_end:

        .section .bss
        .align 16
        stack_bottom:
            .skip 16384                    /* 16 KiB 内核栈 */
        stack_top:

        .section .text
        .global _start
        .type _start, @function
        _start:
            movq $stack_top, %rsp
            /* 16 字节对齐：System V ABI 要求，且 SSE 指令会假设对齐。 */
            andq $-16, %rsp

            /* GRUB 把 Multiboot 信息结构地址放在 %ebx，魔数放在 %eax。
             * 按 System V ABI 的第一个参数是 %rdi。 */
            movq %rbx, %rsi
            movl %eax, %edi

            call kernel_main

        /* 内核返回不该发生。停下来而不是跑进未知内存。 */
        1:  cli
            hlt
            jmp 1b

        .size _start, . - _start
        """;

    private const string BootMultiboot32 = """
        /* 汇编入口：由 MetalForge 生成（32 位）。
         *
         * 32 位下参数通过栈传递，这一点与 64 位不同。
         */

        .set MB2_MAGIC, 0xE85250D6
        .set MB2_ARCH,  0

        .section .multiboot_header, "a"
        .align 8
        multiboot_header_start:
            .long MB2_MAGIC
            .long MB2_ARCH
            .long multiboot_header_end - multiboot_header_start
            .long -(MB2_MAGIC + MB2_ARCH + (multiboot_header_end - multiboot_header_start))
        .short 0
        .short 0
        .long 8
        multiboot_header_end:

        .section .bss
        .align 16
        stack_bottom:
            .skip 16384
        stack_top:

        .section .text
        .global _start
        .type _start, @function
        _start:
            movl $stack_top, %esp
            andl $-16, %esp

            /* cdecl：参数从右向左压栈。 */
            pushl %ebx
            pushl %eax

            call kernel_main

        1:  cli
            hlt
            jmp 1b

        .size _start, . - _start
        """;

    private const string MultibootMainC = """
        /* 内核入口：由 MetalForge 生成。
         *
         * 这是一个最小但完整的内核：初始化串口、报告引导信息、停机。
         * 它刻意保持短小 —— 能跑起来看到输出，比一堆注释掉的 TODO 有用得多。
         */

        #include "kernel.h"

        /* Multiboot 2 的引导魔数。引导器必须传这个值。 */
        #define MULTIBOOT2_BOOTLOADER_MAGIC 0x36D76289

        void kernel_main(uint32_t magic, void *multiboot_info)
        {
            serial_initialize();

            serial_write("\n");
            serial_write("MetalForge kernel running on @@ARCH_DISPLAY@@.\n");

            if (magic == MULTIBOOT2_BOOTLOADER_MAGIC)
            {
                serial_write("Booted by a Multiboot 2 compliant loader.\n");
                serial_write("Multiboot info structure: ");
                serial_write_hex((uint64_t)(uintptr_t)multiboot_info);
                serial_write("\n");
            }
            else
            {
                /* 不直接停机：说清发生了什么，否则用户只会看到"没有输出"。 */
                serial_write("Warning: bootloader passed magic ");
                serial_write_hex(magic);
                serial_write(" instead of the Multiboot 2 value.\n");
            }

            serial_write("Halting. Edit src/kernel/main.c to continue.\n");

            for (;;)
            {
                __asm__ volatile ("hlt");
            }
        }
        """;

    private const string SerialImplementation = """
        /* 串口输出：由 MetalForge 生成。
         *
         * 为什么第一个驱动是串口：它在内核还没有任何其他能力时就能用，
         * 而且 QEMU 可以把它直接接到终端（-serial stdio），
         * 是裸机上最省事的观察通道。
         */

        #include "kernel.h"

        #define COM1 0x3F8

        /* 串口寄存器相对 COM1 的偏移。 */
        #define SERIAL_DATA         (COM1 + 0)
        #define SERIAL_INTERRUPT    (COM1 + 1)
        #define SERIAL_FIFO         (COM1 + 2)
        #define SERIAL_LINE_CONTROL (COM1 + 3)
        #define SERIAL_MODEM        (COM1 + 4)
        #define SERIAL_LINE_STATUS  (COM1 + 5)

        static void port_write_byte(uint16_t port, uint8_t value)
        {
            __asm__ volatile ("outb %0, %1" : : "a"(value), "Nd"(port));
        }

        static uint8_t port_read_byte(uint16_t port)
        {
            uint8_t value;
            __asm__ volatile ("inb %1, %0" : "=a"(value) : "Nd"(port));
            return value;
        }

        void serial_initialize(void)
        {
            port_write_byte(SERIAL_INTERRUPT, 0x00);    /* 关闭串口中断 */
            port_write_byte(SERIAL_LINE_CONTROL, 0x80); /* 打开 DLAB 以便设置波特率 */
            port_write_byte(SERIAL_DATA, 0x03);         /* 除数低字节：38400 baud */
            port_write_byte(SERIAL_INTERRUPT, 0x00);    /* 除数高字节 */
            port_write_byte(SERIAL_LINE_CONTROL, 0x03); /* 8 位数据、无校验、1 位停止 */
            port_write_byte(SERIAL_FIFO, 0xC7);         /* 启用并清空 FIFO */
            port_write_byte(SERIAL_MODEM, 0x0B);        /* 置位 DTR/RTS/OUT2 */
        }

        void serial_write(const char *text)
        {
            while (*text != '\0')
            {
                /* 等待发送保持寄存器为空。
                 * 不等待会丢字符 —— 这是串口输出"缺字"的最常见原因。 */
                while ((port_read_byte(SERIAL_LINE_STATUS) & 0x20) == 0)
                {
                }

                port_write_byte(SERIAL_DATA, (uint8_t)*text);
                text++;
            }
        }

        void serial_write_hex(uint64_t value)
        {
            static const char digits[] = "0123456789ABCDEF";
            char buffer[19];
            int index = 0;

            buffer[index++] = '0';
            buffer[index++] = 'x';

            for (int shift = 60; shift >= 0; shift -= 4)
            {
                buffer[index++] = digits[(value >> shift) & 0xF];
            }

            buffer[index] = '\0';
            serial_write(buffer);
        }
        """;

    private const string MemoryImplementation = """
        /* 内存操作：由 MetalForge 生成。
         *
         * 编译器会把结构体赋值、数组初始化编译成对 memcpy/memset 的调用，
         * 因此即使你不主动调用它们，链接时也可能需要这两个符号。
         * 不提供它们会得到 "undefined reference to `memset'" 这种看起来莫名其妙的错误。
         */

        #include "kernel.h"

        void *memory_set(void *destination, int value, size_t count)
        {
            uint8_t *bytes = (uint8_t *)destination;

            for (size_t index = 0; index < count; index++)
            {
                bytes[index] = (uint8_t)value;
            }

            return destination;
        }

        void *memory_copy(void *destination, const void *source, size_t count)
        {
            uint8_t *to = (uint8_t *)destination;
            const uint8_t *from = (const uint8_t *)source;

            for (size_t index = 0; index < count; index++)
            {
                to[index] = from[index];
            }

            return destination;
        }

        /* 编译器可能按名字直接调用这两个标准函数，因此提供同名符号。
         * 用 __attribute__((weak)) 是为了让将来引入真正的实现时不冲突。 */
        __attribute__((weak)) void *memset(void *destination, int value, size_t count)
            => memory_set(destination, value, count);

        __attribute__((weak)) void *memcpy(void *destination, const void *source, size_t count)
            => memory_copy(destination, source, count);
        """;

    // =====================================================================
    // UEFI（C）
    // =====================================================================

    private static IReadOnlyList<(string, string)> UefiFiles =>
    [
        ("README.md", UefiReadme),
        ("linker.ld", LinkerUefi),
        ("include/efi.h", EfiHeader),
        ("src/main.c", UefiMainC),
        ("Makefile", Makefile),
    ];

    private const string UefiReadme = """
        # @@PROJECT_NAME@@

        由 MetalForge 生成的 UEFI 应用。

        | 项 | 值 |
        |---|---|
        | 架构 | @@ARCH_DISPLAY@@ |
        | 目标三元组 | `@@ARCH_TRIPLE@@` |
        | 产物 | `kernel.efi`（PE32+）+ 内嵌它的 `kernel.iso` |
        | 入口 | `EfiMain` |

        ## 为什么 UEFI 是很好的起点

        固件已经提供了控制台输出、内存分配、文件系统访问。
        你可以在几分钟内看到自己的代码在真实固件里运行，
        而不必先写引导加载器和一堆驱动。

        ## 运行链路

        MetalForge 会依次做三件事：

        1. 把 `.efi` 放进一个 FAT 映像的 `EFI/BOOT/@@EFI_BOOT_NAME@@`；
        2. 用 El Torito 把该 FAT 映像包进可引导 ISO；
        3. 用 OVMF 固件启动 QEMU，并把串口接到终端。

        这条链路与真实机器上的 U 盘启动完全一致，
        因此在 QEMU 里能跑起来的 `.efi` 拷到 U 盘上也能跑。

        ## 本模板刻意不引入 GNU-EFI 或 EDK2

        它们的头文件规模很大，而"打印一行字"只需要 `include/efi.h` 里那些声明。
        等你需要更多协议（文件系统、网络、图形）时再引入它们也不迟 ——
        但那时你会更清楚自己需要哪一部分。

        ## 目标架构备忘

        @@ARCH_NOTES@@
        """;

    private const string LinkerUefi = """
        /* UEFI 应用的链接脚本。
         *
         * PE 格式的头与节表由链接器负责生成，这里只需要保证段的对齐与顺序合理，
         * 并丢弃宿主工具链塞进来的、固件不认识的段。
         */

        ENTRY(EfiMain)

        SECTIONS
        {
            . = 0;

            .text : ALIGN(4K)
            {
                *(.text .text.*)
            }

            .rodata : ALIGN(4K)
            {
                *(.rodata .rodata.*)
            }

            .data : ALIGN(4K)
            {
                *(.data .data.*)
            }

            .bss : ALIGN(4K)
            {
                *(.bss .bss.*)
                *(COMMON)
            }

            /DISCARD/ :
            {
                *(.reloc)
                *(.comment)
                *(.eh_frame)
                *(.note.*)
            }
        }
        """;

    private const string EfiHeader = """
        /* UEFI 的最小类型与协议声明：由 MetalForge 生成。
         *
         * 只声明"打印一行字"与"取固件版本"需要的那部分。
         * 完整规范在两三千页文档里，但你需要的开头几页就在这里。
         */
        #ifndef METALFORGE_EFI_H
        #define METALFORGE_EFI_H

        #include <stdint.h>
        #include <stddef.h>

        /* UEFI 在 x86_64 上使用 Microsoft x64 调用约定。
         * 不加这个属性时，函数参数会按 System V 传递，固件调用你的入口就会拿到垃圾参数。 */
        #define EFIAPI __attribute__((ms_abi))

        #define EFI_SUCCESS 0

        typedef uint64_t EFI_STATUS;
        typedef void *EFI_HANDLE;
        typedef uint16_t CHAR16;
        typedef uint64_t UINTN;

        typedef struct EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL;

        struct EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL
        {
            void *Reset;
            EFI_STATUS (EFIAPI *OutputString)(EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL *self, const CHAR16 *text);
            void *TestString;
            void *QueryMode;
            void *SetMode;
            void *SetAttribute;
            void *ClearScreen;
            void *SetCursorPosition;
            void *EnableCursor;
            void *Mode;
        };

        /* EFI_SYSTEM_TABLE 的头部在所有版本里布局一致。 */
        typedef struct
        {
            char Reserved[24];
            uint32_t Revision;
            EFI_HANDLE ParentHandle;
            void *SystemTable;
        } EFI_SYSTEM_TABLE_HEADER;

        typedef struct
        {
            EFI_SYSTEM_TABLE_HEADER Header;
            CHAR16 *FirmwareVendor;
            uint32_t FirmwareRevision;
            EFI_HANDLE ConsoleInHandle;
            void *ConIn;
            EFI_HANDLE ConsoleOutHandle;
            EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL *ConOut;
            EFI_HANDLE StandardErrorHandle;
            void *StdErr;
            void *RuntimeServices;
            void *BootServices;
            UINTN NumberOfTableEntries;
            void *ConfigurationTable;
        } EFI_SYSTEM_TABLE;

        #endif /* METALFORGE_EFI_H */
        """;

    private const string UefiMainC = """
        /* UEFI 应用入口：由 MetalForge 生成。
         *
         * 固件加载这个 PE 映像后会调用 EfiMain，并把系统表交给它。
         * 控制台、内存分配、文件系统都已经在里面了。
         */

        #include "efi.h"

        /* UEFI 的字符串是 UTF-16，而源码里的字面量是 ASCII，因此需要转换。 */
        static void to_wide(const char *source, CHAR16 *destination, size_t capacity)
        {
            size_t index = 0;

            while (source[index] != '\0' && index + 1 < capacity)
            {
                destination[index] = (CHAR16)(unsigned char)source[index];
                index++;
            }

            destination[index] = 0;
        }

        static void print(EFI_SYSTEM_TABLE *system_table, const char *text)
        {
            CHAR16 buffer[256];
            to_wide(text, buffer, sizeof(buffer) / sizeof(buffer[0]));
            system_table->ConOut->OutputString(system_table->ConOut, buffer);
        }

        static void print_hex(EFI_SYSTEM_TABLE *system_table, uint64_t value)
        {
            static const char digits[] = "0123456789ABCDEF";
            char buffer[19];
            int index = 0;

            buffer[index++] = '0';
            buffer[index++] = 'x';

            for (int shift = 60; shift >= 0; shift -= 4)
            {
                buffer[index++] = digits[(value >> shift) & 0xF];
            }

            buffer[index] = '\0';
            print(system_table, buffer);
        }

        EFI_STATUS EFIAPI EfiMain(EFI_HANDLE image_handle, EFI_SYSTEM_TABLE *system_table)
        {
            (void)image_handle;

            print(system_table, "\r\n");
            print(system_table, "MetalForge UEFI application on @@ARCH_DISPLAY@@.\r\n");
            print(system_table, "Firmware: ");

            if (system_table->FirmwareVendor != 0)
            {
                /* 直接输出固件自己给的宽字符串，不需要转换。 */
                system_table->ConOut->OutputString(system_table->ConOut, system_table->FirmwareVendor);
            }

            print(system_table, "\r\nUEFI revision: ");
            print_hex(system_table, system_table->Header.Revision);

            print(system_table, "\r\nConfiguration table entries: ");
            print_hex(system_table, system_table->NumberOfTableEntries);

            print(system_table, "\r\nThis application was built by MetalForge.\r\n");
            print(system_table, "Edit src/main.c to continue.\r\n");

            return EFI_SUCCESS;
        }
        """;

    // =====================================================================
    // 裸机（-kernel 直启）
    // =====================================================================

    private static IReadOnlyList<(string, string)> BareMetalFiles =>
    [
        ("README.md", BareMetalReadme),
        ("linker.ld", LinkerBareMetal),
        ("src/boot/boot.S", BootBareMetal),
        ("src/kernel/main.c", BareMetalMainC),
        ("Makefile", Makefile),
    ];

    private const string BareMetalReadme = """
        # @@PROJECT_NAME@@

        由 MetalForge 生成的裸机内核，不经过引导程序。

        | 项 | 值 |
        |---|---|
        | 架构 | @@ARCH_DISPLAY@@ |
        | 目标三元组 | `@@ARCH_TRIPLE@@` |
        | 启动方式 | QEMU `-kernel`，直接把 ELF 加载到内存 |

        ## 适合什么

        早期内核开发。省掉引导程序后迭代最快 —— 改一行代码就能重新跑起来。
        代价是拿不到引导程序提供的规范信息（内存映射、命令行、模块列表），
        这些要自己探测。

        ## 入口做了什么

        `src/boot/boot.S` 里没有引导程序帮忙，因此它必须自己做三件事：

        1. 建立栈 —— C 代码运行前必须有栈；
        2. 清空 BSS —— C 语言假设未初始化的全局变量为 0；
        3. 跳到 `kernel_main`。

        这三件事用 C 写内核时是隐形的，这里全部是看得见的代码。

        ## 输出在哪里

        @@ARCH_DISPLAY@@ 的串口需要平台相关的初始化（时钟、引脚复用、波特率寄存器），
        因此本模板先用调试输出：x86 用 `0xE9` 端口（QEMU 会转发到标准输出），
        其他架构用 `@@DEBUG_INSTRUCTION@@` 前的占位说明。

        下一步就是补上对应平台的串口初始化。

        ## 目标架构备忘

        @@ARCH_NOTES@@
        """;

    private const string LinkerBareMetal = """
        /* 裸机链接脚本：由 MetalForge 生成。
         *
         * QEMU 的 -kernel 会按 ELF 的程序头把镜像放到指定地址，
         * 因此加载地址可以按目标架构的习惯选择。
         */

        ENTRY(_start)

        SECTIONS
        {
            . = @@LOAD_ADDRESS@@;

            .text : ALIGN(4K)
            {
                /* 入口必须排在最前面：没有引导程序替我们找入口点时，
                 * 加载地址处的第一条指令就会被执行。 */
                KEEP(*(.text.entry))
                *(.text .text.*)
            }

            .rodata : ALIGN(4K)
            {
                *(.rodata .rodata.*)
            }

            .data : ALIGN(4K)
            {
                *(.data .data.*)
            }

            .bss : ALIGN(4K)
            {
                __bss_start = .;
                *(.bss .bss.*)
                *(COMMON)
                __bss_end = .;
            }

            __kernel_end = .;

            /DISCARD/ :
            {
                *(.eh_frame)
                *(.comment)
                *(.note.*)
            }
        }
        """;

    private const string BootBareMetal = """
        /* 裸机入口：由 MetalForge 生成。
         *
         * 没有引导程序帮忙，因此这里要自己建立栈、清空 BSS，然后跳进 C。
         * 这三件事用 C 写内核时是隐形的。
         */

        .section .text.entry, "ax"
        .global _start
        .type _start, @function
        _start:
        @@BOOT_SOURCE@@

            /* 清空 BSS：C 语言假设未初始化的全局变量为 0。 */
        @@BSS_CLEAR@@

            call kernel_main

        /* 内核返回不该发生。 */
        4:  @@HALT@@

        .section .bss
        .align 16
        stack_bottom:
            .skip 16384
        stack_top:
        """;

    private const string BareMetalMainC = """
        /* 裸机内核入口：由 MetalForge 生成。 */

        #include <stdint.h>
        #include <stddef.h>

        static void halt_forever(void)
        {
            for (;;)
            {
                __asm__ volatile ("@@DEBUG_INSTRUCTION@@");
            }
        }

        void kernel_main(void)
        {
            /* @@ARCH_DISPLAY@@ 上，串口需要平台相关的初始化：
             * 时钟门控、引脚复用、波特率除数寄存器 —— 这些因芯片而异，
             * 因此本模板先用 QEMU 的调试端口。
             *
             * x86 上 0xE9 端口是 QEMU 的调试控制台，写一个字节就会出现在标准输出里。
             * 不需要任何初始化，是最省事的"我到底跑到这里没有"验证手段。 */

            volatile uint8_t *debug_port = (volatile uint8_t *)0xE9;
            const char *message = "MetalForge bare-metal kernel on @@ARCH_DISPLAY@@.\n";

            for (const char *cursor = message; *cursor != '\0'; cursor++)
            {
                *debug_port = (uint8_t)*cursor;
            }

            /* 下一步：在这里加上目标平台的串口初始化与输出函数，
             * 然后就可以开始做中断、内存管理与调度了。 */

            halt_forever();
        }
        """;

    // =====================================================================
    // Multiboot 2（纯汇编）
    // =====================================================================

    private static IReadOnlyList<(string, string)> MultibootAsmFiles =>
    [
        ("README.md", MultibootAsmReadme),
        ("linker.ld", LinkerMultiboot),
        ("src/boot/boot.asm", BootNasm),
        ("src/kernel/main.asm", MainNasm),
        ("Makefile", Makefile),
    ];

    private const string MultibootAsmReadme = """
        # @@PROJECT_NAME@@

        由 MetalForge 生成的**纯汇编** Multiboot 2 内核。

        | 项 | 值 |
        |---|---|
        | 架构 | @@ARCH_DISPLAY@@ |
        | 汇编器 | NASM |
        | 引导方式 | Multiboot 2（GRUB 2） |

        ## 为什么值得用汇编写一次

        用 C 写内核时，编译器替你做了很多假设：栈已经可用、BSS 已经清零、
        结构体已经对齐。这些假设在汇编里全部变成看得见的指令。

        写一遍之后，你会更清楚 C 编译器在你的内核里做了什么 ——
        这对调试"莫名其妙的崩溃"很有帮助。

        ## 输出

        通过 QEMU 的 `0xE9` 调试端口输出，它会转发到宿主的标准输出。
        不需要任何初始化，是最省事的验证手段。

        ## 构建

        `Ctrl+Shift+B`。注意 NASM 需要单独安装。

        ## 目标架构备忘

        @@ARCH_NOTES@@
        """;

    private const string BootNasm = """
        ; Multiboot 2 内核入口（纯 NASM）：由 MetalForge 生成。

        MB2_MAGIC equ 0xE85250D6
        MB2_ARCH  equ 0

        section .multiboot_header
        align 8
        header_start:
            dd MB2_MAGIC
            dd MB2_ARCH
            dd header_end - header_start
            ; 校验和：前三个字段之和的补码。GRUB 靠它判断这是一个合法的 Multiboot 头。
            dd -(MB2_MAGIC + MB2_ARCH + (header_end - header_start))
        ; 结束标记：类型 0、标志 0、大小 8。
        dw 0
        dw 0
        dd 8
        header_end:

        section .bss
        align 16
        stack_bottom:
            resb 16384
        stack_top:

        section .text
        global _start
        _start:
            mov rsp, stack_top
            ; 16 字节对齐。
            and rsp, -16

            ; GRUB 把 Multiboot 信息结构地址放在 ebx。
            mov rdi, rbx

            call kernel_main

        .hang:
            cli
            hlt
            jmp .hang
        """;

    private const string MainNasm = """
        ; 内核主体（纯汇编）：由 MetalForge 生成。
        ;
        ; 通过 QEMU 的 0xE9 调试端口输出。写一个字节就会出现在宿主的标准输出里，
        ; 不需要任何初始化。

        %define DEBUG_PORT 0xE9

        section .text
        global kernel_main

        ; 输出一个字符。入参：al = 字符
        debug_putc:
            out DEBUG_PORT, al
            ret

        ; 输出以 0 结尾的字符串。入参：rsi = 字符串地址
        debug_puts:
            push rax
        .next:
            lodsb
            test al, al
            jz .done
            call debug_putc
            jmp .next
        .done:
            pop rax
            ret

        kernel_main:
            mov rsi, banner
            call debug_puts

            ; 下一步：加上 GDT/IDT、中断处理与内存管理。
        .forever:
            cli
            hlt
            jmp .forever

        section .rodata
        banner db 'MetalForge assembly kernel running on @@ARCH_DISPLAY@@.', 0x0A, 0
        """;

    // =====================================================================
    // 共用
    // =====================================================================

    private const string Makefile = """
        # 便捷入口：由 MetalForge 生成。
        #
        # 真正的构建由 MetalForge 生成的 CMake 工程完成；
        # 这个 Makefile 只是让"不用 IDE 也能构建"变得简单。

        BUILD_DIR := build/cmake
        TOOLCHAIN := cmake/@@ARCH_TRIPLE@@.toolchain.cmake

        .PHONY: all configure clean run

        all: configure
        	cmake --build $(BUILD_DIR)

        configure:
        	cmake -S . -B $(BUILD_DIR) -DCMAKE_TOOLCHAIN_FILE=$(TOOLCHAIN)

        clean:
        	rm -rf build

        run: all
        	@@QEMU_SYSTEM@@ -m 256 -serial stdio
        """;
}
