// purpose: BepInEx entry point and composition root; opens the mod log, binds config, installs every feature
// owns:    ShelfTransferPlugin
// deps:    PluginInfo, ModConfig, FileModLog, TransferFeature, LabelsFeature, UiFeature, ShelfTransfer.Core (IModLog, ShelfKey — touched so the Core DLL is resolved at load)
// caution: Load() runs before any scene; no UnityEngine object access here beyond AddComponent

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using BepInEx;
using BepInEx.Unity.IL2CPP;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Labels;
using ShelfTransfer.Plugin.Logging;
using ShelfTransfer.Plugin.Runtime;
using ShelfTransfer.Plugin.Ui;

namespace ShelfTransfer.Plugin;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class ShelfTransferPlugin : BasePlugin
{
    private const string _logFolderName = "ShelfTransfer";
    private const string _errorProbePattern = "error-probe*";

    public override void Load()
    {
        string coreVersion = typeof(ShelfKey).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        Log.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded (Core {coreVersion})");

        string logDirectory = Path.Combine(Paths.BepInExRootPath, _logFolderName);
        IModLog modLog = new FileModLog(logDirectory, Log);
        ModConfig config = new(Config);
        modLog.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[]
        {
            new("version", PluginInfo.Version),
            new("core", coreVersion),
            new("bepinex", GetBepInExVersion()),
            new("transfer_key", config.TransferKey.Value),
            new("label_key", config.LabelKey.Value),
            new("discovery_key", config.DiscoveryKey.Value),
            new("items_per_frame", config.ItemsPerFrame.Value),
            new("discovery", config.DiscoveryEnabled.Value),
            new("fault_inject", config.InjectFaultAfterItems.Value),
        });

        TransferFeature.Install(modLog);
        LabelsFeature.Install(modLog);
        UiFeature.Install(modLog);

        RunErrorProbeIfRequested(logDirectory, modLog);
    }

    // Paths.BepInExVersion is parsed from this attribute; read directly so the plugin needs no
    // SemanticVersioning reference. Build metadata is cut so the line reads "6.0.0-be.755".
    private static string GetBepInExVersion()
    {
        string? informational = typeof(Paths).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "unknown";
        }

        int buildStart = informational.IndexOf('+', StringComparison.Ordinal);
        return buildStart < 0 ? informational : informational[..buildStart];
    }

    // D5 evidence that an exception reaches the log as an error line and the game keeps running.
    // Triggered by an "error-probe*" file in the log folder; a file, not config, so it needs no ModConfig entry.
    private static void RunErrorProbeIfRequested(string logDirectory, IModLog modLog)
    {
        if (!IsErrorProbeRequested(logDirectory, modLog))
        {
            return;
        }

        try
        {
            ThrowProbeException();
        }
        catch (InvalidOperationException ex)
        {
            modLog.WriteException("debug.probe", ex);
        }
    }

    private static bool IsErrorProbeRequested(string logDirectory, IModLog modLog)
    {
        try
        {
            return Directory.Exists(logDirectory) && Directory.GetFiles(logDirectory, _errorProbePattern).Length > 0;
        }
        catch (Exception ex)
        {
            // Catch-all: GetFiles has several documented exception types, and a debug check must never abort Load().
            modLog.Write(LogLevel.Warn, "debug.probe", new KeyValuePair<string, object?>[]
            {
                new("outcome", "skipped"),
                new("type", ex.GetType().Name),
                new("message", ex.Message),
            });
            return false;
        }
    }

    private static void ThrowProbeException() =>
        throw new InvalidOperationException("deliberate exception from the error probe");
}
