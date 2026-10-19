// purpose: decides which stored labels an overlay-open scan may prune (§07, I5): only keys absent on two consecutive
//          scans of one loaded world, never in the shared fallback file, never on a scan that listed no shelf
// owns:    LabelPruneGuard, LabelPruneOutcome, LabelPruneResult, PrunedLabel
// deps:    LabelStore, ShelfLabel, WorldId
// caution: one scan is not proof (inactive shelves are not found, a scan during load is partial) and a prune is
//          irreversible once saved. State follows one LabelStore instance; a new one (every world entry) starts over

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShelfTransfer.Core.Labels;

public enum LabelPruneOutcome
{
    Checked,
    /// <summary>The store is the shared <see cref="WorldId.Fallback"/> file: another world's labels are never stale here.</summary>
    SkippedUnknownWorld,
    SkippedNoShelves,
}

public sealed record PrunedLabel(string Key, ShelfLabel Label);

/// <summary>Pending: keys absent on this scan only, kept until the next scan confirms them.</summary>
public sealed record LabelPruneResult(LabelPruneOutcome Outcome, IReadOnlyList<PrunedLabel> Pruned, int Pending);

public sealed class LabelPruneGuard
{
    private static readonly IReadOnlyList<PrunedLabel> _nonePruned = Array.Empty<PrunedLabel>();

    private readonly HashSet<string> _missingAtLastScan = new(StringComparer.Ordinal);
    private LabelStore? _store;

    /// <summary>
    /// Removes from <paramref name="store"/> each key missing from <paramref name="registryKeys"/> and
    /// <paramref name="sourceKey"/> on this call and on the previous call for the same store, in ordinal order.
    /// The caller saves the store when anything was pruned.
    /// </summary>
    public LabelPruneResult Prune(LabelStore store, IReadOnlyCollection<string> registryKeys, string sourceKey)
    {
        if (!ReferenceEquals(store, _store))
        {
            _store = store;
            _missingAtLastScan.Clear();
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
        List<PrunedLabel> pruned = missing
            .Where(_missingAtLastScan.Contains)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new PrunedLabel(key, store.Entries[key]))
            .ToList();
        var prunedKeys = new HashSet<string>(pruned.Select(entry => entry.Key), StringComparer.Ordinal);
        List<string> pending = missing.Where(key => !prunedKeys.Contains(key)).ToList();

        if (pruned.Count > 0)
        {
            store.Prune(live.Concat(pending));
        }

        _missingAtLastScan.Clear();
        _missingAtLastScan.UnionWith(pending);
        return new LabelPruneResult(LabelPruneOutcome.Checked, pruned, pending.Count);
    }

    private LabelPruneResult Skip(LabelPruneOutcome outcome)
    {
        _missingAtLastScan.Clear();
        return new LabelPruneResult(outcome, _nonePruned, 0);
    }
}
