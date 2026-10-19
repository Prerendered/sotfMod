// purpose: scriptable ILabelFileSystem so label tests (cards 13, 15, 20) run without disk
// owns:    FakeLabelFileSystem
// deps:    ILabelFileSystem, LabelFile, PrunedLabel

using System;
using System.Collections.Generic;

using ShelfTransfer.Core.Labels;

namespace ShelfTransfer.Core.Tests.Fakes;

public sealed class FakeLabelFileSystem : ILabelFileSystem
{
    /// <summary>What Read returns; defaults to NoFile. Set via <see cref="Store"/> for the common Ok case.</summary>
    public LabelReadResult ReadResult { get; set; } = LabelReadResult.NoFile;

    public LabelWriteResult WriteResult { get; set; } = LabelWriteResult.Ok;

    public Exception? ThrowOnRead { get; set; }

    public Exception? ThrowOnWrite { get; set; }

    /// <summary>World ids passed to Read, in call order.</summary>
    public List<string> ReadWorldIds { get; } = new();

    /// <summary>Every file passed to Write, in call order (recorded even when the write is scripted to fail or throw).</summary>
    public List<LabelFile> Writes { get; } = new();

    public LabelWriteResult AppendResult { get; set; } = LabelWriteResult.Ok;

    /// <summary>Runs at the start of AppendPruned, so a test can see the store as the archive write found it.</summary>
    public Action? OnAppend { get; set; }

    /// <summary>Every AppendPruned call, in call order (recorded even when scripted to fail).</summary>
    public List<(string WorldId, IReadOnlyList<PrunedLabel> Entries)> Appends { get; } = new();

    /// <summary>Makes subsequent Reads return Ok(file); a successful Write also becomes the stored file.</summary>
    public FakeLabelFileSystem Store(LabelFile file)
    {
        ReadResult = LabelReadResult.Ok(file);
        return this;
    }

    public LabelReadResult Read(string worldId)
    {
        ReadWorldIds.Add(worldId);
        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        return ReadResult;
    }

    public LabelWriteResult Write(LabelFile file)
    {
        Writes.Add(file);
        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        if (WriteResult.IsOk)
        {
            ReadResult = LabelReadResult.Ok(file);
        }

        return WriteResult;
    }

    public LabelWriteResult AppendPruned(string worldId, IReadOnlyList<PrunedLabel> entries)
    {
        OnAppend?.Invoke();
        Appends.Add((worldId, entries));
        return AppendResult;
    }
}
