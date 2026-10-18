// purpose: BepInEx entry point; proves both DLLs load, then (in later stories) wires config, log, gateway and UI
// owns:    ShelfTransferPlugin
// deps:    PluginInfo, ShelfTransfer.Core (ShelfKey — touched so the Core DLL is resolved at load)
// caution: Load() runs before any scene; no UnityEngine object access here beyond AddComponent

using BepInEx;
using BepInEx.Unity.IL2CPP;

using ShelfTransfer.Core.Shelves;

namespace ShelfTransfer.Plugin;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class ShelfTransferPlugin : BasePlugin
{
    public override void Load()
    {
        string coreVersion = typeof(ShelfKey).Assembly.GetName().Version?.ToString() ?? "unknown";
        Log.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded (Core {coreVersion})");
    }
}
