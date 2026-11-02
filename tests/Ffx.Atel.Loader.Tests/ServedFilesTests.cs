// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Loader.Tests;

public sealed class ServedFilesTests : IDisposable {
    private readonly string _dir = Directory.CreateTempSubdirectory("atel-served-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string archivePath, int size) {
        string file = Path.Combine(_dir, archivePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[size]);
    }

    [Theory]
    [InlineData("host0:/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp", "ffx_ps2/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp")]
    [InlineData("host0:ffx/master/jppc/event/obj/sc/scene1/scene1.ebp", "ffx_ps2/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp")]
    [InlineData("cdrom0:\\FFX\\Master\\JPPC\\battle\\mon\\_m209\\m209.bin", "ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin")]
    [InlineData("HOST0://ffx/master/x.ebp", "ffx_ps2/ffx/master/x.ebp")]
    [InlineData("host0:", null)]
    [InlineData("host0:/", null)]
    [InlineData("/ffx/master/x.ebp", null)]
    [InlineData("mc0:/BISLPS-25088", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MapsGamePathsToArchivePaths(string? gamePath, string? expected) {
        Assert.Equal(expected, ServedFiles.ArchivePathOf(gamePath));
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 16u)]
    [InlineData(15, 16u)]
    [InlineData(16, 16u)]
    [InlineData(17, 32u)]
    [InlineData(83_648, 83_648u)]
    [InlineData(55_857, 55_872u)]
    [InlineData(int.MaxValue - 15, (uint)int.MaxValue - 15)]
    public void RoundsSizesUpTo16(long size, uint expected) {
        Assert.Equal(expected, ServedFiles.RoundUp(size));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(1L << 32)]
    public void RejectsSizesTheGameCannotHold(long size) {
        Assert.Null(ServedFiles.RoundUp(size));
    }

    [Fact]
    public void ServesTheFilesInItsFolderWithTheirRoundedSize() {
        Write("ffx_ps2/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp", 83_650);
        Write("ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin", 55_856);

        ServedFiles served = ServedFiles.Scan(_dir);

        Assert.Equal(2, served.Count);
        Assert.Equal(83_664u, served.SizeOf("host0:/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp"));
        Assert.Equal(55_856u, served.SizeOf("host0:/FFX/master/jppc/battle/mon/_m209/m209.bin"));
    }

    [Fact]
    public void LeavesOtherFilesToTheGame() {
        Write("ffx_ps2/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp", 100);

        ServedFiles served = ServedFiles.Scan(_dir);

        Assert.Null(served.SizeOf("host0:/ffx/master/jppc/event/obj/sc/scene2/scene2.ebp"));
        Assert.Null(served.SizeOf("host0:/ffx/master/jppc/event/obj/sc/scene1"));
        Assert.Null(served.SizeOf("mc0:/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp"));
        Assert.Null(served.SizeOf(null));
    }

    [Fact]
    public void AnEmptyOrMissingFolderServesNothing() {
        Assert.Equal(0, ServedFiles.Scan(_dir).Count);
        Assert.Equal(0, ServedFiles.Scan(Path.Combine(_dir, "missing")).Count);
        Assert.Null(ServedFiles.Empty.SizeOf("host0:/ffx/master/x.ebp"));
    }

    private static string? Table(int n) => n == 1000 ? "host0:/ffx/master/jppc/event/obj/ka/kami0000/kami0000.ebp" : null;
    private static string? Scene(int map) => map is >= 393 and <= 399 ? $"host0:/ffx/master/jppc/event/obj/sc/scene{map - 392}/scene{map - 392}.ebp" : null;
    private static string? Mgrp(int map) => map is >= 393 and <= 399 ? $"host0:/ffx/master/jppc/event/obj/sc/scene{map - 392}/scene{map - 392}00.mgrp" : null;

    [Theory]
    [InlineData(1000, "host0:/ffx/master/jppc/event/obj/ka/kami0000/kami0000.ebp")] // from the table
    [InlineData(393 * 18, "host0:/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp")] // empty entry: built-in .ebp
    [InlineData(399 * 18 + 3, "host0:/ffx/master/jppc/event/obj/sc/scene7/scene700.mgrp")] // and .mgrp
    [InlineData(393 * 18 + 1, null)] // other files of those maps have no built-in name
    [InlineData(392 * 18, null)]
    [InlineData(400 * 18 + 3, null)]
    public void NamesFilesAsCdQueueReadDoes(int number, string? expected) {
        Assert.Equal(expected, ServedFiles.GamePathOf(number, Table, Scene, Mgrp));
    }

    [Fact]
    public void TheTableNameWinsOverTheBuiltInOne() {
        Assert.Equal("host0:/table", ServedFiles.GamePathOf(393 * 18, _ => "host0:/table", Scene, Mgrp));
    }

    [Fact]
    public void ServesAGrownEternalCalmScript() {
        Write("ffx_ps2/ffx/master/jppc/event/obj/sc/scene1/scene1.ebp", 70_001);
        ServedFiles served = ServedFiles.Scan(_dir);

        Assert.Equal(70_016u, served.SizeOf(ServedFiles.GamePathOf(393 * 18, _ => null, Scene, Mgrp)));
        Assert.Null(served.SizeOf(ServedFiles.GamePathOf(394 * 18, _ => null, Scene, Mgrp)));
    }
}
