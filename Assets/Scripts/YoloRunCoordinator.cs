using System.Threading.Tasks;
using UnityEngine;

public class YoloRunCoordinator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CHARAYoloWorker yoloSource;
    [SerializeField] private YoloARRaycastManager raycastRecorder;
    [SerializeField] private YoloARRaycastHitVisualizer hitVisualizer;
    [SerializeField] private DbscanClassAnchors dbscanAnchorManager;
    // [SerializeField] private TapManager tapManager;
    [SerializeField] private UI ui;
    [SerializeField] private Counter Counter;

    [Header("Run Settings")]
    [Tooltip("Run YOLO + raycast every N frames. 1 = every frame.")]
    [SerializeField, Range(1, 60)] private int runEveryNFrames = 30;
    [Tooltip("Max detections to run. If 0 , never stop detecting.")]
    [SerializeField, Range(1, 1000)] private int detectionCutoff = 100;

    private int _detectionsRan = 0;

    private void Awake()
    {
        // Auto-wire if not set in Inspector (runtime-safe).
        if (!yoloSource) yoloSource = FindFirstObjectByType<CHARAYoloWorker>();
        if (!raycastRecorder) raycastRecorder = FindFirstObjectByType<YoloARRaycastManager>();
        if (!dbscanAnchorManager) dbscanAnchorManager = FindFirstObjectByType<DbscanClassAnchors>();
        if (!ui) ui = FindFirstObjectByType<UI>();

        // Optional: warn loudly so you notice miswiring immediately.
        if (!ui)
            Debug.LogWarning($"{nameof(YoloRunCoordinator)}: Calibration not found. Gating will be bypassed.");
    }

    private void OnEnable()
    {
        _detectionsRan = 0;
    }

    private void Update()
    {
        // Gate safely: if calibrator is missing, either bypass OR block—your choice.
        // Bypass gating when missing:
        if (ui == null || !ui.CanRunGatedUpdates()) 
        {
            if (ui.CalibrationComplete) Counter.UpdateText("Detection off.");
            if (!ui.ShowBoundingBoxes()) yoloSource.HideAllBoxes();
            return;
        }

        if (!yoloSource || !raycastRecorder) return;

        if (!yoloSource || !raycastRecorder) return;

        // 1) Cutoff gate: trigger calibration exactly once
        if (_detectionsRan >= detectionCutoff && detectionCutoff != 0)
        {
            yoloSource.HideAllBoxes();
            hitVisualizer.ClearAll();
            Debug.Log("Detection completed.");
            return;
        }

        // 2) Frame throttle gate
        if (runEveryNFrames > 1 && (Time.frameCount % runEveryNFrames) != 0)
            return;

        // Trigger inference; rays are generated at detection time in your YOLO worker
        yoloSource.RunInferenceOnce();
        _detectionsRan++;

        _ = dbscan(); // fire-and-forget async

        Counter.UpdateCount(dbscanAnchorManager.GetTrackedClusterCount());
    }

    private async Task dbscan()
    {
        if (dbscanAnchorManager != null)
        {
            Debug.Log("Running DBSCAN clustering on recorded raycast hits...");
            await dbscanAnchorManager.RunPassAsync(exportThisPass: true);
            Debug.Log("DBSCAN clustering complete.");
        }
        else
        {
            Debug.LogWarning("No DbscanClassAnchors assigned; skipping DBSCAN.");
        }
    }

    // private async Task CalibrateAsync()
    // {
    //     // Wait for any in-flight inference to finish
    //     while (yoloSource != null && yoloSource.inferenceIsRunning)
    //         await Task.Yield();

    //     if (yoloSource == null) return;

    //     // Hide debug boxes now that scanning is complete
    //     yoloSource.HideAllBoxes();

    //     // IMPORTANT: If your raycast recorder consumes queued rays over time,
    //     // you may want to wait until the ray queue is empty before clustering.
    //     // If you have a property like yoloSource.HasQueuedRays or raycastRecorder.HasPendingRays,
    //     // wait on it here. Otherwise, DBSCAN might run before all hits are recorded.
    //     //
    //     // Example (if you add such a property):
    //     // while (yoloSource.HasQueuedRays) await Task.Yield();

    //     // Run DBSCAN to create anchors at cluster centroids
    //     if (dbscanAnchorManager != null)
    //     {
    //         Debug.Log("Running DBSCAN clustering on recorded raycast hits...");
    //         await dbscanAnchorManager.RunOnceAsync();
    //         Debug.Log("DBSCAN clustering complete.");
    //     }
    //     else
    //     {
    //         Debug.LogWarning("No DbscanClassAnchors assigned; skipping DBSCAN.");
    //     }

    //     Debug.Log("Calibration complete.");
    // }
}