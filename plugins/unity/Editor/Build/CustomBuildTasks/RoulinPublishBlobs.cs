using Roulin.Editor.Build.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Roulin.Editor;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using System.Runtime.InteropServices;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    internal sealed class RoulinPublishBlobs : RoulinBuildTaskBase
    {
#pragma warning disable 649
        [InjectContext(ContextUsage.In)]
        private IBundleBuildResults _sbpResults;
#pragma warning restore 649
        public override int Version => 1;

        public RoulinServerClient Server { get; set; }
        public MetaClient Meta { get; set; }
        public string OutputDir { get; set; }
        public bool Verbose { get; set; }


        [DllImport("roulin_core", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe void rln_compute_blake3(void* data, UIntPtr len, byte* outHash);

        public static unsafe string Blake3Hex(byte[] data)
        {
            var hash = new byte[32];
            fixed (byte* dp = data, hp = hash)
            {
                rln_compute_blake3(dp, (UIntPtr)data.Length, hp);
            }

            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public override ReturnCode Run()
        {
            if (Server == null)
            {
                throw new InvalidOperationException(
                    "RoulinPublishBlobs.Server is null — set before adding to task list");
            }

            try
            {
                return RunCore();
            }
            finally
            {
                // Last consumer; release RoulinUnityBlob dict (~1 GB) for GC.
                roulinContext.BlobMetasByBundle.Clear();
            }
        }

        // Cap in-flight HTTP. Unbounded fan-out exhausts HttpClient's connection
        // pool and surfaces as "invalid or unrecognized response" via stale sockets.
        private const int MaxParallel = 4;

        private ReturnCode RunCore()
        {
            var inputs = roulinContext.BundleInputs;
            var blobMetasByBundle = roulinContext.BlobMetasByBundle;

            var counters = new Counters();
            var total = _sbpResults.BundleInfos.Count;

            using var srcToken = new CancellationTokenSource();
            using var sem = new SemaphoreSlim(0);
            using var throttle = new SemaphoreSlim(MaxParallel, MaxParallel);
            var tasks = new List<Task>(total);

            // BundleInfos is stable post-writer; direct iteration is safe.
            foreach (var kv in _sbpResults.BundleInfos)
            {
                var bi = inputs[kv.Key];
                var fileName = kv.Value.FileName;
                tasks.Add(Task.Run(async () =>
                {
                    await throttle.WaitAsync(srcToken.Token);
                    try
                    {
                        await UploadOne(bi, fileName, blobMetasByBundle, counters, srcToken.Token);
                    }
                    finally
                    {
                        throttle.Release();
                        sem.Release();
                    }
                }, srcToken.Token));
            }

            // EditorUtility requires main thread; this loop owns the progress bar.
            for (int i = 0; i < total; i++)
            {
                sem.Wait(srcToken.Token);
                var done = i + 1;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Roulin Build",
                        $"Publishing blobs… [{done}/{total}]",
                        (float)done / Math.Max(1, total)))
                {
                    srcToken.Cancel();
                    break;
                }
            }

            // Drain stragglers. SBP convention: WaitAny over WhenAll for fast-fail.
            Task.WaitAny(Task.WhenAll(tasks));

            // blob-body upload throws on non-2xx → fatal. blob_meta failures
            // are swallowed inside UploadOne, so anything reaching here is fatal.
            var fatal = 0;
            foreach (var t in tasks)
            {
                if (t.Exception == null)
                {
                    continue;
                }
                fatal++;
                Debug.LogException(t.Exception);
            }
            if (fatal > 0 || srcToken.IsCancellationRequested)
            {
                return ReturnCode.Error;
            }

            Debug.Log(
                $"[RoulinPublishBlobs] {counters.Uploaded} uploaded, " +
                $"{counters.Skipped} skipped (unchanged)" +
                (counters.BlobMeta > 0 ? $", {counters.BlobMeta} blob_meta sidecar(s)" : "") +
                $" — total {RoulinUtil.FormatBytes(counters.TotalBytes)}");

            return ReturnCode.Success;
        }

        // Reference-held so async workers can Interlocked.Increment fields
        // (ref params aren't allowed in async methods).
        private sealed class Counters
        {
            public int Uploaded;
            public int Skipped;
            public int BlobMeta;
            public long TotalBytes;
        }

        // Mutates only the BundleInput it owns + counters via Interlocked.
        private async Task UploadOne(
            BundleInput bi,
            string fileName,
            Dictionary<string, RoulinUnityBlob> blobMetasByBundle,
            Counters counters,
            CancellationToken ct)
        {
            var path = Path.IsPathRooted(fileName)
                ? fileName
                : Path.Combine(OutputDir, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"SBP reported bundle '{bi.Name}' but file is missing", path);
            }

            var bytes = File.ReadAllBytes(path);
            var hash = Blake3Hex(bytes);

            if (await Server.BlobExists(hash, ct))
            {
                Interlocked.Increment(ref counters.Skipped);
                if (Verbose)
                {
                    Debug.Log(
                        $"[RoulinPublishBlobs]   skipped  {bi.Name,-32} {RoulinUtil.FormatBytes(bytes.LongLength),10} " +
                        $"→ {hash[..12]}… (unchanged)");
                }
            }
            else
            {
                await Server.PostBlob(bytes, ct);
                Interlocked.Increment(ref counters.Uploaded);
                if (Verbose)
                {
                    Debug.Log(
                        $"[RoulinPublishBlobs]   uploaded {bi.Name,-32} {RoulinUtil.FormatBytes(bytes.LongLength),10} " +
                        $"→ {hash[..12]}…");
                }
            }

            bi.BinaryHash = hash;
            bi.SizeBytes = bytes.LongLength;
            Interlocked.Add(ref counters.TotalBytes, bytes.LongLength);

            // blob_meta is idempotent on server; failures are non-fatal —
            // game still ships, only warm-rebuild speedup is forfeited.
            if (blobMetasByBundle.TryGetValue(bi.Name, out var unityBlob))
            {
                var envelope = new RoulinBlobMeta(unityBlob, hash);
                try
                {
                    await Meta.PublishBlobMeta(hash, envelope);
                    Interlocked.Increment(ref counters.BlobMeta);
                    if (Verbose)
                    {
                        Debug.Log(
                            $"[RoulinPublishBlobs]   blob_meta  {bi.Name,-32} " +
                            $"→ {hash[..12]}… ({unityBlob.assets.Count} asset(s))");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RoulinPublishBlobs] blob_meta upload failed for {bi.Name}: {ex.Message} " +
                        "— warm rebuild speedup for this blob is lost, build continues");
                }
            }
        }
    }
}
