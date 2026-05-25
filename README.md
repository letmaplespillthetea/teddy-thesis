# Teddy: Sketch-Based 3D Mesh Generation

Thesis project | Unity 3D, C#, Teddy Algorithm, Domain Stitching

[![Unity](https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity)](https://unity.com/)
[![C#](https://img.shields.io/badge/C%23-.NET%204.x-purple?logo=csharp)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-blue)](https://unity.com/)

---

## Overview

**Teddy** is a sketch-based 3D modeling system built on **Unity**. The user draws a 2D freehand contour with the mouse and the system automatically:

- Generates an inflated 3D mesh using the **Teddy** algorithm (Igarashi et al., 1999)
- Extracts a skeleton automatically via the **Medial Axis Transform**
- Deforms the mesh through the skeleton using **Harmonic Skinning**
- Simulates real-time **Mass-Spring** soft-body physics
- Supports **Domain Stitching** to merge complex multi-part mesh regions
---
## System Requirements

### Required Software

| Software | Version | Download |
|----------|---------|----------|
| Unity Hub | Latest | [unity.com/download](https://unity.com/download) |
| Unity Editor | **2022.3 LTS** or newer | Via Unity Hub |
| Git | Optional | [git-scm.com](https://git-scm.com/) |

### Required Unity Modules (installed via Unity Hub)

- Windows Build Support (or Mac/Linux as applicable)
- Universal Render Pipeline (if using URP)

### Recommended Hardware

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| CPU | Intel Core i5 | Intel Core i7 / Ryzen 7 |
| RAM | 8 GB | 16 GB |
| GPU | Integrated | NVIDIA GTX 1060 / AMD RX 580 |
| Storage | 5 GB | 10 GB |

---

## Installation

### Step 1: Install Unity Hub

1. Download Unity Hub from https://unity.com/download.
2. Run the installer and complete the setup.
3. Sign in or create a free Unity account.

### Step 2: Install Unity Editor

1. Open Unity Hub and go to the **Installs** tab.
2. Click **Install Editor** and select version **2022.3.x LTS**.
3. Select the following modules:
   - Microsoft Visual Studio Community (C# IDE)
   - Windows Build Support (IL2CPP)
4. Click **Install** and wait for the process to finish (20 to 40 minutes).

### Step 3: Open the Project

1. Open Unity Hub and go to the **Projects** tab.
2. Click **Open** > **Add project from disk**.
3. Navigate to the root project directory (the folder containing `Assets\`):
4. Click **Open**. Unity will import all assets automatically. The first import may take 5 to 10 minutes.

### Step 4: Verify the Setup

After Unity finishes loading, confirm the following:

- No red errors appear in the **Console** window.
- The `Assets > Teddy` folder is visible in the **Project** panel.
- The target platform in **Project Settings > Player** is set to Windows.

---

## Running the Application in Play Mode

### Main Scene: Drawer

1. In the **Project** panel, open:
   ```
   Assets > Teddy > Scenes > Drawer.unity
   ```
2. Press the **Play** button in the Unity toolbar.
3. The **Game** window will appear. Use the controls below to interact.

### Demo Scene

1. Open:
   ```
   Assets > Teddy > Scenes > Demo.unity
   ```
2. Press **Play** to view the Teddy algorithm demo using the built-in duck model loaded from `duck.json`.
---

## Troubleshooting

### Unity cannot open the project

Cause: Unity version mismatch.  
Fix: In Unity Hub, go to **Projects**, click the Unity version icon next to the project name, and select the correct **2022.3.x** version.

### "Assembly Definition not found" error

Cause: Missing Unity packages.  
Fix: Go to **Window > Package Manager** and install the following packages:
- Input System
- Universal RP (if needed)

### Mesh is deformed or fails to generate

Cause: The sketch has fewer than four points.  
Fix: Draw more slowly and ensure the contour is closed and contains at least four points.

### Physics stutters or is not smooth

Fix:
- Lower **Shape Stiffness** to `0.1`.
- Raise **Damping** to `0.3`.
- Reduce the number of puppet meshes in the scene.

### NullReferenceException in the Console

Fix: Ensure all serialized fields on the **Drawer** GameObject are assigned in the Inspector:
- `Prefab`: Assign the Puppet Prefab.
- `Floor`: Assign the Floor GameObject.
- `Line Mat`: Assign the Line Material.
- `Json`: Assign the `duck.json` TextAsset.

---

## References

| Resource | Link / Reference |
| :--- | :--- |
| Teddy Algorithm Paper (Igarashi 1999) | *"Teddy: A Sketching Interface for 3D Freeform Design"* |
| Base Source Code (Unity-Teddy) | [github.com/mattatz/unity-teddy](https://github.com/mattatz/unity-teddy) |
| Unity Manual | [docs.unity3d.com/Manual](https://docs.unity3d.com/Manual) |
| Unity Scripting API | [docs.unity3d.com/ScriptReference](https://docs.unity3d.com/ScriptReference) |

---

## Author

Undergraduate Thesis, Department of Information Technology  
Academic Year: 2025 to 2026

---

## License & Credits

This project was developed for academic and research purposes.  

### Credits
* **Original Algorithm:** The original **Teddy** algorithm is credited to **Takeo Igarashi, Satoshi Matsuoka, and Hidehiko Tanaka (1999)**.
* **Base Source Code:** This project is built and upgraded upon the foundation of [unity-teddy by mattatz](https://github.com/mattatz/unity-teddy) as part of a thesis project.
