// purpose: turns any world identifier into a safe label file name stem (F3: sanitised to [A-Za-z0-9_-])
// owns:    WorldId.Sanitise, WorldId.Fallback, WorldId.MaxLength
// deps:    none
// caution: the result is a path component; it must never be able to contain a separator, "..", or a device name

using System;
using System.Text;

namespace ShelfTransfer.Core.Labels;

public static class WorldId
{
    public const string Fallback = "unknown";

    /// <summary>Keeps BepInEx/config/ShelfTransfer/&lt;id&gt;.json well inside Windows MAX_PATH.</summary>
    public const int MaxLength = 64;

    // Windows treats these as devices even with an extension ("CON.json").
    private static readonly string[] _reservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Each char outside [A-Za-z0-9_-] becomes '_'; capped at <see cref="MaxLength"/>; blank input gives <see cref="Fallback"/>.</summary>
    public static string Sanitise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fallback;
        }

        string trimmed = raw.Trim();
        var builder = new StringBuilder(trimmed.Length + 1);
        foreach (char c in trimmed)
        {
            builder.Append(IsAllowed(c) ? c : '_');
        }

        string sanitised = builder.ToString();
        if (Array.Exists(_reservedNames, name => string.Equals(name, sanitised, StringComparison.OrdinalIgnoreCase)))
        {
            sanitised = "_" + sanitised;
        }

        // Capped last so the prefix can never push the result past MaxLength.
        return sanitised.Length <= MaxLength ? sanitised : sanitised.Substring(0, MaxLength);
    }

    // ASCII only: char.IsLetterOrDigit would admit non-ASCII letters the F3 rule excludes.
    private static bool IsAllowed(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';
}
