using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using mattatz.Triangulation2DSystem;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Main coordinator for the Domain Stitching Algorithm.
    /// Handles multi-part mesh generation with proper domain closure and stitching.
    /// </summary>
    public class DomainStitchingSystem {

        public class StitchedDomain {
            public List<Vector2> boundary;           // User-drawn contour
            public List<Vector2> closureCurve;       // Closing curve Bp (if open)
            public List<Vector2> vertices;           // All vertices in the domain
            public List<int> triangles;              // Triangle connectivity
            public float inflationAmount = 1.0f;     // Inflation distance c
            public bool isOpenContour = false;       // Whether contour is open
            public int domainID;
            public bool isAppendage = false;         // Mark as appendage (leg/arm)
        }

        public class BodyPartConfig {
            public StitchedDomain frontFacing;       // Forward-facing region
            public StitchedDomain backFacing;        // Back-facing region (-c inflation)
            public int partID;
        }

        private List<BodyPartConfig> bodyParts = new List<BodyPartConfig>();
        private List<Vector3> stitchedVertices = new List<Vector3>();
        private List<int> stitchedTriangles = new List<int>();
        private float laplacianWeight = 1.0f;

        /// <summary>
        /// Initialize domain stitching from user-drawn contours
        /// </summary>
        public void InitializeFromContours(List<List<Vector2>> contours, List<bool> isOpenList = null) {
            Debug.Log($"[DomainStitching] Initializing with {contours?.Count ?? 0} contours...");
            bodyParts.Clear();
            stitchedVertices.Clear();
            stitchedTriangles.Clear();

            if (contours == null || contours.Count == 0) return;

            for (int i = 0; i < contours.Count; i++) {
                bool isOpen = isOpenList != null && i < isOpenList.Count ? isOpenList[i] : IsContourOpen(contours[i]);
                CreateBodyPart(contours[i], i, isOpen);
            }
        }

        /// <summary>
        /// Detect if a contour is open or closed
        /// </summary>
        private bool IsContourOpen(List<Vector2> contour) {
            if (contour.Count < 3) return false;
            float distance = Vector2.Distance(contour[0], contour[contour.Count - 1]);
            return distance > 0.1f; // Consider open if endpoints are far apart
        }

        /// <summary>
        /// Create a body part (front and back facing regions) from a contour
        /// </summary>
        private void CreateBodyPart(List<Vector2> contour, int partID, bool isOpen) {
            var config = new BodyPartConfig { partID = partID };

            // Front-facing region
            config.frontFacing = new StitchedDomain {
                domainID = partID * 2,
                boundary = new List<Vector2>(contour),
                isOpenContour = isOpen,
                isAppendage = isOpen
            };

            // Back-facing region (mirror)
            config.backFacing = new StitchedDomain {
                domainID = partID * 2 + 1,
                boundary = new List<Vector2>(contour),
                isOpenContour = isOpen,
                isAppendage = isOpen
            };

            // If contour is open, create a closure curve
            if (isOpen) {
                config.frontFacing.closureCurve = GenerateClosureCurve(contour);
                config.backFacing.closureCurve = GenerateClosureCurve(contour);
            }

            bodyParts.Add(config);
        }

        /// <summary>
        /// Generate a closure curve Bp for open contours
        /// Uses a smooth curve connecting the endpoints of the open contour
        /// </summary>
        private List<Vector2> GenerateClosureCurve(List<Vector2> openContour) {
            if (openContour.Count < 2) return new List<Vector2>();

            Vector2 start = openContour[0];
            Vector2 end = openContour[openContour.Count - 1];

            // Generate a smooth Bezier curve connecting the endpoints
            List<Vector2> closure = new List<Vector2>();
            int steps = 10;

            for (int i = 0; i <= steps; i++) {
                float t = i / (float)steps;
                // Simple linear interpolation for now; could use Bezier for smoother curves
                Vector2 point = Vector2.Lerp(end, start, t);
                closure.Add(point);
            }

            return closure;
        }

        /// <summary>
        /// Execute full domain stitching pipeline
        /// </summary>
        public Mesh GenerateStitchedMesh(Vector3 cameraLocalPos, float inflationAmount = 1.0f, bool smoothHeightFields = true) {
            if (bodyParts.Count == 0) return null;

            Debug.Log($"[DomainStitching] Generating stitched mesh for {bodyParts.Count} body parts...");

            // Step 1: Generate Delaunay triangulations for each domain
            Debug.Log($"[DomainStitching] Step 1: Triangulating {bodyParts.Count} domains...");
            foreach (var part in bodyParts) {
                TriangulateDomain(part.frontFacing, inflationAmount);
                TriangulateDomain(part.backFacing, -inflationAmount);
                
                int fv = part.frontFacing.vertices?.Count ?? 0;
                int ft = part.frontFacing.triangles?.Count ?? 0;
                Debug.Log($"[DomainStitching] Part {part.partID} - Front: {fv} verts, {ft/3} tris | Back: {part.backFacing.vertices?.Count ?? 0} verts");
            }

            // Step 2: Merge domains into a single mesh
            Debug.Log("[DomainStitching] Step 2: Merging domains...");
            var domainMerger = new DomainMerger();
            (stitchedVertices, stitchedTriangles) = domainMerger.MergeDomains(bodyParts);
            
            Debug.Log($"[DomainStitching] Merged mesh: {stitchedVertices.Count} vertices, {stitchedTriangles.Count / 3} triangles.");

            if (stitchedVertices.Count == 0 || stitchedTriangles.Count == 0) {
                Debug.LogError("[DomainStitching] Merged mesh is empty. Triangulation or merging failed.");
                return null;
            }

            // Log appendage triangle count
            int appTriCount = 0;
            if (domainMerger.triangleIsAppendage != null) {
                appTriCount = domainMerger.triangleIsAppendage.Count(t => t);
            }
            Debug.Log($"[DomainStitching] Internal logic check: {appTriCount} appendage triangles found.");

            // Spatial check based on sketch coordinates as requested
            int spatialAppCount = 0;
            var appendageContours = bodyParts
                .Where(p => p.frontFacing.isAppendage)
                .Select(p => p.frontFacing.boundary)
                .ToList();

            // Print 4 sample points for each appendage sketch as requested
            foreach (var part in bodyParts) {
                if (part.frontFacing.isAppendage) {
                    var contour = part.frontFacing.boundary;
                    string samplePoints = "";
                    for (int j = 0; j < Mathf.Min(4, contour.Count); j++) {
                        samplePoints += $"({contour[j].x:F3}, {contour[j].y:F3}) ";
                    }
                    Debug.Log($"[DomainStitching] Leg Sketch Sample Points (XY): {samplePoints}");
                }
            }

            for (int i = 0; i < stitchedTriangles.Count; i += 3) {
                Vector3 v0 = stitchedVertices[stitchedTriangles[i]];
                Vector3 v1 = stitchedVertices[stitchedTriangles[i + 1]];
                Vector3 v2 = stitchedVertices[stitchedTriangles[i + 2]];
                
                // Use centroid for spatial check
                Vector2 triCentroid = new Vector2(
                    (v0.x + v1.x + v2.x) / 3f,
                    (v0.y + v1.y + v2.y) / 3f
                );

                foreach (var poly in appendageContours) {
                    if (IsPointInPolygon(triCentroid, poly)) {
                        spatialAppCount++;
                        break;
                    }
                }
            }
            Debug.Log($"[DomainStitching] Spatial check: Found {spatialAppCount} triangles inside leg sketch regions.");

            // Step 3: Compute height fields via Poisson equation
            Debug.Log("[DomainStitching] Step 3: Solving Poisson height fields...");
            var heightSolver = new PoissonHeightFieldSolver();
            List<float> heightFields = heightSolver.SolveHeightFields(
                stitchedVertices.Cast<Vector3>().ToList(),
                stitchedTriangles,
                bodyParts,
                inflationAmount
            );

            if (heightFields != null && heightFields.Count > 0) {
                float minH = heightFields.Min();
                float maxH = heightFields.Max();
                float avgH = heightFields.Average();
                Debug.Log($"[DomainStitching] Poisson solver complete. Heights - Min: {minH:F4}, Max: {maxH:F4}, Avg: {avgH:F4}, Count: {heightFields.Count}");
            } else {
                Debug.LogWarning("[DomainStitching] Poisson solver returned no height fields.");
            }

            // Step 4: Inflate to 3D mesh
            Debug.Log("[DomainStitching] Step 4: Inflating to 3D and finalizing mesh...");
            var inflater = new MeshInflationUtility();
            Mesh finalMesh = inflater.InflateTo3D(
                stitchedVertices.Cast<Vector3>().ToList(),
                stitchedTriangles,
                heightFields,
                cameraLocalPos,
                smoothHeightFields
            );

            if (finalMesh == null) {
                Debug.LogError("[DomainStitching] Mesh generation failed.");
                return null;
            }

            // Step 5: Apply ARAP-L deformation with depth-ordering constraints
            Debug.Log("[DomainStitching] Step 5: Applying ARAP-L deformation with depth constraints...");
            var constraints = GenerateClosureConstraints();
            if (constraints != null && constraints.Count > 0) {
                Debug.Log($"[DomainStitching] Applying {constraints.Count} depth-ordering constraints...");
                // Reduced iterations to prevent mesh distortion
                inflater.ApplyARAPDeformation(ref finalMesh, constraints, iterations: 3);
            } else {
                Debug.Log("[DomainStitching] No depth constraints to apply.");
            }

            // Final statistics
            Vector3 centroid = Vector3.zero;
            Vector3[] verts = finalMesh.vertices;
            foreach (var v in verts) centroid += v;
            if (verts.Length > 0) centroid /= verts.Length;

            Debug.Log($"[DomainStitching] Mesh generation complete. " +
                      $"Vertices: {finalMesh.vertexCount}, " +
                      $"Triangles: {finalMesh.triangles.Length / 3}, " +
                      $"Centroid: {centroid.ToString("F3")}");

            return finalMesh;
        }

        /// <summary>
        /// Triangulate a single domain using the robust Triangulation2D system
        /// </summary>
        private void TriangulateDomain(StitchedDomain domain, float inflationSign) {
            // Combine boundary and closure curve to form a closed loop
            var points = new List<Vector2>(domain.boundary);
            if (domain.closureCurve != null && domain.closureCurve.Count > 0) {
                points.AddRange(domain.closureCurve);
            }

            // --- Clean points to prevent triangulation failures ---
            var cleanPoints = new List<Vector2>();
            if (points.Count > 0) {
                cleanPoints.Add(points[0]);
                for (int i = 1; i < points.Count; i++) {
                    if (Vector2.Distance(points[i], points[i - 1]) > 0.001f) {
                        cleanPoints.Add(points[i]);
                    }
                }
            }
            // Ensure loop is closed but without duplicate start/end for the triangulator
            if (cleanPoints.Count > 2 && Vector2.Distance(cleanPoints[0], cleanPoints.Last()) < 0.001f) {
                cleanPoints.RemoveAt(cleanPoints.Count - 1);
            }

            if (cleanPoints.Count < 3) {
                Debug.LogWarning($"[DomainStitching] Domain {domain.domainID} has insufficient points ({cleanPoints.Count}).");
                domain.vertices = new List<Vector2>();
                domain.triangles = new List<int>();
                return;
            }

            try {
                // Use the reliable Triangulation2D from the package
                var polygon = Polygon2D.Contour(cleanPoints.ToArray());
                var triangulation = new Triangulation2D(polygon, 0f);

                // Map results back to domain
                domain.vertices = triangulation.Points.Select(v => v.Coordinate).ToList();
                domain.triangles = new List<int>();
                
                var pointsList = triangulation.Points.ToList();
                foreach (var t in triangulation.Triangles) {
                    domain.triangles.Add(pointsList.IndexOf(t.a));
                    domain.triangles.Add(pointsList.IndexOf(t.b));
                    domain.triangles.Add(pointsList.IndexOf(t.c));
                }
            } catch (System.Exception e) {
                Debug.LogError($"[DomainStitching] Triangulation failed for domain {domain.domainID}: {e.Message}");
                domain.vertices = new List<Vector2>();
                domain.triangles = new List<int>();
            }

            // Store inflation amount
            domain.inflationAmount = inflationSign;
        }

        /// <summary>
        /// Get the generated body parts (for debugging or further processing)
        /// </summary>
        public List<BodyPartConfig> GetBodyParts() {
            return bodyParts;
        }

        /// <summary>
        /// Create deformation constraints for mesh closure
        /// Ensures back-facing vertices align with body cavity during deformation
        /// </summary>
        public List<DeformationConstraint> GenerateClosureConstraints() {
            var constraints = new List<DeformationConstraint>();

            // Only apply constraints to open contours (attached parts like legs)
            for (int partIdx = 0; partIdx < bodyParts.Count; partIdx++) {
                var part = bodyParts[partIdx];
                
                if (part.frontFacing.isOpenContour) {
                    Debug.Log($"[DomainStitching] Generating depth constraints for open contour part {partIdx}...");
                    
                    // Inequality constraint: front half boundary vertices must lift above body
                    var frontBoundaryIndices = GetGlobalBoundaryVertices(part.frontFacing);
                    if (frontBoundaryIndices.Count > 0) {
                        constraints.Add(new DeformationConstraint {
                            type = ConstraintType.Inequality,
                            targetZ = 0.5f,  // Lift above the base plane
                            affectedVertices = frontBoundaryIndices
                        });
                        Debug.Log($"[DomainStitching] Added inequality constraint for {frontBoundaryIndices.Count} front boundary vertices.");
                    }

                    // Equality constraint: back half stays pinned inside body
                    var backBoundaryIndices = GetGlobalBoundaryVertices(part.backFacing);
                    if (backBoundaryIndices.Count > 0) {
                        constraints.Add(new DeformationConstraint {
                            type = ConstraintType.Equality,
                            affectedVertices = backBoundaryIndices
                        });
                        Debug.Log($"[DomainStitching] Added equality constraint for {backBoundaryIndices.Count} back boundary vertices.");
                    }
                }
            }

            return constraints;
        }

        /// <summary>
        /// Get global vertex indices for boundary vertices of a domain in the merged mesh
        /// IMPORTANT: This searches in the INFLATED mesh, not stitchedVertices
        /// </summary>
        private List<int> GetGlobalBoundaryVertices(StitchedDomain domain) {
            var globalIndices = new List<int>();
            
            // NOTE: stitchedVertices are 2D (z=0), but after inflation vertices have height
            // We need to match based on XY coordinates only
            
            // Search through stitchedVertices (2D planar) to find matching boundary vertices
            for (int i = 0; i < stitchedVertices.Count; i++) {
                Vector3 vert3D = stitchedVertices[i];
                Vector2 vert2D = new Vector2(vert3D.x, vert3D.y);
                
                // Check if this vertex matches any boundary vertex
                foreach (var boundaryVert in domain.boundary) {
                    if (Vector2.Distance(vert2D, boundaryVert) < 0.01f) {
                        if (!globalIndices.Contains(i)) {
                            globalIndices.Add(i);
                        }
                        break;
                    }
                }
            }
            
            Debug.Log($"[DomainStitching] Found {globalIndices.Count} global boundary vertices for domain {domain.domainID}");
            return globalIndices;
        }

        /// <summary>
        /// Get statistics about the stitched mesh
        /// </summary>
        public (int vertexCount, int triangleCount, int domainCount) GetMeshStats() {
            return (
                stitchedVertices.Count,
                stitchedTriangles.Count / 3,
                bodyParts.Count
            );
        }

        /// <summary>
        /// Extracts skeleton bones for the stitched puppet.
        /// Iterates through each body part and uses the Teddy algorithm to find the chordal axis.
        /// Connects bones between parts to form a cohesive skeleton.
        /// </summary>
        public List<(Vector3, Vector3)> GetSkeletonBones() {
            var allBones = new List<(Vector3, Vector3)>();
            var partSkeletons = new List<List<(Vector3, Vector3)>>();
            
            if (bodyParts == null || bodyParts.Count == 0) {
                Debug.LogWarning("[DomainStitching] No body parts available for skeleton extraction.");
                return allBones;
            }

            Debug.Log($"[DomainStitching] Extracting skeletons for {bodyParts.Count} body parts...");

            // 1. Extract skeleton for each part independently
            foreach (var part in bodyParts) {
                var rawPoints = new List<Vector2>(part.frontFacing.boundary);
                if (part.frontFacing.closureCurve != null && part.frontFacing.closureCurve.Count > 0) {
                    rawPoints.AddRange(part.frontFacing.closureCurve);
                }

                // Cleanup points (duplicates crash triangulation)
                var cleanPoints = CleanContour(rawPoints);
                if (cleanPoints.Count < 3) continue;

                var bones = new List<(Vector3, Vector3)>();
                try {
                    Teddy t = new Teddy(cleanPoints);
                    var extracted = t.GetSkeletonBones();
                    if (extracted != null && extracted.Count > 0) {
                        bones.AddRange(extracted);
                    } else {
                        AddFallbackBone(cleanPoints, bones);
                    }
                } catch {
                    AddFallbackBone(cleanPoints, bones);
                }
                partSkeletons.Add(bones);
            }

            // 2. Connect skeletons between parts
            // We assume the first part is the main body. Other parts connect to the nearest joint in the accumulated skeleton.
            if (partSkeletons.Count > 0) {
                allBones.AddRange(partSkeletons[0]);

                for (int i = 1; i < partSkeletons.Count; i++) {
                    var currentPartBones = partSkeletons[i];
                    if (currentPartBones.Count == 0) continue;

                    // Find attachment point (midpoint of the closure curve for this part)
                    var part = bodyParts[i];
                    if (part.frontFacing.closureCurve != null && part.frontFacing.closureCurve.Count > 0) {
                        Vector2 attachment2D = Vector2.zero;
                        foreach (var p in part.frontFacing.closureCurve) attachment2D += p;
                        attachment2D /= part.frontFacing.closureCurve.Count;
                        Vector3 attachment3D = new Vector3(attachment2D.x, attachment2D.y, 0.001f);

                        // Find closest joint in existing skeleton
                        Vector3 closestJointInAll = FindClosestJoint(attachment3D, allBones);
                        // Find closest joint in current part's skeleton
                        Vector3 closestJointInCurrent = FindClosestJoint(attachment3D, currentPartBones);

                        // Add connecting bone
                        allBones.Add((closestJointInAll, closestJointInCurrent));
                    }

                    allBones.AddRange(currentPartBones);
                }
            }

            Debug.Log($"[DomainStitching] Final skeleton has {allBones.Count} bones.");
            return allBones;
        }

        private List<Vector2> CleanContour(List<Vector2> points) {
            var result = new List<Vector2>();
            if (points.Count == 0) return result;
            result.Add(points[0]);
            for (int i = 1; i < points.Count; i++) {
                if (Vector2.Distance(points[i], points[i - 1]) > 0.001f) result.Add(points[i]);
            }
            if (result.Count > 1 && Vector2.Distance(result[0], result.Last()) < 0.001f) result.RemoveAt(result.Count - 1);
            return result;
        }

        /// <summary>
        /// Ray casting algorithm for point-in-polygon test
        /// </summary>
        private bool IsPointInPolygon(Vector2 p, List<Vector2> poly) {
            int n = poly.Count;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++) {
                if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                    (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)) {
                    inside = !inside;
                }
            }
            return inside;
        }

        private Vector3 FindClosestJoint(Vector3 target, List<(Vector3, Vector3)> bones) {
            Vector3 closest = target;
            float minDist = float.MaxValue;
            foreach (var b in bones) {
                float d0 = Vector3.Distance(target, b.Item1);
                if (d0 < minDist) { minDist = d0; closest = b.Item1; }
                float d1 = Vector3.Distance(target, b.Item2);
                if (d1 < minDist) { minDist = d1; closest = b.Item2; }
            }
            return closest;
        }

        private void AddFallbackBone(List<Vector2> points, List<(Vector3, Vector3)> boneList) {
            if (points.Count == 0) return;
            Vector2 centroid = Vector2.zero;
            foreach (var p in points) centroid += p;
            centroid /= points.Count;
            boneList.Add((new Vector3(centroid.x, centroid.y, 0.001f), new Vector3(points[0].x, points[0].y, 0.001f)));
        }
    }

    /// <summary>
    /// Deformation constraint for mesh closure enforcement
    /// </summary>
    public class DeformationConstraint {
        public ConstraintType type;
        public List<int> affectedVertices;
        public float targetZ;
    }

    public enum ConstraintType {
        Equality,      // C = : vertices must match exactly
        Inequality     // C ≥ : vertices must stay above target
    }

}
