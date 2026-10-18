// purpose: byte budget for the mod log, so a chatty session or a failing rotation cannot fill the disk (F4 log.cap)
// owns:    LogSizeCap, LogSizeCap.Admission, LogSizeCap.MaxSessionBytes, LogSizeCap.IsAppendAllowed
// deps:    none
// caution: not thread-safe; the caller admits and writes under the same lock

using System;

namespace ShelfTransfer.Core.Logging;

public sealed class LogSizeCap
{
    public const long MaxSessionBytes = 10L * 1024 * 1024;

    private readonly long _maxBytes;
    private bool _isCapped;

    public LogSizeCap(long maxBytes)
    {
        if (maxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "must be at least 1");
        }

        _maxBytes = maxBytes;
    }

    public enum Admission
    {
        Write,
        /// <summary>The event does not fit: drop it and write the one log.cap notice instead (returned once).</summary>
        WriteCapNotice,
        Drop,
    }

    /// <summary>Bytes admitted as <see cref="Admission.Write"/> so far; the cap notice itself is not counted.</summary>
    public long WrittenBytes { get; private set; }

    /// <summary>
    /// Whether a log that could not be rotated may be appended to. At or past one session's budget it must be
    /// truncated instead, so a persistently failing rotation keeps the file under twice the cap across sessions.
    /// </summary>
    public bool IsAppendAllowed(long existingBytes)
    {
        if (existingBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(existingBytes), existingBytes, "must not be negative");
        }

        return existingBytes < _maxBytes;
    }

    public Admission Admit(long byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "must not be negative");
        }

        if (_isCapped)
        {
            return Admission.Drop;
        }

        // Compared as headroom so a huge byteCount cannot overflow the sum.
        if (byteCount <= _maxBytes - WrittenBytes)
        {
            WrittenBytes += byteCount;
            return Admission.Write;
        }

        _isCapped = true;
        return Admission.WriteCapNotice;
    }
}
