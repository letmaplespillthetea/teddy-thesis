# Domain Stitching Implementation Plan
Based on "Teddy: A Sketching Interface for 3D Freeform Design" - Figure 4

## Overview
Implement full domain stitching pipeline that:
1. Duplicates domains symmetrically (Ωp → Ωp & Ωp′)
2. Creates holes in closed domains where open domains attach
3. Stitches domains along boundaries
4. Applies inequality/equality constraints for proper layering
5. Inflates to 3D with constraints

## Architecture

### Phase 1: Domain Analysis & Preparation
**Input:** Multiple 2D contours (open/closed)
**Output:** Analyzed domains with attachment points

```
For each contour:
  - Detect if open or closed
  - Find chordal axis
  - Identify attachment points (Bp curves)
  - Classify as body part (closed) or appendage (open)
```

### Phase 2: Domain Duplication
**For each domain Ωi:**
```
1. Triangulate domain → vertices Vi, triangles Ti
2. Create symmetric replica Ωi′:
   - Mirror all vertices: Vi′ = {(x, y, -z) for (x,y,z) in Vi}
   - Reverse triangle winding for back-face
3. Store: (Ωi, Ωi′, boundary_i, is_open_i)
```

### Phase 3: Hole Creation (for closed domains)
**For each closed domain Ωq that has open domains attaching:**
```
1. Find attachment curve Bp (where open domain p connects)
2. Split Ωq along Bp:
   - Duplicate vertices along Bp
   - Create hole by separating front/back boundaries
3. Result: Ωq with hole ready for Ωp attachment
```

### Phase 4: Domain Stitching
**Stitch symmetric pairs:**
```
For each domain i:
  If open:
    - Stitch Ωi to Ωi′ along boundary EXCEPT at Bp (attachment point)
    - Leave Bp open for connection to parent domain
  If closed:
    - Stitch Ωi to Ωi′ along entire boundary
    - If has hole: keep hole edges unstitched
```

**Stitch parent-child connections:**
```
For each open domain p attaching to closed domain q:
  1. Find hole in Ωq created for p
  2. Connect Ωp boundary to upper half of hole
  3. Add equality constraint: Ωp′ boundary = lower half of hole
     (forces back half to penetrate and meet inside)
```

### Phase 5: Constraint Setup
**Inequality constraints (C≥):**
```
For each open domain p (appendage):
  - Front half vertices: z ≥ z_parent
  - Ensures appendage lies in front of body
```

**Equality constraints (C=):**
```
For each open domain p:
  - Back half boundary vertices = corresponding hole vertices in parent
  - Forces back half to penetrate body and connect inside
```

### Phase 6: Inflation with Constraints
```
1. Compute height fields for merged domain
2. Apply ARAP-L deformation with constraints:
   - Minimize: E_ARAP + λ_inequality * E_inequality + λ_equality * E_equality
   - Iterative solver (5-10 iterations)
3. Result: 3D mesh with proper layering
```

## Implementation Files

### 1. `DomainStitchingPipeline.cs` (NEW)
Main orchestrator for the entire pipeline
- `AnalyzeDomains(contours, isOpenList)`
- `DuplicateDomains(domains)`
- `CreateHoles(closedDomains, attachmentInfo)`
- `StitchDomains(domains, attachments)`
- `SetupConstraints(domains, attachments)`
- `InflateWithConstraints(mergedDomain, constraints)`

### 2. `DomainAnalyzer.cs` (NEW)
Analyzes contours and finds attachment points
- `DetectAttachmentPoints(openContour, closedContour)`
- `FindChordalAxis(contour)`
- `ClassifyDomain(contour, isOpen)`

### 3. `HoleCreator.cs` (NEW)
Creates holes in closed domains for attachment
- `CreateHole(domain, attachmentCurve)`
- `SplitDomainAlongCurve(domain, curve)`
- `DuplicateBoundaryVertices(domain, curve)`

### 4. `ConstraintSolver.cs` (NEW)
Handles inequality/equality constraints during deformation
- `AddInequalityConstraint(vertices, minZ)`
- `AddEqualityConstraint(vertices1, vertices2)`
- `SolveWithConstraints(mesh, constraints, iterations)`

### 5. Update `MeshInflationUtility.cs`
Add constraint-aware inflation
- `InflateWithConstraints(domain, constraints)`

### 6. Update `Teddy.cs`
Integrate with traditional pipeline
- Add option to use domain stitching
- Keep traditional path for single contours

## Data Structures

```csharp
class Domain {
    List<Vector2> vertices;
    List<int> triangles;
    List<Vector2> boundary;
    Vector2[] chordalAxis;
    bool isOpen;
    int domainID;
    Domain symmetricReplica; // Ωi′
}

class AttachmentInfo {
    int childDomainID;  // Open domain (leg)
    int parentDomainID; // Closed domain (body)
    List<Vector2> attachmentCurve; // Bp
    int attachmentPointIndex; // Where on chordal axis
}

class Constraint {
    enum Type { Inequality, Equality }
    Type type;
    List<int> vertexIndices;
    Vector3 targetValue; // For inequality: minZ, For equality: target position
}
```

## Testing Strategy

### Test 1: Simple Leg + Body
- Draw closed circle (body)
- Draw open curve from circle (leg)
- Verify: leg front is in front, leg back penetrates

### Test 2: Multiple Appendages
- Body + 2 legs + 2 arms
- Verify: all appendages layer correctly

### Test 3: Complex Topology
- Body + head + legs + arms + tail
- Verify: no holes, proper connections

## Implementation Order

1. ✅ Fix current bugs (infinite recursion)
2. ⬜ Create `DomainAnalyzer.cs` - detect attachments
3. ⬜ Create `HoleCreator.cs` - hole creation logic
4. ⬜ Update `MeshInflationUtility.cs` - symmetric duplication
5. ⬜ Create `ConstraintSolver.cs` - constraint handling
6. ⬜ Create `DomainStitchingPipeline.cs` - orchestrate all
7. ⬜ Update `Drawer.cs` - integrate new pipeline
8. ⬜ Test & debug

## Key Challenges

1. **Attachment Detection:** How to automatically find where open curves attach to closed curves
2. **Hole Creation:** Splitting domain along curve while maintaining triangulation
3. **Constraint Solving:** Balancing ARAP energy with hard constraints
4. **Performance:** Multiple domains with constraints can be slow

## References
- Paper: "Teddy: A Sketching Interface for 3D Freeform Design"
- Figure 4: Domain stitching illustration
- ARAP-L: As-Rigid-As-Possible with Layering constraints
