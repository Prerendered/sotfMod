// purpose: structured event logging Core can call without knowing about files or BepInEx (D5)
// owns:    IModLog
// deps:    LogLevel
// caution: one event per call; fields become key=value pairs, so keys contain no spaces or '='

using System;
using System.Collections.Generic;

namespace ShelfTransfer.Core.Logging;

public interface IModLog
{
    void Write(LogLevel level, string eventName, IReadOnlyList<KeyValuePair<string, object?>> fields);

    void WriteException(string eventName, Exception exception);
}
