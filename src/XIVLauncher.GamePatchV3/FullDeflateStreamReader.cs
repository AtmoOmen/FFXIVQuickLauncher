using System.IO.Compression;

namespace XIVLauncher.GamePatchV3;

internal static class FullDeflateStreamReader
{
    public static void FullRead(this DeflateStream stream, byte[] array, int offset, int count)
    {
        var totalRead = 0;

        while (totalRead < count)
        {
            var bytesRead = stream.Read(array, offset + totalRead, count - totalRead);
            if (bytesRead == 0)
                break;
            totalRead += bytesRead;
        }
    }
}
