// purpose: installs the transfer feature (driver, gateway) from the composition root; empty until those stories land
// owns:    TransferFeature
// deps:    ModConfig, ShelfTransfer.Core.Logging (IModLog, LogLevel)

using System.Collections.Generic;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Config;

namespace ShelfTransfer.Plugin.Runtime;

public static class TransferFeature
{
    public static void Install(IModLog log, ModConfig config) =>
        log.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[] { new("feature", "transfer") });
}
