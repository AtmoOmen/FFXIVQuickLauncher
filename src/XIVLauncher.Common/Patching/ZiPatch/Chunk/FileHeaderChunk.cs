using System.IO;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk;

public class FileHeaderChunk : ZiPatchChunk
{
    public new static string Type = "FHDR";

    // V1?/2
    public byte   Version    { get; protected set; }
    public string PatchType  { get; protected set; }
    public uint   EntryFiles { get; protected set; }

    // V3
    public uint AddDirectories     { get; protected set; }
    public uint DeleteDirectories  { get; protected set; }
    public long DeleteDataSize     { get; protected set; } // Split in 2 DWORD; Low, High
    public uint MinorVersion       { get; protected set; }
    public uint RepositoryName     { get; protected set; }
    public uint Commands           { get; protected set; }
    public uint SqpkAddCommands    { get; protected set; }
    public uint SqpkDeleteCommands { get; protected set; }
    public uint SqpkExpandCommands { get; protected set; }
    public uint SqpkHeaderCommands { get; protected set; }
    public uint SqpkFileCommands   { get; protected set; }

    public FileHeaderChunk(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override string ToString() =>
        $"{Type}:V{Version}:{RepositoryName}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();

        Version    = (byte)(Reader.ReadUInt32() >> 16);
        PatchType  = Reader.ReadFixedLengthString(4u);
        EntryFiles = Reader.ReadUInt32BE();

        if (Version == 3)
        {
            AddDirectories     = Reader.ReadUInt32BE();
            DeleteDirectories  = Reader.ReadUInt32BE();
            DeleteDataSize     = Reader.ReadUInt32BE() | (long)Reader.ReadUInt32BE() << 32;
            MinorVersion       = Reader.ReadUInt32BE();
            RepositoryName     = Reader.ReadUInt32BE();
            Commands           = Reader.ReadUInt32BE();
            SqpkAddCommands    = Reader.ReadUInt32BE();
            SqpkDeleteCommands = Reader.ReadUInt32BE();
            SqpkExpandCommands = Reader.ReadUInt32BE();
            SqpkHeaderCommands = Reader.ReadUInt32BE();
            SqpkFileCommands   = Reader.ReadUInt32BE();
        }

        // 0xB8 of unknown data for V3, 0x08 of 0x00 for V2
        // ... Probably irrelevant.
    }
}
