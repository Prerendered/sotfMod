// purpose: pins AimChangeDebouncer (card 8): one aim.change per settled change, silent through flicker and no-target reason flips
// owns:    AimChangeDebouncerTests
// deps:    AimChangeDebouncer, AimTarget, ShelfRef, SlotRef, ItemId

using ShelfTransfer.Core.Gateway;

using Xunit;

namespace ShelfTransfer.Core.Tests.Gateway;

public sealed class AimChangeDebouncerTests
{
    private const double _settle = AimChangeDebouncer.SettleSeconds;

    private static AimTarget Shelf(string key = "A", int? slot = 0, int item = 312, int count = 6) =>
        new(new ShelfRef(key, "StorageShelf", default), new SlotRef(slot), new ItemId(item), count);

    // Feeds the same state every 0.05 s from `from` until `to` inclusive; returns number of true results.
    private static int Hold(AimChangeDebouncer d, AimTarget? t, double from, double to)
    {
        int logs = 0;
        for (double now = from; now <= to + 1e-9; now += 0.05)
        {
            if (d.ShouldLog(t, now)) { logs++; }
        }
        return logs;
    }

    [Fact]
    public void Startup_into_none_logs_once_after_window()
    {
        var d = new AimChangeDebouncer();
        // Start at 0.0 so now - start is exactly _settle (0.2 is not exact in binary at larger offsets).
        Assert.False(d.ShouldLog(null, 0.0));
        Assert.False(d.ShouldLog(null, 0.1));
        Assert.True(d.ShouldLog(null, _settle));
        Assert.False(d.ShouldLog(null, 11.0));
    }

    [Fact]
    public void None_to_none_with_different_reasons_is_silent()
    {
        // The reason is not an input: every no-target frame is the same null, so alternating reasons are one state.
        var d = new AimChangeDebouncer();
        int logs = 0;
        for (int i = 0; i < 200; i++)
        {
            if (d.ShouldLog(null, i * 0.013)) { logs++; }
        }
        Assert.Equal(1, logs);
    }

    [Fact]
    public void Target_to_none_logs_once_after_window()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf(), 0, 1);
        Assert.False(d.ShouldLog(null, 2.0));
        Assert.False(d.ShouldLog(null, 2.0 + _settle - 0.001));
        Assert.True(d.ShouldLog(null, 2.0 + _settle));
        Assert.False(d.ShouldLog(null, 2.5));
    }

    [Fact]
    public void Flicker_shorter_than_window_is_silent()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf(), 0, 1);
        Assert.False(d.ShouldLog(null, 1.1));
        Assert.False(d.ShouldLog(null, 1.113));
        Assert.False(d.ShouldLog(Shelf(), 1.126));
        Assert.Equal(0, Hold(d, Shelf(), 1.14, 3));
    }

    [Fact]
    public void A_to_B_to_A_within_window_logs_nothing()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), 0, 1);
        Assert.False(d.ShouldLog(Shelf("B"), 1.05));
        Assert.False(d.ShouldLog(Shelf("B"), 1.15));
        Assert.False(d.ShouldLog(Shelf("A"), 1.2));
        Assert.Equal(0, Hold(d, Shelf("A"), 1.25, 3));
    }

    [Fact]
    public void Held_change_logs_exactly_once()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), 0, 1);
        Assert.Equal(1, Hold(d, Shelf("B"), 2, 5));
    }

    [Fact]
    public void Count_change_on_same_slot_logs_once_when_settled()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf(count: 6), 0, 1);
        Assert.Equal(1, Hold(d, Shelf(count: 5), 2, 4));
    }

    [Fact]
    public void Repeated_identical_frames_log_once()
    {
        var d = new AimChangeDebouncer();
        Assert.Equal(1, Hold(d, Shelf(), 0, 10));
    }

    [Fact]
    public void Equal_but_distinct_instances_are_the_same_state()
    {
        var d = new AimChangeDebouncer();
        int logs = 0;
        for (int i = 0; i < 100; i++)
        {
            if (d.ShouldLog(Shelf(), i * 0.02)) { logs++; }
        }
        Assert.Equal(1, logs);
    }

    [Fact]
    public void Exactly_window_logs_and_just_under_does_not()
    {
        var d = new AimChangeDebouncer();
        Assert.False(d.ShouldLog(Shelf(), 0.0));
        Assert.False(d.ShouldLog(Shelf(), 0.1999999));
        Assert.True(d.ShouldLog(Shelf(), _settle));
    }

    [Fact]
    public void Long_gap_then_same_state_does_not_relog()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf(), 0, 1);
        Assert.False(d.ShouldLog(Shelf(), 1000.0));
        Assert.False(d.ShouldLog(Shelf(), 1000.5));
    }

    [Fact]
    public void Long_gap_then_new_state_needs_a_fresh_window()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), 0, 1);
        Assert.False(d.ShouldLog(Shelf("B"), 1000.0));
        Assert.True(d.ShouldLog(Shelf("B"), 1000.0 + _settle));
    }

    [Fact]
    public void None_shelf_none_sequence_logs_each_settled_change()
    {
        var d = new AimChangeDebouncer();
        int logs = Hold(d, null, 0, 1);
        logs += Hold(d, Shelf(), 1.05, 2);
        logs += Hold(d, null, 2.05, 3);
        Assert.Equal(3, logs);
    }

    [Fact]
    public void Return_to_the_logged_state_after_a_flicker_is_silent()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), 0, 1);
        d.ShouldLog(null, 1.05);
        Assert.False(d.ShouldLog(Shelf("A"), 1.10));
        Assert.False(d.ShouldLog(Shelf("A"), 9.0));
    }

    [Fact]
    public void Pending_state_settles_from_when_it_first_appeared()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), -2, -1);
        Assert.False(d.ShouldLog(Shelf("B"), 0.0));
        Assert.False(d.ShouldLog(Shelf("B"), 0.1));
        Assert.True(d.ShouldLog(Shelf("B"), _settle));
    }

    [Fact]
    public void Same_shelf_different_slot_item_or_null_slot_are_distinct_states()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf(slot: 0), 0, 1);
        Assert.Equal(1, Hold(d, Shelf(slot: 1), 2, 3));
        Assert.Equal(1, Hold(d, Shelf(slot: 1, item: 999), 4, 5));
        Assert.Equal(1, Hold(d, Shelf(slot: null, item: 999), 6, 7));
        Assert.Equal(1, Hold(d, Shelf(slot: 0, item: 999), 8, 9));
    }

    [Fact]
    public void Empty_slot_on_a_shelf_is_distinct_from_no_target()
    {
        var d = new AimChangeDebouncer();
        Hold(d, null, 0, 1);
        Assert.Equal(1, Hold(d, Shelf(item: 0, count: 0), 2, 3));
        Assert.Equal(1, Hold(d, null, 4, 5));
    }

    [Fact]
    public void Shelf_identity_is_the_key_alone_not_position_or_type()
    {
        var d = new AimChangeDebouncer();
        var a = new AimTarget(new ShelfRef("K", "TypeX", default), new SlotRef(0), new ItemId(1), 1);
        var b = new AimTarget(new ShelfRef("K", "TypeY", new WorldPosition(1, 2, 3)), new SlotRef(0), new ItemId(1), 1);
        Assert.Equal(1, Hold(d, a, 0, 1));
        Assert.Equal(0, Hold(d, b, 2, 3));
    }

    [Fact]
    public void Clock_going_backwards_does_not_log_early_or_throw()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), 100, 101);
        Assert.False(d.ShouldLog(Shelf("B"), 50.0));
        Assert.False(d.ShouldLog(Shelf("B"), 50.1));
        Assert.True(d.ShouldLog(Shelf("B"), 50.0 + _settle));
    }

    [Fact]
    public void Clock_reset_to_earlier_value_with_unchanged_state_stays_silent()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf(), 100, 101);
        Assert.Equal(0, Hold(d, Shelf(), 0, 5));
    }

    [Fact]
    public void Huge_clock_values_still_debounce()
    {
        var d = new AimChangeDebouncer();
        double big = 1e15;
        Assert.False(d.ShouldLog(Shelf(), big));
        Assert.True(d.ShouldLog(Shelf(), big + 1.0));
        Assert.False(d.ShouldLog(Shelf(), big + 2.0));
    }

    [Fact]
    public void Infinite_clock_does_not_throw()
    {
        var d = new AimChangeDebouncer();
        d.ShouldLog(Shelf(), 0);
        var ex = Record.Exception(() => d.ShouldLog(Shelf("B"), double.PositiveInfinity));
        Assert.Null(ex);
    }

    [Fact]
    public void Nan_clock_current_behavior_logs_first_observation_immediately()
    {
        // Characterization, not a requirement: NaN fails the '<' compare so the window is skipped (candidate card).
        // Unreachable with a real Unity clock; flip to Assert.False if the debouncer is hardened.
        var d = new AimChangeDebouncer();
        Assert.True(d.ShouldLog(Shelf(), double.NaN));
    }

    [Fact]
    public void Nan_clock_mid_stream_does_not_log_per_frame()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), 0, 1);
        int logs = 0;
        for (int i = 0; i < 50; i++)
        {
            if (d.ShouldLog(Shelf("B"), double.NaN)) { logs++; }
        }
        Assert.True(logs <= 1, $"NaN clock logged {logs} times");
    }

    [Fact]
    public void Fast_flicker_every_frame_never_logs()
    {
        var d = new AimChangeDebouncer();
        Hold(d, Shelf("A"), 0, 1);
        int logs = 0;
        for (int i = 0; i < 1000; i++)
        {
            var t = i % 2 == 0 ? Shelf("B") : null;
            if (d.ShouldLog(t, 1.0 + i * 0.013)) { logs++; }
        }
        Assert.Equal(0, logs);
    }

    [Fact]
    public void Log_count_over_a_walk_is_bounded_by_settled_changes()
    {
        var d = new AimChangeDebouncer();
        int logs = 0;
        double now = 0;
        string[] keys = { "A", "B", "C", "D" };
        for (int seg = 0; seg < 4; seg++)
        {
            for (int f = 0; f < 100; f++, now += 0.016)
            {
                if (d.ShouldLog(Shelf(keys[seg]), now)) { logs++; }
            }
        }
        Assert.Equal(4, logs);
    }
}
