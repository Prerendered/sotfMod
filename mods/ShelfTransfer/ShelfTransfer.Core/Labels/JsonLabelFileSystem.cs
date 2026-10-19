// purpose: the label file on disk, <dir>/<worldId>.json via System.Text.Json, written temp-then-move (F3, invariant 5),
//          and its pruned-label archive <dir>/<worldId>.pruned.json, read-append-write the same way
// owns:    JsonLabelFileSystem, JsonLabelFileSystem.MaxFileBytes, JsonLabelFileSystem.MaxBadCopies
// deps:    ILabelFileSystem, LabelFile, PrunedLabelArchive, WorldId, IModLog
// caution: P4 catch site: every disk or JSON step runs through Guard once, never from inside another guarded step.
//          Corrupt is returned only once a .bad with the same content exists (new copy, or an identical one already
//          there, so a still-corrupt file is not copied again each launch); if neither, Failed keeps Save blocked.
//          Exception text is reduced to type + path-stripped message before it reaches Detail or the log.
//          A prune failure after that still returns Corrupt (logged step=bad_prune); the copy it made is never pruned.
//          A link (reparse point) at <id>.json.tmp or at the chosen .bad name is refused (Failed), never written through.
//          Main thread only: concurrent Read/Write for one world is unsupported (both share <id>.json.tmp).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Core.Labels;

public sealed class JsonLabelFileSystem : ILabelFileSystem
{
    /// <summary>A larger file is never deserialised; it is treated as Corrupt (and preserved as .bad).</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>.bad copies kept per world, newest by the stamp in the name; older ones are pruned on each new copy.</summary>
    public const int MaxBadCopies = 5;

    private const string _readEvent = "labels.read";
    private const string _writeEvent = "labels.write";
    private const string _archiveEvent = "labels.archive";
    private const string _extension = ".json";
    private const string _archiveSuffix = ".pruned";
    private const string _prunedAtFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string _tempSuffix = ".tmp";
    private const string _badSuffix = ".bad";
    private const string _badStampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string _directoryPlaceholder = "<labels>";
    private const string _pathPlaceholder = "<path>";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string _versionProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(LabelFile.Version));
    private static readonly string _shelvesProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(LabelFile.Shelves));
    private static readonly string _prunedProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(PrunedLabelArchive.Pruned));
    private static readonly byte[] _utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly char[] _separators = { '\\', '/' };

    // Only names this class writes; the enumeration pattern alone also matches 8.3 short names and other suffixes.
    private static readonly Regex _badCopyName = new(
        "^(?<world>[A-Za-z0-9_-]+)" + Regex.Escape(_extension) + @"\.(?<stamp>[0-9]{8}T[0-9]{6}Z)(?:-(?<n>[0-9]{1,9}))?"
            + Regex.Escape(_badSuffix) + "$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // IO messages quote the full path ('C:\Users\<name>\...'); the username must not reach the log or Detail.
    private static readonly Regex _quotedPath = new(@"'(?:[A-Za-z]:[\\/]|\\\\|/)[^']+'", RegexOptions.CultureInvariant);
    private static readonly Regex _barePath = new(@"(?:\b[A-Za-z]:[\\/]|\\\\|(?<![\w.~'""])/)[^\s'""]+", RegexOptions.CultureInvariant);

    private readonly string _directory;
    private readonly IModLog _log;

    /// <summary>Throws ArgumentException for a blank <paramref name="directory"/> (programmer error, P4).</summary>
    public JsonLabelFileSystem(string directory, IModLog log)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("labels directory is required", nameof(directory));
        }

        _directory = Path.GetFullPath(directory);
        _log = log;
    }

    /// <summary>Never throws. A file of another version comes back Ok with only Version set; LabelStore ignores it.</summary>
    public LabelReadResult Read(string worldId)
    {
        if (!IsSanitised(worldId))
        {
            LogEvent(LogLevel.Error, _readEvent, worldId, 0, Field("reason", "invalid_world"));
            return LabelReadResult.Failed("world id is not sanitised");
        }

        string path = GetPath(worldId);
        FileContent? content = null;
        Exception? readError = Guard(() => content = ReadContent(path));
        if (readError is FileNotFoundException or DirectoryNotFoundException)
        {
            LogEvent(LogLevel.Info, _readEvent, worldId, 0, Field("reason", "no_file"));
            return LabelReadResult.NoFile;
        }

        if (readError is not null || content is null)
        {
            return FailRead(worldId, readError, "read");
        }

        if (content.Bytes is null)
        {
            return PreserveCorrupt(worldId, path, $"file is {content.Length} bytes, limit {MaxFileBytes}");
        }

        byte[] bytes = content.Bytes;
        ParsedFile? parsed = null;
        Exception? parseError = Guard(() => parsed = Parse(bytes));
        if (parseError is not null)
        {
            return PreserveCorrupt(worldId, path, FormatDetail(parseError));
        }

        if (parsed?.File is null)
        {
            return PreserveCorrupt(worldId, path, parsed?.Problem ?? "not a label file");
        }

        return AcceptRead(worldId, parsed.File);
    }

    /// <summary>
    /// Throws only ArgumentNullException for a null file (programmer error, P4). Creates the folder if missing; the
    /// existing file is replaced only by a complete one.
    /// </summary>
    public LabelWriteResult Write(LabelFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string worldId = file.WorldId;
        int count = file.Shelves.Count;
        if (!IsSanitised(worldId))
        {
            LogEvent(LogLevel.Error, _writeEvent, worldId, count, Field("reason", "invalid_world"));
            return LabelWriteResult.Failed("world id is not sanitised");
        }

        return WriteJson(_writeEvent, worldId, count, GetPath(worldId), file);
    }

    /// <summary>
    /// Never throws for a non-null list. Reads the existing &lt;id&gt;.pruned.json first; one that cannot be read, is
    /// not a version-1 archive, or would pass MaxFileBytes refuses the append (Failed) and is left untouched.
    /// </summary>
    public LabelWriteResult AppendPruned(string worldId, IReadOnlyList<PrunedLabel> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int count = entries.Count;
        if (!IsSanitised(worldId))
        {
            LogEvent(LogLevel.Error, _archiveEvent, worldId, count, Field("reason", "invalid_world"));
            return LabelWriteResult.Failed("world id is not sanitised");
        }

        string path = GetArchivePath(worldId);
        FileContent? content = null;
        Exception? readError = Guard(() => content = ReadContent(path));
        bool isNew = readError is FileNotFoundException or DirectoryNotFoundException;
        if (!isNew && (readError is not null || content is null))
        {
            return FailWrite(_archiveEvent, worldId, count, readError, "read");
        }

        PrunedLabelArchive? archive = isNew ? new PrunedLabelArchive { WorldId = worldId } : null;
        string? problem = null;
        if (content is { Bytes: null })
        {
            problem = $"archive is {content.Length} bytes, limit {MaxFileBytes}";
        }
        else if (content?.Bytes is byte[] bytes)
        {
            Exception? parseError = Guard(() => archive = ParseArchive(bytes));
            problem = parseError is not null ? FormatDetail(parseError) : "not a version-1 archive";
        }

        if (archive is null)
        {
            // Never rewritten: the archive is the only copy of every earlier prune.
            LogEvent(LogLevel.Error, _archiveEvent, worldId, count, Field("reason", "unreadable"), Field("detail", problem));
            return LabelWriteResult.Failed($"archive left untouched: {problem}");
        }

        string stamp = DateTime.UtcNow.ToString(_prunedAtFormat, CultureInfo.InvariantCulture);
        foreach (PrunedLabel entry in entries)
        {
            archive.Pruned.Add(new PrunedLabelEntry(entry.Key, entry.Label.Label, entry.Label.Group, stamp));
        }

        return WriteJson(_archiveEvent, worldId, count, path, archive);
    }

    // Equality with the sanitiser's output is the whole path defence: [A-Za-z0-9_-] only, no separator, no "..".
    private static bool IsSanitised(string? worldId) =>
        worldId is not null && string.Equals(worldId, WorldId.Sanitise(worldId), StringComparison.Ordinal);

    // The one catch in this class; a throw from disk or JSON becomes a value here and nowhere else.
    private static Exception? Guard(Action step)
    {
        try
        {
            step();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Opening rather than File.Exists: Exists reports an unreadable file as absent, and NoFile would allow a Save.
    // Length comes from the open handle so the cap and the read see the same file.
    private static FileContent ReadContent(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > MaxFileBytes)
        {
            return new FileContent(null, length);
        }

        using var reader = new BinaryReader(stream);
        return new FileContent(reader.ReadBytes((int)length), length);
    }

    // Throws on malformed JSON. A version other than current is returned before the rest is parsed, because a
    // future schema may not fit LabelFile and must never be classed Corrupt (Corrupt allows Save to overwrite it).
    private static ParsedFile Parse(byte[] bytes)
    {
        ReadOnlyMemory<byte> utf8 = bytes;
        if (bytes.AsSpan().StartsWith(_utf8Bom))
        {
            utf8 = utf8.Slice(_utf8Bom.Length);
        }

        using JsonDocument document = JsonDocument.Parse(utf8);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(_versionProperty, out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int versionNumber))
        {
            return new ParsedFile(null, "version missing or not an integer");
        }

        if (versionNumber != LabelFile.CurrentVersion)
        {
            return new ParsedFile(new LabelFile { Version = versionNumber }, null);
        }

        if (!root.TryGetProperty(_shelvesProperty, out JsonElement shelves) || shelves.ValueKind != JsonValueKind.Object)
        {
            return new ParsedFile(null, "shelves map missing");
        }

        LabelFile? file = root.Deserialize<LabelFile>(_jsonOptions);
        return file is null ? new ParsedFile(null, "no content") : new ParsedFile(file, null);
    }

    // Throws on malformed JSON; null for any other version or a missing list, which the caller must not overwrite.
    private static PrunedLabelArchive? ParseArchive(byte[] bytes)
    {
        ReadOnlyMemory<byte> utf8 = bytes;
        if (bytes.AsSpan().StartsWith(_utf8Bom))
        {
            utf8 = utf8.Slice(_utf8Bom.Length);
        }

        using JsonDocument document = JsonDocument.Parse(utf8);
        JsonElement root = document.RootElement;
        bool isCurrent = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(_versionProperty, out JsonElement version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out int versionNumber)
            && versionNumber == PrunedLabelArchive.CurrentVersion
            && root.TryGetProperty(_prunedProperty, out JsonElement pruned)
            && pruned.ValueKind == JsonValueKind.Array;
        PrunedLabelArchive? archive = isCurrent ? root.Deserialize<PrunedLabelArchive>(_jsonOptions) : null;
        return archive?.Pruned is null ? null : archive;
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // Attributes describe the link itself, not its target; (FileAttributes)(-1) means nothing is there.
    private static bool IsReparsePoint(string path)
    {
        FileAttributes attributes = new FileInfo(path).Attributes;
        return (int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static int CompareNewestFirst(BadEntry left, BadEntry right)
    {
        int byStamp = string.CompareOrdinal(right.Stamp, left.Stamp);
        return byStamp != 0 ? byStamp : right.Sequence.CompareTo(left.Sequence);
    }

    private static string BuildBadPath(string stem, int sequence) =>
        sequence == 1 ? stem + _badSuffix : $"{stem}-{sequence}{_badSuffix}";

    private static KeyValuePair<string, object?> Field(string key, object? value) => new(key, value);

    private string GetPath(string worldId) => Path.Combine(_directory, worldId + _extension);

    // A sanitised id has no '.', so <id>.pruned.json can never be another world's label file or match the .bad pattern.
    private string GetArchivePath(string worldId) => Path.Combine(_directory, worldId + _archiveSuffix + _extension);

    // The temp file sits beside the target so the move is a same-volume rename, never a copy. The rename replaces a
    // link at <id>.json rather than writing through it. False = refused before anything was written.
    private bool TryWriteAtomically(byte[] json, string tempPath, string path)
    {
        Directory.CreateDirectory(_directory);
        if (IsReparsePoint(tempPath))
        {
            return false;
        }

        // Delete + CreateNew, not Create: a stale hard link is unlinked instead of written through, and a link
        // planted after the check makes the open fail rather than follow it.
        DeleteIfPresent(tempPath);
        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(json, 0, json.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
        return true;
    }

    private LabelReadResult AcceptRead(string worldId, LabelFile file)
    {
        if (file.Version != LabelFile.CurrentVersion)
        {
            LogEvent(LogLevel.Warn, _readEvent, worldId, 0, Field("reason", "version"), Field("version", file.Version));
            return LabelReadResult.Ok(file);
        }

        int count = file.Shelves.Count;
        if (!string.Equals(file.WorldId, worldId, StringComparison.Ordinal))
        {
            // The file name is authoritative; the next Save rewrites the field.
            LogEvent(
                LogLevel.Warn,
                _readEvent,
                worldId,
                count,
                Field("outcome", "world_mismatch"),
                Field("file_world", file.WorldId));
            return LabelReadResult.Ok(file);
        }

        LogEvent(LogLevel.Info, _readEvent, worldId, count);
        return LabelReadResult.Ok(file);
    }

    // At Read time, never deferred: LabelStore allows Save after Corrupt, which would overwrite the only copy.
    // Pruning shares the bad_copy step but runs after bad is set, so its failure cannot hide a copy that exists.
    private LabelReadResult PreserveCorrupt(string worldId, string path, string detail)
    {
        BadCopy? bad = null;
        var pruned = new List<string>();
        Exception? copyError = Guard(() =>
        {
            List<BadEntry> copies = ListBadCopies(worldId);
            bad = PreserveAsBad(path, worldId, copies);
            if (bad is { IsExisting: false })
            {
                PruneBadCopies(copies, pruned);
            }
        });
        if (bad is null && copyError is null)
        {
            LogEvent(LogLevel.Error, _readEvent, worldId, 0, Field("reason", "reparse_point"), Field("step", "bad_copy"));
            return LabelReadResult.Failed("bad copy target is a link (reparse point); refused");
        }

        if (bad is null)
        {
            return FailRead(worldId, copyError, "bad_copy");
        }

        if (copyError is not null)
        {
            LogError(_readEvent, worldId, copyError, "bad_prune");
        }

        LogEvent(
            LogLevel.Warn,
            _readEvent,
            worldId,
            0,
            Field("reason", "corrupt"),
            Field("detail", detail),
            Field("bad", bad.Name),
            Field("bad_copy", bad.IsExisting ? "existing" : "new"),
            Field("bad_pruned", pruned.Count));
        return LabelReadResult.Corrupt(detail);
    }

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }

    // Only the newest MaxBadCopies are compared, length before hash, so the cost per corrupt launch stays bounded.
    private static string? FindIdenticalBad(string path, IReadOnlyList<BadEntry> copies)
    {
        long length = new FileInfo(path).Length;
        byte[]? hash = null;
        for (int i = 0; i < copies.Count && i < MaxBadCopies; i++)
        {
            FileInfo candidate = copies[i].File;
            if (candidate.Length != length)
            {
                continue;
            }

            hash ??= HashFile(path);
            if (HashFile(candidate.FullName).AsSpan().SequenceEqual(hash))
            {
                return candidate.Name;
            }
        }

        return null;
    }

    // copies was listed before the new copy existed, so the new one is never among those deleted.
    private static void PruneBadCopies(IReadOnlyList<BadEntry> copies, List<string> pruned)
    {
        for (int i = MaxBadCopies - 1; i < copies.Count; i++)
        {
            copies[i].File.Delete();
            pruned.Add(copies[i].File.Name);
        }
    }

    // Null = refused: the free .bad name is a link.
    private BadCopy? PreserveAsBad(string path, string worldId, IReadOnlyList<BadEntry> copies)
    {
        string? existing = FindIdenticalBad(path, copies);
        if (existing is not null)
        {
            return new BadCopy(existing, true);
        }

        string? created = CopyToBad(path, worldId, copies);
        return created is null ? null : new BadCopy(created, false);
    }

    // Newest first by the stamp in the name. Links are left out so no hash reads, and no prune deletes, through to a
    // file outside the folder.
    private List<BadEntry> ListBadCopies(string worldId)
    {
        var copies = new List<BadEntry>();
        string pattern = worldId + _extension + ".*" + _badSuffix;
        foreach (FileInfo file in new DirectoryInfo(_directory).EnumerateFiles(pattern))
        {
            Match match = _badCopyName.Match(file.Name);
            bool isOwnCopy = match.Success
                && string.Equals(match.Groups["world"].Value, worldId, StringComparison.OrdinalIgnoreCase)
                && !file.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (!isOwnCopy)
            {
                continue;
            }

            Group sequence = match.Groups["n"];
            int number = sequence.Success ? int.Parse(sequence.Value, NumberStyles.None, CultureInfo.InvariantCulture) : 1;
            copies.Add(new BadEntry(file, match.Groups["stamp"].Value, number));
        }

        copies.Sort(CompareNewestFirst);
        return copies;
    }

    // No-clobber: a same-second name gets -2, -3, ...; File.Copy without overwrite still refuses a racing file.
    // The suffix starts above the highest one already used for this stamp, so the new copy ranks newest and a name
    // freed by a prune is never reused. File.Exists is false for a junction, so the chosen name is checked last.
    private string? CopyToBad(string path, string worldId, IReadOnlyList<BadEntry> copies)
    {
        string stamp = DateTime.UtcNow.ToString(_badStampFormat, CultureInfo.InvariantCulture);
        string stem = Path.Combine(_directory, $"{worldId}{_extension}.{stamp}");
        int sequence = 1;
        foreach (BadEntry copy in copies)
        {
            if (string.Equals(copy.Stamp, stamp, StringComparison.OrdinalIgnoreCase))
            {
                sequence = Math.Max(sequence, copy.Sequence + 1);
            }
        }

        string badPath = BuildBadPath(stem, sequence);
        while (File.Exists(badPath))
        {
            badPath = BuildBadPath(stem, ++sequence);
        }

        if (IsReparsePoint(badPath))
        {
            return null;
        }

        File.Copy(path, badPath, overwrite: false);
        return Path.GetFileName(badPath);
    }

    private LabelReadResult FailRead(string worldId, Exception? error, string step)
    {
        if (error is null)
        {
            LogEvent(LogLevel.Error, _readEvent, worldId, 0, Field("reason", step));
            return LabelReadResult.Failed(step);
        }

        LogError(_readEvent, worldId, error, step);
        return LabelReadResult.Failed(FormatDetail(error));
    }

    private LabelWriteResult FailWrite(string eventName, string worldId, int count, Exception? error, string step)
    {
        if (error is null)
        {
            LogEvent(LogLevel.Error, eventName, worldId, count, Field("reason", step));
            return LabelWriteResult.Failed(step);
        }

        LogError(eventName, worldId, error, step);
        return LabelWriteResult.Failed(FormatDetail(error));
    }

    private LabelWriteResult WriteJson<T>(string eventName, string worldId, int count, string path, T value)
    {
        byte[]? serialized = null;
        Exception? serializeError = Guard(() => serialized = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions));
        if (serializeError is not null || serialized is null)
        {
            return FailWrite(eventName, worldId, count, serializeError, "serialize");
        }

        // Refused before touching disk: a file this class could not read back would turn Corrupt on next launch.
        byte[] json = serialized;
        if (json.Length > MaxFileBytes)
        {
            LogEvent(
                LogLevel.Error,
                eventName,
                worldId,
                count,
                Field("reason", "too_large"),
                Field("bytes", json.Length),
                Field("limit_bytes", MaxFileBytes));
            return LabelWriteResult.Failed($"file would be {json.Length} bytes, limit {MaxFileBytes}");
        }

        string tempPath = path + _tempSuffix;
        bool isRefused = false;
        Exception? writeError = Guard(() => isRefused = !TryWriteAtomically(json, tempPath, path));
        if (isRefused)
        {
            // The link is left in place: removing it is the user's call, and Save stays failed until then.
            LogEvent(LogLevel.Error, eventName, worldId, count, Field("reason", "reparse_point"), Field("step", "write"));
            return LabelWriteResult.Failed("temp file is a link (reparse point); refused");
        }

        if (writeError is null)
        {
            LogEvent(LogLevel.Info, eventName, worldId, count);
            return LabelWriteResult.Ok;
        }

        Exception? cleanupError = Guard(() => DeleteIfPresent(tempPath));
        if (cleanupError is not null)
        {
            LogError(eventName, worldId, cleanupError, "temp_cleanup");
        }

        return FailWrite(eventName, worldId, count, writeError, "write");
    }

    private void LogEvent(
        LogLevel level,
        string eventName,
        string? worldId,
        int count,
        params KeyValuePair<string, object?>[] extra)
    {
        var fields = new List<KeyValuePair<string, object?>>(2 + extra.Length)
        {
            Field("world", worldId),
            Field("count", count),
        };
        fields.AddRange(extra);
        _log.Write(level, eventName, fields);
    }

    // Not WriteException: its message and stack frames carry absolute paths.
    private void LogError(string eventName, string worldId, Exception error, string step) =>
        _log.Write(LogLevel.Error, "error", new[]
        {
            Field("event", eventName),
            Field("type", error.GetType().Name),
            Field("message", StripPaths(error.Message)),
            Field("world", worldId),
            Field("step", step),
        });

    private string FormatDetail(Exception error) => $"{error.GetType().Name}: {StripPaths(error.Message)}";

    private string StripPaths(string message)
    {
        string text = message.Replace(_directory, _directoryPlaceholder, StringComparison.OrdinalIgnoreCase);
        text = _quotedPath.Replace(text, match =>
        {
            string quoted = match.Value.Trim('\'');
            return "'" + quoted.Substring(quoted.LastIndexOfAny(_separators) + 1) + "'";
        });
        return _barePath.Replace(text, _pathPlaceholder);
    }

    private sealed record FileContent(byte[]? Bytes, long Length);

    private sealed record ParsedFile(LabelFile? File, string? Problem);

    private sealed record BadCopy(string Name, bool IsExisting);

    private sealed record BadEntry(FileInfo File, string Stamp, int Sequence);
}
