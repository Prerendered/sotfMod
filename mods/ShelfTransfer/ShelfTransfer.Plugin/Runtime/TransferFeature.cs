// purpose: installs the transfer feature from the composition root: registry, gateway and the per-frame aim watcher
// owns:    TransferFeature
// deps:    ModConfig, ShelfRegistry, InteropShelfGateway, AimWatcher, ShelfTransfer.Core.Logging (IModLog, LogLevel), BepInEx IL2CPPChainloader

using System.Collections.Generic;

using BepInEx.Unity.IL2CPP;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Game;

namespace ShelfTransfer.Plugin.Runtime;

public static class TransferFeature
{
    public static void Install(IModLog log, ModConfig config)
    {
        log.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[] { new("feature", "transfer") });

        // What BasePlugin.AddComponent calls; registers the injected type and adds it to BepInEx's persistent manager object.
        IL2CPPChainloader.AddUnityComponent<AimWatcher>().Init(log, new InteropShelfGateway(new ShelfRegistry(log), log));
    }
}
