using System;
using System.Runtime.InteropServices;

namespace Roulin
{
    public enum RoulinHttpMode
    {
        Auto      = 0,
        Http1Only = 1,
    }

    internal static unsafe class RoulinFetchNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        const string Lib = "__Internal";
#else
        const string Lib = "roulin_fetch";
#endif

        // Mirrors `rln_fetch_config` in fetch/bindings/c/fetch.h.
        [StructLayout(LayoutKind.Sequential)]
        internal struct AcFetchConfig
        {
            public int             max_parallel;
            public RoulinHttpMode http_mode;
            public int             max_attempts;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr rln_fetch_session_new(ref AcFetchConfig cfg);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_fetch_session_free(IntPtr session);

        // Returns 0 on failure (handle is never 0 on success).
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong rln_fetch_enqueue(
            IntPtr session,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            IntPtr expected_hash);

        // 0 = in-progress, 1 = completed, -1 = failed/cancelled.
        // On 1, caller owns out_buf and must release it via rln_fetch_free_buf.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rln_fetch_poll(
            IntPtr   session,
            ulong    handle,
            out ulong out_bytes_done,
            out ulong out_bytes_total,
            out IntPtr out_buf,
            out UIntPtr out_len,
            out int    out_http_version);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_fetch_cancel(IntPtr session, ulong handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_fetch_free_buf(IntPtr buf);
    }
}
