using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Roulin
{
    public class RoulinLocator : IResourceLocator
    {
        public string LocatorId => "RoulinLocator";

        // Same address may resolve to multiple locations (e.g. a Scene and a
        // Prefab under one key); Locate returns all and Addressables filters
        // by resourceType at LoadAsync time.
        readonly Dictionary<string, List<RoulinResourceLocation>> _assetsByAddress
            = new(StringComparer.Ordinal);

        // AssetReference.RuntimeKey is engine-native asset id; Locate accepts both.
        readonly Dictionary<string, RoulinResourceLocation> _assetsById
            = new(StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<string, List<IResourceLocation>> _locationsByLabel
            = new(StringComparer.Ordinal);

        readonly List<IResourceLocation> _allLocations = new();

        public IEnumerable<object>            Keys         => _assetsByAddress.Keys;
        public IEnumerable<IResourceLocation> AllLocations => _allLocations;

        // Pinned set for orphan-GC at boot / SwitchRevisionAsync.
        public HashSet<string> GetPinnedBlobHashes()
        {
            var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in _allLocations)
                if (loc.Data is RoulinBundleData d && !string.IsNullOrEmpty(d.BlobHashHex))
                    pinned.Add(d.BlobHashHex);
            return pinned;
        }

        // Direct + transitive; maps a still-loaded hash back to its address.
        public IReadOnlyList<string> GetAssetAddressesForBlob(string blobHashHex)
        {
            var matches = new List<string>();
            if (string.IsNullOrEmpty(blobHashHex)) return matches;
            var visited = new HashSet<IResourceLocation>();
            foreach (var pair in _assetsByAddress)
            {
                foreach (var loc in pair.Value)
                {
                    visited.Clear();
                    if (DependsOnBlob(loc, blobHashHex, visited))
                    {
                        matches.Add(pair.Key);
                        break;
                    }
                }
            }
            return matches;
        }

        static bool DependsOnBlob(IResourceLocation loc, string targetHash, HashSet<IResourceLocation> visited)
        {
            if (!visited.Add(loc)) return false;
            if (loc.Data is RoulinBundleData d
                && string.Equals(d.BlobHashHex, targetHash, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!loc.HasDependencies) return false;
            foreach (var dep in loc.Dependencies)
                if (DependsOnBlob(dep, targetHash, visited)) return true;
            return false;
        }

        internal RoulinLocator(IntPtr parcel)
        {
            // Resolve the Index's type intern table to System.Type instances.
            // Address.type_idxs (from FFI) point into this array.
            int typesCount = (int)RoulinNative.rln_index_types_count(parcel);
            var types = new Type[typesCount];
            for (int i = 0; i < typesCount; i++)
            {
                var p = RoulinNative.rln_index_type_at(parcel, (UIntPtr)i);
                var name = p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
                types[i] = string.IsNullOrEmpty(name) ? null : Type.GetType(name, throwOnError: false);
            }

            var assetCtx    = new AssetCollectCtx();
            var assetHandle = GCHandle.Alloc(assetCtx);
            try     { RoulinNative.rln_parcel_foreach(parcel, OnCollectAsset, GCHandle.ToIntPtr(assetHandle)); }
            finally { assetHandle.Free(); }

            var depsCtx    = new BundleDepsCtx();
            var depsHandle = GCHandle.Alloc(depsCtx);
            try     { RoulinNative.rln_index_for_each_bundle_deps(parcel, OnCollectBundleDeps, GCHandle.ToIntPtr(depsHandle)); }
            finally { depsHandle.Free(); }

            // Bundle→bundle deps wired in a second pass once all bundles exist.
            var bundleByHash = new Dictionary<string, RoulinResourceLocation>(StringComparer.OrdinalIgnoreCase);

            RoulinResourceLocation GetOrCreateBundle(string hashHex, ulong bundleSize)
            {
                if (bundleByHash.TryGetValue(hashHex, out var existing))
                {
                    // bundle_size is uniform per bundle; first-entry-missing fallback.
                    if (existing.Data is RoulinBundleData d && d.BundleSize == 0 && bundleSize != 0)
                        d.BundleSize = (long)bundleSize;
                    return existing;
                }
                var loc = new RoulinResourceLocation(
                    internalId:   hashHex,
                    providerId:   RoulinBundleProvider.Id,
                    resourceType: typeof(IAssetBundleResource),
                    data: new RoulinBundleData {
                        BlobHashHex = hashHex,
                        BundleSize  = (long)bundleSize,
                    },
                    deps: new List<IResourceLocation>());
                bundleByHash[hashHex] = loc;
                _allLocations.Add(loc);
                return loc;
            }

            // Cover dep-only bundles (no addresses) that asset rows never visit.
            foreach (var (bundleHashHex, depHashes) in depsCtx.DepsByBundle)
            {
                GetOrCreateBundle(bundleHashHex, 0);
                foreach (var depHex in depHashes)
                    GetOrCreateBundle(depHex, 0);
            }

            // Flat [primary, ...transitive] matches stock Addressables and
            // keeps ProviderOperation chains DAG-safe.
            foreach (var row in assetCtx.Rows)
            {
                var bundleLoc = GetOrCreateBundle(row.bundleHashHex, row.bundleSize);

                var depList = new List<IResourceLocation> { bundleLoc };
                if (depsCtx.DepsByBundle.TryGetValue(row.bundleHashHex, out var closureHexes))
                {
                    foreach (var depHex in closureHexes)
                    {
                        if (string.Equals(depHex, row.bundleHashHex, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (bundleByHash.TryGetValue(depHex, out var depLoc))
                            depList.Add(depLoc);
                    }
                }

                // Pick the first resolved type from row.typeIdxs (typically 1
                // entry). Falls back to UnityEngine.Object when the parcel
                // didn't carry a type or Type.GetType failed.
                Type resType = typeof(UnityEngine.Object);
                if (row.typeIdxs != null)
                {
                    foreach (var idx in row.typeIdxs)
                    {
                        if (idx < types.Length && types[idx] != null)
                        {
                            resType = types[idx];
                            break;
                        }
                    }
                }

                var assetLoc = new RoulinResourceLocation(
                    internalId:   row.fullAddr,
                    providerId:   RoulinAssetProvider.Id,
                    resourceType: resType,
                    data: new RoulinAssetData { InternalName = row.fullAddr },
                    deps: depList);

                if (!_assetsByAddress.TryGetValue(row.fullAddr, out var bucket))
                {
                    bucket = new List<RoulinResourceLocation>();
                    _assetsByAddress[row.fullAddr] = bucket;
                }
                bucket.Add(assetLoc);
                _allLocations.Add(assetLoc);

                if (!string.IsNullOrEmpty(row.assetId))
                    _assetsById[row.assetId] = assetLoc;

                if (row.labels != null)
                {
                    foreach (var label in row.labels)
                    {
                        if (string.IsNullOrEmpty(label)) continue;
                        if (!_locationsByLabel.TryGetValue(label, out var labelBucket))
                        {
                            labelBucket = new List<IResourceLocation>();
                            _locationsByLabel[label] = labelBucket;
                        }
                        labelBucket.Add(assetLoc);
                    }
                }
            }

            UnityEngine.Debug.Log(
                $"[RoulinLocator] built: bundles={bundleByHash.Count}, " +
                $"unique_addresses={_assetsByAddress.Count}, " +
                $"(addresses serving 2+ locations — Locate returns all so Addressables filters by type)");
        }

        // P/Invoke callbacks must be static; instance delegates fail under IL2CPP.

        sealed class AssetCollectCtx
        {
            public readonly List<(string fullAddr, string bundleHashHex, ulong bundleSize, string[] labels, string assetId, uint[] typeIdxs)> Rows = new();
        }

        sealed class BundleDepsCtx
        {
            public readonly Dictionary<string, string[]> DepsByBundle
                = new(StringComparer.OrdinalIgnoreCase);
        }

        [MonoPInvokeCallback(typeof(RoulinNative.ForEachFn))]
        static void OnCollectAsset(
            IntPtr addrPtr, IntPtr blobHashPtr, ulong blobSize,
            IntPtr labelsPtr, UIntPtr labelsCount,
            IntPtr assetIdPtr,
            IntPtr typeIdxsPtr, UIntPtr typeIdxsCount,
            IntPtr userdata)
        {
            var ctx           = (AssetCollectCtx)GCHandle.FromIntPtr(userdata).Target;
            string addr       = Marshal.PtrToStringAnsi(addrPtr) ?? string.Empty;
            string[] labs     = RoulinNative.ReadStringArray(labelsPtr, labelsCount);
            string assetId    = assetIdPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(assetIdPtr);
            string bundleHash = HashHexFromPtr(blobHashPtr);
            uint[] typeIdxs   = RoulinNative.ReadUInt32Array(typeIdxsPtr, typeIdxsCount);
            ctx.Rows.Add((addr, bundleHash, blobSize, labs, assetId, typeIdxs));
        }

        [MonoPInvokeCallback(typeof(RoulinNative.BundleDepsFn))]
        static void OnCollectBundleDeps(
            IntPtr bundleHashPtr, IntPtr depsPtr, UIntPtr depsCount, IntPtr userdata)
        {
            var ctx           = (BundleDepsCtx)GCHandle.FromIntPtr(userdata).Target;
            string bundleHash = HashHexFromPtr(bundleHashPtr);
            string[] deps     = RoulinNative.ReadStringArray(depsPtr, depsCount);
            ctx.DepsByBundle[bundleHash] = deps ?? Array.Empty<string>();
        }

        static string HashHexFromPtr(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            var bytes = new byte[32];
            Marshal.Copy(ptr, bytes, 0, 32);
            return RoulinNative.HashToHex(bytes);
        }

        // Lookup priority: address → asset_id → label. Type filter is a no-op
        // (AssetBundle.LoadAsset<T> decides subtype at load time).
        public bool Locate(object key, Type resourceType, out IList<IResourceLocation> locations)
        {
            locations = null;
            if (key is not string keyStr)
            {
                UnityEngine.Debug.Log($"[RoulinLocator] Locate: non-string key ({key?.GetType().Name}) → miss");
                return false;
            }

            if (_assetsByAddress.TryGetValue(keyStr, out var bucket) && bucket.Count > 0)
            {
                if (resourceType != null && bucket.Count > 1)
                {
                    var filtered = new List<IResourceLocation>(bucket.Count);
                    foreach (var l in bucket)
                        if (resourceType.IsAssignableFrom(l.ResourceType))
                            filtered.Add(l);
                    if (filtered.Count > 0)
                    {
                        locations = filtered;
                        return true;
                    }
                }
                locations = new List<IResourceLocation>(bucket);
                return true;
            }
            if (_assetsById.TryGetValue(keyStr, out var idLoc))
            {
                locations = new List<IResourceLocation> { idLoc };
                UnityEngine.Debug.Log($"[RoulinLocator] Locate: '{keyStr}' → id hit (addr={idLoc.PrimaryKey})");
                return true;
            }
            if (_locationsByLabel.TryGetValue(keyStr, out var labelBucket) && labelBucket.Count > 0)
            {
                locations = new List<IResourceLocation>(labelBucket);
                UnityEngine.Debug.Log($"[RoulinLocator] Locate: '{keyStr}' → label hit ({labelBucket.Count})");
                return true;
            }
            UnityEngine.Debug.Log($"[RoulinLocator] Locate: '{keyStr}' → MISS (falling through to Addressables default)");
            return false;
        }
    }
}
