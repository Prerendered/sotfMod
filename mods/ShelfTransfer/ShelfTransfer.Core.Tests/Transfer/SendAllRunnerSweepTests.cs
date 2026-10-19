// purpose: invariant sweep over seeded random worlds for the Send all queue (card 75): several types, units 1/10/50, target capacities and
//          one injected fault at a random gateway call; asserts I1 (one chunk in the backpack, no take while items are queued),
//          I3 (nothing after a stop or after a non-clean run) and the accounting
// owns:    SendAllRunnerSweepTests
// deps:    SendAllRunner, FakeShelfGateway, FakeModLog
// caution: faults fire from OnCall, which runs before the call acts: throws, drifts and Abort land on read calls only (an Abort
//          while a take is on the stack would be a mutation after the stop, which the real driver cannot do)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class SendAllRunnerSweepTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly int[] _units = { 1, 10, 50 };

    private enum Fault
    {
        None,
        Throw,
        ThrowNoAbort,
        NestedAbort,
        Cancel,
        TargetDrift,
        SourceDrift,
        ShelfGone,
        InventoryDrop,
        TakeRefused,
        StoreRefused,
    }

    private sealed class SweepFault : Exception
    {
        public SweepFault() : base("sweep fault")
        {
        }
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 600).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Queue_RandomWorldWithOneInjectedFault_KeepsTheSafetyInvariants(int seed)
    {
        Fault fault = (Fault)(seed % Enum.GetValues<Fault>().Length);
        int calls = Run(seed, Fault.None, 0).CallCount;
        int at = calls == 0 ? 1 : 1 + new Random(seed * 7919 + 13).Next(calls);

        Outcome run = Run(seed, fault, at);

        Assert.True(run.Violations.Count == 0, $"seed {seed} fault {fault}@{at}: {string.Join("; ", run.Violations)}");
    }

    // The sweep must reach every ending, or its green says nothing: each stop kind, multi-type queues, skips, strandings.
    [Fact]
    public void Seeds_CoverEveryEndingAndTheFaultsActuallyFire()
    {
        List<SendAllReport> reports = new();
        foreach (object[] seed in Seeds())
        {
            int s = (int)seed[0];
            Fault fault = (Fault)(s % Enum.GetValues<Fault>().Length);
            int calls = Run(s, Fault.None, 0).CallCount;
            int at = calls == 0 ? 1 : 1 + new Random(s * 7919 + 13).Next(calls);
            reports.Add(Assert.IsType<SendAllReport>(Run(s, fault, at).Report));
        }

        Assert.True(reports.Count(r => r.Reason == SendAllStopReason.Completed && r.Types.Count >= 3) >= 30);
        Assert.True(reports.Count(r => r.Reason == SendAllStopReason.SafetyStop && r.Types.Any(t => t.Outcome == SendAllTypeOutcome.NotStarted)) >= 60);
        Assert.True(reports.Count(r => r.Reason == SendAllStopReason.Cancelled) >= 20);
        Assert.True(reports.Count(r => r.Types.Any(t => t.Outcome == SendAllTypeOutcome.Skipped)) >= 30);
        Assert.True(reports.Count(r => r.Types.Any(t => t.Outcome == SendAllTypeOutcome.Stranded)) >= 5);
        Assert.True(reports.Count(r => r.Types.Any(t => t.Outcome == SendAllTypeOutcome.Moved && t.Moved >= 50)) >= 10);
    }

    private sealed class Outcome
    {
        public int CallCount { get; set; }

        public SendAllReport? Report { get; set; }

        public List<string> Violations { get; } = new();
    }

    private static Outcome Run(int seed, Fault fault, int faultAt)
    {
        Random random = new(seed);
        FakeShelfGateway gateway = new();
        FakeShelf source = gateway.Add(_src, 0);
        FakeShelf target = gateway.Add(_dst, 0);
        FakeModLog log = new();
        Dictionary<int, int> unitOf = new();
        int typeCount = random.Next(0, 5);
        int slotIndex = 1;
        for (int type = 0; type < typeCount; type++)
        {
            ItemId item = new(100 + type);
            int unit = _units[random.Next(_units.Length)];
            gateway.ItemUnits[item] = unit;
            unitOf[item.Value] = unit;
            int stock = random.Next(0, unit * 3 + 7);
            for (int slot = random.Next(1, 4); slot > 0 && stock > 0; slot--)
            {
                int part = slot == 1 ? stock : random.Next(0, stock + 1);
                source.Slots.Add((new SlotRef(slotIndex++), item, part));
                stock -= part;
            }
        }

        target.Capacity = random.Next(3) == 0 ? random.Next(0, 160) : int.MaxValue;
        target.CanHold = random.Next(12) != 0;
        int confirmTicks = 0;
        int startItems = source.Slots.Sum(s => s.Count);
        SendAllRunner queue = new(gateway, log, _src, _dst, confirmTicks);
        Outcome outcome = new();
        int callCount = 0;
        int finishedCalls = -1;
        bool isFired = false;
        int currentUnit = 0;

        if (fault == Fault.TakeRefused)
        {
            gateway.TakeBehavior = n => n == Math.Max(1, faultAt % 7) ? GatewayOutcome.Refused("sweep") : null;
        }

        if (fault == Fault.StoreRefused)
        {
            gateway.StoreBehavior = n => n == Math.Max(1, faultAt % 23) ? GatewayOutcome.Refused("sweep") : null;
        }

        gateway.OnCall = call =>
        {
            callCount++;
            if (queue.State == SendAllState.Finished && finishedCalls < 0)
            {
                finishedCalls = callCount;
                outcome.Violations.Add($"gateway call {call} after the queue finished");
            }

            LogEntry? start = log.Named("transfer.start").LastOrDefault();
            if (start is not null)
            {
                currentUnit = unitOf[int.Parse(start.Text("item"), CultureInfo.InvariantCulture)];
            }

            if (gateway.Inventory > currentUnit)
            {
                outcome.Violations.Add($"backpack {gateway.Inventory} exceeds one chunk of {currentUnit} before {call}");
            }

            if (call == "TakeOne(src)" && gateway.Inventory != 0)
            {
                outcome.Violations.Add($"TakeOne while {gateway.Inventory} items are queued");
            }

            if (call == "TakeOne(src)" && log.Named("queue.type").Any(e => e.Text("outcome") is "Stopped" or "Stranded" or "Cancelled"))
            {
                outcome.Violations.Add("TakeOne after a non-clean run");
            }

            if (fault != Fault.None && !isFired && callCount >= faultAt && !call.StartsWith("TakeOne", StringComparison.Ordinal)
                && !call.StartsWith("StoreOne", StringComparison.Ordinal) && fault is not (Fault.TakeRefused or Fault.StoreRefused))
            {
                isFired = true;
                Inject(fault, queue, gateway, source, target);
            }
        };

        for (int tick = 0; tick < 3000 && queue.State != SendAllState.Finished; tick++)
        {
            try
            {
                queue.Tick(random.Next(1, 4));
            }
            catch (SweepFault) when (fault == Fault.Throw)
            {
                queue.Abort(StopReason.GatewayError, "sweep fault");
            }
            catch (SweepFault)
            {
                // ThrowNoAbort: the next Tick must end the queue itself.
            }
        }

        if (queue.State != SendAllState.Finished)
        {
            outcome.Violations.Add("queue never finished");
            outcome.CallCount = callCount;
            return outcome;
        }

        int mutations = gateway.Mutations;
        int total = gateway.Calls.Count;
        queue.Tick(1);
        queue.Abort(StopReason.GatewayError, "late");
        queue.RequestCancel();
        queue.Tick(2);
        if (gateway.Calls.Count != total || gateway.Mutations != mutations)
        {
            outcome.Violations.Add("gateway call after Finished");
        }

        Check(outcome, queue, log, gateway, source, target, fault, startItems);
        outcome.CallCount = callCount;
        outcome.Report = queue.Report;
        return outcome;
    }

    private static void Inject(Fault fault, SendAllRunner queue, FakeShelfGateway gateway, FakeShelf source, FakeShelf target)
    {
        switch (fault)
        {
            case Fault.Throw:
            case Fault.ThrowNoAbort:
                throw new SweepFault();
            case Fault.NestedAbort:
                queue.Abort(StopReason.GatewayError, "sweep abort");
                break;
            case Fault.Cancel:
                queue.RequestCancel();
                break;
            case Fault.TargetDrift:
                target.Count++;
                break;
            case Fault.SourceDrift:
                int at = source.Slots.FindIndex(s => s.Count > 0);
                if (at >= 0)
                {
                    source.Slots[at] = (source.Slots[at].Slot, source.Slots[at].Item, source.Slots[at].Count - 1);
                }

                break;
            case Fault.ShelfGone:
                source.IsAlive = false;
                break;
            case Fault.InventoryDrop:
                if (gateway.Inventory > 0)
                {
                    gateway.Inventory--;
                }

                break;
        }
    }

    private static void Check(Outcome outcome, SendAllRunner queue, FakeModLog log, FakeShelfGateway gateway, FakeShelf source,
        FakeShelf target, Fault fault, int startItems)
    {
        SendAllReport report = Assert.IsType<SendAllReport>(queue.Report);
        bool isDrift = fault is Fault.TargetDrift or Fault.SourceDrift or Fault.ShelfGone or Fault.InventoryDrop;

        // The queue never continues past a non-clean type, and Completed means every type was reached and clean.
        bool isBroken = false;
        foreach (SendAllTypeResult type in report.Types)
        {
            if (isBroken && type.Outcome != SendAllTypeOutcome.NotStarted)
            {
                outcome.Violations.Add($"type {type.Item.Value} {type.Outcome} after a non-clean type");
            }

            if (type.Outcome is SendAllTypeOutcome.Stopped or SendAllTypeOutcome.Stranded or SendAllTypeOutcome.Cancelled)
            {
                isBroken = true;
            }

            if (type.Outcome == SendAllTypeOutcome.NotStarted && type.Run is not null)
            {
                outcome.Violations.Add("NotStarted type carries a run");
            }

            if (type.Outcome == SendAllTypeOutcome.Skipped && (type.Moved != 0 || type.SkipReason is null))
            {
                outcome.Violations.Add("Skipped type moved items or has no reason");
            }
        }

        if (report.Reason == SendAllStopReason.Completed && report.Types.Any(t => t.Outcome is not (SendAllTypeOutcome.Moved or SendAllTypeOutcome.Skipped)))
        {
            outcome.Violations.Add("Completed with an unfinished or unsafe type");
        }

        if (report.Types.Any(t => t.Outcome == SendAllTypeOutcome.Stranded) && report.Reason != SendAllStopReason.SafetyStop)
        {
            outcome.Violations.Add("stranded items without a SafetyStop");
        }

        if (report.Types.Any(t => t.Outcome == SendAllTypeOutcome.Stopped) && report.Reason != SendAllStopReason.SafetyStop)
        {
            outcome.Violations.Add("unsafe type without a SafetyStop");
        }

        if (report.Moved != queue.Moved)
        {
            outcome.Violations.Add($"Report.Moved {report.Moved} != Queue.Moved {queue.Moved}");
        }

        if (log.Named("queue.stop").Count() != 1 || log.Entries[^1].Event != "queue.stop")
        {
            outcome.Violations.Add("queue.stop is not the single last queue line");
        }

        if (log.Named("queue.start").Count() != 1 || log.Entries[0].Event != "queue.start")
        {
            outcome.Violations.Add("queue.start is not the single first line");
        }

        if (log.Named("queue.type").Count() != report.TypesDone)
        {
            outcome.Violations.Add("queue.type lines != TypesDone");
        }

        if (isDrift)
        {
            return;
        }

        // Nothing outside the fake touched the items, so they are all accounted for: on the shelf, in the target or stranded.
        int held = source.Slots.Sum(s => s.Count) + target.Count + gateway.Inventory;
        if (held != startItems)
        {
            outcome.Violations.Add($"items not conserved: started {startItems} now {held}");
        }

        if (report.Stranded != gateway.Inventory)
        {
            outcome.Violations.Add($"reported stranded {report.Stranded} != backpack {gateway.Inventory}");
        }

        foreach (SendAllTypeResult type in report.Types.Where(t => t.SourceStart is not null))
        {
            if (type.Moved + type.Remaining + type.Stranded != type.SourceStart)
            {
                outcome.Violations.Add($"type {type.Item.Value}: moved {type.Moved} + remaining {type.Remaining} + stranded {type.Stranded} != s0 {type.SourceStart}");
            }
        }

        if (fault is Fault.None or Fault.Cancel && report.Reason == SendAllStopReason.Completed && report.Moved != target.Count)
        {
            outcome.Violations.Add($"moved {report.Moved} != target {target.Count}");
        }
    }
}
