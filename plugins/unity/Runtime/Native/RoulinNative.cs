using System;
using System.Runtime.InteropServices;

namespace Roulin
{
    internal static unsafe class RoulinNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        const string Lib = "__Internal";
#else
        const string Lib = "roulin_core";
#endif



        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr rln_last_error();

        internal static string LastError() =>
            Marshal.PtrToStringAnsi(rln_last_error()) ?? string.Empty;



        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        static extern void rln_compute_blake3(void* data, UIntPtr len, byte* outHash);



        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr rln_parcel_open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string localDir,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string revisionId);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_parcel_close(IntPtr parcel);



        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr rln_parcel_get(
            IntPtr parcel,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string address);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_blob_release(IntPtr blob);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr rln_blob_size(IntPtr blob);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long rln_blob_read(IntPtr blob, void* buf,
                                                  UIntPtr offset, UIntPtr len);



        // Frees a const char** array previously returned by rln_index_bundle_deps_for.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_strings_free(IntPtr strs, UIntPtr count);

        // Returns the deps for one bundle (binary search). NULL when no deps.
        // Caller must free the result with rln_strings_free.
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr rln_index_bundle_deps_for(IntPtr parcel,
                                                                 byte* bundleHash,
                                                                 out UIntPtr outCount);

        // Bulk walk every bundle's deps. Used by RoulinLocator at boot to build
        // the dep graph in one pass.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void BundleDepsFn(IntPtr bundleHash,
                                              IntPtr deps,
                                              UIntPtr depsCount,
                                              IntPtr userdata);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_index_for_each_bundle_deps(IntPtr parcel,
                                                                    BundleDepsFn fn,
                                                                    IntPtr userdata);



        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ForEachFn(IntPtr address,
                                         IntPtr blobHash, ulong blobSize,
                                         IntPtr labels, UIntPtr labelsCount,
                                         IntPtr assetId,
                                         IntPtr typeIdxs, UIntPtr typeIdxsCount,
                                         IntPtr userdata);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_parcel_foreach(IntPtr parcel, ForEachFn fn, IntPtr userdata);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr rln_index_types_count(IntPtr parcel);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr rln_index_type_at(IntPtr parcel, UIntPtr idx);

        // Read a uint32 array of `count` entries into a managed array.
        internal static uint[] ReadUInt32Array(IntPtr ptr, UIntPtr count)
        {
            int n = (int)count;
            if (n == 0 || ptr == IntPtr.Zero) return null;
            var result = new uint[n];
            var arr    = (uint*)ptr;
            for (int i = 0; i < n; i++)
                result[i] = arr[i];
            return result;
        }

        // Read a const char* const* array of `count` strings into a managed array.
        // Returns null when count == 0 to avoid allocating an empty array.
        internal static string[] ReadStringArray(IntPtr ptr, UIntPtr count)
        {
            int n = (int)count;
            if (n == 0 || ptr == IntPtr.Zero) return null;
            var result = new string[n];
            var arr    = (IntPtr*)ptr;
            for (int i = 0; i < n; i++)
                result[i] = Marshal.PtrToStringAnsi(arr[i]) ?? string.Empty;
            return result;
        }



        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int rln_parcel_diff(
            IntPtr remoteParcel,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string localDir,
            out IntPtr outBlobs, out UIntPtr outCount);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void rln_diff_free(IntPtr blobs, UIntPtr count);



        // Read a const char** array out of native memory then free it via
        // rln_strings_free. Used by rln_index_bundle_deps_for results.
        internal static string[] ReadAndFreeStrings(IntPtr strs, UIntPtr count)
        {
            int n = (int)count;
            if (n == 0 || strs == IntPtr.Zero) return Array.Empty<string>();
            var result = new string[n];
            var arr    = (IntPtr*)strs;
            for (int i = 0; i < n; i++)
                result[i] = Marshal.PtrToStringAnsi(arr[i]) ?? string.Empty;
            rln_strings_free(strs, count);
            return result;
        }

        // Read a blob hash (32 bytes) from a native pointer into a managed array.
        internal static byte[] ReadHash(IntPtr ptr)
        {
            var hash = new byte[32];
            Marshal.Copy(ptr, hash, 0, 32);
            return hash;
        }

        // Computes BLAKE3 over data and writes 32 bytes into outHash.
        // Use this to verify a blob downloaded over HTTP matches its expected
        // content-addressed hash before storing it locally.
        internal static void ComputeBlake3(byte[] data, byte[] outHash)
        {
            fixed (byte* dp = data, hp = outHash)
                rln_compute_blake3(dp, (UIntPtr)(data?.Length ?? 0), hp);
        }

        // Byte[] to lower-hex string.
        internal static string HashToHex(byte[] hash)
        {
            var sb = new System.Text.StringBuilder(64);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // 64-char lower-hex string to a 32-byte hash. Inverse of HashToHex.
        // Throws on malformed input.
        internal static byte[] HashFromHex(string hex)
        {
            if (hex == null || hex.Length != 64)
                throw new ArgumentException(
                    $"hash hex must be 64 chars (got {hex?.Length ?? 0})", nameof(hex));
            var bytes = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                bytes[i] = (byte)((HexNibble(hex[i * 2]) << 4) | HexNibble(hex[i * 2 + 1]));
            }
            return bytes;
        }

        static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new ArgumentException($"invalid hex character '{c}'");
        }

        // Read a MissingBlob struct from native memory.
        // Layout: [IntPtr address][32-byte blob_hash]
        // size_t alignment: address is pointer-sized, so stride = sizeof(IntPtr) + 32,
        // padded to pointer alignment.
        internal static (string address, string hashHex) ReadMissingBlob(IntPtr ptr)
        {
            int ptrSize = IntPtr.Size;
            var addrPtr = Marshal.ReadIntPtr(ptr);
            string address = Marshal.PtrToStringAnsi(addrPtr) ?? string.Empty;
            var hash = new byte[32];
            Marshal.Copy(ptr + ptrSize, hash, 0, 32);
            return (address, HashToHex(hash));
        }

        // Returns the natural stride of ACMissingBlob in native memory.
        // struct { const char* address; uint8_t blob_hash[32]; }
        // On 64-bit: 8 + 32 = 40 bytes (no padding needed, 8-byte aligned).
        // On 32-bit: 4 + 32 = 36 bytes.
        internal static int MissingBlobStride => IntPtr.Size + 32;
    }
}
