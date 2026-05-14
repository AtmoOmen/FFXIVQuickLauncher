namespace XIVLauncher.GamePatchV3;

public class PatchTargetViewStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => partList.FileSize;

    public override  long                Position { get; set; }
    private readonly List<Stream>        sources;
    private readonly PatchTargetFile     partList;
    private readonly bool                disposeStreams;

    internal PatchTargetViewStream(List<Stream> sources, PatchTargetFile partList, bool disposeStreams)
    {
        this.sources        = sources;
        this.partList       = partList;
        this.disposeStreams = disposeStreams;
    }

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposeStreams)
        {
            foreach (var s in sources)
                s.Dispose();
            sources.Clear();
        }
    }

    #endregion

    public override int Read(byte[] buffer, int offset, int count)
    {
        var beginOffset = offset;

        while (count > 0 && Position < Length)
        {
            var i = partList.BinarySearchByTargetOffset(Position);
            if (i < 0)
                i = ~i - 1;

            var reconstructedLength = partList[i].Reconstruct(sources, buffer, offset, count, (int)(Position - partList[i].TargetOffset));
            offset   += reconstructedLength;
            count    -= reconstructedLength;
            Position += reconstructedLength;
        }

        return offset - beginOffset;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = Position;

        switch (origin)
        {
            case SeekOrigin.Begin:
                position = offset;
                break;

            case SeekOrigin.Current:
                position += offset;
                break;

            case SeekOrigin.End:
                position = Length - offset;
                break;

            default:
                throw new NotImplementedException();
        }

        if (position < 0)
            throw new ArgumentException("Seeking is attempted before the beginning of the stream.");

        Position = Math.Min(position, Length);

        return Position;
    }

    public override void Flush() => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
