// purpose: cards 14 and 15 - JsonLabelFileSystem file shape, atomic write, no-file/corrupt/failed/version outcomes, .bad copies, path stripping
// owns:    JsonLabelFileSystemTests
// deps:    JsonLabelFileSystem, LabelStore, LabelFile, FakeModLog
// caution: real disk, one temp directory per test. Failure injection that is OS-specific is marked [Windows-only] and
//          returns early elsewhere (a silent pass there, so CI Linux does not exercise it).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Labels;

public sealed class JsonLabelFileSystemTests : IDisposable
{
    private const string _validEmpty = "{\"version\":1,\"worldId\":\"w1\",\"shelves\":{}}";
    private static readonly Regex _badName = new(@"^w1\.json\.\d{8}T\d{6}Z(-\d+)?\.bad$", RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly string _dir;
    private readonly FakeModLog _log = new();

    public JsonLabelFileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "shelftransfer-jlfs-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "labels");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // best effort: a leftover temp folder must not fail a test run
        }
    }

    private JsonLabelFileSystem NewFs() => new(_dir, _log);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private string PutText(string name, string text)
    {
        Directory.CreateDirectory(_dir);
        string path = PathOf(name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private string PutBytes(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        string path = PathOf(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string[] BadFiles() =>
        Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*.bad").Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    private string[] AllFileNames() =>
        Directory.Exists(_dir)
            ? Directory.GetFileSystemEntries(_dir).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    private static LabelFile FileOf(string worldId, params (string Key, string Label, string? Group)[] shelves)
    {
        var file = new LabelFile { WorldId = worldId };
        foreach ((string key, string label, string? group) in shelves)
        {
            file.Shelves[key] = new ShelfLabel(label, group);
        }

        return file;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private LogEntry Single(string eventName, LogLevel level) =>
        Assert.Single(_log.Named(eventName).Where(e => e.Level == level).ToList());

    private IEnumerable<string> AllLogText() =>
        _log.Entries.SelectMany(e => new[] { e.Event }.Concat(e.Fields.Values.Select(v => v?.ToString() ?? string.Empty)));

    // ---- construction ----

    [Fact]
    public void MaxFileBytes_IsOneMebibyte() => Assert.Equal(1048576L, JsonLabelFileSystem.MaxFileBytes);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankDirectory_ThrowsArgumentException(string? directory) =>
        Assert.Throws<ArgumentException>(() => new JsonLabelFileSystem(directory!, _log));

    [Fact]
    public void Constructor_RelativeDirectory_ResolvedAgainstCurrentDirectory()
    {
        string relative = "shelftransfer-rel-" + Guid.NewGuid().ToString("N");
        string full = Path.GetFullPath(relative);
        try
        {
            var fs = new JsonLabelFileSystem(relative, _log);
            Assert.True(fs.Write(FileOf("w1", ("k", "A", null))).IsOk);
            Assert.True(File.Exists(Path.Combine(full, "w1.json")));
        }
        finally
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
    }

    // ---- card 14: Write ----

    [Fact]
    public void Write_CreatesFolderAndFile_WithVersionWorldIdShelves_CamelCase()
    {
        Assert.False(Directory.Exists(_dir));
        LabelWriteResult result = NewFs().Write(FileOf("w1", ("k1", "Logs", "Wood"), ("k2", "Rocks", null)));

        Assert.True(result.IsOk);
        Assert.Null(result.Detail);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(PathOf("w1.json")));
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("w1", root.GetProperty("worldId").GetString());
        JsonElement shelves = root.GetProperty("shelves");
        Assert.Equal("Logs", shelves.GetProperty("k1").GetProperty("label").GetString());
        Assert.Equal("Wood", shelves.GetProperty("k1").GetProperty("group").GetString());
        Assert.Equal("Rocks", shelves.GetProperty("k2").GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, shelves.GetProperty("k2").GetProperty("group").ValueKind);
        Assert.False(shelves.GetProperty("k1").TryGetProperty("Label", out _));
        Assert.False(root.TryGetProperty("Version", out _));
        Assert.Equal(new[] { "version", "worldId", "shelves" }, root.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Write_LogsInfoLabelsWrite_WithWorldAndCount()
    {
        NewFs().Write(FileOf("w1", ("k1", "A", null), ("k2", "B", null), ("k3", "C", null)));

        LogEntry entry = Single("labels.write", LogLevel.Info);
        Assert.Equal("w1", entry.Text("world"));
        Assert.Equal("3", entry.Text("count"));
        Assert.Single(_log.Entries);
    }

    [Fact]
    public void Write_EmptyShelves_WritesCountZero()
    {
        Assert.True(NewFs().Write(FileOf("w1")).IsOk);
        Assert.Equal("0", Single("labels.write", LogLevel.Info).Text("count"));
    }

    [Fact]
    public void Write_FileNameComesFromFileWorldId()
    {
        Assert.True(NewFs().Write(FileOf("My_World-2", ("k", "A", null))).IsOk);

        Assert.Equal(new[] { "My_World-2.json" }, AllFileNames());
    }

    [Fact]
    public void Write_LeavesNoTempFile()
    {
        NewFs().Write(FileOf("w1", ("k", "A", null)));

        Assert.Equal(new[] { "w1.json" }, AllFileNames());
    }

    [Fact]
    public void Write_ReplacesExistingFile_TwiceInARow()
    {
        JsonLabelFileSystem fs = NewFs();
        Assert.True(fs.Write(FileOf("w1", ("k1", "First", null))).IsOk);
        Assert.True(fs.Write(FileOf("w1", ("k2", "Second", "G"))).IsOk);

        LabelReadResult read = fs.Read("w1");
        Assert.Equal(LabelReadStatus.Ok, read.Status);
        Assert.Equal(new[] { "k2" }, read.File!.Shelves.Keys.ToArray());
        Assert.Equal(new ShelfLabel("Second", "G"), read.File.Shelves["k2"]);
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
    }

    [Fact]
    public void Write_Null_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => NewFs().Write(null!));

    [Fact]
    public void Write_AtExactlyOneMebibyte_Succeeds_AndReadsBack()
    {
        JsonLabelFileSystem fs = NewFs();
        fs.Write(FileOf("w1", ("k", "a", null)));
        long baseline = new FileInfo(PathOf("w1.json")).Length;
        int padding = (int)(JsonLabelFileSystem.MaxFileBytes - baseline);

        Assert.True(fs.Write(FileOf("w1", ("k", new string('a', 1 + padding), null))).IsOk);

        Assert.Equal(JsonLabelFileSystem.MaxFileBytes, new FileInfo(PathOf("w1.json")).Length);
        LabelReadResult read = fs.Read("w1");
        Assert.Equal(LabelReadStatus.Ok, read.Status);
        Assert.Equal(1 + padding, read.File!.Shelves["k"].Label.Length);
    }

    [Fact]
    public void Write_OneByteOverOneMebibyte_Failed_TooLarge_DiskUntouched()
    {
        JsonLabelFileSystem fs = NewFs();
        fs.Write(FileOf("w1", ("k", "a", null)));
        long baseline = new FileInfo(PathOf("w1.json")).Length;
        int padding = (int)(JsonLabelFileSystem.MaxFileBytes - baseline);
        byte[] before = File.ReadAllBytes(PathOf("w1.json"));
        _log.Entries.Clear();

        LabelWriteResult result = fs.Write(FileOf("w1", ("k", new string('a', 2 + padding), null)));

        Assert.False(result.IsOk);
        Assert.False(string.IsNullOrEmpty(result.Detail));
        Assert.Equal(before, File.ReadAllBytes(PathOf("w1.json")));
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
        LogEntry entry = Single("labels.write", LogLevel.Error);
        Assert.Equal("too_large", entry.Text("reason"));
        Assert.Equal("w1", entry.Text("world"));
        Assert.DoesNotContain(_log.Entries, e => e.Level == LogLevel.Info);
    }

    [Fact]
    public void Write_HugeOutput_NoFolderCreated()
    {
        LabelWriteResult result = NewFs().Write(FileOf("w1", ("k", new string('x', 1200000), null)));

        Assert.False(result.IsOk);
        Assert.False(Directory.Exists(_dir));
    }

    // ---- card 14: Read ----

    [Fact]
    public void Read_ValidFile_ReturnsContent_AndLogsInfoCount()
    {
        PutText("w1.json", "{\"version\":1,\"worldId\":\"w1\",\"shelves\":{\"a\":{\"label\":\"Logs\",\"group\":\"Wood\"},\"b\":{\"label\":\"Rocks\",\"group\":null}}}");

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Ok, read.Status);
        Assert.Equal(1, read.File!.Version);
        Assert.Equal("w1", read.File.WorldId);
        Assert.Equal(new ShelfLabel("Logs", "Wood"), read.File.Shelves["a"]);
        Assert.Equal(new ShelfLabel("Rocks", null), read.File.Shelves["b"]);
        LogEntry entry = Single("labels.read", LogLevel.Info);
        Assert.Equal("w1", entry.Text("world"));
        Assert.Equal("2", entry.Text("count"));
        Assert.Single(_log.Entries);
    }

    [Fact]
    public void Read_FileWithUtf8Bom_IsAccepted()
    {
        byte[] body = Encoding.UTF8.GetBytes(_validEmpty);
        PutBytes("w1.json", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());

        Assert.Equal(LabelReadStatus.Ok, NewFs().Read("w1").Status);
        Assert.Empty(BadFiles());
    }

    [Fact]
    public void Read_NonAsciiAndEscapedLabels_RoundTrip()
    {
        string label = "Ca\u00f1o \u2603 \"quoted\" \\ back\nline";
        JsonLabelFileSystem fs = NewFs();
        fs.Write(FileOf("w1", ("k", label, "G\u00e9")));

        LabelReadResult read = fs.Read("w1");

        Assert.Equal(new ShelfLabel(label, "G\u00e9"), read.File!.Shelves["k"]);
    }

    [Fact]
    public void Read_FileExactlyOneMebibyte_ReadNormally()
    {
        byte[] body = Encoding.UTF8.GetBytes(_validEmpty);
        byte[] bytes = new byte[JsonLabelFileSystem.MaxFileBytes];
        Array.Fill(bytes, (byte)' ');
        Array.Copy(body, bytes, body.Length);
        PutBytes("w1.json", bytes);

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Ok, read.Status);
        Assert.Empty(BadFiles());
    }

    [Fact]
    public void Read_FileOneByteOverOneMebibyte_Corrupt_WithBadCopy_NotDeserialised()
    {
        byte[] body = Encoding.UTF8.GetBytes(_validEmpty);
        byte[] bytes = new byte[JsonLabelFileSystem.MaxFileBytes + 1];
        Array.Fill(bytes, (byte)' ');
        Array.Copy(body, bytes, body.Length);
        string path = PutBytes("w1.json", bytes);

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Corrupt, read.Status);
        Assert.False(string.IsNullOrEmpty(read.Detail));
        string bad = Assert.Single(BadFiles());
        Assert.Equal(bytes.Length, new FileInfo(PathOf(bad)).Length);
        Assert.Equal(Sha(bytes), Sha(File.ReadAllBytes(path)));
        Assert.Equal("corrupt", Single("labels.read", LogLevel.Warn).Text("reason"));
    }

    [Fact]
    public void Read_HugeFileOfVersionTwo_StillCorrupt_SizeCheckComesFirst()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"version\":2,\"junk\":\"" + new string('x', 1100000) + "\"}");
        PutBytes("w1.json", bytes);

        Assert.Equal(LabelReadStatus.Corrupt, NewFs().Read("w1").Status);
        Assert.Single(BadFiles());
    }

    [Fact]
    public void Read_WorldIdMismatch_OkWithOneWarn()
    {
        PutText("w1.json", "{\"version\":1,\"worldId\":\"other\",\"shelves\":{\"a\":{\"label\":\"L\",\"group\":null}}}");

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Ok, read.Status);
        Assert.Equal(new ShelfLabel("L", null), read.File!.Shelves["a"]);
        LogEntry warn = Single("labels.read", LogLevel.Warn);
        Assert.Equal("world_mismatch", warn.Text("outcome"));
        Assert.Equal("w1", warn.Text("world"));
        Assert.Single(_log.Entries);
        Assert.Empty(BadFiles());
    }

    [Fact]
    public void Read_WorldIdMissingInFile_IsAMismatchWarn_NotCorrupt()
    {
        PutText("w1.json", "{\"version\":1,\"shelves\":{}}");

        Assert.Equal(LabelReadStatus.Ok, NewFs().Read("w1").Status);
        Assert.Equal("world_mismatch", Single("labels.read", LogLevel.Warn).Text("outcome"));
    }

    [Fact]
    public void Read_StaleTmpFile_IsIgnored_AndOldFileWins()
    {
        PutText("w1.json", "{\"version\":1,\"worldId\":\"w1\",\"shelves\":{\"a\":{\"label\":\"Old\",\"group\":null}}}");
        PutText("w1.json.tmp", "{ half written");

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Ok, read.Status);
        Assert.Equal("Old", read.File!.Shelves["a"].Label);
        Assert.Empty(BadFiles());
        Assert.Equal(new[] { "w1.json", "w1.json.tmp" }, AllFileNames());
    }

    [Fact]
    public void Read_OnlyStaleTmpExists_IsNoFile()
    {
        PutText("w1.json.tmp", "{ half written");

        Assert.Equal(LabelReadStatus.NoFile, NewFs().Read("w1").Status);
    }

    // ---- card 15 crit 1: no file ----

    [Fact]
    public void Read_NoFile_ReturnsNoFile_AndLogsInfoReasonNoFile()
    {
        Directory.CreateDirectory(_dir);

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.NoFile, read.Status);
        Assert.Null(read.File);
        LogEntry entry = Single("labels.read", LogLevel.Info);
        Assert.Equal("w1", entry.Text("world"));
        Assert.Equal("0", entry.Text("count"));
        Assert.Equal("no_file", entry.Text("reason"));
        Assert.Single(_log.Entries);
        Assert.Empty(AllFileNames());
    }

    [Fact]
    public void Read_LabelsFolderMissing_IsNoFile()
    {
        Assert.False(Directory.Exists(_dir));

        Assert.Equal(LabelReadStatus.NoFile, NewFs().Read("w1").Status);
        Assert.Equal("no_file", Single("labels.read", LogLevel.Info).Text("reason"));
    }

    // ---- card 15 crit 2: corrupt ----

    public static IEnumerable<object[]> CorruptDocuments() => new[]
    {
        new object[] { "{ not json" },
        new object[] { "" },
        new object[] { "   " },
        new object[] { "null" },
        new object[] { "[]" },
        new object[] { "42" },
        new object[] { "\"text\"" },
        new object[] { "{}" },
        new object[] { "{\"shelves\":{}}" },
        new object[] { "{\"version\":\"1\",\"shelves\":{}}" },
        new object[] { "{\"version\":null,\"shelves\":{}}" },
        new object[] { "{\"version\":1.5,\"shelves\":{}}" },
        new object[] { "{\"version\":99999999999,\"shelves\":{}}" },
        new object[] { "{\"Version\":1,\"worldId\":\"w1\",\"shelves\":{}}" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\"}" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\",\"shelves\":[]}" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\",\"shelves\":null}" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\",\"Shelves\":{}}" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\",\"shelves\":{\"k\":{\"label\":5}}}" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\",\"shelves\":{}} trailing" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\",\"shelves\":{},}" },
        new object[] { "{\"version\":1,/* c */\"worldId\":\"w1\",\"shelves\":{}}" },
        new object[] { "{\"version\":1,\"worldId\":\"w1\",\"shelves\":{}" },
    };

    [Theory]
    [MemberData(nameof(CorruptDocuments))]
    public void Read_CorruptDocument_Corrupt_AfterBadCopy_OriginalUntouched_WarnLogged(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string path = PutBytes("w1.json", bytes);

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Corrupt, read.Status);
        Assert.False(string.IsNullOrWhiteSpace(read.Detail));
        string bad = Assert.Single(BadFiles());
        Assert.Matches(_badName, bad);
        Assert.Equal(bytes, File.ReadAllBytes(PathOf(bad)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        LogEntry warn = Single("labels.read", LogLevel.Warn);
        Assert.Equal("corrupt", warn.Text("reason"));
        Assert.Equal("w1", warn.Text("world"));
        Assert.Equal(bad, warn.Text("bad"));
        Assert.Equal("new", warn.Text("bad_copy"));
        Assert.DoesNotContain(_log.Entries, e => e.Level == LogLevel.Info);
    }

    [Fact]
    public void Read_CorruptFile_BadCopyExistsBeforeCorruptIsReturned_SoImmediateSaveCannotLoseIt()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{ corrupt");
        PutBytes("w1.json", bytes);
        JsonLabelFileSystem fs = NewFs();
        var store = new LabelStore(fs, "w1");

        LabelLoadResult load = store.Load();
        Assert.Equal(LabelLoadStatus.Corrupt, load.Status);
        store.Set("k", "Fresh", null);
        LabelSaveResult save = store.Save();

        Assert.Equal(LabelSaveStatus.Saved, save.Status);
        string bad = Assert.Single(BadFiles());
        Assert.Equal(bytes, File.ReadAllBytes(PathOf(bad)));
        Assert.Equal(LabelReadStatus.Ok, fs.Read("w1").Status);
    }

    [Fact]
    public void Read_CorruptDetail_ContainsNoAbsolutePath()
    {
        PutText("w1.json", "{ not json");

        LabelReadResult read = NewFs().Read("w1");

        Assert.DoesNotContain(_root, read.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.All(AllLogText(), text => Assert.DoesNotContain(_root, text, StringComparison.OrdinalIgnoreCase));
    }

    // ---- .bad naming and reuse ----

    [Fact]
    public void Bad_SameCorruptContentThreeTimes_OneBadFile_ReusedAfterFirst()
    {
        PutText("w1.json", "{ same corrupt");
        JsonLabelFileSystem fs = NewFs();

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(LabelReadStatus.Corrupt, fs.Read("w1").Status);
        }

        string bad = Assert.Single(BadFiles());
        List<LogEntry> warns = _log.Named("labels.read").Where(e => e.Level == LogLevel.Warn).ToList();
        Assert.Equal(3, warns.Count);
        Assert.Equal(new[] { "new", "existing", "existing" }, warns.Select(w => w.Text("bad_copy")).ToArray());
        Assert.All(warns, w => Assert.Equal(bad, w.Text("bad")));
    }

    [Fact]
    public void Bad_DifferentContent_GetsNewFile_EvenWithSameLength()
    {
        JsonLabelFileSystem fs = NewFs();
        PutText("w1.json", "{ corrupt A");
        fs.Read("w1");
        PutText("w1.json", "{ corrupt B");
        fs.Read("w1");

        string[] bad = BadFiles();
        Assert.Equal(2, bad.Length);
        Assert.All(bad, b => Assert.Matches(_badName, b));
        Assert.Equal(
            new[] { "{ corrupt A", "{ corrupt B" },
            bad.Select(b => File.ReadAllText(PathOf(b))).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        Assert.Equal("new", _log.Named("labels.read").Last().Text("bad_copy"));
    }

    [Fact]
    public void Bad_DifferentLength_GetsNewFile()
    {
        JsonLabelFileSystem fs = NewFs();
        PutText("w1.json", "{ short");
        fs.Read("w1");
        PutText("w1.json", "{ a rather longer corrupt body");
        fs.Read("w1");

        Assert.Equal(2, BadFiles().Length);
    }

    [Fact]
    public void Bad_GoingBackToEarlierContent_ReusesTheEarlierCopy()
    {
        JsonLabelFileSystem fs = NewFs();
        PutText("w1.json", "{ corrupt A");
        fs.Read("w1");
        string first = BadFiles().Single();
        PutText("w1.json", "{ corrupt B");
        fs.Read("w1");
        PutText("w1.json", "{ corrupt A");
        fs.Read("w1");

        Assert.Equal(2, BadFiles().Length);
        LogEntry last = _log.Named("labels.read").Last();
        Assert.Equal("existing", last.Text("bad_copy"));
        Assert.Equal(first, last.Text("bad"));
    }

    [Fact]
    public void Bad_PrefixWorldIds_DoNotShareCopies_W10VsW1()
    {
        PutText("w10.json", "{ identical corrupt");
        PutText("w1.json", "{ identical corrupt");
        JsonLabelFileSystem fs = NewFs();

        fs.Read("w10");
        fs.Read("w1");

        string[] bad = BadFiles();
        Assert.Equal(2, bad.Length);
        Assert.Single(bad, b => b.StartsWith("w10.json.", StringComparison.Ordinal));
        string w1Bad = Assert.Single(bad, b => b.StartsWith("w1.json.", StringComparison.Ordinal));
        LogEntry w1Warn = _log.Named("labels.read").Last();
        Assert.Equal("w1", w1Warn.Text("world"));
        Assert.Equal("new", w1Warn.Text("bad_copy"));
        Assert.Equal(w1Bad, w1Warn.Text("bad"));
    }

    [Fact]
    public void Bad_PrefixWorldIds_ShorterCopyNeverMatchedByLongerId_W1ThenW10()
    {
        PutText("w1.json", "{ identical corrupt");
        PutText("w10.json", "{ identical corrupt");
        JsonLabelFileSystem fs = NewFs();

        fs.Read("w1");
        fs.Read("w10");

        Assert.Equal("new", _log.Named("labels.read").Last().Text("bad_copy"));
        Assert.Equal(2, BadFiles().Length);
    }

    [Fact]
    public void Bad_UnrelatedIdenticalFileWithOtherName_IsNotReused()
    {
        PutText("w1.json", "{ identical corrupt");
        PutText("w1.json.notabad.txt", "{ identical corrupt");
        PutText("other.json.20200101T000000Z.bad", "{ identical corrupt");

        NewFs().Read("w1");

        Assert.Equal("new", Single("labels.read", LogLevel.Warn).Text("bad_copy"));
        Assert.Single(BadFiles(), b => b.StartsWith("w1.json.", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_CopyFailure_Failed_StepBadCopy_NeverCorrupt_AndSaveBlocked()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{ corrupt");
        string path = PutBytes("w1.json", bytes);

        // Occupy every plausible timestamped .bad name with a directory so the copy cannot succeed.
        DateTime now = DateTime.UtcNow;
        for (int s = -3; s <= 10; s++)
        {
            string stamp = now.AddSeconds(s).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(PathOf("w1.json." + stamp + ".bad"));
        }

        JsonLabelFileSystem fs = NewFs();
        LabelReadResult read = fs.Read("w1");

        Assert.Equal(LabelReadStatus.Failed, read.Status);
        Assert.False(string.IsNullOrWhiteSpace(read.Detail));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        LogEntry error = Single("error", LogLevel.Error);
        Assert.Equal("labels.read", error.Text("event"));
        Assert.Equal("bad_copy", error.Text("step"));
        Assert.Equal("w1", error.Text("world"));
        Assert.DoesNotContain(_log.Entries, e => e.Fields.TryGetValue("reason", out object? r) && r?.ToString() == "corrupt");

        var store = new LabelStore(fs, "w1");
        Assert.Equal(LabelLoadStatus.Failed, store.Load().Status);
        Assert.Equal(LabelSaveStatus.Blocked, store.Save().Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    // ---- card 15 crit 3: unwritable ----

    [Fact]
    public void Write_TargetIsADirectory_Failed_ErrorLogged_NeverThrows_NoTempLeft()
    {
        Directory.CreateDirectory(PathOf("w1.json"));

        LabelWriteResult result = NewFs().Write(FileOf("w1", ("k", "A", null)));

        Assert.False(result.IsOk);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.True(Directory.Exists(PathOf("w1.json")));
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
        LogEntry error = Single("error", LogLevel.Error);
        Assert.Equal("labels.write", error.Text("event"));
        Assert.Equal("w1", error.Text("world"));
        Assert.Equal("write", error.Text("step"));
        Assert.DoesNotContain(_log.Named("labels.write"), e => e.Level == LogLevel.Info);
    }

    [Fact]
    public void Write_LabelsDirectoryParentIsARegularFile_Failed_NeverThrows()
    {
        string blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "i am a file");
        var fs = new JsonLabelFileSystem(Path.Combine(blocker, "labels"), _log);

        LabelWriteResult result = fs.Write(FileOf("w1", ("k", "A", null)));

        Assert.False(result.IsOk);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.Equal("labels.write", Single("error", LogLevel.Error).Text("event"));
        Assert.Equal("i am a file", File.ReadAllText(blocker));
    }

    [Fact]
    public void Write_Failure_DetailAndLog_HaveNoAbsolutePathOrTempFolder()
    {
        Directory.CreateDirectory(PathOf("w1.json"));

        LabelWriteResult result = NewFs().Write(FileOf("w1", ("k", "A", null)));

        Assert.DoesNotContain(_root, result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.All(AllLogText(), text => Assert.DoesNotContain(_root, text, StringComparison.OrdinalIgnoreCase));
        Assert.All(AllLogText(), text => Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/'), text, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_log.Exceptions);
    }

    [Fact]
    public void Read_TargetIsADirectory_Failed_NotNoFile_NotCorrupt_NoBadCopy()
    {
        Directory.CreateDirectory(PathOf("w1.json"));

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Failed, read.Status);
        Assert.False(string.IsNullOrWhiteSpace(read.Detail));
        Assert.DoesNotContain(_root, read.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(BadFiles());
        Assert.Equal("labels.read", Single("error", LogLevel.Error).Text("event"));
        Assert.All(AllLogText(), text => Assert.DoesNotContain(_root, text, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_log.Exceptions);
    }

    [Fact]
    public void Read_TargetIsADirectory_StoreSaveStaysBlocked()
    {
        Directory.CreateDirectory(PathOf("w1.json"));
        var store = new LabelStore(NewFs(), "w1");

        Assert.Equal(LabelLoadStatus.Failed, store.Load().Status);
        Assert.Equal(LabelSaveStatus.Blocked, store.Save().Status);
        Assert.True(Directory.Exists(PathOf("w1.json")));
    }

    // [Windows-only] sharing violations: .NET emulates FileShare with advisory locks on Unix, but that is not verified here.
    [Fact]
    public void Read_FileLockedExclusively_Failed_NotCorrupt_NoBadCopy_FileIntact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(_validEmpty);
        string path = PutBytes("w1.json", bytes);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            LabelReadResult read = NewFs().Read("w1");

            Assert.Equal(LabelReadStatus.Failed, read.Status);
            Assert.Empty(BadFiles());
        }

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    // [Windows-only] a locked target makes File.Move fail after the temp file was fully written.
    [Fact]
    public void Write_MoveFails_OldFileIntact_TempCleanedUp_Failed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string path = PutText("w1.json", _validEmpty);
        byte[] before = File.ReadAllBytes(path);

        LabelWriteResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = NewFs().Write(FileOf("w1", ("k", "New", null)));
        }

        Assert.False(result.IsOk);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
        Assert.Equal("write", Single("error", LogLevel.Error).Text("step"));
    }

    // ---- interrupted write (cross-platform) ----

    [Fact]
    public void Write_TempPathBlocked_OldFileIntactByteForByte_Failed()
    {
        string path = PutText("w1.json", _validEmpty);
        byte[] before = File.ReadAllBytes(path);
        Directory.CreateDirectory(PathOf("w1.json.tmp"));

        LabelWriteResult result = NewFs().Write(FileOf("w1", ("k", "New", null)));

        Assert.False(result.IsOk);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(LabelReadStatus.Ok, NewFs().Read("w1").Status);
        Assert.Equal("write", Single("error", LogLevel.Error).Text("step"));
    }

    [Fact]
    public void Write_StaleTmpFromACrashedWrite_IsOverwritten_AndGone_AfterSuccess()
    {
        PutText("w1.json", _validEmpty);
        PutText("w1.json.tmp", "{ half written by a crash and much longer than the new body will ever be, ".PadRight(4000, 'x'));

        LabelWriteResult result = NewFs().Write(FileOf("w1", ("k", "New", null)));

        Assert.True(result.IsOk);
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
        LabelReadResult read = NewFs().Read("w1");
        Assert.Equal("New", read.File!.Shelves["k"].Label);
    }

    // ---- card 15 crit 5: future version ----

    [Theory]
    [InlineData("{\"version\":2,\"worldId\":\"w1\",\"shelves\":{\"k\":{\"title\":\"new schema\"}},\"extra\":[1,2,3]}", 2)]
    [InlineData("{\"version\":2,\"shelves\":[1,2]}", 2)]
    [InlineData("{\"version\":2}", 2)]
    [InlineData("{\"version\":0,\"shelves\":{}}", 0)]
    [InlineData("{\"version\":-1,\"worldId\":\"w1\",\"shelves\":{}}", -1)]
    [InlineData("{\"version\":2147483647}", int.MaxValue)]
    public void Read_VersionNotOne_OkWithOnlyVersion_WarnReasonVersion_FileByteIdentical_NoBadCopy(string text, int version)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string path = PutBytes("w1.json", bytes);

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Ok, read.Status);
        Assert.Equal(version, read.File!.Version);
        Assert.Empty(read.File.Shelves);
        Assert.Equal(string.Empty, read.File.WorldId);
        LogEntry warn = Single("labels.read", LogLevel.Warn);
        Assert.Equal("version", warn.Text("reason"));
        Assert.Equal("w1", warn.Text("world"));
        Assert.Equal(version.ToString(CultureInfo.InvariantCulture), warn.Text("version"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(BadFiles());
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
    }

    [Fact]
    public void FutureVersion_ThroughStore_LoadIsVersion_SaveBlocked_FileUntouched()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"version\":2,\"worldId\":\"w1\",\"shelves\":{\"k\":{\"title\":\"x\"}}}");
        string path = PutBytes("w1.json", bytes);
        var store = new LabelStore(NewFs(), "w1");

        LabelLoadResult load = store.Load();
        store.Set("k", "Mine", null);
        LabelSaveResult save = store.Save();

        Assert.Equal(LabelLoadStatus.Version, load.Status);
        Assert.Equal(LabelSaveStatus.Blocked, save.Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
        Assert.Empty(_log.Named("labels.write"));
    }

    [Fact]
    public void Version_FractionalOrNonInteger_IsCorrupt_NotFutureVersion()
    {
        PutText("w1.json", "{\"version\":2.5,\"shelves\":{}}");

        LabelReadResult read = NewFs().Read("w1");

        Assert.Equal(LabelReadStatus.Corrupt, read.Status);
        Assert.Single(BadFiles());
    }

    // ---- round trip through LabelStore ----

    [Fact]
    public void RoundTrip_StoreSave_NewInstances_LoadRestoresEverything()
    {
        var first = new LabelStore(NewFs(), "w1");
        Assert.Equal(LabelLoadStatus.NoFile, first.Load().Status);
        first.Set("k1", "Logs", "Wood");
        first.Set("k2", "Rocks", null);
        first.Set("k3", string.Empty, "OnlyGroup");
        Assert.Equal(LabelSaveStatus.Saved, first.Save().Status);

        var second = new LabelStore(new JsonLabelFileSystem(_dir, new FakeModLog()), "w1");
        LabelLoadResult load = second.Load();

        Assert.Equal(LabelLoadStatus.Loaded, load.Status);
        Assert.Equal(3, load.Count);
        Assert.Null(load.Detail);
        Assert.Equal(new ShelfLabel("Logs", "Wood"), second.Get("k1"));
        Assert.Equal(new ShelfLabel("Rocks", null), second.Get("k2"));
        Assert.Equal(new ShelfLabel(string.Empty, "OnlyGroup"), second.Get("k3"));
    }

    [Fact]
    public void RoundTrip_StoreUsesSanitisedWorldIdAsFileName()
    {
        var store = new LabelStore(NewFs(), "My World/../x!");
        store.Load();
        store.Set("k", "A", null);

        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);

        Assert.Equal(new[] { store.SanitisedWorldId + ".json" }, AllFileNames());
        Assert.Empty(Directory.GetFiles(_root));
        Assert.DoesNotContain(_log.Named("labels.read"), e => e.Level == LogLevel.Warn);
    }

    [Fact]
    public void RoundTrip_ClearedStore_SavesEmptyFile_LoadsZero()
    {
        var store = new LabelStore(NewFs(), "w1");
        store.Load();
        store.Set("k", "A", null);
        store.Save();
        store.Set("k", null, null);
        Assert.Equal(LabelSaveStatus.Saved, store.Save().Status);

        var again = new LabelStore(NewFs(), "w1");
        LabelLoadResult load = again.Load();

        Assert.Equal(LabelLoadStatus.Loaded, load.Status);
        Assert.Equal(0, load.Count);
    }

    [Fact]
    public void Store_OverRealUnreadableDisk_NeverThrows_SeeLabelStoreTestsForThrowingFake()
    {
        // Criterion 4 (a fake file system that throws on load and on save) is covered in LabelStoreTests:
        // Load_FileSystemThrows_BecomesFailedWithTypeAndMessage, Save_WriteThrows_BecomesFailed_NeverThrows,
        // Save_AfterThrownLoad_Blocked. This case adds the real implementation's unreadable-disk equivalent.
        Directory.CreateDirectory(PathOf("w1.json"));
        var store = new LabelStore(NewFs(), "w1");

        LabelLoadResult load = store.Load();
        LabelSaveResult save = store.Save();

        Assert.Equal(LabelLoadStatus.Failed, load.Status);
        Assert.Equal(LabelSaveStatus.Blocked, save.Status);
    }

    // ---- world id validation ----

    public static IEnumerable<object[]> InvalidWorldIds() => new[]
    {
        new object[] { null! },
        new object[] { string.Empty },
        new object[] { ".." },
        new object[] { "." },
        new object[] { "a/b" },
        new object[] { "a\\b" },
        new object[] { "..\\evil" },
        new object[] { " w1 " },
        new object[] { "w1 " },
        new object[] { "w 1" },
        new object[] { new string('a', 65) },
        new object[] { "con" },
        new object[] { "CON" },
        new object[] { "w1.json" },
        new object[] { "w\u00e91" },
    };

    [Theory]
    [MemberData(nameof(InvalidWorldIds))]
    public void Read_InvalidWorldId_Failed_ErrorInvalidWorld_TouchesNothing(string? worldId)
    {
        LabelReadResult read = NewFs().Read(worldId!);

        Assert.Equal(LabelReadStatus.Failed, read.Status);
        LogEntry error = Single("labels.read", LogLevel.Error);
        Assert.Equal("invalid_world", error.Text("reason"));
        Assert.False(Directory.Exists(_dir));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Theory]
    [MemberData(nameof(InvalidWorldIds))]
    public void Write_InvalidWorldId_Failed_ErrorInvalidWorld_TouchesNothing(string? worldId)
    {
        var file = new LabelFile { WorldId = worldId! };
        file.Shelves["k"] = new ShelfLabel("A", null);

        LabelWriteResult result = NewFs().Write(file);

        Assert.False(result.IsOk);
        LogEntry error = Single("labels.write", LogLevel.Error);
        Assert.Equal("invalid_world", error.Text("reason"));
        Assert.False(Directory.Exists(_dir));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void ValidBoundaryWorldIds_SixtyFourChars_UnderscoreDash_Work()
    {
        JsonLabelFileSystem fs = NewFs();
        foreach (string id in new[] { new string('a', 64), "_", "-", "A_b-9", "Z" })
        {
            Assert.True(fs.Write(FileOf(id, ("k", "A", null))).IsOk);
            Assert.Equal(LabelReadStatus.Ok, fs.Read(id).Status);
        }
    }

    // ---- path stripping ----

    private string Strip(string message)
    {
        MethodInfo method = typeof(JsonLabelFileSystem).GetMethod("StripPaths", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string)method.Invoke(NewFs(), new object[] { message })!;
    }

    [Fact]
    public void StripPaths_LabelsFolder_BecomesPlaceholder()
    {
        string stripped = Strip($"Could not find file '{_dir}{Path.DirectorySeparatorChar}w1.json'.");

        Assert.DoesNotContain(_root, stripped, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Could not find file '<labels>", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void StripPaths_QuotedAbsolutePaths_KeepOnlyFileName()
    {
        Assert.Equal("Access to the path 'foo.json' is denied.", Strip("Access to the path 'C:\\Users\\bob\\AppData\\foo.json' is denied."));
        Assert.Equal("Access to the path 'foo.json' is denied.", Strip("Access to the path '/home/bob/.config/foo.json' is denied."));
        Assert.Equal("denied 'foo.json'", Strip("denied '\\\\server\\share\\bob\\foo.json'"));
    }

    [Theory]
    [InlineData("failed at C:\\Users\\bob\\secret\\x.txt now")]
    [InlineData("failed at D:/Users/bob/secret/x.txt now")]
    [InlineData("failed at \\\\fileserver\\bob\\secret\\x.txt now")]
    [InlineData("failed at /home/bob/secret/x.txt now")]
    [InlineData("failed at /Users/bob/secret/x.txt now")]
    public void StripPaths_BareAbsolutePaths_BecomePathPlaceholder_NoUsernameSurvives(string message)
    {
        string stripped = Strip(message);

        Assert.DoesNotContain("bob", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", stripped, StringComparison.Ordinal);
        Assert.Contains("<path>", stripped, StringComparison.Ordinal);
        Assert.StartsWith("failed at ", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void StripPaths_PlainMessage_Unchanged() =>
        Assert.Equal("'n' is an invalid start of a value. Path: $ | LineNumber: 0", Strip("'n' is an invalid start of a value. Path: $ | LineNumber: 0"));

    [Fact]
    public void StripPaths_MixedLabelsFolderAndUserPath_BothHandled()
    {
        string stripped = Strip($"copy '{_dir}{Path.DirectorySeparatorChar}w1.json' to C:\\Users\\bob\\Desktop\\x");

        Assert.DoesNotContain(_root, stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bob", stripped, StringComparison.Ordinal);
    }

    // ---- logging and exceptions ----

    [Fact]
    public void Failures_NeverUseWriteException_AndNeverThrow()
    {
        Directory.CreateDirectory(PathOf("w1.json"));
        JsonLabelFileSystem fs = NewFs();

        fs.Read("w1");
        fs.Write(FileOf("w1", ("k", "A", null)));
        fs.Read("bad/id");
        fs.Write(FileOf("bad/id"));

        Assert.Empty(_log.Exceptions);
    }

    [Fact]
    public void ErrorLine_CarriesTypeMessageWorldStepAndEvent_ButNoStackOrPath()
    {
        Directory.CreateDirectory(PathOf("w1.json"));

        NewFs().Read("w1");

        LogEntry error = Single("error", LogLevel.Error);
        Assert.Equal(
            new[] { "event", "message", "step", "type", "world" },
            error.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(error.Text("type")));
        Assert.Equal("read", error.Text("step"));
    }

    [Fact]
    public void Read_HealthyFile_IsNotModified_AndNoFilesCreated()
    {
        string path = PutText("w1.json", _validEmpty);
        byte[] before = File.ReadAllBytes(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);

        NewFs().Read("w1");
        NewFs().Read("w1");

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new[] { "w1.json" }, AllFileNames());
    }
}
