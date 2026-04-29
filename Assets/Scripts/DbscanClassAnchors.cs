using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class DbscanClassAnchors : MonoBehaviour
{
    [Header("Provider (must expose SoA + WorldPositions)")]
    [SerializeField] private YoloARRaycastManager providerBehaviour;

    [Header("AR Foundation")]
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private ARRaycastManager raycastManager;   // optional fallback
    [SerializeField] private ARPlaneManager planeManager;       // optional fallback
    [SerializeField] private Camera arCamera;                   // optional fallback
    [SerializeField] private UI ui;

    [Header("Iterative Run")]
    [Tooltip("If true, runs continuously every intervalSeconds. Otherwise call RunPassAsync() yourself.")]
    [SerializeField] private bool autoRun = false;

    [SerializeField] private float intervalSeconds = 0.5f;

    [Header("DBSCAN Params")]
    [SerializeField] private float eps = 0.10f;
    [SerializeField] private int minPts = 5;

    [Header("Cluster Filtering")]
    [SerializeField] private int minClusterSize = 10;

    [Header("Optional filtering")]
    [SerializeField] private bool filterByConfidence = false;
    [SerializeField] private float minConfidence = 0.0f;

    [Header("Tracking / Matching")]
    [Tooltip("Only consider matching clusters within this centroid distance (meters).")]
    [SerializeField] private float matchMaxCentroidDistance = 0.35f;

    [Tooltip("Minimum Jaccard overlap to accept a match (0..1).")]
    [SerializeField] private float minJaccardToMatch = 0.20f;

    [Tooltip("If centroid moves by >= this, recreate anchor.")]
    [SerializeField] private float recreateAnchorIfMoved = 0.01f;

    [Tooltip("If membership overlap drops below this, recreate anchor even if matched.")]
    [SerializeField] private float recreateAnchorIfJaccardBelow = 0.60f;

    [Tooltip("Delete a tracked cluster if it is unmatched for this many passes.")]
    [SerializeField] private int deleteAfterMissedPasses = 3;

    [Header("Spawn / Organization")]
    [SerializeField] private Transform anchorsRoot; // should NOT be under XROrigin

    [SerializeField] private GameObject bucketPrefab;
    [SerializeField] private float bucketVisualScale = 0.1f;
    [SerializeField] private GameObject cutterPrefab;
    [SerializeField] private float cutterVisualScale = 0.1f;
    [SerializeField] private GameObject drillPrefab;
    [SerializeField] private float drillVisualScale = 0.1f;
    [SerializeField] private GameObject grinderPrefab;
    [SerializeField] private float grinderVisualScale = 0.1f;
    [SerializeField] private GameObject hammerPrefab;
    [SerializeField] private float hammerVisualScale = 0.1f;
    [SerializeField] private GameObject knifePrefab;
    [SerializeField] private float knifeVisualScale = 0.1f;
    [SerializeField] private GameObject sawPrefab;
    [SerializeField] private float sawVisualScale = 0.1f;
    [SerializeField] private GameObject shovelPrefab;
    [SerializeField] private float shovelVisualScale = 0.1f;
    [SerializeField] private GameObject spannerPrefab;
    [SerializeField] private float spannerVisualScale = 0.1f;
    [SerializeField] private GameObject tackerPrefab;
    [SerializeField] private float tackerVisualScale = 0.1f;
    [SerializeField] private GameObject trowelPrefab;
    [SerializeField] private float trowelVisualScale = 0.1f;
    [SerializeField] private GameObject wrenchPrefab;
    [SerializeField] private float wrenchVisualScale = 0.1f;
    private bool _lastVizState = true;


    // Provider interfaces (UPDATED: includes pointIds)
    public interface ISoAProvider
    {
        void GetSoA(
            out NativeArray<float3> localPositions, // ignored here
            out NativeArray<int> classIds,
            out NativeArray<float> confidences,
            out NativeArray<int> frameIndices,
            out NativeArray<int> detectionIndices,
            out NativeArray<int> pointIds);
    }

    public interface IWorldPositionsProvider
    {
        void GetWorldPositions(out NativeArray<float3> worldPositions);
    }

    private ISoAProvider SoAProvider => providerBehaviour as ISoAProvider;
    private IWorldPositionsProvider WorldProvider => providerBehaviour as IWorldPositionsProvider;

    // ----------------------------
    // Internal types
    // ----------------------------
    private struct ClusterObs
    {
        public int ClassId;
        public float3 CentroidWorld;
        public int Count;
        public float AvgConfidence;

        // Stable membership IDs for overlap matching
        public HashSet<int> PointIds;
    }

    private class TrackedCluster
    {
        public int Uid;
        public int ClassId;
        public ARAnchor Anchor;
        public GameObject Visual;

        public float3 LastCentroid;
        public int LastCount;
        public float LastAvgConfidence;

        public HashSet<int> LastPointIds;

        public int MissedPasses;
    }

    // Export records (optional)
    private struct HitRecord
    {
        public int PointId;
        public int ClassId;
        public float3 HitWorld;
    }

    private struct ClusterRecord
    {
        public int ClusterUid;
        public int ClassId;
        public int Count;
        public float3 CentroidWorld;
        public float3 AnchorWorld;
        public float AvgConfidence;
        public string PointIdsPacked; // for export readability
    }

    // ----------------------------
    // State
    // ----------------------------
    private readonly Dictionary<int, TrackedCluster> _tracked = new();
    private int _nextClusterUid = 1;

    private readonly List<int> _neighbors = new(256);
    private readonly Queue<int> _seedQueue = new(256);

    private readonly List<ARRaycastHit> _arHits = new(8);

    private bool _isRunning;
    private float _nextRunTime;

    private void Awake()
    {
        if (!raycastManager) raycastManager = FindFirstObjectByType<ARRaycastManager>();
        if (!anchorManager)  anchorManager  = FindFirstObjectByType<ARAnchorManager>();
        if (!planeManager)   planeManager   = FindFirstObjectByType<ARPlaneManager>();
        if (!arCamera)       arCamera       = Camera.main;
        if (!ui)             ui             = FindAnyObjectByType<UI>();

        if (anchorsRoot == null)
        {
            var go = new GameObject("WorldAnchorsRoot");
            anchorsRoot = go.transform;
        }

        if (arCamera == null) arCamera = Camera.main;

        if (SoAProvider == null || WorldProvider == null)
        {
            Debug.LogError($"{nameof(DbscanClassAnchors)}: Provider must implement both ISoAProvider and IWorldPositionsProvider.");
        }
    }

    private void Update()
    {
        // --- visualization toggle from UI ---
        ApplyVisibility(ui.ShowVisualizations());

        if (!autoRun) return;
        if (Time.time < _nextRunTime) return;
        _nextRunTime = Time.time + Mathf.Max(0.05f, intervalSeconds);

        _ = RunPassAsync();
    }

    [ContextMenu("Run Iterative Pass")]
    public void RunPass_Menu() => _ = RunPassAsync();

    [ContextMenu("Clear All Tracked Clusters/Anchors")]
    public void ClearAll_Menu() => ClearAllTracked();

    public async Task RunPassAsync(bool exportThisPass = false, string exportFileName = "dbscan_iterative_pass.txt")
    {
        if (_isRunning) return;
        _isRunning = true;

        try
        {
            if (SoAProvider == null || WorldProvider == null)
                return;

            if (anchorManager == null)
            {
                Debug.LogError("ARAnchorManager missing.");
                return;
            }

            SoAProvider.GetSoA(out _, out var classIds, out var confidences, out _, out _, out var pointIds);
            WorldProvider.GetWorldPositions(out var worldPositions);

            if (!worldPositions.IsCreated || worldPositions.Length == 0)
                return;

            if (classIds.Length != worldPositions.Length ||
                confidences.Length != worldPositions.Length ||
                pointIds.Length != worldPositions.Length)
            {
                Debug.LogError("WorldPositions / classIds / confidences / pointIds length mismatch.");
                return;
            }

            if (minClusterSize < 1) minClusterSize = 1;

            // Optional confidence filtering
            NativeArray<float3> pts = worldPositions;
            NativeArray<int> cids = classIds;
            NativeArray<float> conf = confidences;
            NativeArray<int> pids = pointIds;

            NativeArray<float3> ptsF = default;
            NativeArray<int> cidsF = default;
            NativeArray<float> confF = default;
            NativeArray<int> pidsF = default;

            bool didFilter = false;

            // For optional export
            List<HitRecord> hitRecords = exportThisPass ? new List<HitRecord>(pts.Length) : null;
            List<ClusterRecord> clusterRecords = exportThisPass ? new List<ClusterRecord>(64) : null;

            try
            {
                if (filterByConfidence)
                {
                    didFilter = true;
                    FilterByConfidenceWithIds(pts, cids, conf, pids, minConfidence,
                        out ptsF, out cidsF, out confF, out pidsF, Allocator.Temp);

                    pts = ptsF;
                    cids = cidsF;
                    conf = confF;
                    pids = pidsF;
                }

                if (exportThisPass)
                {
                    for (int i = 0; i < pts.Length; i++)
                    {
                        hitRecords.Add(new HitRecord
                        {
                            PointId = pids[i],
                            ClassId = cids[i],
                            HitWorld = pts[i]
                        });
                    }
                }

                // Build observations from DBSCAN per class
                var observations = BuildObservationsPerClass(pts, cids, conf, pids);

                // Match + update anchors incrementally
                await MatchAndUpdateAsync(observations, clusterRecords);

                if (exportThisPass)
                {
                    ExportIterativePassToTxt(
                        hitRecords,
                        clusterRecords,
                        totalPoints: pts.Length,
                        fileName: exportFileName,
                        optionalHeader:
                            $"Iterative DBSCAN pass (eps={eps:F4}, minPts={minPts}, minClusterSize={minClusterSize}, " +
                            $"matchMaxDist={matchMaxCentroidDistance:F3}, minJaccard={minJaccardToMatch:F2})");
                }

                // Logging
                for (var c = _tracked.Values.GetEnumerator(); c.MoveNext();)
                {
                    Debug.Log($"Cluster {c.Current.Uid}: Class {c.Current.ClassId}, Count {c.Current.LastCount}, AvgConf {c.Current.LastAvgConfidence:F2}, " +
                              $"Centroid {c.Current.LastCentroid}, AnchorPos {(c.Current.Anchor != null ? c.Current.Anchor.transform.position.ToString() : "null")}");
                }
            }
            finally
            {
                if (didFilter)
                {
                    if (ptsF.IsCreated) ptsF.Dispose();
                    if (cidsF.IsCreated) cidsF.Dispose();
                    if (confF.IsCreated) confF.Dispose();
                    if (pidsF.IsCreated) pidsF.Dispose();
                }
            }
        }
        finally
        {
            _isRunning = false;
        }
    }

    // ----------------------------
    // Build observations (DBSCAN per class)
    // ----------------------------
    private List<ClusterObs> BuildObservationsPerClass(
        NativeArray<float3> pts,
        NativeArray<int> cids,
        NativeArray<float> conf,
        NativeArray<int> pids)
    {
        var perClassIndices = GroupIndicesByClass(cids);
        float epsSq = eps * eps;

        var observations = new List<ClusterObs>(64);

        foreach (var kv in perClassIndices)
        {
            int classId = kv.Key;
            List<int> indices = kv.Value;

            var classPts = new NativeArray<float3>(indices.Count, Allocator.Temp);
            var classConf = new NativeArray<float>(indices.Count, Allocator.Temp);
            var classPids = new NativeArray<int>(indices.Count, Allocator.Temp);

            try
            {
                for (int i = 0; i < indices.Count; i++)
                {
                    int src = indices[i];
                    classPts[i] = pts[src];
                    classConf[i] = conf[src];
                    classPids[i] = pids[src];
                }

                var clusters = Dbscan(classPts, epsSq, minPts);

                for (int ci = 0; ci < clusters.Count; ci++)
                {
                    var cluster = clusters[ci];
                    if (cluster.Count < minClusterSize) continue;

                    float3 centroid = ComputeCentroid(classPts, cluster);
                    float avgConf = ComputeAverageConfidence(classConf, cluster);

                    // stable membership set
                    var set = new HashSet<int>(cluster.Count);
                    for (int k = 0; k < cluster.Count; k++)
                        set.Add(classPids[cluster[k]]);

                    observations.Add(new ClusterObs
                    {
                        ClassId = classId,
                        CentroidWorld = centroid,
                        Count = cluster.Count,
                        AvgConfidence = avgConf,
                        PointIds = set
                    });
                }
            }
            finally
            {
                if (classPts.IsCreated) classPts.Dispose();
                if (classConf.IsCreated) classConf.Dispose();
                if (classPids.IsCreated) classPids.Dispose();
            }
        }

        return observations;
    }

    // ----------------------------
    // Matching + incremental anchor updates
    // ----------------------------
    private async Task MatchAndUpdateAsync(List<ClusterObs> obs, List<ClusterRecord> exportClusterRecordsOrNull)
    {
        // Group tracked clusters by class
        var trackedByClass = new Dictionary<int, List<TrackedCluster>>();
        foreach (var t in _tracked.Values)
        {
            if (!trackedByClass.TryGetValue(t.ClassId, out var list))
                trackedByClass[t.ClassId] = list = new List<TrackedCluster>();
            list.Add(t);
        }

        // Mark all tracked as missed initially
        foreach (var t in _tracked.Values)
            t.MissedPasses++;

        // For each observation, find best match among same-class tracked clusters
        for (int oi = 0; oi < obs.Count; oi++)
        {
            var o = obs[oi];

            TrackedCluster best = null;
            float bestJ = -1f;
            float bestDist = float.PositiveInfinity;

            if (trackedByClass.TryGetValue(o.ClassId, out var candidates))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];

                    float dist = math.distance(c.LastCentroid, o.CentroidWorld);
                    if (dist > matchMaxCentroidDistance)
                        continue;

                    float j = Jaccard(c.LastPointIds, o.PointIds);

                    // Primary: maximize overlap; secondary: minimize distance
                    if (j > bestJ || (math.abs(j - bestJ) < 1e-5f && dist < bestDist))
                    {
                        bestJ = j;
                        bestDist = dist;
                        best = c;
                    }
                }
            }

            if (best != null && bestJ >= minJaccardToMatch)
            {
                // Matched
                best.MissedPasses = 0;

                bool movedTooMuch = bestDist >= recreateAnchorIfMoved;
                bool membershipChangedTooMuch = bestJ < recreateAnchorIfJaccardBelow;

                if (movedTooMuch || membershipChangedTooMuch)
                {
                    await ReplaceAnchorAsync(best, o.CentroidWorld);
                    Debug.LogError($"Replaced anchor for cluster {best.Uid} due to " +
                               $"{(movedTooMuch ? $"movement ({bestDist:F3}m)" : "")}" +
                               $"{(membershipChangedTooMuch ? $"membership change (Jaccard {bestJ:F2})" : "")}");
                }

                best.LastCentroid = o.CentroidWorld;
                best.LastCount = o.Count;
                best.LastAvgConfidence = o.AvgConfidence;
                best.LastPointIds = o.PointIds;

                // Prevent double matching
                candidates.Remove(best);

                if (exportClusterRecordsOrNull != null)
                    exportClusterRecordsOrNull.Add(MakeClusterRecord(best));
            }
            else
            {
                // New cluster
                var created = await CreateNewTrackedClusterAsync(o);
                if (created != null && exportClusterRecordsOrNull != null)
                    exportClusterRecordsOrNull.Add(MakeClusterRecord(created));
            }
        }

        // Delete stale clusters
        var toDelete = new List<int>();
        foreach (var kv in _tracked)
        {
            if (kv.Value.MissedPasses >= deleteAfterMissedPasses)
                toDelete.Add(kv.Key);
        }

        for (int i = 0; i < toDelete.Count; i++)
            DeleteTrackedCluster(toDelete[i]);
    }

    private ClusterRecord MakeClusterRecord(TrackedCluster t)
    {
        Vector3 aPos = t.Anchor != null ? t.Anchor.transform.position : (Vector3)t.LastCentroid;

        // pack point ids (can be large; keep it readable)
        string packed = "";
        if (t.LastPointIds != null)
        {
            // limit to avoid giant files
            int limit = 200;
            int n = 0;
            var sb = new StringBuilder(1024);
            sb.Append('[');
            foreach (var id in t.LastPointIds)
            {
                if (n > 0) sb.Append(';');
                sb.Append(id);
                n++;
                if (n >= limit) { sb.Append(";..."); break; }
            }
            sb.Append(']');
            packed = sb.ToString();
        }

        return new ClusterRecord
        {
            ClusterUid = t.Uid,
            ClassId = t.ClassId,
            Count = t.LastCount,
            AvgConfidence = t.LastAvgConfidence,
            CentroidWorld = t.LastCentroid,
            AnchorWorld = new float3(aPos.x, aPos.y, aPos.z),
            PointIdsPacked = packed
        };
    }

    private async Task<TrackedCluster> CreateNewTrackedClusterAsync(ClusterObs o)
    {
        var anchor = await CreateAnchorAtWorldCentroidAsync(o.CentroidWorld);
        if (anchor == null) return null;

        var t = new TrackedCluster
        {
            Uid = _nextClusterUid++,
            ClassId = o.ClassId,
            Anchor = anchor,
            LastCentroid = o.CentroidWorld,
            LastCount = o.Count,
            LastAvgConfidence = o.AvgConfidence,
            LastPointIds = o.PointIds,
            MissedPasses = 0
        };

        anchor.name = $"Cluster_{t.Uid}_Class{t.ClassId}";
        anchor.transform.SetParent(anchorsRoot, true);

        t.Visual = SpawnVisualForClass(t.ClassId, anchor.transform);

        _tracked[t.Uid] = t;
        return t;
    }

    private async Task ReplaceAnchorAsync(TrackedCluster t, float3 newCentroidWorld)
    {
        if (t.Anchor != null)
            Destroy(t.Anchor.gameObject);

        var anchor = await CreateAnchorAtWorldCentroidAsync(newCentroidWorld);
        if (anchor == null) return;

        t.Anchor = anchor;
        anchor.name = $"Cluster_{t.Uid}_Class{t.ClassId}";
        anchor.transform.SetParent(anchorsRoot, true);

        t.Visual = SpawnVisualForClass(t.ClassId, anchor.transform, t.Visual);
    }

    private void DeleteTrackedCluster(int uid)
    {
        if (!_tracked.TryGetValue(uid, out var t)) return;
        if (t.Anchor != null) Destroy(t.Anchor.gameObject);
        _tracked.Remove(uid);
    }

    public void ClearAllTracked()
    {
        var keys = new List<int>(_tracked.Keys);
        for (int i = 0; i < keys.Count; i++)
            DeleteTrackedCluster(keys[i]);
    }

    // ----------------------------
    // Anchor creation (same logic as your original: free anchor + fallback)
    // ----------------------------
    private async Task<ARAnchor> CreateAnchorAtWorldCentroidAsync(float3 centroidWorld)
    {
        Pose pose = new Pose((Vector3)centroidWorld, Quaternion.identity);

        // Best case: free anchor supported
        {
            var result = await anchorManager.TryAddAnchorAsync(pose);
            if (result.status.IsSuccess())
                return result.value;
        }

        // Fallback: try attach to plane by raycasting from camera through centroid
        if (raycastManager != null && arCamera != null && planeManager != null)
        {
            Vector3 sp = arCamera.WorldToScreenPoint((Vector3)centroidWorld);
            if (sp.z > 0f)
            {
                var screenPoint = new Vector2(sp.x, sp.y);
                _arHits.Clear();

                if (raycastManager.Raycast(screenPoint, _arHits, TrackableType.PlaneWithinPolygon))
                {
                    var hit = _arHits[0];
                    var plane = planeManager.GetPlane(hit.trackableId);
                    if (plane != null && anchorManager.descriptor != null && anchorManager.descriptor.supportsTrackableAttachments)
                    {
                        var attached = anchorManager.AttachAnchor(plane, pose);
                        if (attached != null) return attached;
                    }
                }
            }
        }

        Debug.LogWarning("Failed to create ARAnchor for cluster centroid.");
        return null;
    }

    // ----------------------------
    // Prefab spawn
    // ----------------------------
    private GameObject SpawnVisualForClass(int classId, Transform parent, GameObject oldVisualToReplace = null)
    {
        if (oldVisualToReplace != null)
            Destroy(oldVisualToReplace);

        GameObject prefab = null;
        float scale = 0.1f;

        switch (classId)
        {
            case 0: prefab = bucketPrefab; scale = bucketVisualScale; break;
            case 1: prefab = cutterPrefab; scale = cutterVisualScale; break;
            case 2: prefab = drillPrefab; scale = drillVisualScale; break;
            case 3: prefab = grinderPrefab; scale = grinderVisualScale; break;
            case 4: prefab = hammerPrefab; scale = hammerVisualScale; break;
            case 5: prefab = knifePrefab; scale = knifeVisualScale; break;
            case 6: prefab = sawPrefab; scale = sawVisualScale; break;
            case 7: prefab = shovelPrefab; scale = shovelVisualScale; break;
            case 8: prefab = spannerPrefab; scale = spannerVisualScale; break;
            case 9: prefab = tackerPrefab; scale = tackerVisualScale; break;
            case 10: prefab = trowelPrefab; scale = trowelVisualScale; break;
            case 11: prefab = wrenchPrefab; scale = wrenchVisualScale; break;
        }

        if (prefab == null) return null;

        var visual = Instantiate(prefab, parent);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = Vector3.one * scale;

        // Class-specific adjustments, only for visualization (not affecting anchor logic)
        // Customized based on models uploaded
        if (prefab == knifePrefab) // e.g. rotate knife to point downwards
            visual.transform.localRotation = Quaternion.Euler(90, 0, 90);
        if (prefab == drillPrefab) // e.g. rotate drill to point downwards
            visual.transform.localRotation = Quaternion.Euler(0, 90, 0);

        visual.SetActive(_lastVizState);

        return visual;
    }

    // ----------------------------
    // Utilities: grouping + filtering + stats
    // ----------------------------
    private static Dictionary<int, List<int>> GroupIndicesByClass(NativeArray<int> classIds)
    {
        var dict = new Dictionary<int, List<int>>();
        for (int i = 0; i < classIds.Length; i++)
        {
            int cid = classIds[i];
            if (!dict.TryGetValue(cid, out var list))
            {
                list = new List<int>(128);
                dict[cid] = list;
            }
            list.Add(i);
        }
        return dict;
    }

    private static void FilterByConfidenceWithIds(
        NativeArray<float3> pts,
        NativeArray<int> cids,
        NativeArray<float> conf,
        NativeArray<int> pids,
        float minConf,
        out NativeArray<float3> ptsOut,
        out NativeArray<int> cidsOut,
        out NativeArray<float> confOut,
        out NativeArray<int> pidsOut,
        Allocator allocator)
    {
        int keep = 0;
        for (int i = 0; i < conf.Length; i++)
            if (conf[i] >= minConf) keep++;

        ptsOut = new NativeArray<float3>(keep, allocator);
        cidsOut = new NativeArray<int>(keep, allocator);
        confOut = new NativeArray<float>(keep, allocator);
        pidsOut = new NativeArray<int>(keep, allocator);

        int w = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            if (conf[i] < minConf) continue;
            ptsOut[w] = pts[i];
            cidsOut[w] = cids[i];
            confOut[w] = conf[i];
            pidsOut[w] = pids[i];
            w++;
        }
    }

    private static float ComputeAverageConfidence(NativeArray<float> conf, List<int> clusterIndices)
    {
        int n = clusterIndices.Count;
        if (n <= 0) return 0f;
        float sum = 0f;
        for (int i = 0; i < n; i++)
            sum += conf[clusterIndices[i]];
        return sum / n;
    }

    private static float3 ComputeCentroid(NativeArray<float3> pts, List<int> clusterIndices)
    {
        float3 sum = float3.zero;
        for (int i = 0; i < clusterIndices.Count; i++)
            sum += pts[clusterIndices[i]];
        return sum / math.max(1, clusterIndices.Count);
    }

    private static float Jaccard(HashSet<int> a, HashSet<int> b)
    {
        if (a == null || b == null) return 0f;
        if (a.Count == 0 && b.Count == 0) return 1f;
        if (a.Count == 0 || b.Count == 0) return 0f;

        // iterate smaller set
        HashSet<int> small = a.Count <= b.Count ? a : b;
        HashSet<int> large = a.Count <= b.Count ? b : a;

        int inter = 0;
        foreach (var x in small)
            if (large.Contains(x)) inter++;

        int uni = a.Count + b.Count - inter;
        return uni <= 0 ? 0f : (float)inter / uni;
    }

    // ----------------------------
    // DBSCAN (O(n^2)) - same as your original
    // ----------------------------
    private List<List<int>> Dbscan(NativeArray<float3> pts, float epsSq, int minPts)
    {
        int n = pts.Length;
        var labels = new int[n];
        int clusterId = 0;
        var clusters = new List<List<int>>();

        for (int i = 0; i < n; i++)
        {
            if (labels[i] != 0) continue;

            _neighbors.Clear();
            RegionQuery(pts, i, epsSq, _neighbors);

            if (_neighbors.Count < minPts)
            {
                labels[i] = -1;
                continue;
            }

            clusterId++;
            var cluster = new List<int>(_neighbors.Count);

            labels[i] = clusterId;
            cluster.Add(i);

            _seedQueue.Clear();
            for (int k = 0; k < _neighbors.Count; k++)
            {
                int p = _neighbors[k];
                if (p != i) _seedQueue.Enqueue(p);
            }

            while (_seedQueue.Count > 0)
            {
                int p = _seedQueue.Dequeue();

                if (labels[p] == -1)
                {
                    labels[p] = clusterId;
                    cluster.Add(p);
                }

                if (labels[p] != 0)
                    continue;

                labels[p] = clusterId;
                cluster.Add(p);

                _neighbors.Clear();
                RegionQuery(pts, p, epsSq, _neighbors);

                if (_neighbors.Count >= minPts)
                {
                    for (int k = 0; k < _neighbors.Count; k++)
                    {
                        int q = _neighbors[k];
                        if (labels[q] == 0 || labels[q] == -1)
                            _seedQueue.Enqueue(q);
                    }
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static void RegionQuery(NativeArray<float3> pts, int index, float epsSq, List<int> outNeighbors)
    {
        float3 p = pts[index];
        for (int i = 0; i < pts.Length; i++)
        {
            float3 d = pts[i] - p;
            if (math.lengthsq(d) <= epsSq)
                outNeighbors.Add(i);
        }
    }

    public void ApplyVisibility(bool show)
    {
        _lastVizState = show;

        foreach (var kv in _tracked)
        {
            var t = kv.Value;
            if (t == null) continue;

            if (t.Visual != null)
                t.Visual.SetActive(show);
            else if (t.Anchor != null)
            {
                // fallback: if visual wasn't tracked for some reason, toggle children
                for (int i = 0; i < t.Anchor.transform.childCount; i++)
                    t.Anchor.transform.GetChild(i).gameObject.SetActive(show);
            }
        }
    }

    // ----------------------------
    // Export (iterative pass)
    // ----------------------------
    private static void ExportIterativePassToTxt(
        List<HitRecord> hitRecords,
        List<ClusterRecord> clusterRecords,
        int totalPoints,
        string fileName = "dbscan_iterative_pass.txt",
        string optionalHeader = null)
    {
        string path;
#if UNITY_EDITOR
        string folder = Path.Combine(Application.dataPath, "DBSCAN_Reports");
        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);
        path = Path.Combine(folder, fileName);
#else
        path = Path.Combine(Application.persistentDataPath, fileName);
#endif

        var sb = new StringBuilder(8 * 1024);
        if (!string.IsNullOrEmpty(optionalHeader))
            sb.AppendLine(optionalHeader);

        sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Points: {totalPoints}");
        sb.AppendLine($"TrackedClusters: {clusterRecords?.Count ?? 0}");
        sb.AppendLine($"File: {path}");
        sb.AppendLine();

        sb.AppendLine("clusters: clusterUid,classId,count,avgConfidence,centroidX,centroidY,centroidZ,anchorX,anchorY,anchorZ,pointIds");
        if (clusterRecords != null)
        {
            for (int i = 0; i < clusterRecords.Count; i++)
            {
                var r = clusterRecords[i];
                sb.Append(r.ClusterUid).Append(',').Append(r.ClassId).Append(',').Append(r.Count).Append(',').Append(r.AvgConfidence.ToString("F6")).Append(',').Append(r.CentroidWorld.x.ToString("F6")).Append(',').Append(r.CentroidWorld.y.ToString("F6")).Append(',').Append(r.CentroidWorld.z.ToString("F6")).Append(',').Append(r.AnchorWorld.x.ToString("F6")).Append(',').Append(r.AnchorWorld.y.ToString("F6")).Append(',').Append(r.AnchorWorld.z.ToString("F6")).Append(',').Append('"').Append(r.PointIdsPacked ?? "").Append('"').AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("hits: pointId,classId,hitWorldX,hitWorldY,hitWorldZ");
        if (hitRecords != null)
        {
            for (int i = 0; i < hitRecords.Count; i++)
            {
                var h = hitRecords[i];
                sb.Append(h.PointId).Append(',').Append(h.ClassId).Append(',').Append(h.HitWorld.x.ToString("F6")).Append(',').Append(h.HitWorld.y.ToString("F6")).Append(',').Append(h.HitWorld.z.ToString("F6")).AppendLine();
            }
        }

        try
        {
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"Wrote iterative DBSCAN pass report: {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to write iterative DBSCAN pass report to {path}\n{e}");
        }
    }

    public int GetTrackedClusterCount() => _tracked.Count;
}
