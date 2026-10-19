// purpose: decides which stored labels an overlay-open scan may prune (§07, I5): only keys absent on two consecutive
//          scans of one loaded world, never in the shared fallback file, never on a scan that listed no shelf; in
//          Archive mode a pruned entry is moved to the world's archive (<id>.pruned.json), in DryRun nothing changes
// owns:    LabelPruneGuard, LabelPruneMode, LabelPruneOutcome, LabelPruneResult, PrunedLabel
// deps:    LabelStore, ShelfLabel, WorldId
// caution: one scan is not proof (inactive shelves are not found, a scan during load is partial). Order is archive,
//          then remove, then SavePruned: a failed archive removes nothing and a failed save puts the entries back.
//          State follows one LabelStore instance; a new one (every world entry) starts over

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShelfTransfer.Core.Labels;

public enum LabelPruneMode
{
    /// <summary>Reports the keys that would be pruned and changes nothing: the registry misses real shelves (card 82).</summary>
    DryRun,
    Archive,
}

public enum LabelPruneOutcome
{
    Checked,
    /// <summary>DryRun mode: due keys were reported, not removed.</summary>
    DryRun,
    /// <summary>The store is the shared <see cref="WorldId.Fallback"/> file: another world's labels are never stale here.</summary>
    SkippedUnknownWorld,
    SkippedNoShelves,
    /// <summary>Keys were due but the archive append failed; nothing was removed and they stay pending.</summary>
    ArchiveFailed,
}

public sealed record PrunedLabel(string Key, ShelfLabel Label);

/// <summary>Pending: keys absent on this scan only, kept until the next scan confirms them. Detail: why the archive failed.</summary>
public sealed record LabelPruneResult(
    LabelPruneOutcome Outcome, IReadOnlyList<PrunedLabel> Pruned, int Pending, string? Detail = null)
{
    /// <summary>Keys due on this scan (absent twice running), whether or not they were pruned.</summary>
    public int Candidates { get; init; }

    /// <summary>DryRun: due keys not reported before for this store, so each is logged once per world session.</summary>
    public IReadOnlyList<PrunedLabel> NewCandidates { get; init; } = Array.Empty<PrunedLabel>();
}

public sealed class LabelPruneGuard
{
    private static readonly IReadOnlyList<PrunedLabel> _nonePruned = Array.Empty<PrunedLabel>();

    private readonly HashSet<string> _missingAtLastScan = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedCandidates = new(StringComparer.Ordinal);
    private LabelStore? _store;

    public LabelPruneGuard(LabelPruneMode mode)
    {
        Mode = mode;
    }

    public LabelPruneMode Mode { get; }

    /// <summary>
    /// Finds each key missing from <paramref name="registryKeys"/> and <paramref name="sourceKey"/> on this call and on
    /// the previous call for the same store, in ordinal order. Archive mode archives then removes them, and the caller
    /// saves through <see cref="SavePruned"/>; DryRun mode only reports them.
    /// </summary>
    public LabelPruneResult Prune(LabelStore store, IReadOnlyCollection<string> registryKeys, string sourceKey)
    {
        if (!ReferenceEquals(store, _store))
        {
            _store = store;
            _missingAtLastScan.Clear();
            _reportedCandidates.Clear();
        }

        if (store.SanitisedWorldId == WorldId.Fallback)
        {
            return Skip(LabelPruneOutcome.SkippedUnknownWorld);
        }

        // An empty listing proves nothing about any shelf, so it also ends a run of consecutive scans.
        if (registryKeys.Count == 0)
        {
            return Skip(LabelPruneOutcome.SkippedNoShelves);
        }

        var live = new HashSet<string>(registryKeys, StringComparer.Ordinal) { sourceKey };
        List<string> missing = store.Entries.Keys.Where(key => !live.Contains(key)).ToList();
        List<PrunedLabel> due = missing
            .Where(_missingAtLastScan.Contains)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new PrunedLabel(key, store.Entries[key]))
            .ToList();
        _missingAtLastScan.Clear();

        if (Mode == LabelPruneMode.DryRun)
        {
            // Due keys stay in the run, so a later scan that still misses them finds them due again.
            _missingAtLastScan.UnionWith(missing);
            List<PrunedLabel> newCandidates = due.Where(entry => _reportedCandidates.Add(entry.Key)).ToList();
            return new LabelPruneResult(LabelPruneOutcome.DryRun, _nonePruned, missing.Count - due.Count)
            {
                Candidates = due.Count,
                NewCandidates = newCandidates,
            };
        }

        var dueKeys = new HashSet<string>(due.Select(entry => entry.Key), StringComparer.Ordinal);
        List<string> pending = missing.Where(key => !dueKeys.Contains(key)).ToList();
        if (due.Count > 0)
        {
            LabelWriteResult archived = store.ArchivePruned(due);
            if (!archived.IsOk)
            {
                // All stay pending, so a later scan that still misses them retries the archive.
                _missingAtLastScan.UnionWith(missing);
                return new LabelPruneResult(LabelPruneOutcome.ArchiveFailed, _nonePruned, missing.Count, archived.Detail)
                {
                    Candidates = due.Count,
                };
            }

            store.Prune(live.Concat(pending));
        }

        _missingAtLastScan.UnionWith(pending);
        return new LabelPruneResult(LabelPruneOutcome.Checked, due, pending.Count) { Candidates = due.Count };
    }

    /// <summary>
    /// Saves after a prune; null when <paramref name="pruned"/> is empty. On anything but Saved the entries go back into
    /// <paramref name="store"/>, so a later save cannot persist the prune; a null from <paramref name="save"/> (no
    /// store) comes back as Failed.
    /// </summary>
    public static LabelSaveResult? SavePruned(
        LabelStore store, IReadOnlyList<PrunedLabel> pruned, Func<LabelSaveResult?> save)
    {
        if (pruned.Count == 0)
        {
            return null;
        }

        LabelSaveResult? saved = save();
        if (saved?.Status != LabelSaveStatus.Saved)
        {
            store.Restore(pruned);
        }

        return saved ?? new LabelSaveResult(LabelSaveStatus.Failed, store.Count, "no store to save");
    }

    private LabelPruneResult Skip(LabelPruneOutcome outcome)
    {
        _missingAtLastScan.Clear();
        return new LabelPruneResult(outcome, _nonePruned, 0);
    }
}
