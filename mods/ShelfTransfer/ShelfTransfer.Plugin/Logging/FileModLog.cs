// purpose: IModLog that appends LogLine output to shelftransfer.log, keeping the previous run as .prev.log (D5)
// owns:    FileModLog
// deps:    ShelfTransfer.Core.Logging (IModLog, LogLine, LogLevel), BepInEx ManualLogSource (fallback)
// caution: never throws into the game; any failure goes to the BepInEx log instead

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using BepInEx.Logging;

using ShelfTransfer.Core.Logging;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Logging;

public sealed class FileModLog : IModLog
{
    public const string FileName = "shelftransfer.log";
    public const string PreviousFileName = "shelftransfer.prev.log";

    private readonly ManualLogSource _fallback;
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    public FileModLog(string directory, ManualLogSource fallback)
    {
        _fallback = fallback;
        try
        {
            Directory.CreateDirectory(directory);
            string current = Path.Combine(directory, FileName);
            if (File.Exists(current))
            {
                File.Move(current, Path.Combine(directory, PreviousFileName), overwrite: true);
            }

            // FileShare.ReadWrite so the log can be opened in an editor while the game runs.
            FileStream stream = new(current, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            _fallback.LogError($"mod log unavailable, using BepInEx log only: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Write(LogLevel level, string eventName, IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        try
        {
            Append(LogLine.Format(DateTimeOffset.Now, level, eventName, fields));
        }
        catch (Exception ex)
        {
            _fallback.LogWarning($"mod log write failed for '{eventName}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void WriteException(string eventName, Exception exception)
    {
        try
        {
            Append(LogLine.FormatException(DateTimeOffset.Now, eventName, exception));
        }
        catch (Exception ex)
        {
            _fallback.LogError($"mod log write failed for '{eventName}': {ex.GetType().Name}: {ex.Message}");
            _fallback.LogError(exception.ToString());
        }
    }

    // Flushed per event so the file is complete even if the game crashes or is killed.
    private void Append(string text)
    {
        if (_writer is null)
        {
            _fallback.LogInfo(text);
            return;
        }

        lock (_gate)
        {
            _writer.Write(text);
            _writer.Write('\n');
            _writer.Flush();
        }
    }
}
