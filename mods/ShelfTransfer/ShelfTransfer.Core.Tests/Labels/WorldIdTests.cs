// purpose: criterion 5 (card 13) - WorldId.Sanitise yields a safe, non-empty file name stem for any input
// owns:    WorldIdTests
// deps:    WorldId

using System.Linq;
using System.Text.RegularExpressions;

using ShelfTransfer.Core.Labels;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public class WorldIdTests
{
    private static readonly Regex _allowed = new("^[A-Za-z0-9_-]+$");

    [Theory]
    [InlineData("world-1_A", "world-1_A")]
    [InlineData("  padded  ", "padded")]
    [InlineData("my world", "my_world")]
    [InlineData("a.b", "a_b")]
    public void Sanitise_KeepsAllowedAndReplacesRest(string raw, string expected) =>
        Assert.Equal(expected, WorldId.Sanitise(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Sanitise_BlankOrNull_GivesFallback(string? raw)
    {
        Assert.Equal("unknown", WorldId.Sanitise(raw));
        Assert.Equal("unknown", WorldId.Fallback);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("C:\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("a/b\\c:d.e")]
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("lpt9.txt")]
    [InlineData("\u00e9\u00e8")]
    [InlineData("\U0001F600")]
    [InlineData("\u0000\u0001")]
    [InlineData("w\u00e4rld")]
    public void Sanitise_HostileInput_IsSafeStem(string raw)
    {
        string result = WorldId.Sanitise(raw);

        Assert.NotEmpty(result);
        Assert.Matches(_allowed, result);
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain('.', result);
        Assert.DoesNotContain(':', result);
    }

    [Fact]
    public void Sanitise_DotDot_BecomesUnderscores() => Assert.Equal("__", WorldId.Sanitise(".."));

    [Fact]
    public void Sanitise_NonAsciiLetters_AreReplaced() =>
        Assert.Equal("w_rld", WorldId.Sanitise("w\u00e4rld"));

    [Fact]
    public void Sanitise_SurrogatePairEmoji_ReplacedPerChar() =>
        Assert.Equal("__", WorldId.Sanitise("\U0001F600"));

    [Theory]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("Prn")]
    [InlineData("aux")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    public void Sanitise_ReservedDeviceName_GetsUnderscorePrefix(string raw) =>
        Assert.Equal("_" + raw, WorldId.Sanitise(raw));

    [Fact]
    public void Sanitise_ReservedNameWithExtension_DotBecomesUnderscore() =>
        Assert.Equal("lpt9_txt", WorldId.Sanitise("lpt9.txt"));

    [Theory]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("CONX")]
    public void Sanitise_NearMissDeviceNames_Untouched(string raw) =>
        Assert.Equal(raw, WorldId.Sanitise(raw));

    [Fact]
    public void Sanitise_LongInput_CutToMaxLength()
    {
        string result = WorldId.Sanitise(new string('a', 100));

        Assert.Equal(64, WorldId.MaxLength);
        Assert.Equal(64, result.Length);
    }

    [Fact]
    public void Sanitise_ExactlyMaxLength_Unchanged()
    {
        string raw = new('b', WorldId.MaxLength);
        Assert.Equal(raw, WorldId.Sanitise(raw));
    }

    [Fact]
    public void Sanitise_LongInputStartingWithDeviceName_NotPrefixed()
    {
        string result = WorldId.Sanitise("CON" + new string('x', 100));

        Assert.Equal(64, result.Length);
        Assert.StartsWith("CONxxx", result);
    }

    [Fact]
    public void Sanitise_OutputAlwaysMatchesAllowedSet_AcrossBmp()
    {
        for (int i = 0; i <= 0xFFFF; i += 7)
        {
            string result = WorldId.Sanitise("w" + (char)i + "z");
            Assert.Matches(_allowed, result);
        }
    }

    [Fact]
    public void Sanitise_IsIdempotent()
    {
        foreach (string raw in new[] { "con", "../x", "a b", new string('q', 100), "lpt9.txt" })
        {
            string once = WorldId.Sanitise(raw);
            Assert.Equal(once, WorldId.Sanitise(once));
        }
    }

    [Fact]
    public void Sanitise_ResultNeverContainsFileNameInvalidChars()
    {
        char[] bad = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', '.', ':' }).ToArray();
        foreach (string raw in new[] { "a/b", "a\\b", "a:b", "a|b", "a*b", "a?b", "a\"b", "a<b>c" })
        {
            Assert.Empty(WorldId.Sanitise(raw).Intersect(bad));
        }
    }
}
