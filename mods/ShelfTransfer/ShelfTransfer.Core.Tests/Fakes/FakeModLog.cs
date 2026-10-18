// purpose: IModLog that records events so tests can assert log lines without a file
// owns:    FakeModLog, LogEntry
// deps:    IModLog

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Core.Tests.Fakes;

public sealed record LogEntry(LogLevel Level, string Event, IReadOnlyDictionary<string, object?> Fields)
{
    /// <summary>Field value rendered as text, so enums and ints compare the way the log shows them.</summary>
    public string Text(string key) => Fields[key]?.ToString() ?? "null";
}

public sealed class FakeModLog : IModLog
{
    public List<LogEntry> Entries { get; } = new();

    public List<(string Event, Exception Exception)> Exceptions { get; } = new();

    public void Write(LogLevel level, string eventName, IReadOnlyList<KeyValuePair<string, object?>> fields) =>
        Entries.Add(new LogEntry(level, eventName, fields.ToDictionary(f => f.Key, f => f.Value)));

    public void WriteException(string eventName, Exception exception) => Exceptions.Add((eventName, exception));

    public IEnumerable<LogEntry> Named(string eventName) => Entries.Where(e => e.Event == eventName);
}
