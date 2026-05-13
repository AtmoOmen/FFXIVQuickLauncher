using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace XIVLauncher.Common.Patching.Util;

public class MultipartResponseHandler : IDisposable
{
    public           string               MultipartBoundary;
    private readonly HttpResponseMessage  response;
    private          bool                 noMoreParts;
    private          Stream               baseStream;
    private          string               multipartEndBoundary;
    private          CircularMemoryStream multipartBufferStream;
    private          List<string>         multipartHeaderLines;

    public MultipartResponseHandler(HttpResponseMessage responseMessage) =>
        response = responseMessage;

    #region Disposal

    public void Dispose()
    {
        multipartBufferStream?.Dispose();
        baseStream?.Dispose();
        response?.Dispose();
    }

    #endregion

    public async Task<MultipartPartStream> NextPart(CancellationToken? cancellationToken = null)
    {
        if (noMoreParts)
            return null;

        if (baseStream == null)
            baseStream = new BufferedStream(await response.Content.ReadAsStreamAsync(), 16384);

        if (MultipartBoundary == null)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    noMoreParts = true;
                    var stream = new MultipartPartStream(response.Content.Headers.ContentLength.Value, 0, response.Content.Headers.ContentLength.Value);
                    stream.AppendBaseStream(new ReadLengthLimitingStream(baseStream, response.Content.Headers.ContentLength.Value));
                    return stream;
                }

                case HttpStatusCode.PartialContent:
                    if (response.Content.Headers.ContentType.MediaType.ToLowerInvariant() != "multipart/byteranges")
                    {
                        noMoreParts = true;
                        var rangeHeader = response.Content.Headers.ContentRange;
                        var rangeLength = rangeHeader.To.Value + 1 - rangeHeader.From.Value;
                        var stream      = new MultipartPartStream(rangeHeader.Length.Value, rangeHeader.From.Value, rangeLength);
                        stream.AppendBaseStream(new ReadLengthLimitingStream(baseStream, rangeLength));
                        return stream;
                    }

                    MultipartBoundary     = "--"              + response.Content.Headers.ContentType.Parameters.Where(p => p.Name.ToLowerInvariant() == "boundary").First().Value;
                    multipartEndBoundary  = MultipartBoundary + "--";
                    multipartBufferStream = new();
                    multipartHeaderLines  = new();
                    break;

                default:
                    response.EnsureSuccessStatusCode();
                    throw new EndOfStreamException($"Unhandled success status code {response.StatusCode}");
            }
        }

        while (true)
        {
            if (cancellationToken.HasValue)
                cancellationToken.Value.ThrowIfCancellationRequested();

            var eof = false;

            using (var buffer = ReusableByteBufferManager.GetBuffer())
            {
                int readSize;
                if (cancellationToken == null)
                    readSize = await baseStream.ReadAsync(buffer.Buffer, 0, buffer.Buffer.Length);
                else
                    readSize = await baseStream.ReadAsync(buffer.Buffer, 0, buffer.Buffer.Length, (CancellationToken)cancellationToken);

                if (readSize == 0)
                    eof = true;
                else
                    multipartBufferStream.Feed(buffer.Buffer, 0, readSize);
            }

            for (var i = 0; i < multipartBufferStream.Length - 1; ++i)
            {
                if (multipartBufferStream[i + 0] != '\r' || multipartBufferStream[i + 1] != '\n')
                    continue;

                var isEmptyLine = i == 0;

                if (isEmptyLine)
                    multipartBufferStream.Consume(null, 0, 2);
                else
                {
                    using var buffer = ReusableByteBufferManager.GetBuffer();
                    if (i > buffer.Buffer.Length)
                        throw new IOException($"Multipart header line is too long ({i} bytes)");

                    multipartBufferStream.Consume(buffer.Buffer, 0, i + 2);
                    multipartHeaderLines.Add(Encoding.UTF8.GetString(buffer.Buffer, 0, i));
                }

                i = -1;

                if (multipartHeaderLines.Count == 0)
                    continue;

                if (multipartHeaderLines.Last() == multipartEndBoundary)
                {
                    noMoreParts = true;
                    return null;
                }

                if (!isEmptyLine)
                    continue;

                ContentRangeHeaderValue rangeHeader = null;

                foreach (var headerLine in multipartHeaderLines)
                {
                    var kvs = headerLine.Split(new[] { ':' }, 2);
                    if (kvs.Length != 2)
                        continue;
                    if (kvs[0].ToLowerInvariant() != "content-range")
                        continue;
                    if (ContentRangeHeaderValue.TryParse(kvs[1], out rangeHeader))
                        break;
                }

                if (rangeHeader == null)
                    throw new IOException("Content-Range not found in multipart part");

                multipartHeaderLines.Clear();
                var rangeFrom   = rangeHeader.From.Value;
                var rangeLength = rangeHeader.To.Value - rangeFrom + 1;
                var stream      = new MultipartPartStream(rangeHeader.Length.Value, rangeFrom, rangeLength);
                stream.AppendBaseStream(new ConsumeLengthLimitingStream(multipartBufferStream, Math.Min(rangeLength, multipartBufferStream.Length)));
                stream.AppendBaseStream(new ReadLengthLimitingStream(baseStream, stream.UnfulfilledBaseStreamLength));
                return stream;
            }

            if (eof && !noMoreParts)
                throw new EndOfStreamException("Reached premature EOF");
        }
    }

    private class ReadLengthLimitingStream : Stream
    {
        public override long Length => limitedLength;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override  long   Position { get => limitedPointer; set => throw new NotSupportedException(); }
        private readonly Stream baseStream;
        private readonly long   limitedLength;
        private          long   limitedPointer;

        public ReadLengthLimitingStream(Stream stream, long length)
        {
            baseStream    = stream;
            limitedLength = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, limitedLength - limitedPointer);
            if (count == 0)
                return 0;

            var read = baseStream.Read(buffer, offset, count);
            if (read == 0)
                throw new EndOfStreamException("Premature end of stream detected");
            limitedPointer += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class ConsumeLengthLimitingStream : Stream
    {
        public override long Length => limitedLength;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override  long                 Position { get => limitedPointer; set => throw new NotSupportedException(); }
        private readonly CircularMemoryStream baseStream;
        private readonly long                 limitedLength;
        private          long                 limitedPointer;

        public ConsumeLengthLimitingStream(CircularMemoryStream stream, long length)
        {
            baseStream    = stream;
            limitedLength = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, limitedLength - limitedPointer);
            if (count == 0)
                return 0;

            var read = baseStream.Consume(buffer, offset, count);
            if (read == 0)
                throw new EndOfStreamException("Premature end of stream detected");
            limitedPointer += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public class MultipartPartStream : Stream
    {
        public readonly long OriginTotalLength;
        public readonly long OriginOffset;
        public readonly long OriginLength;
        public          long OriginEnd => OriginOffset + OriginLength;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => OriginTotalLength;

        public override long Position { get => positionInternal; set => Seek(value, SeekOrigin.Begin); }

        internal         long                 UnfulfilledBaseStreamLength => OriginLength - baseStreams.Select(x => x.Length).Sum();
        private readonly CircularMemoryStream loopStream  = new(16384, CircularMemoryStream.FeedOverflowMode.DiscardOldest);
        private readonly List<Stream>         baseStreams = new();
        private          int                  baseStreamIndex;
        private          long                 positionInternal;

        internal MultipartPartStream(long originTotalLength, long originOffset, long originLength)
        {
            OriginTotalLength = originTotalLength;
            OriginOffset      = originOffset;
            OriginLength      = originLength;
            positionInternal  = originOffset;
        }

        public void CaptureBackwards(long captureCapacity) =>
            loopStream.Reserve(captureCapacity);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;

            while (count > 0 && loopStream.Position < loopStream.Length)
            {
                var read1 = (int)Math.Min(count, loopStream.Length - loopStream.Position);
                var read2 = loopStream.Read(buffer, offset, read1);
                if (read2 == 0)
                    throw new EndOfStreamException("MultipartPartStream.Read:1");

                totalRead        += read2;
                positionInternal += read2;
                count            -= read2;
                offset           += read2;
            }

            while (count > 0 && baseStreamIndex < baseStreams.Count)
            {
                var stream = baseStreams[baseStreamIndex];
                var read1  = (int)Math.Min(count, stream.Length - stream.Position);
                var read2  = stream.Read(buffer, offset, read1);
                if (read2 == 0)
                    throw new EndOfStreamException("MultipartPartStream.Read:2");

                loopStream.Feed(buffer, offset, read2);
                loopStream.Position = loopStream.Length;

                totalRead        += read2;
                positionInternal += read2;
                count            -= read2;
                offset           += read2;

                if (stream.Position == stream.Length)
                    baseStreamIndex++;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    offset -= positionInternal;
                    break;

                case SeekOrigin.End:
                    offset = OriginTotalLength - offset - positionInternal;
                    break;
            }

            var finalPosition = positionInternal + offset;

            if (finalPosition > OriginOffset + OriginLength)
                throw new ArgumentException("Tried to seek after the end of the segment.");
            if (finalPosition < OriginOffset)
                throw new ArgumentException("Tried to seek behind the beginning of the segment.");

            var backwards          = loopStream.Length - loopStream.Position;
            var backwardAdjustment = Math.Min(backwards, offset);
            loopStream.Position += backwardAdjustment; // This will throw if there are not enough old data available
            offset              -= backwardAdjustment;
            positionInternal    += backwardAdjustment;

            if (offset > 0)
            {
                using var buf = ReusableByteBufferManager.GetBuffer();

                for (var i = 0; i < offset; i += buf.Buffer.Length)
                {
                    if (0 == Read(buf.Buffer, 0, (int)Math.Min(offset - i, buf.Buffer.Length)))
                        throw new EndOfStreamException("MultipartPartStream.Read:3");
                }
            }

            if (positionInternal != finalPosition)
                throw new IOException("Failed to seek properly.");

            return positionInternal;
        }

        public override void Flush() => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        internal void AppendBaseStream(Stream stream)
        {
            if (stream.Length == 0)
                return;
            if (UnfulfilledBaseStreamLength < stream.Length)
                throw new ArgumentException("Total length of given streams exceed OriginTotalLength.");
            baseStreams.Add(stream);
        }
    }
}
