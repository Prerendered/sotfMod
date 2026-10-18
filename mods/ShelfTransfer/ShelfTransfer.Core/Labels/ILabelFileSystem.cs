// purpose: the I/O seam for the label file; the plugin implements it with System.Text.Json, tests with a fake (invariant 5)
// owns:    ILabelFileSystem, LabelReadResult, LabelReadStatus, LabelWriteResult
// deps:    LabelFile
// caution: implementations convert their own I/O and JSON exceptions to Failed/Corrupt; LabelStore still guards the call.
//          Returning Corrupt MUST first preserve the corrupt file (e.g. as .bad) at Read time — never defer it to Write —
//          because LabelStore allows Save after a Corrupt load and the first Save would overwrite the only copy.

namespace ShelfTransfer.Core.Labels;

public enum LabelReadStatus
{
    Ok,
    /// <summary>No file for this world yet; a first launch, not an error.</summary>
    NoFile,
    /// <summary>The file exists but is not valid label JSON.</summary>
    Corrupt,
    /// <summary>The file could not be read (locked, permissions). It may still be valid, so it must not be overwritten.</summary>
    Failed,
}

public sealed record LabelReadResult(LabelReadStatus Status, LabelFile? File = null, string? Detail = null)
{
    public static readonly LabelReadResult NoFile = new(LabelReadStatus.NoFile);

    public static LabelReadResult Ok(LabelFile file) => new(LabelReadStatus.Ok, file);

    public static LabelReadResult Corrupt(string detail) => new(LabelReadStatus.Corrupt, null, detail);

    public static LabelReadResult Failed(string detail) => new(LabelReadStatus.Failed, null, detail);
}

public sealed record LabelWriteResult(bool IsOk, string? Detail = null)
{
    public static readonly LabelWriteResult Ok = new(true);

    public static LabelWriteResult Failed(string detail) => new(false, detail);
}

public interface ILabelFileSystem
{
    /// <summary><paramref name="worldId"/> is already sanitised (<see cref="WorldId.Sanitise"/>).</summary>
    LabelReadResult Read(string worldId);

    /// <summary>Writes the file named by <see cref="LabelFile.WorldId"/>, replacing any existing one.</summary>
    LabelWriteResult Write(LabelFile file);
}
