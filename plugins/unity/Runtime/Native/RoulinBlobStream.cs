using System;
using System.IO;

namespace Roulin
{
    // Stream wrapper around ACBlob* for zero-copy AssetBundle.LoadFromStream.
    internal sealed unsafe class RoulinBlobStream : Stream
    {
        IntPtr _blob;      // ACBlob* — owned by this stream
        long   _length;
        long   _position;
        bool   _disposed;

        internal RoulinBlobStream(IntPtr blob)
        {
            _blob     = blob;
            _length   = (long)(ulong)RoulinNative.rln_blob_size(blob);
            _position = 0;
        }

        public override bool CanRead  => !_disposed;
        public override bool CanSeek  => !_disposed;
        public override bool CanWrite => false;
        public override long Length   => _length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RoulinBlobStream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentException("Invalid offset/count");

            long remaining = _length - _position;
            int  toRead    = (int)Math.Min(count, remaining);
            if (toRead <= 0) return 0;

            fixed (byte* dst = &buffer[offset])
            {
                long read = RoulinNative.rln_blob_read(
                    _blob, dst,
                    (UIntPtr)(ulong)_position,
                    (UIntPtr)(ulong)toRead);

                if (read < 0)
                    throw new IOException(
                        $"rln_blob_read failed: {RoulinNative.LastError()}");

                _position += read;
                return (int)read;   // bounded by toRead (= int) so cast is safe
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin   => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End     => _length + offset,
                _                  => throw new ArgumentException("Invalid SeekOrigin")
            };
            Position = newPos;
            return _position;
        }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && _blob != IntPtr.Zero)
            {
                RoulinNative.rln_blob_release(_blob);
                _blob    = IntPtr.Zero;
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
