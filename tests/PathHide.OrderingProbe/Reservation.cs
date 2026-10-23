using System;
using System.IO;
using System.Threading;
using PathHide.Services;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PathHide.Tests")]

namespace PathHide.OrderingProbe;

internal static class Reservation
{
    private static string Key(string path)
    {
        PathNormalizer.TryNormalize(Path.GetFullPath(path), out var normalized, out var family);
        return PathNormalizer.IdentityKey(normalized!, family);
    }
    internal static string Name(string path) => AttributeWriteReservation.Name(Key(path));
    internal static void Run(string path, Action write, ApplyParentProcess? parent = null,
        TimeSpan? bound = null, Action? waiting = null)
    {
        if (OperatingSystem.IsWindows())
        {
            AttributeWriteReservation.RunIdentity(Key(path), write, parent, bound, waiting);
            return;
        }
        using var mutex = new Mutex(false, Name(path));
        bool Acquire(TimeSpan timeout)
        {
            try { return mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { return true; }
        }
        AttributeWriteReservation.RunReserved(Acquire, mutex.ReleaseMutex, write, parent, bound, waiting);
    }
}
