using System;
using System.IO;
using PathHide.Services;

namespace PathHide.OrderingProbe;

/// <summary>Disposable subprocesses for real reservation/process-lifetime interleavings.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args[0] == "parent")
        {
            Console.WriteLine(ElevatedApplyCommand.Parent.Current);
            _ = Console.ReadLine();
            return 0;
        }

        using var parent = args[3] == "none" ? null : new ApplyParentProcess(new(
            int.Parse(args[3].Split(':')[0], System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(args[3].Split(':')[1], System.Globalization.CultureInfo.InvariantCulture)));
        Console.WriteLine("ready");
        if (args[4] == "before")
            _ = Console.ReadLine();
        try
        {
            AttributeWriteReservation.Run(args[1], () =>
            {
                Console.WriteLine("entered");
                if (args[4] == "inside")
                    _ = Console.ReadLine();
                File.WriteAllText(args[1], args[2]);
            }, parent, TimeSpan.FromSeconds(10), waiting: () => Console.WriteLine("waiting"));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("parent exited");
            return 4;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("busy");
            return 5;
        }
    }
}
