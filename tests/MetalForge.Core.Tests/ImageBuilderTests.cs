using System.Buffers.Binary;
using System.Text;
using MetalForge.Core.Build.Images;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Tests;

/// <summary>
/// ISO9660 + El Torito 与 FAT 映像生成的测试。
///
/// 镜像生成的特点是"写错了也不报错"：生成的文件看起来很正常，
/// 问题要等到挂载或固件引导时才暴露，而且现象是"启动不了"这种无法定位的描述。
/// 因此这里全部采用**读回校验**：把生成的字节按规范解析一遍，
/// 确认关键字段真的落在正确的偏移上。
///
/// 每个断言都对应一条规范要求，注释里写明依据。
/// </summary>
public sealed class ImageBuilderTests : IDisposable
{
    private const int SectorSize = 2048;

    private readonly string _root;

    public ImageBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-image-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string CreateBinaryFile(string name, int size)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        for (var index = 0; index < size; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly DateTimeOffset _timestamp = new(2026, 2, 14, 12, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------
    // ISO9660 基础结构
    // -----------------------------------------------------------------

    [Fact]
    public void Iso_StartsWithSixteenEmptySystemSectors()
    {
        // ISO9660 规定前 16 个扇区保留给系统使用（El Torito 之外的引导方式会用它们）。
        var kernel = CreateFile("kernel.elf", "fake kernel");
        var output = Path.Combine(_root, "out.iso");

        var result = IsoImageBuilder.Build(output, "TEST", [new IsoEntry(kernel, "/KERNEL.ELF")], [], _timestamp);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
        var image = File.ReadAllBytes(output);

        Assert.True(image.Length >= 17 * SectorSize);
        Assert.All(image.Take(16 * SectorSize), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Iso_PrimaryVolumeDescriptorHasCorrectIdentifierAndGeometry()
    {
        var kernel = CreateFile("kernel.elf", "fake kernel");
        var output = Path.Combine(_root, "out.iso");
        IsoImageBuilder.Build(output, "METALFORGE", [new IsoEntry(kernel, "/KERNEL.ELF")], [], _timestamp);

        var image = File.ReadAllBytes(output);
        var pvd = image.AsSpan(16 * SectorSize, SectorSize);

        // 类型 1 = 主卷描述符；标识符 "CD001"；版本 1。
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(pvd[1..6]));
        Assert.Equal(1, pvd[6]);

        // 卷标识符是 32 字节空白填充的 ASCII。
        Assert.Equal("METALFORGE", Encoding.ASCII.GetString(pvd[40..72]).TrimEnd());

        // 逻辑块大小必须正好是 2048（both-endian）。
        Assert.Equal(SectorSize, BinaryPrimitives.ReadUInt16LittleEndian(pvd[128..]));
        Assert.Equal(SectorSize, BinaryPrimitives.ReadUInt16BigEndian(pvd[130..]));

        // 卷空间大小 = 整个镜像的扇区数（both-endian）。
        var declaredSectors = BinaryPrimitives.ReadUInt32LittleEndian(pvd[80..]);
        Assert.Equal(image.Length / SectorSize, (int)declaredSectors);
        Assert.Equal(declaredSectors, BinaryPrimitives.ReadUInt32BigEndian(pvd[84..]));
    }

    [Fact]
    public void Iso_VolumeDescriptorSetEndsWithTerminator()
    {
        var kernel = CreateFile("kernel.elf", "x");
        var output = Path.Combine(_root, "out.iso");
        IsoImageBuilder.Build(output, "TEST", [new IsoEntry(kernel, "/KERNEL.ELF")], [], _timestamp);

        var image = File.ReadAllBytes(output);
        var terminator = image.AsSpan(18 * SectorSize, SectorSize);

        // 类型 255 = 卷描述符集终止符。缺少它时部分挂载工具会继续读到垃圾数据。
        Assert.Equal(255, terminator[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(terminator[1..6]));
    }

    [Fact]
    public void Iso_RootDirectoryContainsDotDotAndFiles()
    {
        var kernel = CreateFile("kernel.elf", "kernel contents here");
        var config = CreateFile("grub.cfg", "set timeout=0");
        var output = Path.Combine(_root, "out.iso");

        IsoImageBuilder.Build(
            output,
            "TEST",
            [new IsoEntry(kernel, "/KERNEL.ELF"), new IsoEntry(config, "/GRUB.CFG")],
            [],
            _timestamp);

        var image = File.ReadAllBytes(output);
        var names = ReadRootDirectoryNames(image);

        // 每个目录都必须以 "." 与 ".." 开头，否则部分工具不认为它是目录。
        Assert.Contains(".", names);
        Assert.Contains("..", names);

        // 目录里必须真的列出文件 —— 只写 "." 与 ".." 时镜像挂载后是空盘。
        Assert.Contains("KERNEL.ELF", names);
        Assert.Contains("GRUB.CFG", names);
    }

    [Fact]
    public void Iso_FileContentIsReadableThroughDirectoryRecord()
    {
        // 最关键的一条：目录记录里的扇区号与长度必须真的指向文件内容。
        const string content = "MetalForge kernel image placeholder";
        var kernel = CreateFile("kernel.elf", content);
        var output = Path.Combine(_root, "out.iso");

        IsoImageBuilder.Build(output, "TEST", [new IsoEntry(kernel, "/KERNEL.ELF")], [], _timestamp);

        var image = File.ReadAllBytes(output);
        var record = FindRootDirectoryRecord(image, "KERNEL.ELF");

        Assert.NotNull(record);
        var sector = (int)BinaryPrimitives.ReadUInt32LittleEndian(record!.Value.Span[2..]);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.Value.Span[10..]);

        Assert.Equal(content.Length, length);

        var stored = Encoding.ASCII.GetString(image, sector * SectorSize, length);
        Assert.Equal(content, stored);

        // 长度字段是 both-endian 的：两半不一致说明写入时漏了一半。
        Assert.Equal(
            BinaryPrimitives.ReadUInt32LittleEndian(record.Value.Span[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(record.Value.Span[6..]));
    }

    [Fact]
    public void Iso_IsReproducible()
    {
        // 显式传入时间戳的意义：同样的输入必须得到逐字节相同的镜像。
        // 否则每次构建的产物都不同，缓存与校验和比较全部失效。
        var kernel = CreateFile("kernel.elf", "kernel");
        var first = Path.Combine(_root, "a.iso");
        var second = Path.Combine(_root, "b.iso");

        IsoImageBuilder.Build(first, "TEST", [new IsoEntry(kernel, "/KERNEL.ELF")], [], _timestamp);
        IsoImageBuilder.Build(second, "TEST", [new IsoEntry(kernel, "/KERNEL.ELF")], [], _timestamp);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Iso_NormalizesNamesToIso9660Rules()
    {
        // ISO9660 level 1 的字符集极窄。不规范化时，小写名与长名会让
        // 部分固件无法列出目录。
        var file = CreateFile("My-Long-Kernel-Name.elf", "x");
        var output = Path.Combine(_root, "out.iso");

        IsoImageBuilder.Build(output, "test volume", [new IsoEntry(file, "/My Kernel File.elf")], [], _timestamp);

        var image = File.ReadAllBytes(output);
        var names = ReadRootDirectoryNames(image);

        // 8.3 截断 + 大写 + 非法字符转下划线。
        Assert.Contains("MYKERNEL.ELF", names);

        // 卷标也应当是大写。
        var pvd = image.AsSpan(16 * SectorSize, SectorSize);
        Assert.Equal("TEST VOLUME", Encoding.ASCII.GetString(pvd[40..72]).TrimEnd());
    }

    [Fact]
    public void Iso_MissingSourceFileIsReportedNotThrown()
    {
        var output = Path.Combine(_root, "out.iso");

        var result = IsoImageBuilder.Build(
            output,
            "TEST",
            [new IsoEntry(Path.Combine(_root, "nope.elf"), "/NOPE.ELF")],
            [],
            _timestamp);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "MFISO001");
        Assert.False(File.Exists(output));
    }

    // -----------------------------------------------------------------
    // El Torito
    // -----------------------------------------------------------------

    [Fact]
    public void Iso_ElToritoBootRecordPointsAtCatalog()
    {
        var bootImage = CreateBinaryFile("efi.img", 4096);
        var output = Path.Combine(_root, "boot.iso");

        IsoImageBuilder.Build(
            output,
            "TEST",
            [new IsoEntry(bootImage, "/EFIBOOT.IMG")],
            [new ElToritoEntry(ElToritoPlatform.Efi, "/EFIBOOT.IMG")],
            _timestamp);

        var image = File.ReadAllBytes(output);

        // 卷描述符 17 必须是引导记录，且标识符为 "EL TORITO SPECIFICATION"。
        var bootRecord = image.AsSpan(17 * SectorSize, SectorSize);
        Assert.Equal(0, bootRecord[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(bootRecord[1..6]));
        Assert.StartsWith("EL TORITO SPECIFICATION", Encoding.ASCII.GetString(bootRecord[7..39]), StringComparison.Ordinal);

        // 0x47 处是引导目录的扇区号（小端 32 位）。
        var catalogSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(bootRecord[0x47..]);
        Assert.True(catalogSector > 0 && catalogSector < image.Length / SectorSize);

        // 引导目录的前 32 字节是校验条目：类型 1，且校验和必须为 0。
        var catalog = image.AsSpan(catalogSector * SectorSize, SectorSize);
        Assert.Equal(1, catalog[0]);

        var sum = 0u;
        for (var index = 0; index < 32; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(catalog[index..]);
        }

        // El Torito 规定：校验条目的 16 位字之和必须为 0。
        // 校验和不正确时固件会直接忽略整张盘的引导能力 —— 这是最容易漏掉的一步。
        Assert.Equal(0u, sum & 0xFFFF);

        Assert.Equal(0x55, catalog[30]);
        Assert.Equal(0xAA, catalog[31]);
    }

    [Fact]
    public void Iso_ElToritoEntryPointsAtTheBootImage()
    {
        var bootImage = CreateBinaryFile("efi.img", 3000);
        var output = Path.Combine(_root, "boot.iso");

        IsoImageBuilder.Build(
            output,
            "TEST",
            [new IsoEntry(bootImage, "/EFIBOOT.IMG")],
            [new ElToritoEntry(ElToritoPlatform.Efi, "/EFIBOOT.IMG")],
            _timestamp);

        var image = File.ReadAllBytes(output);
        var catalogSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan((17 * SectorSize) + 0x47, 4));

        var entry = image.AsSpan((catalogSector * SectorSize) + 32, 32);

        // 0x88 = 可引导的默认条目。
        Assert.Equal(0x88, entry[0]);

        // 不仿真（值为 0）：EFI 场景下固件直接把映像当 FAT 卷挂载。
        Assert.Equal(0, entry[1]);

        var sectorCount = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
        Assert.Equal(2, sectorCount);

        // 扇区数也要写成 both-endian。
        Assert.Equal(sectorCount, BinaryPrimitives.ReadUInt16BigEndian(entry[8..]));

        // 引导映像的扇区号必须真的指向该文件的目录记录。
        var record = FindRootDirectoryRecord(image, "EFIBOOT.IMG");
        Assert.NotNull(record);
        Assert.Equal(
            BinaryPrimitives.ReadUInt32LittleEndian(record!.Value.Span[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[10..]));
    }

    [Fact]
    public void Iso_ElToritoImageMustBeInFileList()
    {
        // 引导条目只是"指向"一个文件；忘了把该文件加进镜像时，
        // 生成的 ISO 会有一个指向空扇区的条目。
        var kernel = CreateFile("kernel.elf", "x");
        var output = Path.Combine(_root, "boot.iso");

        var result = IsoImageBuilder.Build(
            output,
            "TEST",
            [new IsoEntry(kernel, "/KERNEL.ELF")],
            [new ElToritoEntry(ElToritoPlatform.Efi, "/EFIBOOT.IMG")],
            _timestamp);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "MFISO003");
    }

    [Fact]
    public void Iso_SupportsHybridBiosAndUefiEntries()
    {
        // "混合 ISO"：同一张盘既能 BIOS 启动也能 UEFI 启动，这是发行版的常规做法。
        var biosImage = CreateBinaryFile("eltorito.img", 2048);
        var efiImage = CreateBinaryFile("efi.img", 4096);
        var output = Path.Combine(_root, "hybrid.iso");

        IsoImageBuilder.Build(
            output,
            "TEST",
            [new IsoEntry(biosImage, "/ELTORITO.IMG"), new IsoEntry(efiImage, "/EFIBOOT.IMG")],
            [
                new ElToritoEntry(ElToritoPlatform.X86, "/ELTORITO.IMG"),
                new ElToritoEntry(ElToritoPlatform.Efi, "/EFIBOOT.IMG"),
            ],
            _timestamp);

        var image = File.ReadAllBytes(output);
        var catalogSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan((17 * SectorSize) + 0x47, 4));

        var first = image.AsSpan((catalogSector * SectorSize) + 32, 32);
        var second = image.AsSpan((catalogSector * SectorSize) + 64, 32);

        // 只有第一条是默认条目（0x88）；第二条为普通条目（0x00）。
        Assert.Equal(0x88, first[0]);
        Assert.Equal(0x00, second[0]);

        // 两条的扇区号不同：指向不同的引导映像。
        Assert.NotEqual(
            BinaryPrimitives.ReadUInt32LittleEndian(first[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(second[10..]));
    }

    // -----------------------------------------------------------------
    // FAT 映像（UEFI 的 ESP）
    // -----------------------------------------------------------------

    [Fact]
    public void Fat_BootSectorHasValidBpb()
    {
        var efi = CreateBinaryFile("bootx64.efi", 8192);
        var output = Path.Combine(_root, "efi.img");

        var result = FatImageBuilder.Build(
            output,
            "METALFORGE",
            [new FatEntry(efi, "EFI/BOOT/BOOTX64.EFI")],
            0,
            _timestamp);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));

        var image = File.ReadAllBytes(output);
        var boot = image.AsSpan(0, 512);

        // 跳转指令 + "MSDOS5.0" OEM 名：固件与挂载工具都靠它判断这是 FAT 卷。
        Assert.Equal(0xEB, boot[0]);
        Assert.Equal("MSDOS5.0", Encoding.ASCII.GetString(boot[3..11]));

        // 512 字节扇区 + 1 扇区每簇 + 2 份 FAT：FAT12 软盘映像的经典取值。
        Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]));
        Assert.Equal(1, boot[13]);
        Assert.Equal(2, boot[16]);

        Assert.Equal(0xF0, boot[21]);

        // 卷标与文件系统类型字符串。
        Assert.Equal("METALFORGE", Encoding.ASCII.GetString(boot[43..54]).TrimEnd());
        Assert.Equal("FAT12", Encoding.ASCII.GetString(boot[54..59]));

        // 引导扇区签名：缺少 0x55AA 时部分固件直接判定为非引导卷。
        Assert.Equal(0x55, boot[510]);
        Assert.Equal(0xAA, boot[511]);
    }

    [Fact]
    public void Fat_SecondFatIsIdenticalToFirst()
    {
        // 两份 FAT 不一致时固件会把卷判定为损坏。
        var efi = CreateBinaryFile("bootx64.efi", 3000);
        var output = Path.Combine(_root, "efi.img");

        FatImageBuilder.Build(output, "TEST", [new FatEntry(efi, "EFI/BOOT/BOOTX64.EFI")], 0, _timestamp);

        var image = File.ReadAllBytes(output);
        var fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22, 2));

        var first = image.AsSpan(512, fatSectors * 512);
        var second = image.AsSpan(512 + (fatSectors * 512), fatSectors * 512);

        Assert.True(first.SequenceEqual(second));
    }

    [Fact]
    public void Fat_DirectoryStructureIsWalkable()
    {
        // 这是 UEFI 引导的关键路径：固件要能按 EFI/BOOT/BOOTX64.EFI 找到文件。
        const int efiSize = 5000;
        var efi = CreateBinaryFile("bootx64.efi", efiSize);
        var output = Path.Combine(_root, "efi.img");

        FatImageBuilder.Build(output, "TEST", [new FatEntry(efi, "EFI/BOOT/BOOTX64.EFI")], 0, _timestamp);

        var image = File.ReadAllBytes(output);

        // 根目录里应当有 EFI 目录项（属性 0x10）。
        var efiEntry = FindFatDirectoryEntry(image, rootDirectoryOffset: null, "EFI");
        Assert.NotNull(efiEntry);
        Assert.Equal(0x10, efiEntry!.Value.Attributes);

        // EFI/ 里应当有 BOOT 目录项。
        var bootEntry = FindFatDirectoryEntry(image, efiEntry.Value.Cluster, "BOOT");
        Assert.NotNull(bootEntry);
        Assert.Equal(0x10, bootEntry!.Value.Attributes);

        // BOOT/ 里应当有 BOOTX64.EFI，且大小与源文件一致。
        var fileEntry = FindFatDirectoryEntry(image, bootEntry.Value.Cluster, "BOOTX64.EFI");
        Assert.NotNull(fileEntry);
        Assert.Equal(efiSize, fileEntry!.Value.Size);
        Assert.NotEqual(0, fileEntry.Value.Cluster);

        // 沿着簇号读回内容必须与源文件一致。
        var dataStart = ComputeFatDataStart(image);
        var offset = (dataStart + ((fileEntry.Value.Cluster - 2) * 512)) * 1;
        var stored = image.AsSpan(offset, efiSize);
        var original = File.ReadAllBytes(efi);

        Assert.True(stored.SequenceEqual(original));
    }

    [Fact]
    public void Fat_SubdirectoryStartsWithDotEntries()
    {
        var efi = CreateBinaryFile("bootx64.efi", 1024);
        var output = Path.Combine(_root, "efi.img");

        FatImageBuilder.Build(output, "TEST", [new FatEntry(efi, "EFI/BOOT/BOOTX64.EFI")], 0, _timestamp);

        var image = File.ReadAllBytes(output);
        var efiEntry = FindFatDirectoryEntry(image, null, "EFI")!.Value;
        var directoryOffset = ComputeFatDataStart(image) + ((efiEntry.Cluster - 2) * 512);

        // "." 与 ".." 是目录的前两项，缺少时部分实现无法正确遍历。
        Assert.Equal(".", ReadFatEntryName(image, directoryOffset));

        // 第二项紧跟在第一项之后，每个目录项固定 32 字节。
        // 先确认这一项确实是目录项（首字节不是 0x00/0xE5），否则下面读名字得到的是垃圾。
        Assert.NotEqual(0x00, image[directoryOffset + 32]);
        Assert.NotEqual(0xE5, image[directoryOffset + 32]);
        Assert.Equal(0x10, image[directoryOffset + 32 + 11]);

        Assert.Equal("..", ReadFatEntryName(image, directoryOffset + 32));
    }

    [Fact]
    public void Fat_IsReproducible()
    {
        var efi = CreateBinaryFile("bootx64.efi", 2048);
        var first = Path.Combine(_root, "a.img");
        var second = Path.Combine(_root, "b.img");

        FatImageBuilder.Build(first, "TEST", [new FatEntry(efi, "EFI/BOOT/BOOTX64.EFI")], 0, _timestamp);
        FatImageBuilder.Build(second, "TEST", [new FatEntry(efi, "EFI/BOOT/BOOTX64.EFI")], 0, _timestamp);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void Fat_RejectsLongFileNames()
    {
        // UEFI 只认 8.3 的约定路径。静默截断会让固件找不到引导文件，
        // 而现象只是"启动不了"，因此必须显式拒绝。
        var efi = CreateBinaryFile("bootx64.efi", 512);
        var output = Path.Combine(_root, "efi.img");

        var result = FatImageBuilder.Build(
            output,
            "TEST",
            [new FatEntry(efi, "EFI/BOOT/BootX64Application.efi")],
            0,
            _timestamp);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "MFFAT004");
    }

    [Fact]
    public void Fat_ReportsInsufficientCapacity()
    {
        var big = CreateBinaryFile("kernel.efi", 3 * 1024 * 1024);
        var output = Path.Combine(_root, "efi.img");

        var result = FatImageBuilder.Build(
            output,
            "TEST",
            [new FatEntry(big, "EFI/BOOT/BOOTX64.EFI")],
            capacityBytes: 1024 * 1024,
            _timestamp);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code is "MFFAT007" or "MFFAT010");
    }

    [Fact]
    public void Fat_AutoCapacityFitsContent()
    {
        var efi = CreateBinaryFile("bootx64.efi", 400 * 1024);
        var output = Path.Combine(_root, "efi.img");

        var result = FatImageBuilder.Build(output, "TEST", [new FatEntry(efi, "EFI/BOOT/BOOTX64.EFI")], 0, _timestamp);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
        Assert.True(result.SizeBytes >= 1440 * 1024);
    }

    [Fact]
    public void Fat_MissingSourceFileIsReported()
    {
        var output = Path.Combine(_root, "efi.img");

        var result = FatImageBuilder.Build(
            output,
            "TEST",
            [new FatEntry(Path.Combine(_root, "nope.efi"), "EFI/BOOT/BOOTX64.EFI")],
            0,
            _timestamp);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "MFFAT001");
    }

    // -----------------------------------------------------------------
    // 名称规范化
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("bootx64.efi", true)]
    [InlineData("BOOTX64.EFI", true)]
    [InlineData("EFI", true)]
    [InlineData("BootX64Application.efi", false)]
    [InlineData("boot x64.efi", false)]
    [InlineData("boot.x64.efi", false)]
    [InlineData("", false)]
    public void Fat_ShortNameValidationFollowsSpec(string name, bool expected)
        => Assert.Equal(expected, FatImageBuilder.IsValidShortName(name));

    [Theory]
    [InlineData("kernel.elf", "/KERNEL.ELF")]
    [InlineData("/boot/grub/grub.cfg", "/BOOT/GRUB/GRUB.CFG")]
    [InlineData("\\boot\\kernel.elf", "/BOOT/KERNEL.ELF")]
    [InlineData("my-long-kernel-name.elf", "/MYLONGKE.ELF")]
    public void Iso_PathNormalization(string input, string expected)
        => Assert.Equal(expected, IsoPathOf(input));

    /// <summary>通过公开 API 间接验证路径规范化（NormalizeIsoPath 是内部实现细节）。</summary>
    private static string IsoPathOf(string input)
    {
        // 反射调用内部方法，避免为了测试把它变成公开 API。
        var method = typeof(IsoImageBuilder).GetMethod(
            "NormalizeIsoPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return (string)method!.Invoke(null, [input])!;
    }

    // -----------------------------------------------------------------
    // 读回辅助
    // -----------------------------------------------------------------

    private static List<string> ReadRootDirectoryNames(byte[] image)
    {
        var names = new List<string>();
        var directory = image.AsSpan(20 * SectorSize, SectorSize);
        var offset = 0;

        while (offset < directory.Length && directory[offset] != 0)
        {
            var length = directory[offset];
            if (length == 0)
            {
                break;
            }

            var identifierLength = directory[offset + 32];
            var identifier = directory.Slice(offset + 33, identifierLength);

            names.Add(identifierLength == 1 && identifier[0] == 0
                ? "."
                : identifierLength == 1 && identifier[0] == 1
                    ? ".."
                    : Encoding.ASCII.GetString(identifier));

            offset += length;
        }

        return names;
    }

    private static ReadOnlyMemory<byte>? FindRootDirectoryRecord(byte[] image, string name)
    {
        var directory = image.AsMemory(20 * SectorSize, SectorSize);
        var span = directory.Span;
        var offset = 0;

        while (offset < span.Length && span[offset] != 0)
        {
            var length = span[offset];
            if (length == 0)
            {
                break;
            }

            var identifierLength = span[offset + 32];
            if (identifierLength > 1)
            {
                var identifier = Encoding.ASCII.GetString(span.Slice(offset + 33, identifierLength));
                if (string.Equals(identifier, name, StringComparison.Ordinal))
                {
                    return directory.Slice(offset, length);
                }
            }

            offset += length;
        }

        return null;
    }

    private static (byte Attributes, int Cluster, int Size)? FindFatDirectoryEntry(
        byte[] image,
        int? rootDirectoryOffset,
        string name)
    {
        var fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22, 2));
        var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17, 2));
        var rootStart = (1 + (2 * fatSectors)) * 512;

        var directoryOffset = rootDirectoryOffset is { } startCluster
            ? ComputeFatDataStart(image) + ((startCluster - 2) * 512)
            : rootStart;

        var maximumEntries = rootDirectoryOffset is null ? rootEntries : 16;

        for (var index = 0; index < maximumEntries; index++)
        {
            var offset = directoryOffset + (index * 32);
            var firstByte = image[offset];

            // 0x00 = 该项及其后全部为空；0xE5 = 已删除。
            if (firstByte == 0x00)
            {
                break;
            }

            if (firstByte == 0xE5)
            {
                continue;
            }

            var entryName = ReadFatEntryName(image, offset);
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                var attributes = image[offset + 11];
                var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 26, 2));
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 28, 4));
                return (attributes, firstCluster, size);
            }
        }

        return null;
    }

    private static string ReadFatEntryName(byte[] image, int offset)
    {
        var stem = Encoding.ASCII.GetString(image, offset, 8).TrimEnd();
        var extension = Encoding.ASCII.GetString(image, offset + 8, 3).TrimEnd();
        return extension.Length == 0 ? stem : stem + "." + extension;
    }

    private static int ComputeFatDataStart(byte[] image)
    {
        var fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22, 2));
        var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(17, 2));
        var rootSectors = ((rootEntries * 32) + 511) / 512;
        return (1 + (2 * fatSectors) + rootSectors) * 512;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
