using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

public class AnchorVisualizationApplier : MonoBehaviour
{
    [Serializable]
    public struct ClassPrefab
    {
        public int classId;
        public GameObject prefab;
    }

    [Header("Defaults")]
    [SerializeField] private GameObject defaultPrefab;
    [SerializeField] private bool faceCamera = false;
    [SerializeField] private Camera targetCamera;

    [Header("Optional: per-class prefab overrides")]
    [SerializeField] private List<ClassPrefab> classPrefabs = new();

    [Header("Optional: visual scaling")]
    [SerializeField] private bool applyUniformScale = false;
    [SerializeField] private float uniformScale = 0.1f;

    // classId -> prefab
    private Dictionary<int, GameObject> _prefabByClass;

    private void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;

        _prefabByClass = new Dictionary<int, GameObject>(classPrefabs.Count);
        foreach (var cp in classPrefabs)
        {
            if (cp.prefab != null)
                _prefabByClass[cp.classId] = cp.prefab;
        }
    }

    /// <summary>
    /// Ensures a visualization exists as a child of the anchor.
    /// Returns the visualization instance (created or existing).
    /// </summary>
    public GameObject ApplyVisual(ARAnchor anchor, int classId, string optionalLabel = null)
    {
        if (anchor == null) return null;

        // Pick prefab: class override -> default
        GameObject prefab = null;
        if (_prefabByClass != null) _prefabByClass.TryGetValue(classId, out prefab);
        if (prefab == null) prefab = defaultPrefab;

        if (prefab == null)
        {
            Debug.LogWarning("No prefab assigned for anchor visualization.");
            return null;
        }

        // Look for an existing child visualization (by marker component)
        var marker = anchor.GetComponentInChildren<AnchorVisualMarker>(includeInactive: true);
        GameObject instance;

        if (marker != null)
        {
            instance = marker.gameObject;

            // If the prefab type has changed (class changed), replace it
            if (marker.SourcePrefab != prefab)
            {
                Destroy(instance);
                instance = Instantiate(prefab, anchor.transform);
                marker = instance.GetComponent<AnchorVisualMarker>();
                if (marker == null) marker = instance.AddComponent<AnchorVisualMarker>();
                marker.SourcePrefab = prefab;
            }
        }
        else
        {
            // Create new child visualization
            instance = Instantiate(prefab, anchor.transform);
            marker = instance.GetComponent<AnchorVisualMarker>();
            if (marker == null) marker = instance.AddComponent<AnchorVisualMarker>();
            marker.SourcePrefab = prefab;
        }

        // Reset local transform so it sits on the anchor pose
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;

        if (applyUniformScale)
            instance.transform.localScale = Vector3.one * uniformScale;

        // Optional: set label if you have a TextMesh / TMP child
        if (!string.IsNullOrEmpty(optionalLabel))
            TrySetLabel(instance, optionalLabel);

        // Optional: face camera (billboard)
        if (faceCamera)
        {
            var billboard = instance.GetComponent<BillboardToCamera>();
            if (billboard == null) billboard = instance.AddComponent<BillboardToCamera>();
            billboard.TargetCamera = targetCamera;
        }

        return instance;
    }

    private static void TrySetLabel(GameObject root, string label)
    {
        // TextMesh (built-in)
        var tm = root.GetComponentInChildren<TextMesh>(includeInactive: true);
        if (tm != null)
        {
            tm.text = label;
            return;
        }

        // TMPro (if you use it, uncomment and add TMPro reference)
        // var tmp = root.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: true);
        // if (tmp != null) tmp.text = label;
    }

    /// <summary>
    /// Optional: remove visuals from an anchor.
    /// </summary>
    public void RemoveVisual(ARAnchor anchor)
    {
        if (anchor == null) return;
        var marker = anchor.GetComponentInChildren<AnchorVisualMarker>(includeInactive: true);
        if (marker != null) Destroy(marker.gameObject);
    }
}

/// <summary>
/// Marker component so we can find / replace the visualization reliably.
/// </summary>
public class AnchorVisualMarker : MonoBehaviour
{
    public GameObject SourcePrefab;
}

/// <summary>
/// Simple billboard behavior.
/// </summary>
public class BillboardToCamera : MonoBehaviour
{
    public Camera TargetCamera;

    private void LateUpdate()
    {
        if (TargetCamera == null) return;
        Vector3 dir = transform.position - TargetCamera.transform.position;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
    }
}