using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using mattatz.Triangulation2DSystem;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Creates holes in body meshes for appendage attachment
    /// Implements domain splitting along Bp curves
    /// </summary>
    public class HoleCreator {

        /// <summary>
        /// Triangulate a sketch and prepare it for domain stitching
        /// </summary>
        public static void TriangulateSketch(SketchInfo sketch) {
            if (sketch.contour == null || sketch.contour.Count < 3) {
                Debug.LogWarning($"[HoleCreator] Sketch {sketch.sketchID} has invalid contour");
                return;
            }

            // Build complete contour (original + closure curve for open contours)
            List<Vector2> completeContour = new List<Vector2>(sketch.contour);
            if (sketch.isOpen && sketch.attachmentCurve != null) {
                completeContour.AddRange(sketch.attachmentCurve);
            }

            // Triangulate
            var polygon = Polygon2D.Contour(completeContour.ToArray());
            var triangulation = new Triangulation2D(polygon, 0f);

            // Extract vertices and triangles
            sketch.vertices = new List<Vector2>();
            sketch.triangles = new List<int>();
            sketch.boundary = new List<Vector2>(completeContour);

            var vertexMap = new Dictionary<Vertex2D, int>();
            foreach (var tri in triangulation.Triangles) {
                foreach (var v in new[] { tri.a, tri.b, tri.c }) {
                    if (!vertexMap.ContainsKey(v)) {
                        vertexMap[v] = sketch.vertices.Count;
                        sketch.vertices.Add(v.Coordinate);
                    }
                }

                sketch.triangles.Add(vertexMap[tri.a]);
                sketch.triangles.Add(vertexMap[tri.b]);
                sketch.triangles.Add(vertexMap[tri.c]);
            }

            Debug.Log($"[HoleCreator] Triangulated sketch {sketch.sketchID}: {sketch.vertices.Count} vertices, {sketch.triangles.Count / 3} triangles");
        }

        /// <summary>
        /// Create a hole in a body mesh at the attachment point of an appendage
        /// Returns the hole boundary vertices (for stitching in Phase 4)
        /// </summary>
        public static List<int> CreateHole(SketchInfo bodySketch, SketchInfo appendageSketch) {
            if (bodySketch == null || appendageSketch == null) {
                Debug.LogWarning("[HoleCreator] Invalid sketches for hole creation");
                return new List<int>();
            }

            if (!appendageSketch.isOpen) {
                Debug.LogWarning($"[HoleCreator] Appendage {appendageSketch.sketchID} is not open, cannot create hole");
                return new List<int>();
            }

            Debug.Log($"[HoleCreator] Creating hole in body {bodySketch.sketchID} for appendage {appendageSketch.sketchID}");

            // Find the attachment curve (Bp) on the body
            Vector2 attachPoint = appendageSketch.attachmentPointOnBody;
            List<Vector2> holeCurve = new List<Vector2>(appendageSketch.attachmentCurve);

            // Find vertices on body mesh that are close to the hole curve
            List<int> holeVertexIndices = new List<int>();
            float threshold = 0.05f; // Distance threshold for matching

            foreach (var holePoint in holeCurve) {
                int closestIdx = -1;
                float minDist = float.MaxValue;

                for (int i = 0; i < bodySketch.vertices.Count; i++) {
                    float dist = Vector2.Distance(bodySketch.vertices[i], holePoint);
                    if (dist < minDist) {
                        minDist = dist;
                        closestIdx = i;
                    }
                }

                if (closestIdx >= 0 && minDist < threshold) {
                    if (!holeVertexIndices.Contains(closestIdx)) {
                        holeVertexIndices.Add(closestIdx);
                    }
                } else {
                    // No close vertex found, need to insert one
                    int newIdx = InsertVertexOnBodyMesh(bodySketch, holePoint);
                    if (newIdx >= 0) {
                        holeVertexIndices.Add(newIdx);
                    }
                }
            }

            Debug.Log($"[HoleCreator] Created hole with {holeVertexIndices.Count} vertices");

            // Duplicate hole vertices to create zero-area hole
            // This allows front and back faces to be stitched separately
            List<int> duplicatedIndices = new List<int>();
            foreach (int idx in holeVertexIndices) {
                int dupIdx = bodySketch.vertices.Count;
                bodySketch.vertices.Add(bodySketch.vertices[idx]); // Duplicate at same position
                duplicatedIndices.Add(dupIdx);
            }

            Debug.Log($"[HoleCreator] Duplicated {duplicatedIndices.Count} vertices for hole edges");

            return holeVertexIndices; // Return original indices (duplicated indices are at the end)
        }

        /// <summary>
        /// Insert a new vertex on the body mesh at the specified position
        /// Finds the closest triangle and adds the vertex
        /// </summary>
        private static int InsertVertexOnBodyMesh(SketchInfo bodySketch, Vector2 position) {
            // Find closest triangle
            int closestTriIdx = -1;
            float minDist = float.MaxValue;

            for (int t = 0; t < bodySketch.triangles.Count; t += 3) {
                Vector2 v0 = bodySketch.vertices[bodySketch.triangles[t]];
                Vector2 v1 = bodySketch.vertices[bodySketch.triangles[t + 1]];
                Vector2 v2 = bodySketch.vertices[bodySketch.triangles[t + 2]];

                Vector2 center = (v0 + v1 + v2) / 3f;
                float dist = Vector2.Distance(center, position);

                if (dist < minDist) {
                    minDist = dist;
                    closestTriIdx = t;
                }
            }

            if (closestTriIdx < 0) {
                Debug.LogWarning("[HoleCreator] Could not find triangle to insert vertex");
                return -1;
            }

            // Add new vertex
            int newIdx = bodySketch.vertices.Count;
            bodySketch.vertices.Add(position);

            // Split the closest triangle into 3 triangles
            int i0 = bodySketch.triangles[closestTriIdx];
            int i1 = bodySketch.triangles[closestTriIdx + 1];
            int i2 = bodySketch.triangles[closestTriIdx + 2];

            // Remove original triangle
            bodySketch.triangles.RemoveRange(closestTriIdx, 3);

            // Add 3 new triangles
            bodySketch.triangles.Add(i0); bodySketch.triangles.Add(i1); bodySketch.triangles.Add(newIdx);
            bodySketch.triangles.Add(i1); bodySketch.triangles.Add(i2); bodySketch.triangles.Add(newIdx);
            bodySketch.triangles.Add(i2); bodySketch.triangles.Add(i0); bodySketch.triangles.Add(newIdx);

            Debug.Log($"[HoleCreator] Inserted vertex at {position}, split triangle into 3");

            return newIdx;
        }

        /// <summary>
        /// Validate that hole creation was successful
        /// </summary>
        public static bool ValidateHole(SketchInfo bodySketch, List<int> holeIndices) {
            if (holeIndices == null || holeIndices.Count < 3) {
                Debug.LogWarning("[HoleCreator] Hole has too few vertices");
                return false;
            }

            // Check that all hole vertices exist
            foreach (int idx in holeIndices) {
                if (idx < 0 || idx >= bodySketch.vertices.Count) {
                    Debug.LogWarning($"[HoleCreator] Invalid hole vertex index: {idx}");
                    return false;
                }
            }

            Debug.Log($"[HoleCreator] Hole validation passed: {holeIndices.Count} vertices");
            return true;
        }
    }
}
