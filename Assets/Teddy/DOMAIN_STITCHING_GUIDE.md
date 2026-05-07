# Domain Stitching Algorithm Implementation Guide

## Overview
A complete implementation of the Domain Stitching algorithm for consistent mesh closure, supporting multi-part body models with proper domain merging and topological connectivity.

## Architecture

### Core Components

#### 1. **DomainStitchingSystem.cs** (Main Orchestrator)
- **Purpose**: Coordinates the entire stitching pipeline
- **Key Methods**:
  - `InitializeFromContours()`: Initialize from user-drawn curves
  - `GenerateStitchedMesh()`: Execute full pipeline
  - `GenerateClosureConstraints()`: Create deformation constraints
- **Features**:
  - Detects open vs. closed contours
  - Creates front/back-facing region pairs
  - Generates closure curves for open boundaries

#### 2. **ConstrainedDelaunayTriangulation.cs** (Mesh Triangulation)
- **Purpose**: Creates constrained Delaunay triangulations respecting boundaries
- **Algorithm**:
  - Boundary constraint enforcement
  - Interior vertex insertion for mesh quality
  - Incremental Delaunay construction
  - Circumcircle-based point insertion
- **Key Methods**:
  - `SetBoundaryConstraint()`: Define domain boundaries
  - `Triangulate()`: Generate CDT mesh
  - `InsertInteriorVertices()`: Adaptive mesh refinement

#### 3. **DomainMerger.cs** (Domain Stitching)
- **Purpose**: Merges multiple 2D domains into a single connected mesh
- **Stitching Operations**:
  - Front/back domain pairing
  - Boundary duplication (creates "holes")
  - Domain connection along stitching curves
  - Vertex mapping and deduplication
- **Key Methods**:
  - `MergeDomains()`: Merge all body parts
  - `StitchFrontAndBack()`: Connect paired regions
  - `AttachOpenBoundaries()`: Create mesh closures

#### 4. **PoissonHeightFieldSolver.cs** (Height Field Computation)
- **Purpose**: Solves Poisson equation for 3D inflation heights
- **Equation**: ∇²h = s·a·c
  - s: sign factor (+1 front, -1 back)
  - a: vertex area (1/3 incident triangles)
  - c: inflation amount
- **Methods**:
  - `BuildLaplacianSystem()`: Construct matrix equation
  - `CalculateCotangentWeights()`: Laplace-Beltrami operator
  - `SolveLinearSystem()`: Jacobi iteration solver
  - `ApplySemiEllipticalShaping()`: h' = sign(h)√|h|

#### 5. **MeshInflationUtility.cs** (3D Mesh Generation)
- **Purpose**: Converts 2D height fields to 3D meshes
- **Operations**:
  - 2D to 3D vertex transformation
  - Mesh stitching and topology
  - Laplacian smoothing
  - ARAP deformation constraints
- **Key Methods**:
  - `InflateTo3D()`: Main inflation pipeline
  - `ApplyARAPDeformation()`: Constraint enforcement
  - `SmoothMesh()`: Quality improvement

### Integration with Drawer.cs

The domain stitching system integrates seamlessly with the existing Teddy system:

1. **New Serialized Fields**:
   ```csharp
   [SerializeField] bool useDomainStitching = false;
   [SerializeField, Range(0.1f, 2f)] float domainInflationAmount = 1.0f;
   [SerializeField] bool smoothHeightFields = true;
   ```

2. **Dual Build Paths**:
   - Traditional: Uses existing Teddy algorithm (backward compatible)
   - Domain Stitching: Uses new stitching pipeline

3. **UI Controls**:
   - Toggle to enable/disable domain stitching
   - Slider for inflation amount adjustment
   - Checkbox for height field smoothing

## Pipeline Execution

```
User Draws Contours
        ↓
Detect Open/Closed
        ↓
Create Front/Back Pairs
        ↓
Generate Delaunay Triangulations
        ↓
Merge Domains with Stitching
        ↓
Solve Poisson Height Fields
        ↓
Inflate to 3D Mesh
        ↓
Apply Constraints & Smoothing
        ↓
Final Mesh Output
```

## Usage

### Basic Usage:
```csharp
// In Inspector, enable Domain Stitching
useDomainStitching = true;

// Draw a contour as normal
// The Build() method will automatically use stitching
```

### Advanced Usage:
```csharp
// Direct C# usage:
var stitchingSystem = new DomainStitchingSystem();
stitchingSystem.InitializeFromContours(contours, isOpenList);
var mesh = stitchingSystem.GenerateStitchedMesh(
    inflationAmount: 1.5f,
    smoothHeightFields: true
);
```

## Key Algorithms

### 1. Constrained Delaunay Triangulation
- Maintains boundary integrity
- Adaptive interior vertex insertion
- Circumcircle criterion verification

### 2. Domain Merging Strategy
- Vertex mapping across domains
- Boundary duplication for hole creation
- Connectivity preservation

### 3. Poisson Equation Solver
- Cotangent Laplacian discretization
- Dirichlet boundary conditions (h=0 at user curves)
- Jacobi iteration convergence

### 4. Semi-Elliptical Inflation
- Front-facing: positive heights
- Back-facing: negative heights
- Smooth connection at boundaries

## Performance Characteristics

- **Triangulation**: O(n log n) for n vertices
- **Poisson Solving**: O(n·m) where m = iterations (typically 10-100)
- **Mesh Generation**: O(n) for inflation
- **Memory**: ~12 bytes per vertex + matrix overhead

## Constraints & Deformation

### Closure Constraints
- **Inequality**: Front half moves in front of body
- **Equality**: Back half aligns with body cavity

### Constraint Types
```csharp
enum ConstraintType {
    Equality,      // C = : exact vertex matching
    Inequality     // C ≥ : directional constraint
}
```

## Debugging & Visualization

```csharp
// Get mesh statistics
var (vertexCount, triangleCount, domainCount) = stitchingSystem.GetMeshStats();

// Debug height fields
var debugMesh = inflater.CreateHeightFieldDebugMesh(
    vertices2D, heightFields, triangles
);

// Get mesh statistics
var stats = inflater.GetMeshStatistics(mesh);
Debug.Log(stats.ToString());
```

## Future Enhancements

1. **Multi-Part Support**: Handle separate body/limb drawings
2. **Bone Extraction**: Auto-skeleton from stitched mesh
3. **Adaptive Refinement**: Dynamic mesh quality adjustment
4. **Real-time Constraints**: Interactive deformation
5. **GPU Acceleration**: CUDA/Compute Shader optimization

## File Locations
- [DomainStitchingSystem.cs](Assets/Teddy/Scripts/DomainStitchingSystem.cs)
- [ConstrainedDelaunayTriangulation.cs](Assets/Teddy/Scripts/ConstrainedDelaunayTriangulation.cs)
- [DomainMerger.cs](Assets/Teddy/Scripts/DomainMerger.cs)
- [PoissonHeightFieldSolver.cs](Assets/Teddy/Scripts/PoissonHeightFieldSolver.cs)
- [MeshInflationUtility.cs](Assets/Teddy/Scripts/MeshInflationUtility.cs)
- [Drawer.cs](Assets/Teddy/Scripts/Drawer.cs) - Integration point
