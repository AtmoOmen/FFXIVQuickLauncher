namespace XIVLauncher.Common.Patching.ZiPatch.Util;

public sealed class AdvanceOnDispose : IDisposable
{
    public readonly long OffsetBefore;
    public readonly long OffsetAfter;

    public           long         NumBytesRemaining => OffsetAfter - _reader.BaseStream.Position;
    private readonly BinaryReader _reader;
    private readonly bool         _forceRead;

    public AdvanceOnDispose(BinaryReader reader, long size, bool forceRead)
    {
        _reader      = reader;
        _forceRead   = forceRead;
        OffsetBefore = _reader.BaseStream.Position;
        OffsetAfter  = OffsetBefore + size;
    }

    #region Disposal

    public void Dispose()
    {
        if (_forceRead)
        {
            _ = _reader.ReadBytes((int)NumBytesRemaining);
            return;
        }

        _reader.BaseStream.Position = OffsetAfter;
    }

    #endregion
}
