// purpose: marks the UI feature in plugin.load; the overlay and typed prompt are installed by TransferFeature, which owns
//          the command and gateway they read, and the label editor by LabelsFeature
// owns:    UiFeature
// deps:    ModConfig, ShelfTransfer.Core.Logging (IModLog, LogLevel)

using System.Collections.Generic;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Config;

namespace ShelfTransfer.Plugin.Ui;

public static class UiFeature
{
    public static void Install(IModLog log, ModConfig config) =>
        log.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[] { new("feature", "ui") });
}
