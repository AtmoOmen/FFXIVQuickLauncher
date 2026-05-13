using System.Diagnostics;
using System.Text;

// ReSharper disable InconsistentNaming

namespace XIVLauncher.Common.Patching.Util;

// https://stackoverflow.com/a/15274591
internal static class BinaryReaderHelpers
{
    public static string ReadFixedLengthString(this BinaryReader reader, uint length) =>
        Encoding.ASCII.GetString(reader.ReadBytesRequired((int)length)).TrimEnd((char)0);

    // Note this MODIFIES THE GIVEN ARRAY then returns a reference to the modified array.
    public static byte[] Reverse(this byte[] b)
    {
        Array.Reverse(b);
        return b;
    }

    public static ushort ReadUInt16BE(this BinaryReader binRdr) =>
        BitConverter.ToUInt16(binRdr.ReadBytesRequired(sizeof(ushort)).Reverse(), 0);

    public static short ReadInt16BE(this BinaryReader binRdr) =>
        BitConverter.ToInt16(binRdr.ReadBytesRequired(sizeof(short)).Reverse(), 0);

    public static uint ReadUInt32BE(this BinaryReader binRdr) =>
        BitConverter.ToUInt32(binRdr.ReadBytesRequired(sizeof(uint)).Reverse(), 0);

    public static int ReadInt32BE(this BinaryReader binRdr) =>
        BitConverter.ToInt32(binRdr.ReadBytesRequired(sizeof(int)).Reverse(), 0);

    public static ulong ReadUInt64BE(this BinaryReader binRdr) =>
        BitConverter.ToUInt64(binRdr.ReadBytesRequired(sizeof(ulong)).Reverse(), 0);

    public static long ReadInt64BE(this BinaryReader binRdr) =>
        BitConverter.ToInt64(binRdr.ReadBytesRequired(sizeof(long)).Reverse(), 0);

    public static byte[] ReadBytesRequired(this BinaryReader binRdr, int byteCount)
    {
        var result = binRdr.ReadBytes(byteCount);

        if (result.Length != byteCount)
            throw new EndOfStreamException($"{byteCount} bytes required from stream, but only {result.Length} returned.");

        return result;
    }

    public static void Dump(this byte[] bytes, int offset = 0, int bytesPerLine = 16)
    {
        var hexChars = "0123456789ABCDEF".ToCharArray();

        var offsetBlock = 8           + 3;
        var byteBlock   = offsetBlock + bytesPerLine * 3 + (bytesPerLine - 1) / 8 + 2;
        var lineLength  = byteBlock   + bytesPerLine     + Environment.NewLine.Length;

        var line     = (new string(' ', lineLength - Environment.NewLine.Length) + Environment.NewLine).ToCharArray();
        var numLines = (bytes.Length + bytesPerLine                              - 1) / bytesPerLine;

        var sb = new StringBuilder(numLines * lineLength);
        sb.Append('\n');

        for (var i = 0; i < bytes.Length; i += bytesPerLine)
        {
            var h = i + offset;

            line[0] = hexChars[h >> 28 & 0xF];
            line[1] = hexChars[h >> 24 & 0xF];
            line[2] = hexChars[h >> 20 & 0xF];
            line[3] = hexChars[h >> 16 & 0xF];
            line[4] = hexChars[h >> 12 & 0xF];
            line[5] = hexChars[h >> 8  & 0xF];
            line[6] = hexChars[h >> 4  & 0xF];
            line[7] = hexChars[h >> 0  & 0xF];

            var hexColumn  = offsetBlock;
            var charColumn = byteBlock;

            for (var j = 0; j < bytesPerLine; j++)
            {
                if (j > 0 && (j & 7) == 0)
                    hexColumn++;

                if (i + j >= bytes.Length)
                {
                    line[hexColumn]     = ' ';
                    line[hexColumn + 1] = ' ';
                    line[charColumn]    = ' ';
                }
                else
                {
                    var by = bytes[i + j];
                    line[hexColumn]     = hexChars[by >> 4 & 0xF];
                    line[hexColumn + 1] = hexChars[by      & 0xF];
                    line[charColumn]    = by < 32 ? '.' : (char)by;
                }

                hexColumn += 3;
                charColumn++;
            }

            sb.Append(line);
        }

        Debug.WriteLine(sb.ToString().TrimEnd(Environment.NewLine.ToCharArray()));
    }
}
