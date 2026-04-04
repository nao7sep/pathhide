using System;
using System.IO;

namespace PathHide.Storage;

public static class StorageRoot
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pathhide");

    public static string Directory { get; private set; } = DefaultDirectory;

    public static void Override(string directory)
    {
        Directory = directory;
    }

    public static void EnsureExists()
    {
        System.IO.Directory.CreateDirectory(Directory);
    }
}
