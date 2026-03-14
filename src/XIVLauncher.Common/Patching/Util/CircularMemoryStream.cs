using System;
using System.IO;

namespace XIVLauncher.Common.Patching.Util;

public class CircularMemoryStream : Stream
{
    public int Capacity => reusableBuffer.Buffer.Length;

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => true;
    public override long Length   => length;

    public override long Position
    {
        get => externalPosition;
        set => Seek(value, SeekOrigin.Begin);
    }

    private readonly FeedOverflowMode                     overflowMode;
    private          ReusableByteBufferManager.Allocation reusableBuffer;
    private          int                                  bufferValidTo;
    private          int                                  bufferValidFrom;
    private          int                                  length;
    private          int                                  externalPosition;

    public CircularMemoryStream(int baseCapacity = 0, FeedOverflowMode feedOverflowMode = FeedOverflowMode.ExtendCapacity)
    {
        overflowMode = feedOverflowMode;
        if (feedOverflowMode == FeedOverflowMode.ExtendCapacity && baseCapacity == 0)
            reusableBuffer = ReusableByteBufferManager.GetBuffer();
        else
            reusableBuffer = ReusableByteBufferManager.GetBuffer(baseCapacity);
    }

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        reusableBuffer?.Dispose();
    }

    #endregion

    public void Reserve(long capacity)
    {
        if (capacity <= Capacity)
            return;

        var newBuffer = ReusableByteBufferManager.GetBuffer(capacity);

        if (length > 0)
        {
            if (bufferValidFrom < bufferValidTo)
                Array.Copy(reusableBuffer.Buffer, bufferValidFrom, newBuffer.Buffer, 0, length);
            else
            {
                Array.Copy(reusableBuffer.Buffer, bufferValidFrom, newBuffer.Buffer, 0, Capacity - bufferValidFrom);
                Array.Copy(reusableBuffer.Buffer, 0,               newBuffer.Buffer, Capacity    - bufferValidFrom, bufferValidTo);
            }
        }

        reusableBuffer.Dispose();
        reusableBuffer = newBuffer;

        bufferValidFrom = 0;
        bufferValidTo   = length;
    }

    public void Feed(byte[] buffer, int offset, int count)
    {
        if (count == 0)
            return;

        if (length + count > Capacity)
        {
            switch (overflowMode)
            {
                case FeedOverflowMode.ExtendCapacity:
                    Reserve(Length + count);
                    break;

                case FeedOverflowMode.DiscardOldest:
                    if (count >= Capacity)
                    {
                        bufferValidFrom = 0;
                        bufferValidTo   = 0;
                        Array.Copy(buffer, offset + count - Capacity, reusableBuffer.Buffer, 0, Capacity);
                        externalPosition = 0;
                        length           = Capacity;
                        return;
                    }

                    Consume(null, 0, length + count - Capacity);
                    break;

                case FeedOverflowMode.Throw:
                    throw new InvalidOperationException($"Cannot feed {count} bytes (length={Length}, capacity={Capacity})");
            }
        }

        if (bufferValidFrom < bufferValidTo)
        {
            var rightLength = Capacity - bufferValidTo;

            if (rightLength >= count)
                Buffer.BlockCopy(buffer, offset, reusableBuffer.Buffer, bufferValidTo, count);
            else
            {
                Buffer.BlockCopy(buffer, offset,               reusableBuffer.Buffer, bufferValidTo, rightLength);
                Buffer.BlockCopy(buffer, offset + rightLength, reusableBuffer.Buffer, 0,             count - rightLength);
            }
        }
        else
            Buffer.BlockCopy(buffer, offset, reusableBuffer.Buffer, bufferValidTo, count);

        bufferValidTo =  (bufferValidTo + count) % Capacity;
        length        += count;
    }

    public int Consume(byte[] buffer, int offset, int count, bool peek = false)
    {
        count = Math.Min(count, length);

        if (buffer != null && count > 0)
        {
            if (bufferValidFrom < bufferValidTo)
                Buffer.BlockCopy(reusableBuffer.Buffer, bufferValidFrom, buffer, offset, count);
            else
            {
                var rightLength = Capacity - bufferValidFrom;

                if (rightLength >= count)
                    Buffer.BlockCopy(reusableBuffer.Buffer, bufferValidFrom, buffer, offset, count);
                else
                {
                    Buffer.BlockCopy(reusableBuffer.Buffer, bufferValidFrom, buffer, offset,               rightLength);
                    Buffer.BlockCopy(reusableBuffer.Buffer, 0,               buffer, offset + rightLength, count - rightLength);
                }
            }
        }

        if (!peek)
        {
            length -= count;
            if (length == 0)
                bufferValidFrom = bufferValidTo = 0;
            else
                bufferValidFrom = (bufferValidFrom + count) % Capacity;
            externalPosition = Math.Max(0, externalPosition - count);
        }

        return count;
    }

    public override void Flush() { }

    public override void SetLength(long value)
    {
        if (value > int.MaxValue)
            throw new ArgumentOutOfRangeException("Length can be up to int.MaxValue");

        if (value == 0)
        {
            bufferValidFrom = bufferValidTo = length = 0;
            return;
        }

        var intValue = (int)value;

        if (intValue > Capacity)
            Reserve(intValue);
        else if (intValue > Length)
        {
            var extendLength = (int)(intValue - Length);
            var newValidTo   = (bufferValidTo + extendLength) % Capacity;

            if (bufferValidTo < newValidTo)
                Array.Clear(reusableBuffer.Buffer, bufferValidTo, newValidTo - bufferValidTo);
            else
            {
                Array.Clear(reusableBuffer.Buffer, bufferValidTo, Capacity - bufferValidTo);
                Array.Clear(reusableBuffer.Buffer, 0,             newValidTo);
            }

            bufferValidTo = newValidTo;
        }
        else if (intValue < Length)
            bufferValidTo = (bufferValidFrom + intValue) % Capacity;

        length = (int)value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        count = Math.Min(count, length - externalPosition);

        var adjValidFrom = (bufferValidFrom + externalPosition) % Capacity;

        if (adjValidFrom < bufferValidTo)
            Buffer.BlockCopy(reusableBuffer.Buffer, adjValidFrom, buffer, offset, count);
        else
        {
            var rightLength = Capacity - adjValidFrom;

            if (rightLength >= count)
                Buffer.BlockCopy(reusableBuffer.Buffer, adjValidFrom, buffer, offset, count);
            else
            {
                Buffer.BlockCopy(reusableBuffer.Buffer, adjValidFrom, buffer, offset,               rightLength);
                Buffer.BlockCopy(reusableBuffer.Buffer, 0,            buffer, offset + rightLength, count - rightLength);
            }
        }

        externalPosition += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPosition = externalPosition;

        switch (origin)
        {
            case SeekOrigin.Begin:
                newPosition = offset;
                break;

            case SeekOrigin.Current:
                newPosition += offset;
                break;

            case SeekOrigin.End:
                newPosition = Length - offset;
                break;
        }

        if (newPosition < 0)
            throw new ArgumentException("Seeking is attempted before the beginning of the stream.");
        if (newPosition > length)
            newPosition = length;
        externalPosition = (int)newPosition;
        return newPosition;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Length + count > Capacity)
            Reserve((int)(Length + count));

        var writeOffset = (bufferValidFrom + externalPosition) % Capacity;

        if (writeOffset + count <= Capacity)
            Array.Copy(buffer, offset, reusableBuffer.Buffer, writeOffset, count);
        else
        {
            var writeCount1 = Capacity - writeOffset;
            var writeCount2 = count    - writeCount1;
            Array.Copy(buffer, offset,               reusableBuffer.Buffer, writeOffset, writeCount1);
            Array.Copy(buffer, offset + writeCount1, reusableBuffer.Buffer, 0,           writeCount2);
        }

        externalPosition += count;
    }

    public enum FeedOverflowMode
    {
        ExtendCapacity,
        DiscardOldest,
        Throw
    }

    public byte this[long i]
    {
        get
        {
            if (i < 0 || i >= Length)
                throw new ArgumentOutOfRangeException(nameof(i));
            return reusableBuffer.Buffer[(bufferValidFrom + i) % Capacity];
        }
        set
        {
            if (i < 0 || i >= Length)
                throw new ArgumentOutOfRangeException(nameof(i));
            reusableBuffer.Buffer[(bufferValidFrom + i) % Capacity] = value;
        }
    }
}
