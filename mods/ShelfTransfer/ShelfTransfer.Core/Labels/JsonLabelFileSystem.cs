// purpose: the label file on disk, <dir>/<worldId>.json via System.Text.Json, written temp-then-move (F3, invariant 5)
// owns:    JsonLabelFileSystem, JsonLabelFileSystem.MaxFileBytes
// deps:    ILabelFileSystem, LabelFile, WorldId, IModLog
// caution: P4 catch site: every disk or JSON step runs through Guard once, never from inside another guarded step.
//          Corrupt is returned only after the .bad copy exists; if the copy fails the result is Failed, so Save stays
//          blocked. Exception text is reduced to type + path-stripped message before it reaches Detail or the log.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Core.Labels;

public sealed class JsonLabelFileSystem : ILabelFileSystem
{
    /// <summary>A larger file is never deserialised; it is treated as Corrupt (and preserved as .bad).</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private const string _readEvent = "labels.read";
    private const string _writeEvent = "labels.write";
    private const string _extension = ".json";
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
    private static readonly byte[] _utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly char[] _separators = { '\\', '/' };

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

    /// <summary>Never throws. Creates the folder if missing; the existing file is replaced only by a complete one.</summary>
    public LabelWriteResult Write(LabelFile file)
    {
        string worldId = file.WorldId;
        int count = file.Shelves.Count;
        if (!IsSanitised(worldId))
        {
            LogEvent(LogLevel.Error, _writeEvent, worldId, count, Field("reason", "invalid_world"));
            return LabelWriteResult.Failed("world id is not sanitised");
        }

        byte[]? serialized = null;
        Exception? serializeError = Guard(() => serialized = JsonSerializer.SerializeToUtf8Bytes(file, _jsonOptions));
        if (serializeError is not null || serialized is null)
        {
            return FailWrite(worldId, count, serializeError, "serialize");
        }

        // Refused before touching disk: a file this class could not read back would turn Corrupt on next launch.
        byte[] json = serialized;
        if (json.Length > MaxFileBytes)
        {
            LogEvent(
                LogLevel.Error,
                _writeEvent,
                worldId,
                count,
                Field("reason", "too_large"),
                Field("bytes", json.Length),
                Field("limit_bytes", MaxFileBytes));
            return LabelWriteResult.Failed($"file would be {json.Length} bytes, limit {MaxFileBytes}");
        }

        string path = GetPath(worldId);
        string tempPath = path + _tempSuffix;
        Exception? writeError = Guard(() => WriteAtomically(json, tempPath, path));
        if (writeError is null)
        {
            LogEvent(LogLevel.Info, _writeEvent, worldId, count);
            return LabelWriteResult.Ok;
        }

        Exception? cleanupError = Guard(() => DeleteIfPresent(tempPath));
        if (cleanupError is not null)
        {
            LogError(_writeEvent, worldId, cleanupError, "temp_cleanup");
        }

        return FailWrite(worldId, count, writeError, "write");
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

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static KeyValuePair<string, object?> Field(string key, object? value) => new(key, value);

    private string GetPath(string worldId) => Path.Combine(_directory, worldId + _extension);

    // The temp file sits beside the target so the move is a same-volume rename, never a copy.
    private void WriteAtomically(byte[] json, string tempPath, string path)
    {
        Directory.CreateDirectory(_directory);
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(json, 0, json.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
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
    private LabelReadResult PreserveCorrupt(string worldId, string path, string detail)
    {
        string? badName = null;
        Exception? copyError = Guard(() => badName = CopyToBad(path, worldId));
        if (copyError is not null || badName is null)
        {
            return FailRead(worldId, copyError, "bad_copy");
        }

        LogEvent(
            LogLevel.Warn,
            _readEvent,
            worldId,
            0,
            Field("reason", "corrupt"),
            Field("detail", detail),
            Field("bad", badName));
        return LabelReadResult.Corrupt(detail);
    }

    // No-clobber: a same-second name gets -2, -3, ...; File.Copy without overwrite still refuses a racing file.
    private string CopyToBad(string path, string worldId)
    {
        string stamp = DateTime.UtcNow.ToString(_badStampFormat, CultureInfo.InvariantCulture);
        string stem = Path.Combine(_directory, $"{worldId}{_extension}.{stamp}");
        string badPath = stem + _badSuffix;
        for (int suffix = 2; File.Exists(badPath); suffix++)
        {
            badPath = $"{stem}-{suffix}{_badSuffix}";
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

    private LabelWriteResult FailWrite(string worldId, int count, Exception? error, string step)
    {
        if (error is null)
        {
            LogEvent(LogLevel.Error, _writeEvent, worldId, count, Field("reason", step));
            return LabelWriteResult.Failed(step);
        }

        LogError(_writeEvent, worldId, error, step);
        return LabelWriteResult.Failed(FormatDetail(error));
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
}
