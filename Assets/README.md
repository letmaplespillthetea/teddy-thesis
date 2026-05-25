# Teddy: Sketch-Based 3D Mesh Generation

Thesis project | Unity 3D, C#, Teddy Algorithm, Domain Stitching

[![Unity](https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity)](https://unity.com/)
[![C#](https://img.shields.io/badge/C%23-.NET%204.x-purple?logo=csharp)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-blue)](https://unity.com/)

---

## Overview

**Teddy** is a sketch-based 3D modeling system built on **Unity**. The user draws a 2D freehand contour with the mouse and the system automatically:

- Generates an inflated 3D mesh using the **Teddy** algorithm (Igarashi et al., 1999)
- Extracts a skeleton automatically via the **Medial Axis Transform (MAT)**
- Deforms the mesh through the skeleton using **Harmonic Skinning**
- Simulates real-time **Mass-Spring** soft-body physics
- Supports **Domain Stitching** to merge complex multi-part mesh regions

---

## Features

| Feature | Description |
|---------|-------------|
| Sketch-to-3D | Freehand drawing converted to a 3D mesh automatically |
| Auto Skeleton | Skeleton generated from the medial axis |
| Harmonic Skinning | Smooth mesh deformation driven by joints |
| Mass-Spring Physics | Real-time soft-body simulation |
| Texture Painting | Paint colors directly onto the mesh surface |
| Animation Recording | Record and play back joint motion |
| Domain Stitching | Merge multi-region meshes with topological consistency |
| GLB Export | Export models in the GLB format |
| Undo | Undo sketch drawing operations |
| Lasso Selection | Select joint groups with a lasso region |

---

## Project Structure

```
F:\Thesis\
└── Assets\
    ├── README.md                               <- This file
    ├── InputSystem_Actions.inputactions        <- Unity Input System actions
    ├── Scenes\                                 <- Default Unity scenes
    ├── Settings\                               <- Project settings
    ├── StreamingAssets\                        <- Runtime JSON data
    └── Teddy\                                  <- Main project directory
        ├── JSON\
        │   └── duck.json                       <- Sample duck shape data
        ├── Materials\                          <- Unity materials
        ├── Prefabs\                            <- GameObject prefabs
        ├── Scenes\
        │   ├── Drawer.unity                    <- Main scene (drawing mode)
        │   └── Demo.unity                      <- Algorithm demo scene
        ├── Shaders\                            <- Custom shaders
        ├── Textures\                           <- Texture assets
        └── Scripts\
            ├── Drawer.cs                       <- Main controller (UI + input)
            ├── Puppet.cs                       <- Mesh, skeleton, and animation manager
            ├── Demo.cs                         <- Demo mode (loads from JSON)
            ├── ARAPLayering.cs                 <- ARAP deformation
            ├── ConstrainedDelaunayTriangulation.cs
            ├── DisplacementLogger.cs           <- Displacement logging
            ├── DomainMerger.cs                 <- Domain mesh merging
            ├── DomainStitcher.cs               <- Stitching algorithm
            ├── DomainStitchingSystem.cs        <- Main stitching pipeline
            ├── GLBExporter.cs                  <- GLB file export
            ├── HoleCreator.cs                  <- Mesh hole creation
            ├── MeshInflationUtility.cs         <- 3D inflation utility
            ├── PoissonHeightFieldSolver.cs     <- Poisson solver
            ├── SketchCleaner.cs                <- Sketch noise filtering
            ├── SketchInfo.cs                   <- Sketch metadata
            └── Teddy\                          <- Core Teddy algorithm
                ├── Teddy.cs
                ├── HarmonicSkinning.cs
                ├── LBSSkinning.cs
                ├── Chord2D.cs
                ├── Connection2D.cs
                ├── Face2D.cs
                ├── SkeletonBone.cs
                ├── TextureContourExtractor.cs
                └── VertexNetwork2D.cs
```

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
   ```
   F:\Thesis\
   ```
4. Click **Open**. Unity will import all assets automatically. The first import may take 5 to 10 minutes.

### Step 4: Verify the Setup

After Unity finishes loading, confirm the following:

- No red errors appear in the **Console** window.
- The `Assets > Teddy` folder is visible in the **Project** panel.
- The target platform in **Project Settings > Player** is set to Windows.

---

## Running the Application (Play Mode)

### Main Scene: Drawer

1. In the **Project** panel, open:
   ```
   Assets > Teddy > Scenes > Drawer.unity
   ```
2. Press the **Play** button in the Unity toolbar.
3. The **Game** window will appear. Use the controls below to interact.

#### Basic Controls

| Action | Input | Function |
|--------|-------|----------|
| Draw shape | Hold left mouse button and drag | Draw a 2D freehand contour |
| Generate mesh | Click **"Inflate (Flat)"** or **"Inflate (Round)"** | Convert the sketch to a 3D mesh |
| Move mesh | Left-click drag on mesh body | Move the entire mesh |
| Drag joint | Left-click drag on a red joint dot | Deform the mesh through the joint |
| Undo | Ctrl + Z | Undo the last action |
| Erase | Enable **Eraser Mode** and drag | Erase part of the sketch |

#### Operation Modes (Right Panel)

| Mode | Description |
|------|-------------|
| Default | Default mode: draw contours and select meshes |
| Move Joint | Drag and drop skeleton joints |
| Lasso Joint | Select a group of joints using a lasso region |
| Rig Edit | Edit the skeleton structure directly |
| Draw on Surface | Paint colors onto the 3D mesh surface |
| Animation Mode | Record and play back joint motion |
| Erase | Erase sketch strokes |

### Demo Scene

1. Open:
   ```
   Assets > Teddy > Scenes > Demo.unity
   ```
2. Press **Play** to view the Teddy algorithm demo using the built-in duck model loaded from `duck.json`.

---

## Inspector Configuration

### Drawer GameObject Settings

Select the **Drawer** GameObject in the Hierarchy. The following properties are available in the Inspector.

#### Mesh Generation

| Property | Default | Description |
|----------|---------|-------------|
| Threshold | 1.0 | Minimum distance between sketch points |
| Inflation Amount | 1.0 | Mesh inflation depth (0.1 = flat, 2.0 = very round) |
| Smooth Height Fields | true | Apply smoothing to the mesh surface |

#### Skeleton Appearance

| Property | Default | Description |
|----------|---------|-------------|
| Show Skeleton | true | Display the skeleton in Game View |
| Skeleton Color | Red | Skeleton rendering color |
| Simplify Distance | 0.05 | Merge bones shorter than this threshold |
| Joint Radius | 2.0 | Joint display size in pixels |

#### Mass-Spring Physics

| Property | Default | Description |
|----------|---------|-------------|
| Enable Physics | true | Enable mass-spring simulation |
| Enable Gravity | false | Enable gravitational force |
| Shape Stiffness | 0.2 | Rest-shape restoration stiffness (0 = loose, 1 = rigid) |
| Damping | 0.1 | Velocity damping coefficient (0 = none, 1 = full stop) |

#### Domain Stitching (Advanced)

| Property | Default | Description |
|----------|---------|-------------|
| Use Domain Stitching | false | Enable the advanced domain stitching pipeline |
| Use Full Pipeline | false | Activate Phases 2 through 5 of the pipeline |
| Use ARAP Layering | false | Enable ARAP deformation (Phase 5) |

---

## Building the Application

### Windows Standalone

1. Go to **File > Build Settings**.
2. Select **PC, Mac & Linux Standalone** as the platform.
3. Set **Target Platform** to `Windows` and **Architecture** to `x86_64`.
4. Click **Add Open Scenes** to include the current scene.
5. Click **Build** and choose an output directory (for example, `F:\Thesis\Build\Windows\`).
6. Wait for the build to complete (5 to 15 minutes for the first build).
7. Run the generated `.exe` file.

### WebGL

1. Go to **File > Build Settings > WebGL**.
2. Click **Switch Platform** (requires the WebGL module to be installed in Unity Hub).
3. Click **Build** and select an output directory.
4. Serve the output directory using a local web server, for example:
   ```bash
   npx serve F:\Thesis\Build\WebGL
   ```
5. Open a browser and navigate to `http://localhost:3000`.

---

## System Architecture

### Main Mesh Generation Pipeline

```
User draws a 2D sketch
        |
        v
SketchCleaner.cs         <- Noise filtering and point reduction
        |
        v
Teddy.cs                 <- Teddy algorithm:
        |                   1. Constrained Delaunay Triangulation
        |                   2. Triangle classification (sleeve / junction / terminal)
        |                   3. Medial axis extraction (skeleton)
        |                   4. Height field computation
        |                   5. 3D mesh inflation
        v
Puppet.cs                <- Puppet GameObject:
        |                   - Stores mesh and skeleton
        |                   - Harmonic Skinning deformation
        |                   - Mass-spring physics
        |                   - Texture mapping
        v
Drawer.cs                <- UI Controller:
                            - Input handling
                            - GUI rendering (OnGUI)
                            - Mode coordination
```

### Domain Stitching Pipeline (Advanced)

```
Multiple sketch contours (multi-part input)
        |
        v
ExtractOuterContour()    <- Phase 1: Extract the outer boundary
        |
        v
ConstrainedDelaunay      <- Phase 2: CDT triangulation
Triangulation.cs
        |
        v
DomainMerger.cs          <- Phase 3: Domain merging
        |                   - Front/back domain pairing
        |                   - Boundary duplication
        |                   - Vertex deduplication
        v
PoissonHeightField       <- Phase 4: Solve the Poisson equation
Solver.cs                   (Laplacian: Div^2 h = s * a * c)
        |
        v
MeshInflationUtility.cs  <- Phase 5: 3D mesh inflation
        |                   + ARAP deformation constraints
        v
Puppet.cs                <- Final mesh output
```

---

## Animation and Painting

### Recording Joint Motion

1. Create a mesh, then click the **Animation Mode** button in the right panel.
2. Click a skeleton joint (the red circular dot) to select it.
3. Click and drag to begin recording the motion path.
4. Release the mouse button to stop recording.
5. Click **Play** to replay the animation.

### Painting the Mesh Surface

1. Click **Draw on Surface** in the right panel.
2. Select a color from the **Color Wheel**.
3. Adjust the **Brush Size** slider.
4. Click and drag on the mesh surface to paint.

### Exporting a GLB File

1. Click on a puppet to select it.
2. In the panel, click **Export GLB**.
3. Choose a save path.
4. The exported `.glb` file can be opened in Windows 3D Viewer, Blender, or any GLB-compatible viewer.

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

| Resource | Link |
|----------|------|
| Teddy Algorithm Paper (Igarashi 1999) | *"Teddy: A Sketching Interface for 3D Freeform Design"* |
| Unity Manual | https://docs.unity3d.com/Manual |
| Unity Scripting API | https://docs.unity3d.com/ScriptReference |
| Domain Stitching Guide | [DOMAIN_STITCHING_GUIDE.md](Teddy/DOMAIN_STITCHING_GUIDE.md) |
| Implementation Plan | [DOMAIN_STITCHING_IMPLEMENTATION_PLAN.md](Teddy/DOMAIN_STITCHING_IMPLEMENTATION_PLAN.md) |

---

## Author

Undergraduate Thesis, Department of Information Technology  
Academic Year: 2025 to 2026

---

## License

This project was developed for academic and research purposes.  
The original Teddy algorithm is credited to Takeo Igarashi, Satoshi Matsuoka, and Hidehiko Tanaka (1999).
