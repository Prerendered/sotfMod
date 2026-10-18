// purpose: pure formatter for the D5 mod log line format, so the format is unit-testable without a file
// owns:    LogLine.Format, LogLine.FormatException, LogLine.MaxValueLength, LogLine.MaxFrameLength
// deps:    LogLevel
// caution: values and stack frames escape \ " LF CR as \\ \" \n \r and control, line-separator, Format-category and
//          lone-surrogate chars as \uXXXX, so user text (labels) cannot forge a field or line, drive a terminal,
//          reorder text in a viewer or decay to U+FFFD; values are capped at MaxValueLength, frames at MaxFrameLength

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShelfTransfer.Core.Logging;

public static class LogLine
{
    /// <summary>Escaped characters of one value kept before the truncation marker; the quotes are not counted.</summary>
    public const int MaxValueLength = 256;

    /// <summary>Escaped characters of one stack frame kept before the truncation marker; the indent is not counted.</summary>
    public const int MaxFrameLength = 512;

    private const string _nullLiteral = "null";
    private const string _truncationMarkerStart = "\u2026(+";
    private const string _timestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
    private const string _stackIndent = "    ";
    private const int _levelWidth = 5;
    private const int _codePointEscapeLength = 6;

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

        // Frames are never quoted: the 4-space indent marks a continuation (event lines start with a digit),
        // and escaping keeps every frame on its own line.
        string[] frames = (exception.StackTrace ?? string.Empty).Split('\n');
        foreach (string frame in frames)
        {
            string trimmed = frame.Trim();
            if (trimmed.Length > 0)
            {
                text.Append('\n').Append(_stackIndent).Append(EscapeAndTruncate(trimmed, MaxFrameLength));
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
        string body = EscapeAndTruncate(raw, MaxValueLength);
        return isQuoted ? "\"" + body + "\"" : body;
    }

    // Any char that escapes forces quoting, so an unquoted value passes through unchanged.
    private static bool IsQuotingNeeded(string raw)
    {
        if (raw.Length == 0)
        {
            return true;
        }

        int index = 0;
        while (index < raw.Length)
        {
            char c = raw[index];
            int unitCount = GetUnitCount(raw, index);
            if (char.IsWhiteSpace(c) || c == '=' || GetEscapedLength(raw, index, unitCount) != unitCount)
            {
                return true;
            }

            index += unitCount;
        }

        return false;
    }

    // Per-char mapping, so an escape is never re-escaped. Truncation stops before an escape or surrogate pair that
    // would not fit whole, so the kept prefix always decodes; N counts the UTF-16 chars of the input that were cut.
    private static string EscapeAndTruncate(string raw, int maxLength)
    {
        StringBuilder body = new(Math.Min(raw.Length, maxLength));
        int index = 0;
        while (index < raw.Length)
        {
            int unitCount = GetUnitCount(raw, index);
            if (body.Length + GetEscapedLength(raw, index, unitCount) > maxLength)
            {
                break;
            }

            AppendEscaped(body, raw, index, unitCount);
            index += unitCount;
        }

        if (index < raw.Length)
        {
            body.Append(_truncationMarkerStart)
                .Append((raw.Length - index).ToString(CultureInfo.InvariantCulture))
                .Append(')');
        }

        return body.ToString();
    }

    // A valid surrogate pair is one unit of 2 chars; a lone surrogate is a unit of 1.
    private static int GetUnitCount(string raw, int index) =>
        index + 1 < raw.Length && char.IsHighSurrogate(raw[index]) && char.IsLowSurrogate(raw[index + 1]) ? 2 : 1;

    // Supplementary Format chars (tag characters) hide text just like the BMP ones.
    private static bool IsFormatPair(string raw, int index) =>
        CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(raw[index], raw[index + 1])) == UnicodeCategory.Format;

    // U+0085 is already a control char; U+2028/U+2029 are not, but viewers split lines on all three. Format chars
    // (bidi overrides and isolates, zero-width, BOM) reorder or hide text; a lone surrogate becomes U+FFFD in UTF-8.
    private static bool IsEscapedAsCodePoint(char c) =>
        char.IsControl(c)
        || c == '\u2028'
        || c == '\u2029'
        || char.IsSurrogate(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    private static int GetEscapedLength(string raw, int index, int unitCount)
    {
        if (unitCount == 2)
        {
            return IsFormatPair(raw, index) ? 2 * _codePointEscapeLength : 2;
        }

        return raw[index] switch
        {
            '\\' or '"' or '\n' or '\r' => 2,
            char c when IsEscapedAsCodePoint(c) => _codePointEscapeLength,
            _ => 1,
        };
    }

    private static void AppendEscaped(StringBuilder body, string raw, int index, int unitCount)
    {
        if (unitCount == 2)
        {
            if (IsFormatPair(raw, index))
            {
                AppendCodePointEscape(body, raw[index]);
                AppendCodePointEscape(body, raw[index + 1]);
            }
            else
            {
                body.Append(raw, index, 2);
            }

            return;
        }

        char c = raw[index];
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
                    AppendCodePointEscape(body, c);
                }
                else
                {
                    body.Append(c);
                }

                break;
        }
    }

    private static void AppendCodePointEscape(StringBuilder body, char c) =>
        body.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
}
