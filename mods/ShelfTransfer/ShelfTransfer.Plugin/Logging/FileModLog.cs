// purpose: IModLog that appends LogLine output to shelftransfer.log, keeping the previous run as .prev.log (D5)
// owns:    FileModLog
// deps:    ShelfTransfer.Core.Logging (IModLog, LogLine, LogLevel, LogSizeCap), BepInEx ManualLogSource (fallback)
// caution: never throws into the game; any failure goes to the BepInEx log instead. Past LogSizeCap.MaxSessionBytes
//          one log.cap line is written and later events are dropped; the BepInEx fallback is not capped

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
    private readonly UTF8Encoding _encoding = new(encoderShouldEmitUTF8Identifier: false);
    private readonly LogSizeCap _sizeCap = new(LogSizeCap.MaxSessionBytes);
    private readonly StreamWriter? _writer;

    public FileModLog(string directory, ManualLogSource fallback)
    {
        _fallback = fallback;
        string current = Path.Combine(directory, FileName);
        string? rotationError = RotatePrevious(directory, current);

        // A failed rotation appends to last run's file rather than losing file logging for the session.
        _writer = OpenWriter(current, isAppend: rotationError is not null);
        if (rotationError is not null)
        {
            Write(LogLevel.Warn, "log.rotate", new KeyValuePair<string, object?>[]
            {
                new("outcome", "failed"),
                new("error", rotationError),
            });
        }
    }

    public void Write(LogLevel level, string eventName, IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        try
        {
            Append(level, LogLine.Format(DateTimeOffset.Now, level, eventName, fields));
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
            Append(LogLevel.Error, LogLine.FormatException(DateTimeOffset.Now, eventName, exception));
        }
        catch (Exception ex)
        {
            _fallback.LogError($"mod log write failed for '{eventName}': {ex.GetType().Name}: {ex.Message}");
            _fallback.LogError(exception.ToString());
        }
    }

    // Returns the failure description, or null when the directory is ready and any old log was moved aside.
    private static string? RotatePrevious(string directory, string current)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(current))
            {
                File.Move(current, Path.Combine(directory, PreviousFileName), overwrite: true);
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private StreamWriter? OpenWriter(string path, bool isAppend)
    {
        try
        {
            // FileShare.ReadWrite so the log can be opened in an editor while the game runs.
            FileStream stream = new(path, isAppend ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, _encoding);
        }
        catch (Exception ex)
        {
            _fallback.LogError($"mod log unavailable, using BepInEx log only: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Flushed per event so the file is complete even if the game crashes or is killed.
    private void Append(LogLevel level, string text)
    {
        if (_writer is null)
        {
            WriteFallback(level, text);
            return;
        }

        lock (_gate)
        {
            // +1 for the '\n' terminator.
            switch (_sizeCap.Admit(_encoding.GetByteCount(text) + 1L))
            {
                case LogSizeCap.Admission.Write:
                    WriteLine(_writer, text);
                    break;
                case LogSizeCap.Admission.WriteCapNotice:
                    WriteLine(_writer, LogLine.Format(DateTimeOffset.Now, LogLevel.Warn, "log.cap", new KeyValuePair<string, object?>[]
                    {
                        new("limit_bytes", LogSizeCap.MaxSessionBytes),
                        new("written_bytes", _sizeCap.WrittenBytes),
                        new("action", "drop"),
                    }));
                    break;
                case LogSizeCap.Admission.Drop:
                    break;
            }
        }
    }

    private static void WriteLine(StreamWriter writer, string text)
    {
        writer.Write(text);
        writer.Write('\n');
        writer.Flush();
    }

    private void WriteFallback(LogLevel level, string text)
    {
        switch (level)
        {
            case LogLevel.Debug:
                _fallback.LogDebug(text);
                break;
            case LogLevel.Warn:
                _fallback.LogWarning(text);
                break;
            case LogLevel.Error:
                _fallback.LogError(text);
                break;
            default:
                _fallback.LogInfo(text);
                break;
        }
    }
}
