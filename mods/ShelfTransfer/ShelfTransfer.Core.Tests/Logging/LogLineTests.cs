// purpose: pins the D5 log line format and the F4 escaping rules (card 1 criteria 1, 2, 5)
// owns:    LogLineTests
// deps:    LogLine, LogLevel

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ShelfTransfer.Core.Logging;

using Xunit;

namespace ShelfTransfer.Core.Tests.Logging;

public sealed class LogLineTests
{
    private const string _prefix = "2026-10-18T14:35:02.123+02:00 ";

    private static readonly DateTimeOffset _at = new(2026, 10, 18, 14, 35, 2, 123, TimeSpan.FromHours(2));

    private enum Reason
    {
        StoreRefused,
    }

    // ---- criterion 1: format ----

    [Fact]
    public void Format_NoFields_TimestampLevelEvent()
    {
        string line = LogLine.Format(_at, LogLevel.Info, "plugin.load", Array.Empty<KeyValuePair<string, object?>>());

        Assert.Equal("2026-10-18T14:35:02.123+02:00 INFO  plugin.load", line);
    }

    [Theory]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Info, "INFO ")]
    [InlineData(LogLevel.Warn, "WARN ")]
    [InlineData(LogLevel.Error, "ERROR")]
    public void Format_LevelPaddedToFive(LogLevel level, string expected)
    {
        string line = LogLine.Format(_at, level, "a.b", Array.Empty<KeyValuePair<string, object?>>());

        Assert.Equal(_prefix + expected + " a.b", line);
    }

    [Fact]
    public void Format_TimestampKeepsOffsetNotUtc()
    {
        DateTimeOffset negative = new(2026, 1, 2, 3, 4, 5, 6, TimeSpan.FromHours(-5));

        string line = LogLine.Format(negative, LogLevel.Info, "a", Array.Empty<KeyValuePair<string, object?>>());

        Assert.StartsWith("2026-01-02T03:04:05.006-05:00 ", line);
    }

    [Fact]
    public void Format_Criterion5_PluginLoadLineExact()
    {
        KeyValuePair<string, object?>[] fields =
        {
            new("version", "0.1.0"),
            new("core", "0.1.0"),
            new("bepinex", "6.0.0-be.755"),
        };

        string line = LogLine.Format(_at, LogLevel.Info, "plugin.load", fields);

        Assert.Equal(
            "2026-10-18T14:35:02.123+02:00 INFO  plugin.load version=0.1.0 core=0.1.0 bepinex=6.0.0-be.755",
            line);
    }

    [Fact]
    public void Format_FieldsKeepInsertionOrderSingleSpaced()
    {
        KeyValuePair<string, object?>[] fields = { new("b", 1), new("a", 2), new("c", 3) };

        string line = LogLine.Format(_at, LogLevel.Warn, "x", fields);

        Assert.Equal(_prefix + "WARN  x b=1 a=2 c=3", line);
    }

    [Fact]
    public void Format_ValueWithSpace_IsQuoted()
    {
        Assert.Equal("\"target full\"", ValueOf("target full"));
    }

    [Fact]
    public void Format_ValueWithEquals_IsQuoted()
    {
        Assert.Equal("\"a=b\"", ValueOf("a=b"));
    }

    [Fact]
    public void Format_ValueWithQuote_IsQuotedAndInnerQuoteEscaped()
    {
        Assert.Equal("\"say \\\"hi\\\"\"", ValueOf("say \"hi\""));
    }

    [Fact]
    public void Format_Integers_Unquoted()
    {
        Assert.Equal("5", ValueOf(5));
        Assert.Equal("-5", ValueOf(-5));
        Assert.Equal("0", ValueOf(0));
        Assert.Equal("-9223372036854775808", ValueOf(long.MinValue));
    }

    [Fact]
    public void Format_NegativeFloat_Unquoted()
    {
        Assert.Equal("-6.7", ValueOf(-6.7f));
        Assert.Equal("4.5", ValueOf(4.5));
    }

    [Fact]
    public void Format_Float_UsesInvariantCultureDecimalPoint()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("4.5", ValueOf(4.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_Enum_UnquotedByName()
    {
        Assert.Equal("StoreRefused", ValueOf(Reason.StoreRefused));
    }

    [Fact]
    public void Format_Bools_Lowercase()
    {
        Assert.Equal("true", ValueOf(true));
        Assert.Equal("false", ValueOf(false));
    }

    [Fact]
    public void Format_Null_IsBareNull()
    {
        Assert.Equal("null", ValueOf(null));
    }

    [Fact]
    public void Format_EmptyString_IsTwoQuotes()
    {
        Assert.Equal("\"\"", ValueOf(string.Empty));
    }

    [Fact]
    public void Format_UnicodeAndEmoji_PassThroughUnquoted()
    {
        Assert.Equal("caf\u00e9\U0001F600", ValueOf("caf\u00e9\U0001F600"));
    }

    [Fact]
    public void Format_UnicodeWithSpace_QuotedAndPreserved()
    {
        Assert.Equal("\"\U0001F600 ok\"", ValueOf("\U0001F600 ok"));
    }

    // ---- F4 escaping ----

    [Fact]
    public void Format_Backslash_QuotedAndDoubled()
    {
        Assert.Equal("\"C:\\\\Games\"", ValueOf(@"C:\Games"));
    }

    [Fact]
    public void Format_TrailingBackslash_DoesNotEscapeClosingQuote()
    {
        Assert.Equal("\"abc\\\\\"", ValueOf("abc\\"));
    }

    [Fact]
    public void Format_RealNewline_BecomesBackslashN()
    {
        Assert.Equal("\"a\\nb\"", ValueOf("a\nb"));
    }

    [Fact]
    public void Format_RealCarriageReturn_BecomesBackslashR()
    {
        Assert.Equal("\"a\\rb\"", ValueOf("a\rb"));
    }

    [Fact]
    public void Format_Crlf_BecomesBackslashRBackslashN()
    {
        Assert.Equal("\"a\\r\\nb\"", ValueOf("a\r\nb"));
    }

    [Fact]
    public void Format_LiteralBackslashNText_DiffersFromRealNewline()
    {
        string literal = ValueOf("a\\nb");
        string real = ValueOf("a\nb");

        Assert.Equal("\"a\\\\nb\"", literal);
        Assert.NotEqual(real, literal);
    }

    [Fact]
    public void Format_EscapeOrder_BackslashBeforeQuote()
    {
        // input is backslash + quote; correct output is \\ then \" inside quotes
        Assert.Equal("\"\\\\\\\"\"", ValueOf("\\\""));
    }

    [Fact]
    public void Format_BackslashQuoteInjection_StaysOneField()
    {
        string line = FormatOne("\\\" evil=1 \\\"");

        Assert.Equal(_prefix + "INFO  t.e k=\"\\\\\\\" evil=1 \\\\\\\"\"", line);
    }

    [Fact]
    public void Format_QuoteInjection_StaysOneField()
    {
        string line = FormatOne("x\" evil=1 \"");

        Assert.Equal(_prefix + "INFO  t.e k=\"x\\\" evil=1 \\\"\"", line);
    }

    [Fact]
    public void Format_NewlineForgedLine_StaysOneLine()
    {
        string line = FormatOne("x\n2026-10-18T00:00:00.000+00:00 ERROR forged a=b");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("\r")]
    [InlineData("\n\n")]
    [InlineData("tail\\")]
    [InlineData("\"")]
    [InlineData("\\\"\n\r=")]
    [InlineData("\\n")]
    public void Format_NeverEmitsRawLineBreakForSingleEvent(string value)
    {
        string line = FormatOne(value);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("tail\\")]
    [InlineData("\"")]
    [InlineData("\\\"\n\r=")]
    [InlineData("\\n")]
    [InlineData("")]
    [InlineData("\\\" evil=1 \\\"")]
    public void Format_ValueRoundTripsThroughReferenceDecoder(string value)
    {
        string token = ValueOf(value);

        Assert.Equal(value, Unquote(token));
    }

    // ---- criterion 2: validation ----

    [Theory]
    [InlineData("Bad")]
    [InlineData("has space")]
    [InlineData("a=b")]
    [InlineData("a-b")]
    [InlineData("caf\u00e9")]
    [InlineData("a\nb")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Format_InvalidOrBlankKey_ThrowsArgumentException(string key)
    {
        KeyValuePair<string, object?>[] fields = { new(key, 1) };

        Assert.Throws<ArgumentException>(() => LogLine.Format(_at, LogLevel.Info, "a.b", fields));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("Plugin.Load")]
    [InlineData("plugin load")]
    [InlineData("plugin-load")]
    [InlineData("plugin.load\n")]
    [InlineData("pl\u00fcgin")]
    public void Format_BlankOrInvalidEventName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(
            () => LogLine.Format(_at, LogLevel.Info, name, Array.Empty<KeyValuePair<string, object?>>()));
    }

    [Fact]
    public void Format_NullEventName_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => LogLine.Format(_at, LogLevel.Info, null!, Array.Empty<KeyValuePair<string, object?>>()));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a.b")]
    [InlineData("a_b.c9")]
    [InlineData("0")]
    public void Format_ValidNames_Accepted(string name)
    {
        KeyValuePair<string, object?>[] fields = { new(name, 1) };

        string line = LogLine.Format(_at, LogLevel.Info, name, fields);

        Assert.Equal(_prefix + "INFO  " + name + " " + name + "=1", line);
    }

    [Fact]
    public void Format_UnknownLevel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LogLine.Format(_at, (LogLevel)99, "a", Array.Empty<KeyValuePair<string, object?>>()));
    }

    [Fact]
    public void FormatException_InvalidEventName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => LogLine.FormatException(_at, "Bad Name", new InvalidOperationException("x")));
        Assert.Throws<ArgumentException>(
            () => LogLine.FormatException(_at, " ", new InvalidOperationException("x")));
    }

    // ---- criterion 2 / 6: exceptions ----

    [Fact]
    public void FormatException_FirstLineIsErrorWithEventTypeMessage()
    {
        Exception ex = Thrown(new InvalidOperationException("boom"));

        string first = LogLine.FormatException(_at, "debug.probe", ex).Split('\n')[0];

        Assert.Equal(_prefix + "ERROR error event=debug.probe type=InvalidOperationException message=boom", first);
    }

    [Fact]
    public void FormatException_StackFramesIndentedExactlyFourSpaces()
    {
        Exception ex = Thrown(new InvalidOperationException("boom"));

        string[] lines = LogLine.FormatException(_at, "debug.probe", ex).Split('\n');

        Assert.True(lines.Length >= 2);
        foreach (string continuation in lines[1..])
        {
            Assert.StartsWith("    at ", continuation);
        }
    }

    [Fact]
    public void FormatException_ThrownFromHelper_ContainsHelperFrame()
    {
        Exception ex = Thrown(new InvalidOperationException("boom"));

        string text = LogLine.FormatException(_at, "debug.probe", ex);

        Assert.Contains(nameof(Thrown), text);
    }

    [Fact]
    public void FormatException_MessageWithCrlfAndQuotes_StaysOnFirstLine()
    {
        Exception ex = Thrown(new InvalidOperationException("line one\r\nline \"two\""));

        string[] lines = LogLine.FormatException(_at, "x", ex).Split('\n');

        Assert.Contains("message=\"line one\\r\\nline \\\"two\\\"\"", lines[0]);
        Assert.All(lines[1..], l => Assert.StartsWith("    ", l));
    }

    [Fact]
    public void FormatException_EmptyMessage_IsQuotedEmpty()
    {
        Exception ex = Thrown(new InvalidOperationException(string.Empty));

        string first = LogLine.FormatException(_at, "x", ex).Split('\n')[0];

        Assert.EndsWith(" message=\"\"", first);
    }

    [Fact]
    public void FormatException_NullMessage_UsesFrameworkDefaultOnOneLine()
    {
        Exception ex = Thrown(new InvalidOperationException(null));

        string first = LogLine.FormatException(_at, "x", ex).Split('\n')[0];

        Assert.Contains(" type=InvalidOperationException message=", first);
    }

    [Fact]
    public void FormatException_NotThrown_NoStackLines()
    {
        string text = LogLine.FormatException(_at, "x", new InvalidOperationException("never thrown"));

        Assert.Equal(_prefix + "ERROR error event=x type=InvalidOperationException message=\"never thrown\"", text);
    }

    [Fact]
    public void FormatException_BlankAndCrlfFrames_SkippedAndTrimmed()
    {
        Exception ex = new FakeStackException("m", "   at A.B()\r\n\r\n   \n   at C.D()\n");

        string text = LogLine.FormatException(_at, "x", ex);

        Assert.Equal(
            _prefix + "ERROR error event=x type=FakeStackException message=m\n    at A.B()\n    at C.D()",
            text);
    }

    [Fact]
    public void FormatException_NullStackTrace_NoContinuation()
    {
        string text = LogLine.FormatException(_at, "x", new FakeStackException("m", null));

        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public void FormatException_EmptyStackTrace_NoContinuation()
    {
        string text = LogLine.FormatException(_at, "x", new FakeStackException("m", string.Empty));

        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public void FormatException_InnerExceptionMessageDoesNotReachHeader()
    {
        Exception ex = Thrown(new InvalidOperationException("outer", new ArgumentException("inner\nforged=1")));

        string first = LogLine.FormatException(_at, "x", ex).Split('\n')[0];

        Assert.DoesNotContain("forged", first);
    }

    // ---- cards 31+32: null literal, control chars, caps ----

    private const string _ellipsis = "\u2026";

    [Fact]
    public void Format_StringNull_IsQuotedAndDistinctFromRealNull()
    {
        Assert.Equal("\"null\"", ValueOf("null"));
        Assert.Equal("null", ValueOf(null));
        Assert.NotEqual(ValueOf("null"), ValueOf(null));
        Assert.Equal("null", Unquote(ValueOf("null")));
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("Null")]
    [InlineData("nullx")]
    [InlineData("nul")]
    public void Format_NullLookalikes_StayUnquoted(string value)
    {
        Assert.Equal(value, ValueOf(value));
    }

    [Fact]
    public void Format_NonStringWhoseTextIsNull_IsQuoted()
    {
        Assert.Equal("\"null\"", ValueOf(new TextObject("null")));
    }

    [Theory]
    [InlineData(0x0000, "\\u0000")]
    [InlineData(0x0001, "\\u0001")]
    [InlineData(0x0009, "\\u0009")]
    [InlineData(0x000B, "\\u000B")]
    [InlineData(0x000C, "\\u000C")]
    [InlineData(0x001B, "\\u001B")]
    [InlineData(0x001F, "\\u001F")]
    [InlineData(0x007F, "\\u007F")]
    [InlineData(0x0085, "\\u0085")]
    [InlineData(0x009F, "\\u009F")]
    [InlineData(0x2028, "\\u2028")]
    [InlineData(0x2029, "\\u2029")]
    public void Format_ControlOrSeparatorChar_QuotedAndEscapedAsUppercaseHex(int codePoint, string expectedEscape)
    {
        Assert.Equal("\"" + expectedEscape + "\"", ValueOf(((char)codePoint).ToString()));
    }

    [Fact]
    public void Format_EscSequence_CannotReachFileRaw()
    {
        string line = FormatOne("a\u001B[31mred\u001B[0m");

        Assert.Equal(_prefix + "INFO  t.e k=\"a\\u001B[31mred\\u001B[0m\"", line);
        Assert.DoesNotContain('\u001B', line);
    }

    [Fact]
    public void Format_NonBreakingSpace_QuotedButNotEscaped()
    {
        Assert.Equal("\"a\u00A0b\"", ValueOf("a\u00A0b"));
    }

    [Fact]
    public void Format_LiteralBackslashUText_IsNotAnEscape()
    {
        Assert.Equal("\"\\\\u001B\"", ValueOf("\\u001B"));
        Assert.Equal("\\u001B", Unquote(ValueOf("\\u001B")));
    }

    [Fact]
    public void Format_EveryNonSurrogateChar_NoRawControlOrSeparatorAndRoundTrips()
    {
        for (int cp = 0; cp <= 0xFFFF; cp++)
        {
            char c = (char)cp;
            if (char.IsSurrogate(c))
            {
                continue;
            }

            string line = FormatOne(c.ToString());
            string value = line.Substring(_prefix.Length + "INFO  t.e k=".Length);

            foreach (char o in line)
            {
                Assert.False(
                    char.IsControl(o) || o == '\u2028' || o == '\u2029',
                    "raw U+" + ((int)o).ToString("X4", CultureInfo.InvariantCulture) + " for input U+" + cp.ToString("X4", CultureInfo.InvariantCulture));
            }

            Assert.Equal(c.ToString(), Unquote(value));
        }
    }

    [Theory]
    [InlineData("a\tb\u001Bc\0d")]
    [InlineData("\u0085\u2028\u2029\v\f\u007F")]
    [InlineData("x\u001B[2Jy")]
    [InlineData("\\u001B")]
    [InlineData("\u2028=\"\\")]
    public void Format_ControlValueRoundTripsThroughReferenceDecoder(string value)
    {
        Assert.Equal(value, Unquote(ValueOf(value)));
    }

    // ---- card 32: per-value cap ----

    [Fact]
    public void MaxValueLength_Is256()
    {
        Assert.Equal(256, LogLine.MaxValueLength);
    }

    [Fact]
    public void Format_Unquoted256_NotTruncated()
    {
        string value = new('a', 256);

        Assert.Equal(value, ValueOf(value));
    }

    [Fact]
    public void Format_Unquoted257_TruncatedWithMarkerPlusOne()
    {
        Assert.Equal(new string('a', 256) + _ellipsis + "(+1)", ValueOf(new string('a', 257)));
    }

    [Fact]
    public void Format_UnquotedHuge_MarkerCountsCutChars_AndStaysOneToken()
    {
        string value = ValueOf(new string('a', 1000));

        Assert.Equal(new string('a', 256) + _ellipsis + "(+744)", value);
        Assert.DoesNotContain(' ', value);
        Assert.Equal(value, Unquote(value));
    }

    [Fact]
    public void Format_Quoted256_NotTruncated_AndQuotesNotCounted()
    {
        string value = "a " + new string('b', 254);

        Assert.Equal("\"" + value + "\"", ValueOf(value));
    }

    [Fact]
    public void Format_Quoted257_MarkerInsideQuotes()
    {
        string value = "a " + new string('b', 255);

        Assert.Equal("\"a " + new string('b', 254) + _ellipsis + "(+1)\"", ValueOf(value));
    }

    [Fact]
    public void Format_EmptyAndShort_NeverTruncated()
    {
        Assert.Equal("\"\"", ValueOf(string.Empty));
        Assert.Equal("a", ValueOf("a"));
    }

    [Fact]
    public void Format_CapAppliedAfterEscaping_NewlinesCountTwo()
    {
        string value = ValueOf(new string('\n', 300));

        Assert.Equal("\"" + string.Concat(Enumerable.Repeat("\\n", 128)) + _ellipsis + "(+172)\"", value);
    }

    [Theory]
    [InlineData('\\', "\\\\")]
    [InlineData('"', "\\\"")]
    [InlineData('\n', "\\n")]
    [InlineData('\r', "\\r")]
    public void Format_TwoCharEscapeStraddlingBoundary_IsNotSplit(char c, string escaped)
    {
        // 255 kept + a 2-char escape would be 257: the escape is cut whole.
        string cut = ValueOf(new string('a', 255) + c);
        string fits = ValueOf(new string('a', 254) + c);

        Assert.Equal("\"" + new string('a', 255) + _ellipsis + "(+1)\"", cut);
        Assert.Equal("\"" + new string('a', 254) + escaped + "\"", fits);
    }

    [Fact]
    public void Format_UnicodeEscapeStraddlingBoundary_IsNotSplit()
    {
        string cut = ValueOf(new string('a', 251) + "\u001B");
        string fits = ValueOf(new string('a', 250) + "\u001B");

        Assert.Equal("\"" + new string('a', 251) + _ellipsis + "(+1)\"", cut);
        Assert.Equal("\"" + new string('a', 250) + "\\u001B\"", fits);
    }

    [Fact]
    public void Format_SurrogatePairStraddlingBoundary_IsNotSplit()
    {
        string pair = "\U0001F600";
        string cut = ValueOf(new string('a', 255) + pair);
        string fits = ValueOf(new string('a', 254) + pair);

        Assert.Equal(new string('a', 255) + _ellipsis + "(+2)", cut);
        Assert.Equal(new string('a', 254) + pair, fits);
    }

    [Fact]
    public void Format_TruncatedQuotedValue_StaysSingleLineAndFollowingFieldIntact()
    {
        string line = LogLine.Format(
            _at,
            LogLevel.Info,
            "t.e",
            new KeyValuePair<string, object?>[] { new("k", "x \" = \\ " + new string('z', 400)), new("n", 1) });

        Assert.Single(line.Split('\n'));
        Assert.EndsWith(_ellipsis + "(+154)\" n=1", line);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    public void Format_RoundTripUpToCap(int length)
    {
        string value = new string('q', length - (length > 0 ? 1 : 0)) + (length > 0 ? " " : string.Empty);

        Assert.Equal(value, Unquote(ValueOf(value)));
    }

    private sealed class TextObject
    {
        private readonly string _text;

        public TextObject(string text)
        {
            _text = text;
        }

        public override string ToString() => _text;
    }

    private static string FormatOne(object? value) =>
        LogLine.Format(_at, LogLevel.Info, "t.e", new[] { new KeyValuePair<string, object?>("k", value) });

    private static string ValueOf(object? value) => FormatOne(value).Substring(_prefix.Length + "INFO  t.e k=".Length);

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    // Reference decoder: the inverse of the F4 escape rules.
    private static string Unquote(string token)
    {
        if (!token.StartsWith('"'))
        {
            return token;
        }

        Assert.EndsWith("\"", token);
        StringBuilder sb = new();
        for (int i = 1; i < token.Length - 1; i++)
        {
            char c = token[i];
            Assert.NotEqual('"', c);

            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            i++;
            Assert.True(i < token.Length - 1, "dangling backslash: " + token);
            if (token[i] == 'u')
            {
                Assert.True(i + 4 < token.Length, "short unicode escape: " + token);
                string hex = token.Substring(i + 1, 4);
                Assert.Matches("^[0-9A-F]{4}$", hex);
                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 4;
                continue;
            }
            sb.Append(token[i] switch
            {
                '\\' => '\\',
                '"' => '"',
                'n' => '\n',
                'r' => '\r',
                _ => throw new InvalidOperationException("bad escape in " + token),
            });
        }

        return sb.ToString();
    }

    private sealed class FakeStackException : Exception
    {
        private readonly string? _stack;

        public FakeStackException(string message, string? stack)
            : base(message)
        {
            _stack = stack;
        }

        public override string? StackTrace => _stack;
    }
}
