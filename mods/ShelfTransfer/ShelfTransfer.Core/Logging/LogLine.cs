// purpose: pure formatter for the D5 mod log line format, so the format is unit-testable without a file
// owns:    LogLine.Format, LogLine.FormatException
// deps:    LogLevel
// caution: newlines inside a value are written as the two characters \n so one event stays one line;
//          backslashes are not escaped (lines are for grep, not for round-trip parsing)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShelfTransfer.Core.Logging;

public static class LogLine
{
    private const string _timestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string _stackIndent = "    ";
    private const int _levelWidth = 5;

    public static string Format(
        DateTimeOffset timestamp,
        LogLevel level,
        string eventName,
        IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        ValidateName(eventName, nameof(eventName));

        StringBuilder line = new();
        line.Append(timestamp.ToString(_timestampFormat, CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(FormatLevel(level).PadRight(_levelWidth))
            .Append(' ')
            .Append(eventName);

        foreach (KeyValuePair<string, object?> field in fields)
        {
            ValidateName(field.Key, nameof(fields));
            line.Append(' ').Append(field.Key).Append('=').Append(FormatValue(field.Value));
        }

        return line.ToString();
    }

    /// <summary>An <c>error</c> line, then one continuation line per stack frame, joined with '\n'.</summary>
    public static string FormatException(DateTimeOffset timestamp, string eventName, Exception exception)
    {
        ValidateName(eventName, nameof(eventName));

        KeyValuePair<string, object?>[] fields =
        {
            new("event", eventName),
            new("type", exception.GetType().Name),
            new("message", exception.Message),
        };
        StringBuilder text = new(Format(timestamp, LogLevel.Error, "error", fields));

        string[] frames = (exception.StackTrace ?? string.Empty).Split('\n');
        foreach (string frame in frames)
        {
            string trimmed = frame.Trim();
            if (trimmed.Length > 0)
            {
                text.Append('\n').Append(_stackIndent).Append(trimmed);
            }
        }

        return text.ToString();
    }

    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "unknown log level"),
    };

    // Same charset for keys and event names: lowercase module.verb keeps every line greppable.
    private static void ValidateName(string name, string paramName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name must not be blank", paramName);
        }

        foreach (char c in name)
        {
            bool isAllowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.';
            if (!isAllowed)
            {
                throw new ArgumentException($"'{name}' must match [a-z0-9_.]+", paramName);
            }
        }
    }

    private static string FormatValue(object? value)
    {
        string raw = value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        return IsQuotingNeeded(raw) ? Quote(raw) : raw;
    }

    private static bool IsQuotingNeeded(string raw)
    {
        if (raw.Length == 0)
        {
            return true;
        }

        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c) || c == '=' || c == '"')
            {
                return true;
            }
        }

        return false;
    }

    private static string Quote(string raw)
    {
        string escaped = raw
            .Replace("\"", "\\\"")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "\\n");
        return "\"" + escaped + "\"";
    }
}
