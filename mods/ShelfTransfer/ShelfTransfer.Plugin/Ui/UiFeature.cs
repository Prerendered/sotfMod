// purpose: installs the UI feature (hint, overlay, label editor) from the composition root; empty until those stories land
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
