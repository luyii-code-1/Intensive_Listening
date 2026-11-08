using System.IO.Compression;

namespace IL.Core.Ilp;

public static class ArchiveSafety
{
    public static Stream OpenRead(ZipArchiveEntry entry, long maximum)
    {
        var expected = entry.Length;
        return new BoundedEntryStream(entry.Open(), Math.Min(maximum, expected), expected);
    }

    // Read through the deflater: central-directory sizes are untrusted metadata.
    public static async Task<long> ValidateAsync(ZipArchive archive, long maximum, CancellationToken ct = default)
    {
        if (archive.Entries.Count > 2000) throw new InvalidDataException("压缩包文件数量过多");
        long total = 0;
        var buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            await using var input = OpenRead(entry, maximum - total);
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) != 0) total = checked(total + count);
        }
        return total;
    }

    private sealed class BoundedEntryStream(Stream input, long maximum, long expected) : Stream
    {
        private long consumed;
        private int Account(int count)
        {
            consumed = checked(consumed + count);
            if (consumed > maximum || count == 0 && consumed != expected) throw new InvalidDataException("压缩包实际解压大小无效或超过限制");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => buffer.IsEmpty ? 0 : Account(input.Read(buffer[..(int)Math.Min(buffer.Length, Math.Max(1, maximum - consumed + 1))]));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            return Account(await input.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Math.Max(1, maximum - consumed + 1))], cancellationToken));
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await input.DisposeAsync(); GC.SuppressFinalize(this); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
