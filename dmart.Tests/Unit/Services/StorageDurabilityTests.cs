using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Verifies the startup check that warns when data would be written to a
// filesystem that does not survive a reboot.
//
// The case that motivated it is the third one here: a diskless board whose root
// is a tmpfs, with the real storage mounted over /var/lib/dmart. When that mount
// silently fails, the same path is still writable — backed by the tmpfs root —
// and nothing else in the system can tell the difference. Getting longest-prefix
// matching right is therefore the whole point: match "/" instead of the deeper
// mount and every healthy board reports non-durable; match too eagerly and the
// broken one reports fine.
public class StorageDurabilityTests
{
    // A realistic diskless-Alpine mount table: tmpfs root, the boot medium, and
    // the ext4 data partition mounted over /var/lib/dmart.
    private static readonly string[] HealthyBoard =
    [
        "tmpfs / tmpfs rw,relatime,mode=755 0 0",
        "/dev/mmcblk0p1 /media/sdcard vfat ro,noatime 0 0",
        "/dev/mmcblk0p2 /var/lib/dmart ext4 rw,lazytime,noatime 0 0",
        "tmpfs /run tmpfs rw,nosuid,nodev 0 0",
    ];

    // The same board when the data partition failed to mount — the ONLY
    // difference is the missing mmcblk0p2 line.
    private static readonly string[] BrokenBoard =
    [
        "tmpfs / tmpfs rw,relatime,mode=755 0 0",
        "/dev/mmcblk0p1 /media/sdcard vfat ro,noatime 0 0",
        "tmpfs /run tmpfs rw,nosuid,nodev 0 0",
    ];

    [Fact]
    public void Data_Under_A_Mounted_Ext4_Partition_Is_Durable()
    {
        StorageDurability.RamBackedFilesystem("/var/lib/dmart/spaces", HealthyBoard)
            .ShouldBeNull();
    }

    [Fact]
    public void Same_Path_Is_Flagged_When_The_Partition_Did_Not_Mount()
    {
        // Identical path, identical permissions, writable either way. This is
        // the failure the check exists to catch.
        StorageDurability.RamBackedFilesystem("/var/lib/dmart/spaces", BrokenBoard)
            .ShouldBe("tmpfs");
    }

    [Fact]
    public void Deepest_Mount_Point_Wins_Not_The_First_Match()
    {
        // "/" matches every path; the answer must come from the longest match.
        StorageDurability.RamBackedFilesystem("/var/lib/dmart", HealthyBoard).ShouldBeNull();
    }

    [Fact]
    public void Prefix_Matching_Is_Component_Wise()
    {
        // A plain StartsWith would let the "/var" mount claim "/variable-data"
        // and report an unrelated filesystem's durability.
        string[] mounts =
        [
            "/dev/sda1 / ext4 rw 0 0",
            "tmpfs /var tmpfs rw 0 0",
        ];
        StorageDurability.RamBackedFilesystem("/variable-data/spaces", mounts).ShouldBeNull();
        StorageDurability.RamBackedFilesystem("/var/spaces", mounts).ShouldBe("tmpfs");
    }

    [Fact]
    public void Ramfs_Counts_As_Ram_Backed()
    {
        string[] mounts = ["/dev/sda1 / ext4 rw 0 0", "ramfs /mnt/scratch ramfs rw 0 0"];
        StorageDurability.RamBackedFilesystem("/mnt/scratch/spaces", mounts).ShouldBe("ramfs");
    }

    [Fact]
    public void Overlay_Is_Not_Flagged()
    {
        // An overlay's upper layer is usually on disk. Flagging every container
        // would make the warning noise, which is how warnings stop being read.
        string[] mounts = ["overlay / overlay rw 0 0"];
        StorageDurability.RamBackedFilesystem("/data/spaces", mounts).ShouldBeNull();
    }

    [Fact]
    public void Octal_Escapes_In_Mount_Points_Are_Decoded()
    {
        // /proc/mounts writes a space as \040. Failing to decode it would
        // mis-attribute the path to whatever mount matched next.
        string[] mounts =
        [
            "/dev/sda1 / ext4 rw 0 0",
            @"tmpfs /mnt/my\040data tmpfs rw 0 0",
        ];
        StorageDurability.RamBackedFilesystem("/mnt/my data/spaces", mounts).ShouldBe("tmpfs");
    }

    [Fact]
    public void Trailing_Slashes_Do_Not_Defeat_The_Match()
    {
        StorageDurability.RamBackedFilesystem("/var/lib/dmart/", HealthyBoard).ShouldBeNull();
    }

    [Fact]
    public void Malformed_Lines_Are_Skipped_Not_Fatal()
    {
        string[] mounts = ["", "garbage", "tmpfs / tmpfs rw 0 0"];
        StorageDurability.RamBackedFilesystem("/data", mounts).ShouldBe("tmpfs");
    }

    [Fact]
    public void Unknown_Path_Falls_Back_To_The_Root_Mount()
    {
        StorageDurability.RamBackedFilesystem("/nowhere/at/all", BrokenBoard).ShouldBe("tmpfs");
    }

    // ---- which paths are checked at all -----------------------------------

    [Fact]
    public void Empty_SpacesFolder_Is_Not_Checked()
    {
        var s = new DmartSettings { SpacesFolder = "" };
        StorageDurability.DataPaths(s, DatabaseDriver.Postgresql).ShouldBeEmpty();
    }

    [Fact]
    public void Sqlite_Path_Is_Checked_Only_When_Sqlite_Is_The_Driver()
    {
        // SqlitePath has a non-empty default ("dmart.db"), so including it on a
        // PostgreSQL deployment would warn about a file nothing ever writes.
        var s = new DmartSettings { SpacesFolder = "", SqlitePath = "dmart.db" };
        StorageDurability.DataPaths(s, DatabaseDriver.Postgresql).ShouldBeEmpty();
        StorageDurability.DataPaths(s, DatabaseDriver.Sqlite)
            .ShouldContain(p => p.Path == "dmart.db");
    }

    [Fact]
    public void Both_Paths_Reported_When_Both_Apply()
    {
        var s = new DmartSettings { SpacesFolder = "/srv/spaces", SqlitePath = "/srv/dmart.db" };
        var paths = StorageDurability.DataPaths(s, DatabaseDriver.Sqlite).ToList();
        paths.Count.ShouldBe(2);
        paths.ShouldContain(p => p.What == "spaces folder" && p.Path == "/srv/spaces");
        paths.ShouldContain(p => p.What == "sqlite database" && p.Path == "/srv/dmart.db");
    }

    [Fact]
    public void Real_Proc_Mounts_Query_Does_Not_Throw()
    {
        // The public entry point must be safe to call anywhere, including on a
        // platform with no /proc at all — it is advisory, so "cannot tell" has
        // to degrade to null rather than take the process down at startup.
        Should.NotThrow(() => StorageDurability.RamBackedFilesystem("/tmp"));
        Should.NotThrow(() => StorageDurability.RamBackedFilesystem(""));
    }
}
