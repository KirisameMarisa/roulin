using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.AddressableAssets.ResourceLocators;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
using Roulin.HotReload;
#endif

namespace Roulin
{
    // Snapshot from Roulin.GetLoadedBundles(). Call again for fresh state.
    public class LoadedBundleInfo
    {
        public string BlobHashHex;
        // Direct + transitive addresses reaching this blob — for leak diagnosis.
        public IReadOnlyList<string> AssetAddresses;
    }

    public static class Roulin
    {


        internal static IntPtr  Parcel   { get; private set; }
        public static string  BaseUrl  { get; private set; }
        public static string  LocalDir { get; private set; }

        static RoulinLocator        s_Locator;
        static RoulinBundleProvider s_BundleProvider;
        static RoulinAssetProvider  s_AssetProvider;
        static RoulinFetcher        s_Fetcher;
        static IHttpRequest          s_Http;        // owned only when default UnityWebRequestHttp
        static IRoulinCache         s_Cache;
        static bool                  s_Initialized;
        static string                s_LoadedRevision;

        public static RoulinFetcher Fetcher       => s_Fetcher;
        public static RoulinLocator Locator       => s_Locator;
        public static IRoulinCache  Cache         => s_Cache;
        public static bool           IsInitialized => s_Initialized;
        public static string         LoadedRevision => s_LoadedRevision;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
        // Hot-reload surface; Release builds omit this property entirely, so
        // call sites must wrap in `#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG`.
        public static HotReloadController HotReload { get; private set; }
#endif

        // In-flight HTTP cap for the default UnityWebRequest (HTTP/1.1) backend.
        // Custom IHttpRequest backends ignore this. Set before Initialize.
        public static int MaxConcurrentDownloads = 4;


        // Sync bootstrap, no I/O. Pass custom impls to override defaults.
        public static void Initialize(
            string baseUrl,
            string localDir      = null,
            IRoulinCache cache  = null,
            IHttpRequest  http   = null)
        {
            if (s_Initialized)
                throw new InvalidOperationException(
                    "Roulin is already initialized. Call Shutdown() first.");

            BaseUrl  = baseUrl.TrimEnd('/');
            LocalDir = localDir ?? Path.Combine(Application.persistentDataPath, "roulin");
            Directory.CreateDirectory(Path.Combine(LocalDir, "blobs"));
            Directory.CreateDirectory(Path.Combine(LocalDir, "index"));

            // Default backend self-throttles; custom backends own their cap.
            if (http == null)
                s_Http    = new RoulinFetchHttp(MaxConcurrentDownloads);
            
            s_Fetcher     = new RoulinFetcher(BaseUrl, s_Http);
            s_Cache       = cache ?? new RoulinCache(Path.Combine(LocalDir, "blobs"));
            s_Initialized = true;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
            // Must construct before any LoadAssetAsync resolves — installs the
            // InstanceProvider hijack and subscribes to Provide/Release events.
            // baseUrl is forwarded so the Controller starts its SSE Driver
            // (/watch/changes subscription) in the same step.
            HotReload = new HotReloadController(BaseUrl);
#endif

            Debug.Log($"[Roulin] Initialize: baseUrl={BaseUrl} localDir={LocalDir} http={s_Http.GetType().Name}");
        }


        public static AsyncOperationHandle<IResourceLocator> LoadCatalogAsync(
            string revision, CancellationToken ct = default)
        {
            AssertInitialized();
            var op = new LoadCatalogOperation(revision, ct);
            return Addressables.ResourceManager.StartOperation(op, default);
        }

        static async UniTask<IResourceLocator> LoadCatalogCoreAsync(string revision, CancellationToken ct)
        {
            Debug.Log($"[Roulin] LoadCatalogAsync start: revision={revision}");

            if (s_LoadedRevision == revision)
            {
                Debug.Log($"[Roulin] LoadCatalogAsync: revision already loaded, idempotent return");
                return s_Locator;
            }
            if (s_LoadedRevision != null)
                throw new InvalidOperationException(
                    "Roulin.LoadCatalogAsync: a revision is already loaded. " +
                    "Use SwitchRevisionAsync to swap revisions at runtime.");

            Parcel = await DownloadAndOpenAsync(revision, ct);

            s_Locator        = new RoulinLocator(Parcel);
            s_BundleProvider = new RoulinBundleProvider();
            s_AssetProvider  = new RoulinAssetProvider();

            Debug.Log($"[Roulin] LoadCatalogAsync: locator built. " +
                      $"keys={System.Linq.Enumerable.Count(s_Locator.Keys)} " +
                      $"locations={System.Linq.Enumerable.Count(s_Locator.AllLocations)}");

            if (s_Cache is RoulinCache defaultCache)
                defaultCache.Locator = s_Locator;

            Addressables.ResourceManager.ResourceProviders.Add(s_BundleProvider);
            Addressables.ResourceManager.ResourceProviders.Add(s_AssetProvider);
            Addressables.AddResourceLocator(s_Locator);

            // Dump for routing verification.
            var sb = new System.Text.StringBuilder("[Roulin] providers registered: ");
            foreach (var p in Addressables.ResourceManager.ResourceProviders)
                sb.Append(p.ProviderId).Append(", ");
            Debug.Log(sb.ToString());

            sb = new System.Text.StringBuilder("[Roulin] resource locators registered: ");
            foreach (var l in Addressables.ResourceLocators)
                sb.Append(l.LocatorId).Append(", ");
            Debug.Log(sb.ToString());

            s_LoadedRevision = revision;

            // Boot-time orphan GC: prev run's blobs held by live AssetBundles
            // could not be deleted then; safe to drop now.
            await s_Cache.PurgeOrphansAsync(s_Locator.GetPinnedBlobHashes());

            Debug.Log($"[Roulin] LoadCatalogAsync done: revision={revision}");
            return s_Locator;
        }

        sealed class LoadCatalogOperation : AsyncOperationBase<IResourceLocator>
        {
            readonly string            _revision;
            readonly CancellationToken _ct;

            public LoadCatalogOperation(string revision, CancellationToken ct)
            {
                _revision = revision;
                _ct       = ct;
            }

            protected override void Execute() => RunAsync().Forget();

            async UniTaskVoid RunAsync()
            {
                try
                {
                    var locator = await LoadCatalogCoreAsync(_revision, _ct);
                    Complete(locator, true, null);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Complete(null, false, e.Message);
                }
            }
        }


        // Game must Release() handles for outgoing addresses before calling;
        // bundles still held block their blob file delete and get retried
        // by the next LoadCatalogAsync's orphan sweep.
        public static async UniTask SwitchRevisionAsync(string newRevision, CancellationToken ct = default)
        {
            AssertCatalogLoaded();

            if (s_LoadedRevision == newRevision) return;  // idempotent

            // Old Parcel / Locator stay live during download → game keeps loading.
            IntPtr newParcel = await DownloadAndOpenAsync(newRevision, ct);
            var    newLocator = new RoulinLocator(newParcel);

            Addressables.RemoveResourceLocator(s_Locator);
            Addressables.AddResourceLocator(newLocator);

            IntPtr oldParcel = Parcel;
            s_Locator        = newLocator;
            Parcel           = newParcel;
            s_LoadedRevision = newRevision;

            if (s_Cache is RoulinCache defaultCache)
                defaultCache.Locator = newLocator;

            RoulinNative.rln_parcel_close(oldParcel);

            // GC old-revision-only blobs.
            await s_Cache.PurgeOrphansAsync(newLocator.GetPinnedBlobHashes());
        }

        // Downloads the aggregated Index FlatBuffer and opens it. The locator
        // builds entirely from flat entries[] + bundle_deps[]; no prefetch.
        static async UniTask<IntPtr> DownloadAndOpenAsync(string revision, CancellationToken ct)
        {
            string indexUrl   = $"{BaseUrl}/index/{revision}";
            string indexLocal = Path.Combine(LocalDir, "index", revision);
            await s_Fetcher.DownloadFileAsync(indexUrl, indexLocal, ct);

            IntPtr parcel = RoulinNative.rln_parcel_open(LocalDir, revision);
            if (parcel == IntPtr.Zero)
                throw new Exception(
                    $"Roulin: rln_parcel_open failed: {RoulinNative.LastError()}");
            return parcel;
        }



        // Bundles whose AssetBundle is currently held (= would block delete
        // on Windows). For pre-SwitchRevisionAsync verification or leak diag.
        public static IReadOnlyList<LoadedBundleInfo> GetLoadedBundles()
        {
            AssertCatalogLoaded();
            var hashes = RoulinBundleResource.GetLoadedBlobHashes();
            var result = new List<LoadedBundleInfo>(hashes.Count);
            foreach (var hash in hashes)
                result.Add(new LoadedBundleInfo {
                    BlobHashHex    = hash,
                    AssetAddresses = s_Locator.GetAssetAddressesForBlob(hash),
                });
            return result;
        }



        public static AsyncOperationHandle<T> LoadAsync<T>(string address)
        {
            AssertCatalogLoaded();
            return Addressables.LoadAssetAsync<T>(address);
        }

        public static void Release<T>(AsyncOperationHandle<T> handle)
        {
            Addressables.Release(handle);
        }

        public static void Release(AsyncOperationHandle handle)
        {
            Addressables.Release(handle);
        }



        public static void Shutdown()
        {
            s_Initialized = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || DEBUG
            HotReload?.Dispose();
            HotReload = null;
#endif

            if (s_Locator != null)
            {
                Addressables.RemoveResourceLocator(s_Locator);
                s_Locator = null;
            }
            if (s_BundleProvider != null)
            {
                Addressables.ResourceManager.ResourceProviders.Remove(s_BundleProvider);
                s_BundleProvider = null;
            }
            if (s_AssetProvider != null)
            {
                Addressables.ResourceManager.ResourceProviders.Remove(s_AssetProvider);
                s_AssetProvider = null;
            }
            if (Parcel != IntPtr.Zero)
            {
                RoulinNative.rln_parcel_close(Parcel);
                Parcel = IntPtr.Zero;
            }
            if(s_Http != null) 
            {
                s_Http.Dispose();
            }

            s_Http           = null;
            s_Cache          = null;
            s_Fetcher        = null;
            s_LoadedRevision = null;
        }



        static void AssertInitialized()
        {
            if (!s_Initialized)
                throw new InvalidOperationException(
                    "Roulin.Initialize() must be called before this method.");
        }

        static void AssertCatalogLoaded()
        {
            AssertInitialized();
            if (s_LoadedRevision == null)
                throw new InvalidOperationException(
                    "Roulin.LoadCatalogAsync() must complete before this method.");
        }

    }
}
