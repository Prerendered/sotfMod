// purpose: pure formatter for the D5 mod log line format, so the format is unit-testable without a file
// owns:    LogLine.Format, LogLine.FormatException, LogLine.MaxValueLength
// deps:    LogLevel
// caution: quoted values escape \ " LF CR as \\ \" \n \r and other control or line-separator chars as \uXXXX, so user
//          text (labels) cannot forge a field, break a line or drive a terminal; each value is capped at MaxValueLength

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShelfTransfer.Core.Logging;

public static class LogLine
{
    /// <summary>Escaped characters of one value kept before the truncation marker; the quotes are not counted.</summary>
    public const int MaxValueLength = 256;

    private const string _nullLiteral = "null";
    private const string _truncationMarkerStart = "\u2026(+";
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
        if (value is null)
        {
            return _nullLiteral;
        }

        string raw = value switch
        {
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        // Text "null" is quoted so a bare null always means a real null.
        bool isQuoted = raw == _nullLiteral || IsQuotingNeeded(raw);
        string body = EscapeAndTruncate(raw);
        return isQuoted ? "\"" + body + "\"" : body;
    }

    private static bool IsQuotingNeeded(string raw)
    {
        if (raw.Length == 0)
        {
            return true;
        }

        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '=' || c == '"' || c == '\\')
            {
                return true;
            }
        }

        return false;
    }

    // Per-char mapping, so an escape is never re-escaped. An unquoted value holds no escapable char and
    // passes through unchanged. Truncation stops before an escape or surrogate pair that would not fit
    // whole, so the kept prefix always decodes; N counts the UTF-16 chars of the value that were cut.
    private static string EscapeAndTruncate(string raw)
    {
        StringBuilder body = new(Math.Min(raw.Length, MaxValueLength));
        int index = 0;
        while (index < raw.Length)
        {
            bool isPair = IsSurrogatePairAt(raw, index);
            int escapedLength = isPair ? 2 : GetEscapedLength(raw[index]);
            if (body.Length + escapedLength > MaxValueLength)
            {
                break;
            }

            if (isPair)
            {
                body.Append(raw, index, 2);
                index += 2;
            }
            else
            {
                AppendEscaped(body, raw[index]);
                index++;
            }
        }

        if (index < raw.Length)
        {
            body.Append(_truncationMarkerStart)
                .Append((raw.Length - index).ToString(CultureInfo.InvariantCulture))
                .Append(')');
        }

        return body.ToString();
    }

    private static bool IsSurrogatePairAt(string raw, int index) =>
        index + 1 < raw.Length && char.IsHighSurrogate(raw[index]) && char.IsLowSurrogate(raw[index + 1]);

    // U+0085 is already a control char; U+2028/U+2029 are not, but viewers split lines on all three.
    private static bool IsEscapedAsCodePoint(char c) => char.IsControl(c) || c == '\u2028' || c == '\u2029';

    private static int GetEscapedLength(char c) => c switch
    {
        '\\' or '"' or '\n' or '\r' => 2,
        _ when IsEscapedAsCodePoint(c) => 6,
        _ => 1,
    };

    private static void AppendEscaped(StringBuilder body, char c)
    {
        switch (c)
        {
            case '\\':
                body.Append("\\\\");
                break;
            case '"':
                body.Append("\\\"");
                break;
            case '\n':
                body.Append("\\n");
                break;
            case '\r':
                body.Append("\\r");
                break;
            default:
                if (IsEscapedAsCodePoint(c))
                {
                    body.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                }
                else
                {
                    body.Append(c);
                }

                break;
        }
    }
}
