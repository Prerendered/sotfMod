// purpose: installs the transfer feature from the composition root: registry, gateway, aim watcher, driver and console command
// owns:    TransferFeature
// deps:    ModConfig, ShelfRegistry, InteropShelfGateway, AimWatcher, TransferDriver, TransferCommand, ShelfTransfer.Core.Logging (IModLog, LogLevel), BepInEx IL2CPPChainloader

using System.Collections.Generic;

using BepInEx.Unity.IL2CPP;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Debug;
using ShelfTransfer.Plugin.Game;

namespace ShelfTransfer.Plugin.Runtime;

public static class TransferFeature
{
    public static void Install(IModLog log, ModConfig config)
    {
        log.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[] { new("feature", "transfer") });

        // What BasePlugin.AddComponent calls; registers the injected type and adds it to BepInEx's persistent manager object.
        ShelfRegistry registry = new(log);
        InteropShelfGateway gateway = new(registry, log);
        AimWatcher aimWatcher = IL2CPPChainloader.AddUnityComponent<AimWatcher>();
        aimWatcher.Init(log, registry, gateway);
        TransferDriver driver = IL2CPPChainloader.AddUnityComponent<TransferDriver>();
        driver.Init(log, config, gateway);
        IL2CPPChainloader.AddUnityComponent<TransferCommand>().Init(log, registry, gateway, aimWatcher, driver);
    }
}
