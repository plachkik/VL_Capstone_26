using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

public class YoloARRaycastManager : MonoBehaviour,
    DbscanClassAnchors.ISoAProvider,
    DbscanClassAnchors.IWorldPositionsProvider
{
    [Header("Dependencies")]
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private ARPlaneManager planeManager; // optional but recommended
    [SerializeField] private Camera arCamera;
    [SerializeField] private XROrigin xrOrigin;

    
    [SerializeField] private CHARAYoloWorker yoloWorker; // Source of queued rays
[Header("Model Input Size (must match YoloWorker)")]
    [SerializeField] private int modelInputWidth = 640;
    [SerializeField] private int modelInputHeight = 640;

    [Header("Trackable Types")]
    [SerializeField] private TrackableType trackables =
        TrackableType.PlaneWithinPolygon |
        TrackableType.FeaturePoint |
        TrackableType.Depth;

    [Header("Anchor Settings")]
    [Tooltip("If true, create a single parent ARAnchor (on first hit) and store positions relative to it.")]
    [SerializeField] private bool useParentAnchorSpace = true;

    [Tooltip("Optional parent transform used to organize the anchor in hierarchy. Must NOT be under XROrigin.")]
    [SerializeField] private Transform anchorsRoot;

    

    [Header("Queued Ray Consumption")]
    [Tooltip("If set, this component will automatically dequeue rays from the worker each frame.")]
    [SerializeField] private bool autoConsumeQueuedRays = true;

    [Tooltip("Maximum number of queued rays to process per frame (to avoid long stalls). 0 = process all.")]
    [SerializeField] private int maxQueuedRaysPerFrame = 0;
    // ----------------------------
    // SoA storage (NativeLists)
    // ----------------------------
    // Now: positions relative to the parent anchor (if enabled), otherwise world positions.
    public NativeList<float3> LocalPositions;
    public NativeList<int> ClassIds;
    public NativeList<float> Confidences;
    public NativeList<int> FrameIndices;
    public NativeList<int> DetectionIndices;
    public NativeList<int> PointIds;

    // Optional: keep world positions for debugging / validation / world-space clustering.
    public NativeList<float3> WorldPositions;

    private readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>(16);

    private ARAnchor _parentAnchor;
    private int _nextPointId = 1;

    private void Awake()
    {
        if (!raycastManager) raycastManager = FindFirstObjectByType<ARRaycastManager>();
        if (!anchorManager)  anchorManager  = FindFirstObjectByType<ARAnchorManager>();
        if (!planeManager)   planeManager   = FindFirstObjectByType<ARPlaneManager>();
        if (!arCamera)       arCamera       = Camera.main;
        if (!xrOrigin)       xrOrigin       = FindFirstObjectByType<XROrigin>();

        if (anchorsRoot == null)
        {
            // Create a scene-root object to keep anchors organized.
            var go = new GameObject("AnchorsRoot");
            anchorsRoot = go.transform;
        }

        LocalPositions   = new NativeList<float3>(Allocator.Persistent);
        WorldPositions   = new NativeList<float3>(Allocator.Persistent);
        ClassIds         = new NativeList<int>(Allocator.Persistent);
        Confidences      = new NativeList<float>(Allocator.Persistent);
        FrameIndices     = new NativeList<int>(Allocator.Persistent);
        DetectionIndices = new NativeList<int>(Allocator.Persistent);
        PointIds         = new NativeList<int>(Allocator.Persistent);
    }

    private void Update()
    {
        if (!autoConsumeQueuedRays || yoloWorker == null) return;
        ProcessQueuedRaysFromWorker(yoloWorker, maxQueuedRaysPerFrame);
    }

    /// <summary>
    /// Dequeues rays that were generated at detection time (in CHARAYoloWorker) and performs AR raycasts.
    /// This avoids pose mismatch between detection and raycast.
    /// </summary>
    public void ProcessQueuedRaysFromWorker(CHARAYoloWorker workerSource, int maxToProcess = 0)
    {
        if (workerSource == null) return;
        if (!raycastManager) return;

        int processed = 0;
        while (workerSource.TryDequeueRay(out var q))
        {
            // Respect max per frame (0 = unlimited)
            if (maxToProcess > 0 && processed >= maxToProcess)
                break;

            _hits.Clear();
            if (!raycastManager.Raycast(q.ray, _hits, trackables))
            {
                processed++;
                continue;
            }

            // Ensure we have a stable parent anchor if requested.
            if (useParentAnchorSpace && _parentAnchor == null)
            {
                _parentAnchor = CreateAnchorFromHitAsync(_hits[0]).Result;
                if (_parentAnchor != null && anchorsRoot != null)
                    _parentAnchor.transform.SetParent(anchorsRoot, worldPositionStays: true);
            }

            for (int h = 0; h < _hits.Count; h++)
            {
                Pose hitPoseWorld = _hits[h].pose;
                float3 worldPos = new float3(hitPoseWorld.position.x, hitPoseWorld.position.y, hitPoseWorld.position.z);
                WorldPositions.Add(worldPos);

                float3 storedPos;
                if (useParentAnchorSpace && _parentAnchor != null)
                {
                    Vector3 anchorLocal = _parentAnchor.transform.InverseTransformPoint(hitPoseWorld.position);
                    storedPos = new float3(anchorLocal.x, anchorLocal.y, anchorLocal.z);
                }
                else
                {
                    storedPos = worldPos;
                }

                LocalPositions.Add(storedPos);
                ClassIds.Add(q.classIndex);
                Confidences.Add(q.confidence);
                FrameIndices.Add(q.unityFrame);
                DetectionIndices.Add(q.detectionIndex);
                PointIds.Add(_nextPointId++);
            }

            processed++;
        }
    }


    private void OnDestroy()
    {
        if (LocalPositions.IsCreated)   LocalPositions.Dispose();
        if (WorldPositions.IsCreated)   WorldPositions.Dispose();
        if (ClassIds.IsCreated)         ClassIds.Dispose();
        if (Confidences.IsCreated)      Confidences.Dispose();
        if (FrameIndices.IsCreated)     FrameIndices.Dispose();
        if (DetectionIndices.IsCreated) DetectionIndices.Dispose();
        if (PointIds.IsCreated)         PointIds.Dispose();
    }

    /// <summary>
    /// Call ONCE per YOLO run.
    /// Feed it: detections and a frameIndex you manage.
    /// </summary>
    public void ProcessYoloRunOnce(IReadOnlyList<CHARAYoloWorker.BoundingBox> detections, int frameIndex)
    {
        // Preferred path: consume rays queued at detection time (pose-correct).
        if (yoloWorker != null && yoloWorker.QueuedRayCount > 0)
        {
            ProcessQueuedRaysFromWorker(yoloWorker, maxQueuedRaysPerFrame);
            return;
        }

        // Legacy fallback (pose may be mismatched if inference is async).
        if (detections == null || detections.Count == 0) return;
        if (!raycastManager || !arCamera) return;

        float srcW = Screen.width;
        float srcH = Screen.height;
        float dstW = modelInputWidth;
        float dstH = modelInputHeight;

        float scale = Mathf.Min(dstW / srcW, dstH / srcH);
        float scaledW = srcW * scale;
        float scaledH = srcH * scale;
        float offX = (dstW - scaledW) * 0.5f;
        float offY = (dstH - scaledH) * 0.5f;

        for (int i = 0; i < detections.Count; i++)
        {
            var det = detections[i];

            float modelX = det.cx;
            float modelY = det.cy;

            if (modelX < offX || modelX > offX + scaledW) continue;
            if (modelY < offY || modelY > offY + scaledH) continue;

            float screenX_topLeft = (modelX - offX) / scale;
            float screenY_topLeft = (modelY - offY) / scale;

            float screenY_bottomLeft = srcH - screenY_topLeft;
            var screenPoint = new Vector3(screenX_topLeft, screenY_bottomLeft, 0f);

            Ray ray = arCamera.ScreenPointToRay(screenPoint);

            _hits.Clear();
            if (!raycastManager.Raycast(ray, _hits, trackables))
                continue;

            if (useParentAnchorSpace && _parentAnchor == null)
            {
                _parentAnchor = CreateAnchorFromHitAsync(_hits[0]).Result;
                if (_parentAnchor != null && anchorsRoot != null)
                {
                    _parentAnchor.transform.SetParent(anchorsRoot, worldPositionStays: true);
                }
            }

            for (int h = 0; h < _hits.Count; h++)
            {
                Pose hitPoseWorld = _hits[h].pose;
                float3 worldPos = new float3(hitPoseWorld.position.x, hitPoseWorld.position.y, hitPoseWorld.position.z);
                WorldPositions.Add(worldPos);

                float3 storedPos;

                if (useParentAnchorSpace && _parentAnchor != null)
                {
                    Vector3 anchorLocal = _parentAnchor.transform.InverseTransformPoint(hitPoseWorld.position);
                    storedPos = new float3(anchorLocal.x, anchorLocal.y, anchorLocal.z);
                }
                else
                {
                    storedPos = worldPos;
                }

                LocalPositions.Add(storedPos);
                ClassIds.Add(det.classIndex);
                Confidences.Add(det.confidence);
                FrameIndices.Add(frameIndex);
                DetectionIndices.Add(i);
                PointIds.Add(_nextPointId++);
            }
        }
    }

    /// <summary>
    /// Creates a parent ARAnchor from an ARRaycastHit.
    /// Prefers plane-attached anchors when possible.
    /// </summary>
    private async Task<ARAnchor> CreateAnchorFromHitAsync(ARRaycastHit hit)
    {
        if (!anchorManager)
        {
            Debug.LogWarning("ARAnchorManager missing; cannot create anchor.");
            return null;
        }

        Pose pose = hit.pose;

        // 1) Prefer attaching to a plane if possible
        if (planeManager != null)
        {
            ARPlane plane = planeManager.GetPlane(hit.trackableId);
            if (plane != null && anchorManager.descriptor != null && anchorManager.descriptor.supportsTrackableAttachments)
            {
                var attached = anchorManager.AttachAnchor(plane, pose);
                if (attached != null)
                {
                    Debug.Log("Created anchor attached to plane.");
                    return attached;
                }
            }
        }

        // 2) Free anchor: TryAddAnchorAsync (recommended in ARF 6.1.1)
        var result = await anchorManager.TryAddAnchorAsync(pose);
        if (result.status.IsSuccess())
        {
            Debug.Log("Created free anchor (TryAddAnchorAsync).");
            return result.value;
        }

        Debug.LogWarning($"Failed to create free anchor. Status: {result.status}");
        return null;
    }

    public void GetSoA(
        out NativeArray<float3> localPositions,
        out NativeArray<int> classIds,
        out NativeArray<float> confidences,
        out NativeArray<int> frameIndices,
        out NativeArray<int> detectionIndices,
        out NativeArray<int> pointIds)
    {
        localPositions = LocalPositions.AsArray();
        classIds = ClassIds.AsArray();
        confidences = Confidences.AsArray();
        frameIndices = FrameIndices.AsArray();
        detectionIndices = DetectionIndices.AsArray();
        pointIds = PointIds.AsArray();
    }

    // Optional: world-space access if you want it
    public void GetWorldPositions(out NativeArray<float3> worldPositions)
    {
        worldPositions = WorldPositions.AsArray();
    }

    /// <summary>
    /// Optional: if you want only "latest run" results instead of accumulating.
    /// </summary>
    public void ClearSoA()
    {
        LocalPositions.Clear();
        WorldPositions.Clear();
        ClassIds.Clear();
        Confidences.Clear();
        FrameIndices.Clear();
        DetectionIndices.Clear();
    }
}