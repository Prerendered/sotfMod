// purpose: result of one vanilla take or store call, as a value — the runner never catches exceptions from the gateway
// owns:    GatewayOutcome, GatewayStatus
// deps:    none

namespace ShelfTransfer.Core.Gateway;

public enum GatewayStatus
{
    Ok,
    /// <summary>The game declined the call (full target, carry cap, wrong item type). Nothing moved.</summary>
    Refused,
    /// <summary>The call threw or the object was gone. Counts must be re-read before trusting anything.</summary>
    Failed,
}

public sealed record GatewayOutcome(GatewayStatus Status, string? Detail = null)
{
    public static readonly GatewayOutcome Ok = new(GatewayStatus.Ok);

    public static GatewayOutcome Refused(string detail) => new(GatewayStatus.Refused, detail);

    public static GatewayOutcome Failed(string detail) => new(GatewayStatus.Failed, detail);

    public bool IsOk => Status == GatewayStatus.Ok;
}
