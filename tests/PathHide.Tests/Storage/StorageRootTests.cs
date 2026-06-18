using System;
using System.IO;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// Storage-root resolution: <c>PATHHIDE_HOME</c> relocates the whole tree when set, the default
/// <c>~/.pathhide</c> is used when it is not, and a relative override resolves against the home
/// directory (never the working directory) so no path can depend on how the app was launched.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class StorageRootTests : IDisposable
{
    private readonly string? _previousHome;

    public StorageRootTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
    }

    [Fact]
    public void Root_Defaults_To_DotPathhide_When_Override_Unset()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, null);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".pathhide"), StorageRoot.Directory);
    }

    [Fact]
    public void Override_Relocates_The_Whole_Root()
    {
        var target = Path.Combine(Path.GetTempPath(), "pathhide-home-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, target);

        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));
        // The logs subpath is derived from the relocated root.
        Assert.Equal(Path.Combine(StorageRoot.Directory, "logs"), StorageRoot.LogsDirectory);
    }

    [Fact]
    public void Empty_Override_Falls_Back_To_The_Default()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "   ");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".pathhide"), StorageRoot.Directory);
    }

    [Fact]
    public void Relative_Override_Resolves_Against_Home_Not_Working_Directory()
    {
        var relative = "pathhide-relative-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, relative);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, relative)), StorageRoot.Directory);
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative)),
            StorageRoot.Directory);
    }

    [Fact]
    public void Tilde_Alone_Override_Expands_To_Home()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "~");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(StorageRoot.Directory));
    }

    [Fact]
    public void Tilde_Slash_Override_Expands_Against_Home()
    {
        var leaf = "pathhide-tilde-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "~/" + leaf);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, leaf)), StorageRoot.Directory);
    }

    [Fact]
    public void Percent_Environment_Reference_In_Override_Expands()
    {
        // A uniquely-named variable avoids colliding with anything in the real environment; it is
        // restored (cleared) in the finally so the process-wide env stays clean for sibling tests.
        var probeVariable = "PATHHIDE_OVERRIDE_PROBE_" + Guid.NewGuid().ToString("N");
        var previousProbe = Environment.GetEnvironmentVariable(probeVariable);
        var target = Path.Combine(Path.GetTempPath(), "pathhide-percent-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(probeVariable, target);
            // The resolver expands both the Windows %VAR% form (here) and the POSIX $VAR / ${VAR}
            // forms (covered by the test below); an unset reference expands to empty.
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "%" + probeVariable + "%");

            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(probeVariable, previousProbe);
        }
    }

    [Fact]
    public void Dollar_Environment_References_In_Override_Expand()
    {
        var probeVariable = "PATHHIDE_OVERRIDE_PROBE_" + Guid.NewGuid().ToString("N");
        var previousProbe = Environment.GetEnvironmentVariable(probeVariable);
        var target = Path.Combine(Path.GetTempPath(), "pathhide-dollar-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(probeVariable, target);

            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "$" + probeVariable);
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));

            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "${" + probeVariable + "}");
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(StorageRoot.Directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(probeVariable, previousProbe);
        }
    }

    [Fact]
    public void Override_That_Expands_To_Empty_Is_Rejected()
    {
        // A reference to a variable that is definitely unset expands to empty; that is a
        // misconfiguration, reported rather than silently collapsing onto the home directory.
        var unsetVariable = "PATHHIDE_UNSET_PROBE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(unsetVariable, null);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, "$" + unsetVariable);

        Assert.Throws<InvalidOperationException>(() => _ = StorageRoot.Directory);
    }
}
