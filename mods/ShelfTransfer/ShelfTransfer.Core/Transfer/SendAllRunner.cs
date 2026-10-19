// purpose: Send all (card 75): every item type on the source shelf, one TransferRunner run per type in ListStock order;
//          the queue continues only after a clean run, any other stop ends it
// owns:    SendAllRunner
// deps:    TransferRunner, IShelfGateway, IModLog, AmountClamp, SendAllReport, SendAllState
// caution: the queue's own gateway call is one ListStock at start (I2); each type is planned by its own run from fresh reads (I4);
//          one run Tick per queue Tick, so ItemsPerFrame bounds chunks per frame as for one run; Tick never catches (P2)

using System;
using System.Collections.Generic;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Core.Transfer;

public sealed class SendAllRunner
{
    // Any hook holding the item: the gateway's take falls through to the next stocked hook, so a type spread over several
    // slots moves all of it; FirstSlot would refuse once that slot is empty and end the queue on a TakeRefused.
    private static readonly SlotRef _anyHook = new(null);

    private readonly IShelfGateway _gateway;
    private readonly IModLog _log;
    private readonly ShelfRef _source;
    private readonly ShelfRef _target;
    private readonly int _confirmTicks;
    private readonly List<SendAllTypeResult> _types = new();
    private TransferRunner? _run;
    private RunStartLog? _runLog;

    // Index of the type the current run works on, or of the next type to start.
    private int _next;
    private int _settledMoved;
    private bool _isCancelRequested;

    // Set by Abort; a run aborted from inside its own tick is settled once that tick returns.
    private string? _abortDetail;
    private bool _isTickInFlight;
    private bool _isTickOnStack;

    // Set while the run's abort re-reads; a nested Abort or Tick from them must not settle a run whose report is not built yet.
    private bool _isAbortOnStack;

    /// <summary><paramref name="confirmTicks"/> is passed to every per-type run (see <see cref="TransferRunner"/>).</summary>
    public SendAllRunner(IShelfGateway gateway, IModLog log, ShelfRef source, ShelfRef target, int confirmTicks = 0)
    {
        if (source.Key == target.Key)
        {
            throw new ArgumentException("target must not be the source shelf", nameof(target));
        }

        if (confirmTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmTicks), confirmTicks, "must not be negative");
        }

        _gateway = gateway;
        _log = log;
        _source = source;
        _target = target;
        _confirmTicks = confirmTicks;
    }

    public SendAllState State { get; private set; } = SendAllState.Listing;

    /// <summary>Null until <see cref="State"/> is <see cref="SendAllState.Finished"/>.</summary>
    public SendAllReport? Report { get; private set; }

    /// <summary>Items stored on the target so far, across all types.</summary>
    public int Moved => _settledMoved + (_run?.Moved ?? 0);

    private bool IsFinished => State == SendAllState.Finished;

    /// <summary>The current run stops after its chunk is stored (D7); no further type starts.</summary>
    public void RequestCancel()
    {
        _isCancelRequested = true;
        _run?.RequestCancel();
    }

    /// <summary>The driver calls it after a throw (P2): aborts the current run (fresh re-reads) and ends the queue. No-op once
    /// finished, and while an earlier Abort is still re-reading (the first stop wins).</summary>
    public void Abort(StopReason reason, string? detail)
    {
        if (IsFinished || _isAbortOnStack)
        {
            return;
        }

        _abortDetail ??= FormatStop(reason, detail);
        if (_run is not TransferRunner run)
        {
            Finish(SendAllStopReason.SafetyStop, _abortDetail);
            return;
        }

        _isAbortOnStack = true;
        try
        {
            run.Abort(reason, detail);
        }
        finally
        {
            _isAbortOnStack = false;
        }

        if (!_isTickOnStack)
        {
            Settle(run);
        }
    }

    /// <summary>Lists the source's stock on the first tick, then ticks the current type's run once; a finished run hands over
    /// to the next type on the next tick.</summary>
    public void Tick(int maxCycles)
    {
        if (maxCycles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCycles), maxCycles, "must be at least 1");
        }

        if (IsFinished || _isAbortOnStack)
        {
            return;
        }

        if (_isTickOnStack)
        {
            // Called from a gateway call inside the running tick; two runs must never interleave (I1).
            Abort(StopReason.GatewayError, "re-entrant queue tick");
            return;
        }

        if (_isTickInFlight)
        {
            // The last tick threw and nobody aborted; the run in it is unverified (I3).
            Abort(StopReason.GatewayError, "previous queue tick threw");
            return;
        }

        _isTickInFlight = true;
        _isTickOnStack = true;
        try
        {
            RunTick(maxCycles);
        }
        finally
        {
            _isTickOnStack = false;
        }

        _isTickInFlight = false;
    }

    private void RunTick(int maxCycles)
    {
        if (State == SendAllState.Listing)
        {
            ListTypes();
            if (IsFinished)
            {
                return;
            }
        }

        if (_run is null)
        {
            if (_isCancelRequested)
            {
                Finish(SendAllStopReason.Cancelled, null);
                return;
            }

            ShelfStockEntry entry = _types[_next].Entry;
            _runLog = new RunStartLog(_log);
            _run = new TransferRunner(_gateway, _runLog,
                new TransferRequest(_source, _anyHook, _target, entry.Item, AmountClamp.All), _confirmTicks);
        }

        TransferRunner run = _run;
        run.Tick(maxCycles);
        Settle(run);
    }

    // The order is fixed here; counts are not: each run re-reads source, target and free space at its own Planning (I4).
    private void ListTypes()
    {
        IReadOnlyList<ShelfStockEntry> stock = _gateway.ListStock(_source);
        if (IsFinished)
        {
            return;
        }

        foreach (ShelfStockEntry entry in stock)
        {
            _types.Add(new SendAllTypeResult(entry, SendAllTypeOutcome.NotStarted, null));
        }

        State = SendAllState.Running;
        LogStart(isListed: true);

        if (_types.Count == 0)
        {
            Finish(SendAllStopReason.Completed, null);
        }
    }

    // No-op while the run goes on and once the queue is finished. Only a clean run with nothing in the inventory lets the
    // queue go on (I3).
    private void Settle(TransferRunner run)
    {
        if (IsFinished)
        {
            return;
        }

        if (run.Report is not TransferReport report)
        {
            if (_abortDetail is not null)
            {
                Finish(SendAllStopReason.SafetyStop, _abortDetail);
            }

            return;
        }

        SendAllTypeOutcome outcome = GetOutcome(report);
        SendAllTypeResult result = _types[_next] with
        {
            Outcome = outcome,
            Run = report,
            SourceStart = _runLog?.SourceStart,
            SkipReason = outcome == SendAllTypeOutcome.Skipped ? GetSkipReason(report.Detail) : null,
        };
        _run = null;
        _runLog = null;
        _settledMoved += report.Moved;
        _types[_next] = result;
        _next++;
        LogType(_next, result);

        if (result.Outcome is SendAllTypeOutcome.Stopped or SendAllTypeOutcome.Stranded)
        {
            Finish(SendAllStopReason.SafetyStop, FormatUnsafe(report));
        }
        else if (_abortDetail is not null)
        {
            Finish(SendAllStopReason.SafetyStop, _abortDetail);
        }
        else if (result.Outcome == SendAllTypeOutcome.Cancelled || _isCancelRequested)
        {
            Finish(SendAllStopReason.Cancelled, null);
        }
        else if (_next == _types.Count)
        {
            Finish(SendAllStopReason.Completed, null);
        }
    }

    // A StoreRefused or TargetMismatch cannot be told apart from an unsafe drift, so only these three reasons are clean.
    private static SendAllTypeOutcome GetOutcome(TransferReport report)
    {
        if (report.StrandedInInventory > 0)
        {
            return SendAllTypeOutcome.Stranded;
        }

        return report.Reason switch
        {
            StopReason.Completed => SendAllTypeOutcome.Moved,
            StopReason.BlockedBeforeStart => SendAllTypeOutcome.Skipped,
            StopReason.Cancelled => SendAllTypeOutcome.Cancelled,
            _ => SendAllTypeOutcome.Stopped,
        };
    }

    // The run states why it was blocked only in its detail (TransferRunner.Plan); card 75 leaves the runner unchanged.
    // Everything else it blocks on (TargetFull, cannot hold) is the target side.
    private static SendAllSkipReason GetSkipReason(string? detail)
    {
        if (detail is null)
        {
            return SendAllSkipReason.DoesntFit;
        }

        if (detail.StartsWith("transfer unit unknown", StringComparison.Ordinal))
        {
            return SendAllSkipReason.UnitUnknown;
        }

        return detail.StartsWith(nameof(ClampReason.NothingToMove), StringComparison.Ordinal)
            ? SendAllSkipReason.NothingToMove
            : SendAllSkipReason.DoesntFit;
    }

    private static string FormatUnsafe(TransferReport report) =>
        report.Reason is StopReason.Completed or StopReason.BlockedBeforeStart or StopReason.Cancelled
            ? $"{report.StrandedInInventory} stranded after {report.Reason}"
            : FormatStop(report.Reason, report.Detail);

    private static string FormatStop(StopReason reason, string? detail) => detail is null ? reason.ToString() : $"{reason}: {detail}";

    private void LogStart(bool isListed)
    {
        List<KeyValuePair<string, object?>> fields = new()
        {
            new("source", _source.Key),
            new("target", _target.Key),
            new("types", _types.Count),
        };
        if (!isListed)
        {
            fields.Add(new("listing_failed", true));
        }

        _log.Write(LogLevel.Info, "queue.start", fields);
    }

    // s0 and the skip reason follow criterion 7's fields so that line keeps its shape.
    private void LogType(int number, SendAllTypeResult result)
    {
        bool isUnsafe = result.Outcome is SendAllTypeOutcome.Stopped or SendAllTypeOutcome.Stranded;
        List<KeyValuePair<string, object?>> fields = new()
        {
            new("k", number),
            new("n", _types.Count),
            new("item", result.Item.Value),
            new("outcome", result.Outcome),
            new("moved", result.Moved),
            new("remaining", result.Remaining),
            new("stranded", result.Stranded),
            new("s0", result.SourceStart),
        };
        if (result.SkipReason is SendAllSkipReason skip)
        {
            fields.Add(new("reason", skip));
        }

        _log.Write(isUnsafe ? LogLevel.Warn : LogLevel.Info, "queue.type", fields);
    }

    private void Finish(SendAllStopReason reason, string? detail)
    {
        if (IsFinished)
        {
            return;
        }

        if (State == SendAllState.Listing)
        {
            // ListStock threw or was aborted; queue.start still opens every queue in the log.
            LogStart(isListed: false);
        }

        State = SendAllState.Finished;
        Report = new SendAllReport(reason, _types.ToArray(), detail);
        _log.Write(reason == SendAllStopReason.SafetyStop ? LogLevel.Warn : LogLevel.Info, "queue.stop",
            new KeyValuePair<string, object?>[]
            {
                new("reason", reason),
                new("types_done", Report.TypesDone),
                new("moved", Report.Moved),
                new("stranded", Report.Stranded),
                new("detail", detail),
            });
    }

    // TransferRunner (unchanged by card 75) publishes its S0 only on transfer.start; this keeps it for the type's report.
    private sealed class RunStartLog : IModLog
    {
        private readonly IModLog _inner;

        public RunStartLog(IModLog inner) => _inner = inner;

        public int? SourceStart { get; private set; }

        public void Write(LogLevel level, string eventName, IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            if (eventName == "transfer.start")
            {
                foreach (KeyValuePair<string, object?> field in fields)
                {
                    if (field.Key == "s0" && field.Value is int s0)
                    {
                        SourceStart = s0;
                    }
                }
            }

            _inner.Write(level, eventName, fields);
        }

        public void WriteException(string eventName, Exception exception) => _inner.WriteException(eventName, exception);
    }
}
