using System;

namespace PathHide.Services;

/// <summary>
/// Where a session's log entries are written: the records database in the app, the results file the
/// elevated child hands back, or the console. <see cref="Write"/> throws when the entry did not land,
/// so the logger can fall back.
/// </summary>
public interface ILogSink : IDisposable
{
    void Write(LogEntry entry);
}

/// <summary>A sink that writes each entry as one JSON line to a text writer, such as the console.</summary>
public sealed class TextWriterLogSink(System.IO.TextWriter writer, bool leaveOpen) : ILogSink
{
    public void Write(LogEntry entry)
    {
        writer.WriteLine(entry.ToLine());
        writer.Flush();
    }

    public void Dispose()
    {
        if (!leaveOpen)
            writer.Dispose();
    }
}
