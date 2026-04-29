using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using UnityEngine.UI;
using TMPro;

public class UI : MonoBehaviour
{
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private Button scanButton;
    [SerializeField] private Button hitButton;
    [SerializeField] private Button boxButton;
    [SerializeField] private Button visualButton;
    [SerializeField] private Image scanTargetImage;
    [SerializeField] private Image hitTargetImage;
    [SerializeField] private Image boxTargetImage;
    [SerializeField] private Image visualTargetImage;
    [SerializeField] private TMP_Text scanButtonText;
    [SerializeField] private Color onColor = Color.green;
    [SerializeField] private Color offColor = Color.red;
    public bool CalibrationComplete = false;
    private bool scanToggleOn = false;
    private bool hitToggleOn = true;
    private bool boxToggleOn = true;
    private bool visualToggleOn = true;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private void Awake()
    {
        if (!planeManager) planeManager = FindFirstObjectByType<ARPlaneManager>();
        if (!scanButton) scanButton = FindFirstObjectByType<Button>();
        if (!scanTargetImage) scanTargetImage = scanButton.targetGraphic as Image;
        if (!scanButtonText) scanButtonText = scanButton.GetComponent<TMP_Text>();
        if (!hitButton) hitButton = FindFirstObjectByType<Button>();
        if (!hitTargetImage) hitTargetImage = hitButton.targetGraphic as Image;
        if (!boxButton) boxButton = FindFirstObjectByType<Button>();
        if (!boxTargetImage) boxTargetImage = boxButton.targetGraphic as Image;
    }

    private void OnEnable()
    {
        if (!CalibrationComplete) 
        {
            ShowPlanes();
            Debug.Log("Planes displayed.");
        }
    }

    public void MarkCalibrationComplete()
    {
        CalibrationComplete = true;
        Debug.Log("Calibration complete. Gated behaviours are now enabled.");
    }

    public void ScanButtonPress()
    {
        scanToggleOn = !scanToggleOn;
        ColorToggle(scanTargetImage, scanToggleOn);

        // First press, mark calibration as completed
        if (!CalibrationComplete) 
        {
            CalibrationComplete = true;
            HidePlanes();
            scanButtonText.text = "Toggle Detection";
            Debug.Log("Calibration complete. Gated behaviours are now enabled.");
        }
    }

    public void HitButtonPress()
    {
        hitToggleOn = !hitToggleOn;
        ColorToggle(hitTargetImage, hitToggleOn);
    }

    public void BoxButtonPress()
    {
        boxToggleOn = !boxToggleOn;
        ColorToggle(boxTargetImage, boxToggleOn);
    }

    public void VisualButtonPress()
    {
        visualToggleOn = !visualToggleOn;
        ColorToggle(visualTargetImage, visualToggleOn);
    }

    private void ColorToggle(Image buttonImage, bool toggle)
    {
        if (buttonImage) buttonImage.color = toggle ? onColor : offColor;
    }

    // -------------------------
    // PLANE VISIBILITY
    // -------------------------
    public void ShowPlanes()
    {
        if (planeManager == null) 
        {
            Debug.LogError("planeManager not found");
            return;
        }

        planeManager.enabled = true;
        foreach (var plane in planeManager.trackables)
        {
            plane.gameObject.SetActive(true);
        }
    }

    public void HidePlanes()
    {
        if (planeManager == null) 
        {
            Debug.LogError("planeManager not found");
            return;
        }

        // Disable manager to stop updates AND hide existing planes.
        planeManager.enabled = false;
        foreach (var plane in planeManager.trackables)
        {
            plane.gameObject.SetActive(false);
            Debug.Log("Hidden plane");
        }
    }

    // -------------------------
    // GATING + GETTERS
    // -------------------------
    /// <summary>
    /// Call this from another script's Update() as a gate:
    /// if (!tapManager.CanRunGatedUpdates()) return;
    /// </summary>
    public bool CanRunGatedUpdates() => scanToggleOn;
    public bool ShowHitMarkers() => hitToggleOn;
    public bool ShowBoundingBoxes() => boxToggleOn;
    public bool ShowVisualizations() => visualToggleOn;
}

