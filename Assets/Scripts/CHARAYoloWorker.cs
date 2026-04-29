using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using Unity.InferenceEngine;


[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(ARCameraManager))]
[RequireComponent(typeof(ARCameraBackground))]

public class CHARAYoloWorker : MonoBehaviour
{
    // ====================================================================
    // 1) AR & MODEL FIELDS
    // ====================================================================
    [Header("AR")]
    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] private ARCameraBackground arCameraBackground;
    [SerializeField] private AROcclusionManager occlusionManager;

    
    [SerializeField] private Camera arCamera;
    [SerializeField] private UI ui_external;
[Header("Model")]
    [SerializeField] private ModelAsset yoloModelAsset;
    [SerializeField] private TextAsset labelsFile;
    [SerializeField] private int modelInputWidth = 640;
    [SerializeField] private int modelInputHeight = 640;

    [Header("Detection")]
    [SerializeField] [Range(0.1f, 0.9f)] private float confidenceThreshold = 0.5f;
    [SerializeField] [Range(0.1f, 0.9f)] private float nmsThreshold = 0.45f;
    [SerializeField] private int maxDetections = 5;

    private const BackendType BACKEND = BackendType.GPUCompute;
    private Model runtimeModel;
    private Worker worker;
    private Tensor<float> inputTensor;
    private string[] labels;
    private int numClasses = 12;

    // GPU path textures
    private RenderTexture cameraRT;
    private RenderTexture modelInputRT;

    public bool inferenceIsRunning;
    private float lastInferenceTime;

    // ====================================================================
    // 2) OVERLAY UI
    // ====================================================================
    [Header("UI Overlay")]
    [SerializeField] private Canvas overlayCanvas;
    [SerializeField] private RectTransform overlayRoot;
    [SerializeField] private Color boxColor = Color.green;
    [SerializeField] private float boxLineWidth = 3f;
    [SerializeField] private int labelFontSize = 16;

    [Serializable]
    public struct BoundingBox
    {
        public float cx, cy, x, y, width, height, confidence;
        public int classIndex;
        public string label;
        public float X2 => x + width;
        public float Y2 => y + height;
    }

    
    [Serializable]
    public struct QueuedDetectionRay
    {
        public Ray ray;                 // World-space ray computed at detection time
        public int classIndex;
        public float confidence;
        public int detectionIndex;       // Index within the detections list for that YOLO run
        public int unityFrame;           // Time.frameCount captured for the camera image used for this detection
        public float time;              // Time.time captured for the camera image used for this detection
    }

    // Rays generated at detection time (so raycasting can happen later without pose mismatch)
    private readonly Queue<QueuedDetectionRay> _queuedRays = new Queue<QueuedDetectionRay>(128);

    [Header("Queued Ray Settings")]
    [Tooltip("Maximum queued rays to retain. Oldest rays are dropped first.")]
    [SerializeField] private int maxQueuedRays = 256;

    public int QueuedRayCount => _queuedRays.Count;

    public bool TryDequeueRay(out QueuedDetectionRay queuedRay)
    {
        if (_queuedRays.Count > 0)
        {
            queuedRay = _queuedRays.Dequeue();
            return true;
        }

        queuedRay = default;
        return false;
    }

    public void ClearQueuedRays()
    {
        _queuedRays.Clear();
    }
private readonly List<BoundingBox> detections = new();
    private readonly List<BoundingBoxUI> boxUIPool = new();

    private class BoundingBoxUI
    {
        public GameObject rootObject;
        public RectTransform rectTransform;
        public RawImage[] lines;
        public Text label;
    }

    // ====================================================================
    // 3) UNITY LIFECYCLE
    // ====================================================================
    private void Awake()
    {
        if (!cameraManager) cameraManager = FindAnyObjectByType<ARCameraManager>();
        if (!arCameraBackground) arCameraBackground = FindAnyObjectByType<ARCameraBackground>();
        if (!occlusionManager) occlusionManager = FindAnyObjectByType<AROcclusionManager>();
        if (!arCamera) arCamera = Camera.main;
        if (!ui_external) ui_external = FindFirstObjectByType<UI>();
        LoadLabels();
        InitModel();
    }

    private void OnEnable()
    {
        if (cameraManager != null)
            cameraManager.frameReceived += OnCameraFrameReceived;
        if (occlusionManager != null)
            occlusionManager.frameReceived += OnOcclusionFrameReceived;
    }

    private void Start()
    {
        if (overlayRoot == null)
            Debug.LogError("ARBackgroundYOLO_GPU: overlayRoot not assigned (no boxes will render).");
        if (overlayCanvas == null && overlayRoot != null)
            overlayCanvas = overlayRoot.GetComponentInParent<Canvas>();

        PreallocateBoxPool(maxDetections);
    }

    private void OnDisable()
    {
        if (cameraManager != null)
            cameraManager.frameReceived -= OnCameraFrameReceived;
        if (occlusionManager != null)
            occlusionManager.frameReceived -= OnOcclusionFrameReceived;
    }

    private void OnDestroy()
    {
        Cleanup();
    }

    // ====================================================================
    // 4) INIT / CLEANUP
    // ====================================================================
    private void LoadLabels()
    {
        if (labelsFile != null)
        {
            labels = labelsFile.text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            numClasses = Mathf.Max(1, labels.Length);
        }
        else
        {
            labels = new string[numClasses];
            for (int i = 0; i < numClasses; i++)
                labels[i] = $"cls_{i}";
            Debug.LogWarning("ARBackgroundYOLO_GPU: labelsFile not set, using dummy labels 0..79.");
        }
    }

    private void InitModel()
    {
        if (!yoloModelAsset)
        {
            Debug.LogError("ARBackgroundYOLO_GPU: No model assigned.");
            return;
        }

        try
        {
            runtimeModel = ModelLoader.Load(yoloModelAsset);
            worker = new Worker(runtimeModel, BACKEND);
            inputTensor = new Tensor<float>(new TensorShape(1, 3, modelInputHeight, modelInputWidth));
        }
        catch (Exception e)
        {
            Debug.LogError($"ARBackgroundYOLO_GPU: Failed to init model/worker: {e.Message}");
        }
    }

    private void Cleanup()
    {
        if (worker != null)
        {
            worker.Dispose();
            worker = null;
        }
        if (inputTensor != null)
        {
            inputTensor.Dispose();
            inputTensor = null;
        }

        DestroyAndNull(ref cameraRT);
        DestroyAndNull(ref modelInputRT);

        foreach (var b in boxUIPool)
            if (b.rootObject) Destroy(b.rootObject);
        boxUIPool.Clear();
    }

    private static void DestroyAndNull(ref RenderTexture rt)
    {
        if (rt != null)
        {
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }
    }

    private void PreallocateBoxPool(int count)
    {
        if (!overlayRoot) return;
        for (int i = 0; i < count; i++)
        {
            var ui = CreateBoundingBoxUI();
            ui.rootObject.SetActive(false);
            boxUIPool.Add(ui);
        }
    }

    // ====================================================================
    // 5) AR FRAME EVENTS
    // ====================================================================
    private void OnOcclusionFrameReceived(AROcclusionFrameEventArgs e)
    {
        // Optional occlusion handling
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs e)
    {
        
    }

    public void RunInferenceOnce()
    {
        if (worker == null || inferenceIsRunning) return;

        var bgMat = arCameraBackground ? arCameraBackground.material : null;
        Texture cameraTex = bgMat ? bgMat.mainTexture : null;
        if (cameraTex == null) return;

        lastInferenceTime = Time.time;
        StartCoroutine(ProcessFrame(cameraTex, bgMat));
    }

    
    // ====================================================================
    //  RAY GENERATION (capture-time)
    // ====================================================================

    private static Ray BuildRayFromScreenPointUsingCapturedProjection(
        Vector2 screenPointBottomLeft,
        float screenWidth,
        float screenHeight,
        Vector3 camWorldPos,
        Quaternion camWorldRot,
        Matrix4x4 projectionMatrix)
    {
        // Convert to Normalized Device Coordinates (-1..1)
        float xNdc = (screenPointBottomLeft.x / screenWidth) * 2f - 1f;
        float yNdc = (screenPointBottomLeft.y / screenHeight) * 2f - 1f;

        // Build a clip-space point on the far plane (z = 1)
        var clip = new Vector4(xNdc, yNdc, 1f, 1f);

        // Unproject to view space
        Matrix4x4 invProj = projectionMatrix.inverse;
        Vector4 view = invProj * clip;
        if (Mathf.Abs(view.w) > 1e-6f)
            view /= view.w;

        // In Unity, camera looks down -Z in view space.
        Vector3 dirView = new Vector3(view.x, view.y, view.z).normalized;

        // Transform direction into world space using captured camera rotation.
        Vector3 dirWorld = camWorldRot * dirView;

        return new Ray(camWorldPos, dirWorld.normalized);
    }

    private bool TryModelCenterToScreenPointBottomLeft(float modelCx, float modelCy, float srcW, float srcH, out Vector2 screenPointBottomLeft)
    {
        // Same math as LetterboxToSquare(src=cameraRT, dst=modelInputRT)
        float dstW = modelInputWidth;
        float dstH = modelInputHeight;

        float scale = Mathf.Min(dstW / srcW, dstH / srcH);
        float scaledW = srcW * scale;
        float scaledH = srcH * scale;
        float offX = (dstW - scaledW) * 0.5f;
        float offY = (dstH - scaledH) * 0.5f;

        // Reject detections in padding
        if (modelCx < offX || modelCx > offX + scaledW || modelCy < offY || modelCy > offY + scaledH)
        {
            screenPointBottomLeft = default;
            return false;
        }

        // Invert letterbox: model (top-left) -> screen pixels (top-left)
        float screenX_topLeft = (modelCx - offX) / scale;
        float screenY_topLeft = (modelCy - offY) / scale;

        // Convert from top-left to Unity bottom-left coordinates
        float screenX_bottomLeft = srcW - screenX_topLeft;   // <-- FIX: horizontal mirror

        screenPointBottomLeft = new Vector2(screenX_bottomLeft, screenY_topLeft);
                return true;
    }

    private void EnqueueRaysForCurrentDetections(
        Vector3 capturedCamPos,
        Quaternion capturedCamRot,
        Matrix4x4 capturedProjection,
        float capturedSrcW,
        float capturedSrcH,
        int capturedUnityFrame,
        float capturedTime)
    {
        if (detections == null || detections.Count == 0) return;

        for (int i = 0; i < detections.Count; i++)
        {
            var det = detections[i];
            if (!TryModelCenterToScreenPointBottomLeft(det.cx, det.cy, capturedSrcW, capturedSrcH, out var screenBL))
                continue;

            Ray ray = BuildRayFromScreenPointUsingCapturedProjection(
                screenBL,
                capturedSrcW,
                capturedSrcH,
                capturedCamPos,
                capturedCamRot,
                capturedProjection);

            // Bounded queue
            while (_queuedRays.Count >= maxQueuedRays)
                _queuedRays.Dequeue();

            _queuedRays.Enqueue(new QueuedDetectionRay
            {
                ray = ray,
                classIndex = det.classIndex,
                confidence = det.confidence,
                detectionIndex = i,
                unityFrame = capturedUnityFrame,
                time = capturedTime
            });
        }
    }

private IEnumerator ProcessFrame(Texture cameraTexture, Material backgroundMaterial)
    {
        inferenceIsRunning = true;

        // Capture camera pose + projection at the moment we kick off this frame's processing.
        // We will use these captured values to build world-space rays that match the detections.
        Vector3 capturedCamPos = arCamera ? arCamera.transform.position : transform.position;
        Quaternion capturedCamRot = arCamera ? arCamera.transform.rotation : transform.rotation;
        Matrix4x4 capturedProjection = arCamera ? arCamera.projectionMatrix : Matrix4x4.identity;
        int capturedUnityFrame = Time.frameCount;
        float capturedTime = Time.time;
        float capturedSrcW = Screen.width;
        float capturedSrcH = Screen.height;

        // Copy AR background to cameraRT
        EnsureRTs(cameraTexture);
        Graphics.Blit(cameraTexture, cameraRT, backgroundMaterial);

        // Letterbox scale into model input size
        LetterboxToSquare(cameraRT, modelInputRT);

        // Let GPU finish blits
        yield return null;

        // Convert to tensor and run inference
        TextureConverter.ToTensor(modelInputRT, inputTensor, default(TextureTransform));
        worker.Schedule(inputTensor);

        // Let GPU produce output for 3 frames
        yield return null;
        yield return null;
        yield return null;

        // Readback and parse results
        var output = worker.PeekOutput() as Tensor<float>;
        if (output != null)
        {
            using var cpu = output.ReadbackAndClone() as Tensor<float>;
            ParseYoloV8(cpu); // [1, 16 (4 spatial vars + 12 classes), 8400]

                // Build rays for these detections using the captured camera pose/projection.
                EnqueueRaysForCurrentDetections(capturedCamPos, capturedCamRot, capturedProjection, capturedSrcW, capturedSrcH, capturedUnityFrame, capturedTime);
        }

        // Update UI overlay
        ProjectDetectionsToOverlay();

        inferenceIsRunning = false;
    }

    // ====================================================================
    // 6) RT HELPERS
    // ====================================================================
    private void EnsureRTs(Texture source)
    {
        int w = Screen.width;
        int h = Screen.height;

        if (cameraRT == null || cameraRT.width != w || cameraRT.height != h)
        {
            DestroyAndNull(ref cameraRT);
            cameraRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "ARBG_CameraRT" };
            cameraRT.Create();
        }

        if (modelInputRT == null || modelInputRT.width != modelInputWidth || modelInputRT.height != modelInputHeight)
        {
            DestroyAndNull(ref modelInputRT);
            modelInputRT = new RenderTexture(modelInputWidth, modelInputHeight, 0, RenderTextureFormat.ARGB32) { name = "ARBG_ModelInputRT" };
            modelInputRT.Create();
        }
    }

    private void LetterboxToSquare(Texture src, RenderTexture dst)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = dst;
        GL.Clear(true, true, Color.black);

        float srcW = src.width;
        float srcH = src.height;
        float dstW = dst.width;
        float dstH = dst.height;

        float scale = Mathf.Min(dstW / srcW, dstH / srcH);
        float scaledW = srcW * scale;
        float scaledH = srcH * scale;
        float offX = (dstW - scaledW) * 0.5f;
        float offY = (dstH - scaledH) * 0.5f;

        GL.PushMatrix();
        GL.LoadPixelMatrix(0, dstW, dstH, 0);
        Graphics.DrawTexture(new Rect(offX, offY, scaledW, scaledH), src);
        GL.PopMatrix();

        RenderTexture.active = prev;
    }

    // ====================================================================
    // 7) YOLO PARSE
    // ====================================================================
    private void ParseYoloV8(Tensor<float> output)
    {
        detections.Clear();

        int rank = output.shape.rank;
        int total = output.shape.length;
        int numCoords = 4;
        int expectedChannels = numCoords + numClasses;
        int channels = 0, anchors = 0;
        bool channelsFirst = false;

        if (rank == 3)
        {
            int b = output.shape[0];
            int d1 = output.shape[1];
            int d2 = output.shape[2];

            if (b != 1)
            {
                Debug.LogWarning($"Batch {b} != 1");
                return;
            }

            if (d1 == expectedChannels)
            {
                channels = d1;
                anchors = d2;
                channelsFirst = true;
            }
            else if (d2 == expectedChannels)
            {
                channels = d2;
                anchors = d1;
                channelsFirst = false;
            }
            else
            {
                Debug.LogWarning($"Unexpected output shape [{b},{d1},{d2}]");
                return;
            }
        }
        else if (rank == 1)
        {
            if (total % expectedChannels != 0)
            {
                Debug.LogWarning("Flat output not divisible by channels");
                return;
            }
            channels = expectedChannels;
            anchors = total / expectedChannels;
            channelsFirst = false;
        }
        else
        {
            Debug.LogWarning($"Unsupported output rank {rank}");
            return;
        }

        float GetVal(int a, int c)
        {
            if (rank == 3)
                return channelsFirst ? output[0, c, a] : output[0, a, c];
            int idx = a * channels + c;
            return output[idx];
        }

        for (int a = 0; a < anchors; a++)
        {
            float cx = GetVal(a, 0);
            float cy = GetVal(a, 1);
            float w = GetVal(a, 2);
            float h = GetVal(a, 3);

            int bestC = -1;
            float bestS = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                float s = GetVal(a, numCoords + c);
                if (s > bestS)
                {
                    bestS = s;
                    bestC = c;
                }
            }

            if (bestC < 0 || bestS < confidenceThreshold) continue;

            float x = cx - 0.5f * w;
            float y = cy - 0.5f * h;

            detections.Add(new BoundingBox
            {
                cx = cx,
                cy = cy,
                x = x,
                y = y,
                width = w,
                height = h,
                confidence = bestS,
                classIndex = bestC,
                label = (bestC < labels.Length ? labels[bestC] : $"cls_{bestC}")
            });
        }

        // NMS per class
        detections.Sort((a, b) => b.confidence.CompareTo(a.confidence));
        var kept = new List<BoundingBox>();
        var sup = new bool[detections.Count];

        for (int i = 0; i < detections.Count; i++)
        {
            if (sup[i]) continue;
            kept.Add(detections[i]);

            for (int j = i + 1; j < detections.Count; j++)
            {
                if (sup[j]) continue;
                if (detections[i].classIndex != detections[j].classIndex) continue;

                float iou = IoU(detections[i], detections[j]);
                if (iou > nmsThreshold)
                    sup[j] = true;
            }
        }

        detections.Clear();
        detections.AddRange(kept);
    }

    private static float IoU(BoundingBox a, BoundingBox b)
    {
        float x1 = Mathf.Max(a.x, b.x);
        float y1 = Mathf.Max(a.y, b.y);
        float x2 = Mathf.Min(a.X2, b.X2);
        float y2 = Mathf.Min(a.Y2, b.Y2);

        float inter = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
        float areaA = a.width * a.height;
        float areaB = b.width * b.height;
        float uni = areaA + areaB - inter;

        return uni > 0 ? inter / uni : 0f;
    }

    // ====================================================================
    // 8) OVERLAY
    // ====================================================================
    private void ProjectDetectionsToOverlay()
    {
        // Clears scene of bounding boxes
        HideAllBoxes();
        // Ensures computation isn't wasted if there are no new detections
        if (overlayRoot == null || detections.Count == 0) return;
        // Ensures bounding boxes aren't shown if user dictates
        if (!ui_external.ShowBoundingBoxes()) return;

        Rect r = overlayRoot.rect;
        float canvasW = r.width;
        float canvasH = r.height;
        if (canvasW <= 0f || canvasH <= 0f) return;

        float srcW = canvasW;
        float srcH = canvasH;
        float dstW = modelInputWidth;
        float dstH = modelInputHeight;

        float scaleToModel = Mathf.Min(dstW / srcW, dstH / srcH);
        float scaledSrcW = srcW * scaleToModel;
        float scaledSrcH = srcH * scaleToModel;
        float offX = (dstW - scaledSrcW) * 0.5f;
        float offY = (dstH - scaledSrcH) * 0.5f;

        for (int i = 0; i < detections.Count && i < boxUIPool.Count; i++)
        {
            var d = detections[i];

            float sx = (d.x - offX) / scaleToModel;
            float sy = (d.y - offY) / scaleToModel;
            float sw = d.width / scaleToModel;
            float sh = d.height / scaleToModel;

            if (sw < 5f || sh < 5f) continue;

            UpdateBoxUI(boxUIPool[i], sx, sy, sw, sh, $"{d.label} {d.confidence:P0}");
        }
    }

    private BoundingBoxUI CreateBoundingBoxUI()
    {
        var ui = new BoundingBoxUI();
        ui.rootObject = new GameObject("BBox");
        ui.rootObject.transform.SetParent(overlayRoot, false);
        ui.rectTransform = ui.rootObject.AddComponent<RectTransform>();
        ui.rectTransform.anchorMin = new Vector2(0, 1);
        ui.rectTransform.anchorMax = new Vector2(0, 1);
        ui.rectTransform.pivot = new Vector2(0, 1);

        ui.lines = new RawImage[4];
        string[] names = { "Top", "Right", "Bottom", "Left" };

        for (int i = 0; i < 4; i++)
        {
            var line = new GameObject(names[i]);
            line.transform.SetParent(ui.rootObject.transform, false);
            var rt = line.AddComponent<RectTransform>();
            var img = line.AddComponent<RawImage>();
            img.color = boxColor;
            ui.lines[i] = img;
        }

        var labelObj = new GameObject("Label");
        labelObj.transform.SetParent(ui.rootObject.transform, false);
        var lrt = labelObj.AddComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0, 1);
        lrt.anchorMax = new Vector2(0, 1);
        lrt.pivot = new Vector2(0, 1);
        lrt.anchoredPosition = new Vector2(5, 0);
        lrt.sizeDelta = new Vector2(220, 30);

        ui.label = labelObj.AddComponent<Text>();
        ui.label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        ui.label.fontSize = labelFontSize;
        ui.label.color = boxColor;
        ui.label.alignment = TextAnchor.UpperLeft;

        var shadow = labelObj.AddComponent<Shadow>();
        shadow.effectColor = new Color(0, 0, 0, 0.8f);
        shadow.effectDistance = new Vector2(1, -1);

        return ui;
    }

    private void UpdateBoxUI(BoundingBoxUI ui, float x, float y, float w, float h, string text)
    {
        ui.rootObject.SetActive(true);
        ui.rectTransform.anchoredPosition = new Vector2(x, -y);
        ui.rectTransform.sizeDelta = new Vector2(w, h);

        // Top
        var top = ui.lines[0].rectTransform;
        top.anchorMin = new Vector2(0, 1);
        top.anchorMax = new Vector2(1, 1);
        top.pivot = new Vector2(0, 1);
        top.anchoredPosition = Vector2.zero;
        top.sizeDelta = new Vector2(0, boxLineWidth);

        // Right
        var right = ui.lines[1].rectTransform;
        right.anchorMin = new Vector2(1, 0);
        right.anchorMax = new Vector2(1, 1);
        right.pivot = new Vector2(1, 1);
        right.anchoredPosition = Vector2.zero;
        right.sizeDelta = new Vector2(boxLineWidth, 0);

        // Bottom
        var bot = ui.lines[2].rectTransform;
        bot.anchorMin = new Vector2(0, 0);
        bot.anchorMax = new Vector2(1, 0);
        bot.pivot = new Vector2(0, 0);
        bot.anchoredPosition = Vector2.zero;
        bot.sizeDelta = new Vector2(0, boxLineWidth);

        // Left
        var left = ui.lines[3].rectTransform;
        left.anchorMin = new Vector2(0, 0);
        left.anchorMax = new Vector2(0, 1);
        left.pivot = new Vector2(0, 1);
        left.anchoredPosition = Vector2.zero;
        left.sizeDelta = new Vector2(boxLineWidth, 0);

        ui.label.text = text;
    }

    public void HideAllBoxes()
    {
        foreach (var b in boxUIPool)
            b.rootObject.SetActive(false);
    }

    public IReadOnlyList<BoundingBox> GetDetections()
    {
        return detections;
    }

}