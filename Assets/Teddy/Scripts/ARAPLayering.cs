using UnityEngine;
using System.Collections.Generic;

namespace mattatz.TeddySystem {

    /// <summary>
    /// ARAP-L (As-Rigid-As-Possible with Layering) constraints
    /// Implements depth ordering for domain stitching
    /// Ensures appendages (legs, arms) are layered above body
    /// </summary>
    public class ARAPLayering {

        /// <summary>
        /// Apply layering constraints to stitched mesh
        /// Assigns Z-depth based on sketch type (body vs appendage)
        /// </summary>
        public static void ApplyLayering(
            SketchCollection collection,
            List<Vector2> vertices2D,
            out List<Vector3> vertices3D) {

            Debug.Log("[ARAP-L] Applying layering constraints...");

            vertices3D = new List<Vector3>();

            // Default depth values
            float bodyDepth = 0f;
            float appendageDepth = 0.1f; // Slightly in front

            // Track which vertices belong to which sketch
            Dictionary<int, int> vertexToSketch = new Dictionary<int, int>();
            int offset = 0;

            foreach (var sketch in collection.sketches) {
                for (int i = 0; i < sketch.vertices.Count; i++) {
                    vertexToSketch[offset + i] = sketch.sketchID;
                }
                offset += sketch.vertices.Count;
            }

            // Assign depths based on sketch type
            for (int i = 0; i < vertices2D.Count; i++) {
                Vector2 v2 = vertices2D[i];
                float depth = bodyDepth;

                // Check if this vertex belongs to an appendage
                if (vertexToSketch.ContainsKey(i)) {
                    int sketchID = vertexToSketch[i];
                    var sketch = collection.sketches.Find(s => s.sketchID == sketchID);
                    if (sketch != null && sketch.type == SketchType.Appendage) {
                        depth = appendageDepth;
                    }
                }

                vertices3D.Add(new Vector3(v2.x, v2.y, depth));
            }

            Debug.Log($"[ARAP-L] Applied layering to {vertices3D.Count} vertices");
        }

        /// <summary>
        /// Compute ARAP energy for mesh deformation
        /// Used to maintain rigidity while allowing layering
        /// </summary>
        public static float ComputeARAPEnergy(
            List<Vector3> originalPositions,
            List<Vector3> deformedPositions,
            List<int> triangles) {

            if (originalPositions.Count != deformedPositions.Count) {
                Debug.LogWarning("[ARAP-L] Position count mismatch!");
                return float.MaxValue;
            }

            float energy = 0f;

            // For each triangle, compute rotation and measure deviation
            for (int t = 0; t < triangles.Count; t += 3) {
                int i0 = triangles[t];
                int i1 = triangles[t + 1];
                int i2 = triangles[t + 2];

                if (i0 >= originalPositions.Count || i1 >= originalPositions.Count || i2 >= originalPositions.Count)
                    continue;

                // Original triangle edges
                Vector3 e1_orig = originalPositions[i1] - originalPositions[i0];
                Vector3 e2_orig = originalPositions[i2] - originalPositions[i0];

                // Deformed triangle edges
                Vector3 e1_def = deformedPositions[i1] - deformedPositions[i0];
                Vector3 e2_def = deformedPositions[i2] - deformedPositions[i0];

                // Measure deviation from rigid transformation
                float deviation = Vector3.Distance(e1_orig.normalized, e1_def.normalized) +
                                  Vector3.Distance(e2_orig.normalized, e2_def.normalized);

                energy += deviation;
            }

            return energy;
        }

        /// <summary>
        /// Smooth depth transitions at boundaries
        /// Prevents sharp Z-discontinuities at stitch seams
        /// </summary>
        public static void SmoothDepthTransitions(
            List<Vector3> vertices,
            List<int> triangles,
            int iterations = 3) {

            Debug.Log($"[ARAP-L] Smoothing depth transitions ({iterations} iterations)...");

            // Build adjacency
            Dictionary<int, List<int>> adjacency = new Dictionary<int, List<int>>();
            for (int i = 0; i < vertices.Count; i++) {
                adjacency[i] = new List<int>();
            }

            for (int t = 0; t < triangles.Count; t += 3) {
                int i0 = triangles[t];
                int i1 = triangles[t + 1];
                int i2 = triangles[t + 2];

                if (!adjacency[i0].Contains(i1)) adjacency[i0].Add(i1);
                if (!adjacency[i0].Contains(i2)) adjacency[i0].Add(i2);
                if (!adjacency[i1].Contains(i0)) adjacency[i1].Add(i0);
                if (!adjacency[i1].Contains(i2)) adjacency[i1].Add(i2);
                if (!adjacency[i2].Contains(i0)) adjacency[i2].Add(i0);
                if (!adjacency[i2].Contains(i1)) adjacency[i2].Add(i1);
            }

            // Laplacian smoothing on Z-coordinate only
            for (int iter = 0; iter < iterations; iter++) {
                List<float> newDepths = new List<float>();

                for (int i = 0; i < vertices.Count; i++) {
                    float avgDepth = vertices[i].z;
                    var neighbors = adjacency[i];

                    if (neighbors.Count > 0) {
                        float sum = 0f;
                        foreach (var n in neighbors) {
                            sum += vertices[n].z;
                        }
                        avgDepth = sum / neighbors.Count;
                    }

                    newDepths.Add(avgDepth);
                }

                // Apply smoothed depths
                for (int i = 0; i < vertices.Count; i++) {
                    vertices[i] = new Vector3(vertices[i].x, vertices[i].y, newDepths[i]);
                }
            }

            Debug.Log("[ARAP-L] Depth smoothing complete");
        }

        /// <summary>
        /// Detect and resolve depth conflicts
        /// Ensures no overlapping geometry at same depth
        /// </summary>
        public static void ResolveDepthConflicts(
            List<Vector3> vertices,
            List<int> triangles,
            float minSeparation = 0.05f) {

            Debug.Log("[ARAP-L] Resolving depth conflicts...");

            // Group vertices by approximate depth
            Dictionary<int, List<int>> depthGroups = new Dictionary<int, List<int>>();

            for (int i = 0; i < vertices.Count; i++) {
                int depthBucket = Mathf.RoundToInt(vertices[i].z / minSeparation);
                if (!depthGroups.ContainsKey(depthBucket)) {
                    depthGroups[depthBucket] = new List<int>();
                }
                depthGroups[depthBucket].Add(i);
            }

            // Check for conflicts within each group
            int conflictsResolved = 0;
            foreach (var group in depthGroups.Values) {
                if (group.Count > 1) {
                    // Check if vertices are spatially close (potential conflict)
                    for (int i = 0; i < group.Count; i++) {
                        for (int j = i + 1; j < group.Count; j++) {
                            int idx1 = group[i];
                            int idx2 = group[j];

                            Vector2 pos1 = new Vector2(vertices[idx1].x, vertices[idx1].y);
                            Vector2 pos2 = new Vector2(vertices[idx2].x, vertices[idx2].y);

                            if (Vector2.Distance(pos1, pos2) < minSeparation) {
                                // Conflict detected - offset one vertex
                                vertices[idx2] = new Vector3(
                                    vertices[idx2].x,
                                    vertices[idx2].y,
                                    vertices[idx2].z + minSeparation);
                                conflictsResolved++;
                            }
                        }
                    }
                }
            }

            Debug.Log($"[ARAP-L] Resolved {conflictsResolved} depth conflicts");
        }
    }
}
