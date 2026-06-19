using System;
using System.Runtime.InteropServices;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Roulin
{
    public sealed class RoulinFetchHttp : IHttpRequest
    {
        IntPtr _session;
        bool   _disposed;

        public RoulinFetchHttp(
            int maxParallel = 8,
            RoulinHttpMode httpMode = RoulinHttpMode.Auto,
            int maxAttempts = 3)
        {
            var cfg = new RoulinFetchNative.AcFetchConfig
            {
                max_parallel = maxParallel,
                http_mode    = httpMode,
                max_attempts = Math.Max(1, maxAttempts),
            };
            _session = RoulinFetchNative.rln_fetch_session_new(ref cfg);
            if (_session == IntPtr.Zero)
                throw new Exception(
                    $"rln_fetch_session_new failed: {RoulinNative.LastError()}");
        }

        public async UniTask<byte[]> GetAsync(
            string            url,
            byte[]            expectedHash = null,
            CancellationToken ct           = default,
            IProgress<float>  progress     = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RoulinFetchHttp));
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("url is null or empty", nameof(url));
            if (expectedHash != null && expectedHash.Length != 32)
                throw new ArgumentException(
                    $"expectedHash must be 32 bytes (got {expectedHash.Length})",
                    nameof(expectedHash));

            GCHandle pin     = default;
            IntPtr   hashPtr = IntPtr.Zero;
            if (expectedHash != null)
            {
                pin     = GCHandle.Alloc(expectedHash, GCHandleType.Pinned);
                hashPtr = pin.AddrOfPinnedObject();
            }
            ulong handle;
            try
            {
                handle = RoulinFetchNative.rln_fetch_enqueue(_session, url, hashPtr);
            }
            finally
            {
                if (pin.IsAllocated) pin.Free();
            }
            if (handle == 0)
                throw new Exception(
                    $"rln_fetch_enqueue {url}: {RoulinNative.LastError()}");

            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        RoulinFetchNative.rln_fetch_cancel(_session, handle);
                        DrainTerminal(handle);
                        ct.ThrowIfCancellationRequested();
                    }

                    int rc = RoulinFetchNative.rln_fetch_poll(
                        _session, handle,
                        out ulong bytesDone, out ulong bytesTotal,
                        out IntPtr buf, out UIntPtr len, out int _);

                    if (rc == 1)
                    {
                        progress?.Report(1f);
                        int n = checked((int)len.ToUInt64());
                        var data = new byte[n];
                        if (n > 0 && buf != IntPtr.Zero)
                            Marshal.Copy(buf, data, 0, n);
                        if (buf != IntPtr.Zero)
                            RoulinFetchNative.rln_fetch_free_buf(buf);
                        return data;
                    }
                    if (rc == -1)
                    {
                        throw new Exception(
                            $"HTTP GET {url}: {RoulinNative.LastError()}");
                    }

                    if (progress != null && bytesTotal > 0)
                        progress.Report((float)bytesDone / bytesTotal);

                    await UniTask.Yield(ct);
                }
            }
            catch when (!ct.IsCancellationRequested)
            {
                RoulinFetchNative.rln_fetch_cancel(_session, handle);
                DrainTerminal(handle);
                throw;
            }
        }

        void DrainTerminal(ulong handle)
        {
            RoulinFetchNative.rln_fetch_poll(
                _session, handle,
                out _, out _,
                out IntPtr buf, out _, out _);
            if (buf != IntPtr.Zero)
                RoulinFetchNative.rln_fetch_free_buf(buf);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_session != IntPtr.Zero)
            {
                RoulinFetchNative.rln_fetch_session_free(_session);
                _session = IntPtr.Zero;
            }
        }
    }
}
