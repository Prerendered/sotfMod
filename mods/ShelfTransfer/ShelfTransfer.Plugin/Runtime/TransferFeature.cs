// purpose: installs the transfer feature from the composition root: registry, gateway, aim watcher, driver, console command,
//          the transfer prompt and the transfer overlay
// owns:    TransferFeature
// deps:    ModConfig, ShelfRegistry, InteropShelfGateway, AimWatcher, TransferDriver, TransferCommand, TransferPromptUi, OverlayUi, LabelsFeature (Store, read-only), ShelfTransfer.Core.Logging (IModLog, LogLevel), BepInEx IL2CPPChainloader

using System.Collections.Generic;

using BepInEx.Unity.IL2CPP;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Commands;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Game;
using ShelfTransfer.Plugin.Labels;
using ShelfTransfer.Plugin.Ui;

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
        // The console registration stays even though the console is unreachable (session 5); the prompt is the way in.
        TransferCommand command = IL2CPPChainloader.AddUnityComponent<TransferCommand>();
        // Read fresh on every command: the store is replaced on each world change. Entries is a read-only view (I5).
        command.Init(log, registry, gateway, aimWatcher, driver, () => LabelsFeature.Store?.Entries);
        IL2CPPChainloader.AddUnityComponent<TransferPromptUi>().Init(log, config, command);
        IL2CPPChainloader.AddUnityComponent<OverlayUi>().Init(log, config, command, gateway, registry.List);
    }
}
