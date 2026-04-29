using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Unity.XR.CoreUtils;

[DefaultExecutionOrder(100)]
public class YoloARRaycastHitVisualizer : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private YoloARRaycastManager recorder;
    [SerializeField] private XROrigin xrOrigin;
    [SerializeField] private UI ui;

    [Header("Coordinate Space")]
    [Tooltip(
        "If your recorder stores LocalPositions relative to a parent ARAnchor (recommended), assign that anchor Transform here.\n" +
        "If set, points are visualized via anchorSpace.TransformPoint(localPosition).\n" +
        "If not set, we fall back to XR Origin (legacy behavior).\n" +
        "If your recorder exposes GetWorldPositions(out NativeArray<float3>), we will prefer those automatically.")]
    [SerializeField] private Transform anchorSpace;

    [Header("Visualization")]
    [SerializeField] private float sphereRadius = 0.025f;
    [SerializeField] private Material sphereMaterial; // assign URP Lit (or HDRP Lit) material in Inspector
    [SerializeField] private int maxVisible = 0; // 0 = unlimited
    [SerializeField] private bool onlyLatestFrame = true;
    [SerializeField] private bool scaleByConfidence = false;
    [SerializeField] private float confidenceScale = 0.03f;

    [Header("Classes")]
    [SerializeField] private int classCount = 12; // used for color assignment; set to your model's number of classes

    private readonly List<GameObject> _spheres = new List<GameObject>(256);
    private readonly List<Renderer> _renderers = new List<Renderer>(256);
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private static readonly Color[] Default12 =
    {
        new Color(0.90f, 0.10f, 0.10f), // Bright Red
        new Color(0.10f, 0.70f, 0.15f), // Strong Green
        new Color(0.10f, 0.35f, 0.95f), // Vivid Blue
        new Color(0.95f, 0.85f, 0.10f), // Bright Yellow
        new Color(0.90f, 0.10f, 0.85f), // Magenta / Pink
        new Color(0.10f, 0.85f, 0.85f), // Cyan / Aqua
        new Color(0.95f, 0.55f, 0.10f), // Orange
        new Color(0.55f, 0.20f, 0.95f), // Purple / Violet
        new Color(0.60f, 0.60f, 0.60f), // Medium Gray
        new Color(0.60f, 0.30f, 0.10f), // Brown
        new Color(0.15f, 0.90f, 0.55f), // Mint / Light Green
        new Color(0.10f, 0.10f, 0.10f), // Near Black
    };

    private void Awake()
    {
        if (!recorder) recorder = FindFirstObjectByType<YoloARRaycastManager>();
        if (!xrOrigin) xrOrigin = FindFirstObjectByType<XROrigin>();
        if (!ui) ui = FindFirstObjectByType<UI>();
    }

    private void OnDisable()
    {
        ClearAll();
    }

    private void Update()
    {
        if (!Application.isPlaying) return;
        if (!recorder) return;

        // Safety: don't touch NativeLists if disposed
        if (!recorder.LocalPositions.IsCreated) return;

        UpdateFromSoA(ui.ShowHitMarkers());
    }

    private void UpdateFromSoA(bool display)
    {
        //display toggle
        if (!display)
        {
            SetActiveCount(0);   // hides all currently created markers
            return;              // do not create/update any markers
        }

        // Prefer true world positions if the recorder exposes them.
        // This avoids ambiguity about what LocalPositions are relative to.
        bool hasWorld = TryGetWorldPositions(out NativeArray<float3> worldPositions);

        recorder.GetSoA(out NativeArray<float3> localPositions,
                        out NativeArray<int> classIds,
                        out NativeArray<float> confidences,
                        out NativeArray<int> frameIndices,
                        out NativeArray<int> detectionIndices,
                        out NativeArray<int> pointIds);

        int count = localPositions.Length;
        if (count == 0)
        {
            SetActiveCount(0);
            return;
        }

        // Find latest frame if needed
        int latestFrame = int.MinValue;
        if (onlyLatestFrame)
        {
            for (int i = 0; i < count; i++)
                if (frameIndices[i] > latestFrame)
                    latestFrame = frameIndices[i];
        }

        // Decide which transform LocalPositions are relative to
        // 1) Anchor space (recommended)
        // 2) XR Origin (legacy)
        // 3) null => treat as world already
        Transform localSpaceT = null;
        if (!hasWorld)
        {
            if (anchorSpace != null) localSpaceT = anchorSpace;
            else if (xrOrigin != null) localSpaceT = xrOrigin.transform;
        }

        int visible = 0;

        for (int i = 0; i < count; i++)
        {
            if (onlyLatestFrame && frameIndices[i] != latestFrame)
                continue;

            if (maxVisible > 0 && visible >= maxVisible)
                break;

            EnsureSphereExists(visible);

            Vector3 worldPos;

            if (hasWorld)
            {
                float3 wp = worldPositions[i];
                worldPos = new Vector3(wp.x, wp.y, wp.z);
            }
            else
            {
                float3 lp = localPositions[i];
                var p = new Vector3(lp.x, lp.y, lp.z);

                worldPos = localSpaceT != null ? localSpaceT.TransformPoint(p) : p;
            }

            int classId = classIds[i];
            float conf = confidences[i];

            float radius = scaleByConfidence
                ? sphereRadius + Mathf.Clamp01(conf) * confidenceScale
                : sphereRadius;

            UpdateSphere(visible, worldPos, radius, GetColorForClass(classId));

            visible++;
        }

        SetActiveCount(visible);
    }

    private void EnsureSphereExists(int index)
    {
        if (index < _spheres.Count) return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "YOLO Hit";
        go.transform.SetParent(transform, true);

        var col = go.GetComponent<Collider>();
        if (col) Destroy(col);

        var rend = go.GetComponent<Renderer>();

        // IMPORTANT: give it a compatible material so it doesn't render magenta in URP/HDRP
        if (sphereMaterial != null)
        {
            // Use an instance so per-object properties won't affect shared assets
            rend.material = new Material(sphereMaterial);
        }

        _spheres.Add(go);
        _renderers.Add(rend);
    }

    private void UpdateSphere(int index, Vector3 position, float radius, Color color)
    {
        var go = _spheres[index];
        go.transform.position = position;
        go.transform.localScale = Vector3.one * (radius * 2f);

        var rend = _renderers[index];

        var mpb = new MaterialPropertyBlock();
        rend.GetPropertyBlock(mpb);
        mpb.SetColor(BaseColorId, color); // URP/HDRP Lit
        mpb.SetColor(ColorId, color);     // Built-in Standard
        rend.SetPropertyBlock(mpb);

        if (!go.activeSelf) go.SetActive(true);
    }

    private void SetActiveCount(int count)
    {
        for (int i = count; i < _spheres.Count; i++)
        {
            if (_spheres[i].activeSelf)
                _spheres[i].SetActive(false);
        }
    }

    public void ClearAll()
    {
        for (int i = 0; i < _spheres.Count; i++)
        {
            if (_spheres[i])
                Destroy(_spheres[i]);
        }
        _spheres.Clear();
        _renderers.Clear();
    }

    private Color GetColorForClass(int classId)
    {
        // --- Fixed overrides for key classes ---
        // switch (classId)
        // {
        //     case 63: return new Color(0.95f, 0.2f, 0.2f);   // Red
        //     case 39: return new Color(0.2f, 0.85f, 0.2f);   // Green
        //     case 64: return new Color(0.2f, 0.4f, 0.95f);   // Blue
        //     case 0:  return new Color(0.95f, 0.85f, 0.2f);  // Yellow
        //     case 56: return new Color(0.9f, 0.2f, 0.9f);    // Magenta
        // }

        // Normalize class index
        int idx = ((classId % classCount) + classCount) % classCount;

        // --- COCO case: assign a random Default12 color per class (stable) ---
        if (classCount == 12 && Default12 != null && Default12.Length > 0)
        {
            // Deterministic pseudo-random based on classId
            unchecked
            {
                int hash = idx * 73856093 ^ 19349663;
                int paletteIndex = Mathf.Abs(hash) % Default12.Length;
                return Default12[paletteIndex];
            }
        }

        // --- Fallback: HSV palette ---
        float h = idx / (float)classCount;
        return Color.HSVToRGB(h, 0.85f, 0.95f);
    }

    /// <summary>
    /// If your recorder exposes: GetWorldPositions(out NativeArray&lt;float3&gt; worldPositions)
    /// then we can visualize directly in world space (recommended).
    /// </summary>
    private bool TryGetWorldPositions(out NativeArray<float3> worldPositions)
    {
        worldPositions = default;
        if (recorder == null) return false;

        var mi = recorder.GetType().GetMethod("GetWorldPositions");
        if (mi == null) return false;

        try
        {
            object[] args = { null };
            mi.Invoke(recorder, args);

            if (args.Length == 1 && args[0] is NativeArray<float3> arr && arr.IsCreated)
            {
                worldPositions = arr;
                return true;
            }
        }
        catch (Exception)
        {
            // Ignore; fall back to LocalPositions + transform
        }

        return false;
    }
}
