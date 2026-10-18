// purpose: installs the UI feature (hint, overlay, label editor) from the composition root; empty until those stories land
// owns:    UiFeature
// deps:    ShelfTransfer.Core.Logging (IModLog, LogLevel)

using System.Collections.Generic;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Plugin.Ui;

public static class UiFeature
{
    public static void Install(IModLog log) =>
        log.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[] { new("feature", "ui") });
}
