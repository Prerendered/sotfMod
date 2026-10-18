// purpose: one world's shelf labels and groups in memory, loaded from and saved to ILabelFileSystem (flow F3, invariant 5)
// owns:    LabelStore, LabelLoadResult, LabelLoadStatus, LabelSaveResult, LabelSaveStatus
// deps:    ILabelFileSystem, LabelFile, ShelfLabel, WorldId
// caution: keys are opaque ShelfKey strings — never reference Core/Gateway here. Not thread-safe (main thread only).
//          Prune trusts its caller: an empty live set from a registry that has not populated yet drops every label.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShelfTransfer.Core.Labels;

public enum LabelLoadStatus
{
    Loaded,
    NoFile,
    Corrupt,
    /// <summary>The file's version is not <see cref="LabelFile.CurrentVersion"/>; it is ignored and never overwritten.</summary>
    Version,
    /// <summary>The file could not be read; it is never overwritten this session.</summary>
    Failed,
}

/// <summary>Detail is null on a clean load, otherwise the reason (or, when Loaded, how many entries were skipped).</summary>
public sealed record LabelLoadResult(LabelLoadStatus Status, int Count, string? Detail);

public enum LabelSaveStatus
{
    Saved,
    Failed,
    /// <summary>No Load ran, or it ended Version/Failed; writing would destroy a file this session never read.</summary>
    Blocked,
}

public sealed record LabelSaveResult(LabelSaveStatus Status, int Count, string? Detail);

public sealed class LabelStore
{
    private readonly ILabelFileSystem _fileSystem;
    private readonly Dictionary<string, ShelfLabel> _labels = new(StringComparer.Ordinal);
    private LabelLoadStatus? _lastLoad;

    public LabelStore(ILabelFileSystem fileSystem, string? worldId)
    {
        _fileSystem = fileSystem;
        SanitisedWorldId = WorldId.Sanitise(worldId);
    }

    /// <summary>The sanitised id the file system receives; the caller's raw id never reaches a path.</summary>
    public string SanitisedWorldId { get; }

    public int Count => _labels.Count;

    /// <summary>Null when the shelf has no label and no group.</summary>
    public ShelfLabel? Get(string key) => _labels.TryGetValue(key, out ShelfLabel? label) ? label : null;

    /// <summary>
    /// Label and group are trimmed; blank means cleared. A cleared label with a group is stored as "".
    /// Clearing both removes the entry. No length or character limit is applied here.
    /// </summary>
    public void Set(string key, string? label, string? group)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("shelf key is required", nameof(key));
        }

        ShelfLabel? normalised = Normalise(label, group);
        if (normalised is null)
        {
            _labels.Remove(key);
            return;
        }

        _labels[key] = normalised;
    }

    /// <summary>Distinct non-null groups in ordinal order; a group exists only while a shelf is assigned to it.</summary>
    public IReadOnlyList<string> ListGroups() =>
        _labels.Values
            .Select(entry => entry.Group)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(group => group, StringComparer.Ordinal)
            .ToList();

    /// <summary>Drops every key not in <paramref name="liveKeys"/>; returns the dropped keys in ordinal order.</summary>
    public IReadOnlyList<string> Prune(IEnumerable<string> liveKeys)
    {
        var live = new HashSet<string>(liveKeys, StringComparer.Ordinal);
        List<string> removed = _labels.Keys
            .Where(key => !live.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        foreach (string key in removed)
        {
            _labels.Remove(key);
        }

        return removed;
    }

    /// <summary>Replaces the in-memory map with the file's content. Never throws; on any failure the map is empty.</summary>
    public LabelLoadResult Load()
    {
        _labels.Clear();
        LabelLoadResult result = ApplyRead(ReadFile());
        _lastLoad = result.Status;
        return result;
    }

    /// <summary>Never throws. Refused (Blocked) until a Load ended Loaded, NoFile or Corrupt.</summary>
    public LabelSaveResult Save()
    {
        if (_lastLoad is not (LabelLoadStatus.Loaded or LabelLoadStatus.NoFile or LabelLoadStatus.Corrupt))
        {
            string reason = _lastLoad is null ? "not loaded" : $"load ended {_lastLoad}";
            return new LabelSaveResult(LabelSaveStatus.Blocked, Count, $"{reason}; existing file left untouched");
        }

        var file = new LabelFile
        {
            Version = LabelFile.CurrentVersion,
            WorldId = SanitisedWorldId,
            Shelves = new Dictionary<string, ShelfLabel>(_labels, StringComparer.Ordinal),
        };

        LabelWriteResult written = WriteFile(file);
        return written.IsOk
            ? new LabelSaveResult(LabelSaveStatus.Saved, Count, null)
            : new LabelSaveResult(LabelSaveStatus.Failed, Count, written.Detail);
    }

    private LabelLoadResult ApplyRead(LabelReadResult read)
    {
        switch (read.Status)
        {
            case LabelReadStatus.NoFile:
                return new LabelLoadResult(LabelLoadStatus.NoFile, 0, read.Detail);
            case LabelReadStatus.Corrupt:
                return new LabelLoadResult(LabelLoadStatus.Corrupt, 0, read.Detail);
            case LabelReadStatus.Ok:
                break;
            default:
                return new LabelLoadResult(LabelLoadStatus.Failed, 0, read.Detail);
        }

        LabelFile? file = read.File;
        if (file is null)
        {
            return new LabelLoadResult(LabelLoadStatus.Corrupt, 0, "file system returned no content");
        }

        if (file.Version != LabelFile.CurrentVersion)
        {
            return new LabelLoadResult(
                LabelLoadStatus.Version, 0, $"version {file.Version} not supported (expected {LabelFile.CurrentVersion})");
        }

        // JSON null bypasses the non-nullable annotations, so every reference is checked.
        if (file.Shelves is null)
        {
            return new LabelLoadResult(LabelLoadStatus.Corrupt, 0, "shelves map missing");
        }

        // A bad entry is skipped rather than failing the file, so one hand-edit cannot cost every other label.
        int skipped = 0;
        foreach (KeyValuePair<string, ShelfLabel> entry in file.Shelves)
        {
            ShelfLabel? normalised = entry.Value is null ? null : Normalise(entry.Value.Label, entry.Value.Group);
            if (string.IsNullOrWhiteSpace(entry.Key) || normalised is null)
            {
                skipped++;
                continue;
            }

            _labels[entry.Key] = normalised;
        }

        string? detail = skipped == 0 ? null : $"skipped {skipped} invalid entries";
        return new LabelLoadResult(LabelLoadStatus.Loaded, Count, detail);
    }

    // The file-system call is the I/O boundary; a throwing implementation becomes a value here and nowhere else.
    private LabelReadResult ReadFile()
    {
        try
        {
            return _fileSystem.Read(SanitisedWorldId);
        }
        catch (Exception ex)
        {
            return LabelReadResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private LabelWriteResult WriteFile(LabelFile file)
    {
        try
        {
            return _fileSystem.Write(file);
        }
        catch (Exception ex)
        {
            return LabelWriteResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ShelfLabel? Normalise(string? label, string? group)
    {
        string trimmedLabel = label?.Trim() ?? string.Empty;
        string? trimmedGroup = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        if (trimmedLabel.Length == 0 && trimmedGroup is null)
        {
            return null;
        }

        return new ShelfLabel(trimmedLabel, trimmedGroup);
    }
}
