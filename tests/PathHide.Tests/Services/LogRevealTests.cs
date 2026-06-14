using System;
using System.IO;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class LogRevealTests
{
    [Fact]
    public void SelectTarget_flushes_before_looking_for_the_current_log()
    {
        using var temp = new TempDirectory();
        var logPath = Path.Combine(temp.Path, "20260610-093015-utc.log");

        var target = LogReveal.SelectTarget(temp.Path, () =>
        {
            Directory.CreateDirectory(temp.Path);
            File.WriteAllText(logPath, "{}\n");
        });

        Assert.Equal(LogRevealTargetKind.File, target.Kind);
        Assert.Equal(logPath, target.Path);
    }

    [Fact]
    public void SelectTarget_uses_the_logs_directory_when_no_log_exists()
    {
        using var temp = new TempDirectory();

        var target = LogReveal.SelectTarget(temp.Path, () => { });

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
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
