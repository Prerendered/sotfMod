// purpose: installs the labels feature (label store, JSON file) from the composition root; empty until those stories land
// owns:    LabelsFeature
// deps:    ShelfTransfer.Core.Logging (IModLog, LogLevel)

using System.Collections.Generic;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Plugin.Labels;

public static class LabelsFeature
{
    public static void Install(IModLog log) =>
        log.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[] { new("feature", "labels") });
}
