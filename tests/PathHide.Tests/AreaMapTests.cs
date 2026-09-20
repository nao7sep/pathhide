using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace PathHide.Tests;

/// <summary>
/// <c>tests/README.md</c> is the balance judgement the tests-folder-conventions require: PathHide's
/// areas and the tests standing for each. A map nobody checks rots — a renamed or deleted test leaves
/// its area looking covered. This reads the live file (located via <see cref="CallerFilePathAttribute"/>,
/// mirroring <c>VersionConsistencyTests</c>' pattern) and fails, naming what drifted, when the map stops
/// describing the suite.
/// </summary>
public sealed class AreaMapTests
{
    private const string MapFileName = "README.md";

    private static readonly Regex Backticked = new("`([^`]+)`", RegexOptions.Compiled);

    private static string TestsRoot([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/PathHide.Tests/AreaMapTests.cs
        var testsProjectDir = Path.GetDirectoryName(callerPath)!;
        return Path.GetFullPath(Path.Combine(testsProjectDir, ".."));
    }

    /// <summary>
    /// The table rows, as (area name, test paths). The table's lines are the ones starting with a pipe;
    /// the first two are the header and its row of dashes, and the leading and trailing pipes of each
    /// remaining row split into empty outer cells that are dropped.
    /// </summary>
    private static List<(string Area, List<string> Paths)> Rows()
    {
        var lines = File.ReadAllLines(Path.Combine(TestsRoot(), MapFileName))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('|'))
            .Skip(2)
            .ToList();

        var rows = new List<(string, List<string>)>();
        foreach (var line in lines)
        {
            var cells = line.Split('|');
            Assert.True(
                cells.Length == 5,
                $"{MapFileName} table row is not three columns: {line}");

            var area = cells[1].Trim();
            var paths = Backticked.Matches(cells[3]).Select(match => match.Groups[1].Value).ToList();
            rows.Add((area, paths));
        }

        return rows;
    }

    [Fact]
    public void The_map_names_more_than_one_area()
    {
        var rows = Rows();

        Assert.True(
            rows.Count > 1,
            $"tests/{MapFileName} names {rows.Count} area(s); a balance judgement covers more than one.");
    }

    [Fact]
    public void Every_area_names_at_least_one_test()
    {
        var unrepresented = Rows().Where(row => row.Paths.Count == 0).Select(row => row.Area).ToList();

        Assert.True(
            unrepresented.Count == 0,
            $"tests/{MapFileName} leaves these areas with no test standing for them: "
                + string.Join(", ", unrepresented));
    }

    [Fact]
    public void Every_path_the_map_names_exists()
    {
        var testsRoot = TestsRoot();
        var missing = Rows()
            .SelectMany(row => row.Paths)
            .Distinct()
            .Where(path => !File.Exists(Path.Combine(testsRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"tests/{MapFileName} names these paths, which no longer exist: " + string.Join(", ", missing));
    }
}
