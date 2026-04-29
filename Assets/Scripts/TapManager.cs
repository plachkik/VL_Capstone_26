using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

[RequireComponent(typeof(ARRaycastManager))]
public class TapManager : MonoBehaviour
{
    [Header("AR Managers")]
    [Tooltip("If left empty, TapManager will try to find one on the same GameObject.")]
    [SerializeField] private ARPlaneManager planeManager;
    [Tooltip("If left empty, TapManager will try to find one on the same GameObject.")]
    [SerializeField] private ARAnchorManager anchorManager;

    [Header("Placement")]
    [Tooltip("How many anchors must be placed before planes are hidden and gated scripts can run.")]
    [SerializeField] private int requiredAnchorCount = 4;
    [Tooltip("Optional: A script to keep disabled until all anchors have been placed.")]
    [SerializeField] private MonoBehaviour gatedBehaviour;

    // Stored anchors + their worldspace positions
    private ARAnchor[] anchors;
    private Vector3[] anchorWorldPositions;
    private int anchorsPlaced = 0;

    private ARRaycastManager arRaycastManager;
    private List<ARRaycastHit> hits = new List<ARRaycastHit>();

    private void Awake()
    {
        arRaycastManager = GetComponent<ARRaycastManager>();

        // Resolve optional dependencies if not wired in the inspector.
        if (planeManager == null) planeManager = GetComponent<ARPlaneManager>();
        if (anchorManager == null) anchorManager = GetComponent<ARAnchorManager>();

        // Clamp required anchors to a sensible minimum.
        requiredAnchorCount = Mathf.Max(1, requiredAnchorCount);

        anchors = new ARAnchor[requiredAnchorCount];
        anchorWorldPositions = new Vector3[requiredAnchorCount];

        // Ensure gated behaviour doesn't run until anchors are ready.
        if (gatedBehaviour != null) gatedBehaviour.enabled = false;
    }

    private void OnEnable()
    {
        EnhancedTouchSupport.Enable();

        // Attempt to show planes while we are still placing anchors.
        if (!AreAllAnchorsPlaced)
        {
            ShowPlanes();
        }
    }

    private void OnDisable()
    {
        EnhancedTouchSupport.Disable();
    }

    void Update()
    {
        // Stop accepting taps once all anchors are placed.
        if (AreAllAnchorsPlaced) return;
        
        HandleTouchInput();
    }

    // -------------------------
    // DEVICE TOUCH INPUT
    // -------------------------
   private void HandleTouchInput()
   {
       var touches = ETouch.activeTouches;
       if (touches.Count == 0) return;

       // Only register the tap once (avoid continuous placement while finger is down).
       ETouch touch = touches[0];
       if (touch.phase != UnityEngine.InputSystem.TouchPhase.Began) return;

       Vector2 screenPos = touch.screenPosition;

       if (arRaycastManager.Raycast(screenPos, hits, TrackableType.PlaneWithinPolygon))
       {
           ARRaycastHit bestHit= hits[0];
           CreateAnchorFromHitAsync(bestHit).ContinueWith(task =>
           {
               if (task.Result != null)
               {
                   Debug.Log("Anchor placed successfully.");
                   anchors[anchorsPlaced] = task.Result;
                   anchorWorldPositions[anchorsPlaced] = task.Result.transform.position; // worldspace
                   anchorsPlaced++;

                   if (AreAllAnchorsPlaced)
                   {
                       HidePlanes();
                       if (gatedBehaviour != null) gatedBehaviour.enabled = true;
                   }
               }
               else
               {
                   Debug.LogWarning("Failed to place anchor from hit.");
               }
           }, TaskScheduler.FromCurrentSynchronizationContext());
       }
   }


    // -------------------------
    // PLACE ANCHOR + STORE HIT
    // -------------------------

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
    // private void TryPlaceAnchor(Pose pose)
    // {
    //     if (AreAllAnchorsPlaced) return;

    //     // Face camera horizontally (optional, but keeps anchor orientation stable/consistent).
    //     Vector3 pos = pose.position;
    //     Vector3 lookDir = Camera.main != null ? (Camera.main.transform.position - pos) : Vector3.forward;
    //     lookDir.y = 0;
    //     Quaternion lookRot = (lookDir.sqrMagnitude > 0.0001f)
    //         ? Quaternion.LookRotation(lookDir)
    //         : Quaternion.identity;

    //     Pose anchorPose = new Pose(pos, lookRot);

    //     ARAnchor created = null;

    //     // Preferred: create a real ARAnchor using ARAnchorManager (device).
    //     if (anchorManager != null)
    //     {
    //         created = anchorManager.AddAnchor(anchorPose);
    //     }

    //     // Fallback: create a GameObject with an ARAnchor component (editor/testing).
    //     if (created == null)
    //     {
    //         var go = new GameObject($"Anchor_{anchorsPlaced}");
    //         go.transform.SetPositionAndRotation(anchorPose.position, anchorPose.rotation);
    //         created = go.AddComponent<ARAnchor>();
    //     }

    //     anchors[anchorsPlaced] = created;
    //     anchorWorldPositions[anchorsPlaced] = created.transform.position; // worldspace
    //     anchorsPlaced++;

    //     if (AreAllAnchorsPlaced)
    //     {
    //         HidePlanes();
    //         if (gatedBehaviour != null) gatedBehaviour.enabled = true;
    //     }
    // }

    // -------------------------
    // PLANE VISIBILITY
    // -------------------------
    public void ShowPlanes()
    {
        if (planeManager == null) return;

        planeManager.enabled = true;
        foreach (var plane in planeManager.trackables)
        {
            plane.gameObject.SetActive(true);
        }
    }

    public void HidePlanes()
    {
        if (planeManager == null) return;

        // Disable manager to stop updates AND hide existing planes.
        planeManager.enabled = false;
        foreach (var plane in planeManager.trackables)
        {
            plane.gameObject.SetActive(false);
        }
    }

    // -------------------------
    // GATING + GETTERS
    // -------------------------
    public bool AreAllAnchorsPlaced => anchorsPlaced >= requiredAnchorCount;

    /// <summary>
    /// Call this from another script's Update() as a gate:
    /// if (!tapManager.CanRunGatedUpdates()) return;
    /// </summary>
    public bool CanRunGatedUpdates() => AreAllAnchorsPlaced;

    /// <summary>
    /// Worldspace positions of placed anchors. Unfilled slots (if any) will be Vector3.zero.
    /// </summary>
    public Vector3[] AnchorWorldPositions => anchorWorldPositions;

    /// <summary>
    /// The ARAnchor components created for each placement.
    /// </summary>
    public ARAnchor[] Anchors => anchors;

    public int AnchorsPlacedCount => anchorsPlaced;
}
