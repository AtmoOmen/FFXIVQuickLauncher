using System.Collections;
using System.Diagnostics;

namespace XIVLauncher.GamePatchV3;

public class PatchTargetFile : IList<PatchPartLocator>
{
    public int Count => underlying.Count;

    public bool IsReadOnly => false;

    public           long                FileSize => underlying.Count > 0 ? underlying.Last().TargetEnd : 0;
    public           string              RelativePath = "";
    private readonly List<PatchPartLocator> underlying   = [];

    public PatchTargetFile(string fileName) =>
        RelativePath = fileName;

    public PatchTargetFile(BinaryReader reader, bool disposeReader = true)
    {
        try
        {
            ReadFrom(reader);
        }
        finally
        {
            if (disposeReader)
                reader.Dispose();
        }
    }

    public void Add(PatchPartLocator item) => underlying.Add(item);

    public void Clear() => underlying.Clear();

    public bool Contains(PatchPartLocator item) => underlying.Contains(item);

    public void CopyTo(PatchPartLocator[] array, int arrayIndex) => underlying.CopyTo(array, arrayIndex);

    public IEnumerator<PatchPartLocator> GetEnumerator() => underlying.GetEnumerator();

    public int IndexOf(PatchPartLocator item) => underlying.IndexOf(item);

    public void Insert(int index, PatchPartLocator item) => underlying.Insert(index, item);

    public bool Remove(PatchPartLocator item) => underlying.Remove(item);

    public void RemoveAt(int index) => underlying.RemoveAt(index);

    public int BinarySearchByTargetOffset(long targetOffset) => underlying.BinarySearch(new() { TargetOffset = targetOffset });

    public void SplitAt(long offset, int targetFileIndex)
    {
        var i = BinarySearchByTargetOffset(offset);

        if (i >= 0)
        {
            // Already split at given offset
            return;
        }

        i = ~i;

        if (i == 0 && offset == 0)
        {
            // Do nothing; split at 0 is a given
        }
        else if (i == 0 && underlying.Count == 0)
        {
            underlying.Add
            (
                new()
                {
                    TargetSize  = offset,
                    TargetIndex = targetFileIndex,
                    SourceIndex = PatchPartLocator.SourceIndexZeros
                }
            );
        }
        else if (i == underlying.Count && underlying[i - 1].TargetEnd == offset)
        {
            // Do nothing; split at TargetEnd of last part is give
        }
        else if (i == underlying.Count && underlying[i - 1].TargetEnd < offset)
        {
            underlying.Add
            (
                new()
                {
                    TargetOffset = underlying[i - 1].TargetEnd,
                    TargetSize   = offset - underlying[i - 1].TargetEnd,
                    TargetIndex  = targetFileIndex,
                    SourceIndex  = PatchPartLocator.SourceIndexZeros
                }
            );
        }
        else
        {
            i -= 1;
            var part = underlying[i];

            if (part.IsDeflatedBlockData || part.IsEmptyBlock)
            {
                underlying[i] = new()
                {
                    TargetOffset                     = part.TargetOffset,
                    TargetSize                       = offset - part.TargetOffset,
                    TargetIndex                      = targetFileIndex,
                    SourceIndex                      = part.SourceIndex,
                    SourceOffset                     = part.SourceOffset,
                    SplitDecodedSourceFrom           = part.SplitDecodedSourceFrom,
                    Crc32OrPlaceholderEntryDataUnits = part.Crc32OrPlaceholderEntryDataUnits,
                    IsDeflatedBlockData              = part.IsDeflatedBlockData
                };
                underlying.Insert
                (
                    i + 1,
                    new()
                    {
                        TargetOffset                     = offset,
                        TargetSize                       = part.TargetEnd - offset,
                        TargetIndex                      = targetFileIndex,
                        SourceIndex                      = part.SourceIndex,
                        SourceOffset                     = part.SourceOffset,
                        SplitDecodedSourceFrom           = part.SplitDecodedSourceFrom + offset - part.TargetOffset,
                        Crc32OrPlaceholderEntryDataUnits = part.Crc32OrPlaceholderEntryDataUnits,
                        IsDeflatedBlockData              = part.IsDeflatedBlockData
                    }
                );
            }
            else
            {
                if (part.SplitDecodedSourceFrom != 0)
                    throw new ArgumentException("Not deflated but SplitDecodeSourceFrom is given");

                underlying[i] = new()
                {
                    TargetOffset                     = part.TargetOffset,
                    TargetSize                       = offset - part.TargetOffset,
                    TargetIndex                      = targetFileIndex,
                    SourceIndex                      = part.SourceIndex,
                    SourceOffset                     = part.SourceOffset,
                    Crc32OrPlaceholderEntryDataUnits = part.Crc32OrPlaceholderEntryDataUnits
                };
                underlying.Insert
                (
                    i + 1,
                    new()
                    {
                        TargetOffset                     = offset,
                        TargetSize                       = part.TargetEnd - offset,
                        TargetIndex                      = targetFileIndex,
                        SourceIndex                      = part.SourceIndex,
                        SourceOffset                     = part.SourceOffset + offset - part.TargetOffset,
                        Crc32OrPlaceholderEntryDataUnits = part.Crc32OrPlaceholderEntryDataUnits
                    }
                );
            }
        }
    }

    public void Update(PatchPartLocator part)
    {
        if (part.TargetSize == 0)
            return;

        SplitAt(part.TargetOffset, part.TargetIndex);
        SplitAt(part.TargetEnd,    part.TargetIndex);

        var left = BinarySearchByTargetOffset(part.TargetOffset);
        if (left < 0)
            left = ~left;

        if (left == underlying.Count)
        {
            underlying.Add(part);
            return;
        }

        var right = BinarySearchByTargetOffset(part.TargetEnd);
        if (right < 0)
            right = ~right;

        if (right - left - 1 < 0)
            Debugger.Break();

        underlying[left] = part;
        underlying.RemoveRange(left + 1, right - left - 1);
    }

    public async Task CalculateCrc32(List<Stream> sources, CancellationToken cancellationToken = default)
    {
        await Task.Run
        (
            () =>
            {
                var list = underlying.ToArray();

                for (var i = 0; i < list.Length; ++i)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (list[i].IsFromSourceFile)
                        PatchPartLocator.CalculateCrc32(ref list[i], sources[list[i].SourceIndex]);
                }

                underlying.Clear();
                underlying.AddRange(list);
            },
            cancellationToken
        );
    }

    public Stream ToStream(List<Stream> sources, bool disposeStreams = true) =>
        new PatchTargetViewStream(sources, this, disposeStreams);

    public void WriteTo(BinaryWriter writer)
    {
        writer.Write(RelativePath);
        writer.Write(underlying.Count);
        foreach (var item in underlying)
            item.WriteTo(writer);
    }

    public void ReadFrom(BinaryReader reader)
    {
        RelativePath = reader.ReadString();
        var dest = new PatchPartLocator[reader.ReadInt32()];
        for (var i = 0; i < dest.Length; ++i)
            dest[i].ReadFrom(reader);
        underlying.Clear();
        underlying.AddRange(dest);
    }

    IEnumerator IEnumerable.GetEnumerator() => underlying.GetEnumerator();

    public PatchPartLocator this[int index]
    {
        get => underlying[index];
        set => underlying[index] = value;
    }
}
