# Unity AR Project Setup Guide

## Build Project
- Using Unity version **6000.0.58f1**, create a project using the **Universal 3D Template**

---

## Install Packages
Navigate to:  
**Window → Package Manager**

Under **Unity Registry**, install:

- **AR Foundation** (version 6.1.0+)
  - Go to *Version History* and ensure version ≥ 6.1.0
  - Default (6.0.6) does NOT include Vulkan API
  - Installs dependencies:
    - XR Core
    - XR Plugin Manager
  - Required script:
    - `ARCommandBufferSupportRendererFeature.cs` (exists in 6.1.1+)

- **Google ARCore XR Plugin** (version 6.1.0+)
  - Use *Version History → See other versions*
  - Default (6.0.6) does NOT include Vulkan API

- **Android Logcat** (1.4.6)
- **Inference Engine** (2.2.2)
- **XR Interaction Toolkit** (3.0.10)

---

## Switch Build Profile to Android
- Go to **File → Build Profiles**
- Select **Android**
- Click **Switch Platform**

---

## Set Up Game Objects
In **Hierarchy**:

1. Delete:
   - `Main Camera`

2. Add:
   - `XR Origin (Main Camera)` OR `XR Origin (Mobile AR)`
   - `AR Session`
   - `AR Default Plane`
   - `XR Interaction Manager`
   - `Canvas`

3. UI Setup:
   - Right-click **Canvas → Create Empty**
   - Rename:
     - `Canvas` → **Overlay Canvas**
     - `GameObject` → **Overlay Root** (optional)

4. Configure Overlay Root:
   - Select anchor preset (top-left square)
   - Hold **Alt** and choose **stretch-stretch** (bottom-right option)

---

## Set Up Renderer
Navigate to:  
**Assets → Settings**

1. Delete:
   - `PC_Renderer`
   - `PC_RPAsset`
   - `Mobile_Renderer`
   - `Mobile_RPAsset`

2. Create new URP asset:
   - **Assets → Create → Rendering → URP Asset (With Universal Renderer)**
   - Rename as desired

3. Configure Renderer:
   - Open renderer
   - Add:
     - **AR Background Renderer Feature**
     - **AR Command Buffer Support Renderer Feature**

---

## Project Settings
Go to:  
**Edit → Project Settings**

### Player → Android → Other Settings

**Rendering**
- Disable **Auto Graphics API**
- Move **Vulkan** to top
- Enable **Multi-Threaded Rendering**

**Identification**
- Minimum API Level: **Android 10 (API 29)**

**Configuration**
- Active Input Handling: **Input System Package (New)**
- Restart required

---

### Graphics
- Set **Default Render Pipeline Asset** to your new URP asset

---

### XR Plug-in Management
- Under **Android**
  - Enable **Google ARCore**

---

## XR Origin Setup
Select **XR Origin**:

- Ensure components:
  - `ARPlaneManager`
  - `ARRaycastManager`
- Enable:
  - Tracking Origin Mode
- Assign:
  - Main Camera

---

## Import and Set Up Scripts

### Download Files
- `CHARACameraBackground.cs`
- `TapManager.cs`
- `TextureTools.cs`
- `best(1).onnx`
- `classes.txt`

*(Insert actual links here)*

### Import
- Go to **Assets → Import New Asset**
- Import all files

---

## Configure Main Camera
Path:
**XR Origin → Camera Offset → Main Camera**

In **Inspector**:

- Tag: `MainCamera`
- Renderer: set to your new renderer

### Add Script
- Add `CHARACameraBackground` component

---

## Configure Script

### AR Section
- Camera Manager → `Main Camera`
- AR Camera Background → `Main Camera`

### Model Section
- Yolo Model Asset → `.onnx` file
- Labels File → `classes.txt`
- Resolution: **640×640** (for YOLOv8+)

### Detection Settings
- Confidence Threshold: `0.7`
- NMS: `0.45`
- Inference Interval: `1.0`
- Max Detection: `5`

### UI Overlay
- Overlay Canvas → `Overlay Canvas`
- Overlay Root → `Overlay Root`

---

## Import Animations

1. Create Empty Object:
   - Name: `AnimationParent`

2. Import:
   - `Caution2.fbx`

3. Drag model into `AnimationParent`

---

## Animator Setup

1. Create Animator Controller:
   - **Assets → Create → Animation → Animator Controller**
   - Name: `AnimatorController`

2. Open Animator:
   - Drag `CubeAction` into graph
   - Create transition → Exit

3. Configure `AnimationParent`:
   - Scale: `(0.1, 0.1, 0.1)`
   - Rotation: `(0, 180, 0)`

4. Add Animator Component:
   - Assign controller

---

## Prefab Setup
- Drag `AnimationParent` into Assets folder
- Delete from scene

---

## Tap Manager Setup
- Select **XR Origin**
- Add component: `TapManager`
- Assign:
  - Prefab Object → `AnimationParent`

---

## Final Step
- Enable **Plane Visualization**

---

# Notes
- Ensure all package versions are **6.1.0+**
- Vulkan is required for compatibility
- YOLO model resolution must match training data
