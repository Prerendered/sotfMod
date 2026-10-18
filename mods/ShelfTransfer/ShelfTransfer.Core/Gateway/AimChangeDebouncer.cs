// purpose: decides when the aim watcher writes an aim.change line: once per settled change, never per frame
// owns:    AimChangeDebouncer, AimChangeDebouncer.SettleSeconds
// deps:    AimTarget
// caution: debounces the log only; the live per-frame aim read must not go through this

namespace ShelfTransfer.Core.Gateway;

public sealed class AimChangeDebouncer
{
    // Longer than hook-to-gap flicker while walking (one or two 13 ms frames in the card 8 log), short enough to feel live.
    public const double SettleSeconds = 0.2;

    private bool _hasLogged;
    private AimKey _logged;
    private bool _hasPending;
    private AimKey _pending;
    private double _pendingSince;

    /// <summary>
    /// Called every frame with the aimed slot (null = no target, whatever the reason) and an unscaled clock in seconds.
    /// True once a state differing from the last logged one has held for <see cref="SettleSeconds"/>.
    /// </summary>
    public bool ShouldLog(AimTarget? aimed, double now)
    {
        AimKey observed = aimed is null
            ? default
            : new AimKey(aimed.Shelf.Key, aimed.Slot.Index, aimed.Item.Value, aimed.Count);
        if (!_hasPending || observed != _pending)
        {
            _hasPending = true;
            _pending = observed;
            _pendingSince = now;
        }

        if (_hasLogged && _pending == _logged)
        {
            return false;
        }

        if (now - _pendingSince < SettleSeconds)
        {
            return false;
        }

        _hasLogged = true;
        _logged = _pending;
        return true;
    }

    // Shelf identity is the key alone (D2); default (null key) is the single no-target state. A count change counts.
    private readonly record struct AimKey(string? ShelfKey, int? Slot, int Item, int Count);
}
