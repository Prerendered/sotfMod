// purpose: severity for the mod log (D5); mirrors BepInEx levels Core must not reference
// owns:    LogLevel
// deps:    none

namespace ShelfTransfer.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}
