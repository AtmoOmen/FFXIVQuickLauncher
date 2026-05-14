namespace XIVLauncher.GamePatchV3;

public class ReusableByteBufferManager
(
    int arraySize,
    int maxBuffers
)
{
    private static readonly int[]                       ArraySizes = [1 << 14, 1 << 16, 1 << 18, 1 << 20, 1 << 22];
    private static readonly ReusableByteBufferManager[] Instances  = ArraySizes.Select(x => new ReusableByteBufferManager(x, 2 * Environment.ProcessorCount)).ToArray();

    private readonly Lock          syncLock = new();
    private readonly Allocation?[] buffers  = new Allocation?[maxBuffers];

    public static Allocation GetBuffer(bool clear = false) =>
        Instances[0].Allocate(clear);

    public static Allocation GetBuffer(long minSize, bool clear = false)
    {
        for (var i = 0; i < ArraySizes.Length; i++)
        {
            if (ArraySizes[i] >= minSize)
                return Instances[i].Allocate(clear);
        }

        return new Allocation(null, minSize);
    }

    public Allocation Allocate(bool clear = false)
    {
        lock (syncLock)
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                var buffer = buffers[i];
                if (buffer is null)
                    continue;

                if (clear)
                    buffer.Clear();

                buffers[i] = null;
                buffer.ResetState();
                return buffer;
            }
        }

        var allocation = new Allocation(this, arraySize);
        allocation.ResetState();
        return allocation;
    }

    internal void Return(Allocation buf)
    {
        lock (syncLock)
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] != null)
                    continue;

                buffers[i] = buf;
                return;
            }
        }
    }

    public class Allocation : IDisposable
    {
        public readonly ReusableByteBufferManager? BufferManager;
        public readonly byte[]                     Buffer;
        public readonly MemoryStream               Stream;
        public readonly BinaryWriter               Writer;

        internal Allocation(ReusableByteBufferManager? bufferManager, long size)
        {
            BufferManager = bufferManager;
            Buffer        = new byte[size];
            Stream        = new MemoryStream(Buffer);
            Writer        = new BinaryWriter(Stream);
        }

        #region Disposal

        public void Dispose() => BufferManager?.Return(this);

        #endregion

        public void ResetState()
        {
            Stream.SetLength(0);
            Stream.Seek(0, SeekOrigin.Begin);
        }

        public void Clear() => Array.Clear(Buffer, 0, Buffer.Length);
    }
}
