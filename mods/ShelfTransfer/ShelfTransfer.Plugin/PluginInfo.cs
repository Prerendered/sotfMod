// purpose: the plugin's BepInEx identity in one place; Version must match the csproj <Version>
// owns:    PluginInfo
// deps:    none

namespace ShelfTransfer.Plugin;

internal static class PluginInfo
{
    public const string Guid = "com.prerendered.sotf.shelftransfer";
    public const string Name = "ShelfTransfer";
    public const string Version = "0.1.0";
}
