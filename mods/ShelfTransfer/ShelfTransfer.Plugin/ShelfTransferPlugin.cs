// purpose: BepInEx entry point; opens the mod log and writes the load line, later stories wire config, gateway and UI
// owns:    ShelfTransferPlugin
// deps:    PluginInfo, FileModLog, ShelfTransfer.Core (IModLog, ShelfKey — touched so the Core DLL is resolved at load)
// caution: Load() runs before any scene; no UnityEngine object access here beyond AddComponent

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using BepInEx;
using BepInEx.Unity.IL2CPP;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Plugin.Logging;

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
        modLog.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[]
        {
            new("version", PluginInfo.Version),
            new("core", coreVersion),
            new("bepinex", GetBepInExVersion()),
        });

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
        try
        {
            if (!Directory.Exists(logDirectory) || Directory.GetFiles(logDirectory, _errorProbePattern).Length == 0)
            {
                return;
            }

            ThrowProbeException();
        }
        catch (InvalidOperationException ex)
        {
            modLog.WriteException("debug.probe", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            modLog.Write(LogLevel.Warn, "debug.probe", new KeyValuePair<string, object?>[]
            {
                new("outcome", "skipped"),
                new("type", ex.GetType().Name),
                new("message", ex.Message),
            });
        }
    }

    private static void ThrowProbeException() =>
        throw new InvalidOperationException("deliberate exception from the error probe");
}
