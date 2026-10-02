using System;
using System.IO;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class LogRevealTests
{
    [Fact]
    public void SelectTarget_is_the_records_database_when_it_exists()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var records = Path.Combine(temp.Path, "records.sqlite3");
        File.WriteAllText(records, "");

        var target = LogReveal.SelectTarget(records);

        Assert.Equal(LogRevealTargetKind.File, target.Kind);
        Assert.Equal(records, target.Path);
    }

    [Fact]
    public void SelectTarget_is_its_folder_when_there_is_no_database_yet()
    {
        using var temp = new TempDirectory();

        var target = LogReveal.SelectTarget(Path.Combine(temp.Path, "records.sqlite3"));

        Assert.Equal(LogRevealTargetKind.Directory, target.Kind);
        Assert.Equal(temp.Path, target.Path);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pathhide-logreveal-tests",
                NanoId.New());
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
