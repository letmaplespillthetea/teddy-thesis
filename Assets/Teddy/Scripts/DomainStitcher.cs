using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Stitches multiple domains together into a unified mesh
    /// Implements symmetric stitching and appendage-to-body connections
    /// </summary>
    public class DomainStitcher {

        /// <summary>
        /// Stitch all sketches into a unified 2D mesh
        /// Returns merged vertices and triangles ready for inflation
        /// </summary>
        public static (List<Vector2> vertices, List<int> triangles) StitchDomains(SketchCollection collection) {
            Debug.Log("[DomainStitcher] Starting domain stitching...");

            List<Vector2> mergedVertices = new List<Vector2>();
            List<int> mergedTriangles = new List<int>();

            // Track vertex offset for each sketch
            Dictionary<int, int> sketchVertexOffset = new Dictionary<int, int>();

            // Step 1: Merge all sketch vertices and triangles
            foreach (var sketch in collection.sketches) {
                sketchVertexOffset[sketch.sketchID] = mergedVertices.Count;

                // Add vertices
                mergedVertices.AddRange(sketch.vertices);

                // Add triangles (with offset)
                int offset = sketchVertexOffset[sketch.sketchID];
                foreach (var tri in sketch.triangles) {
                    mergedTriangles.Add(tri + offset);
                }

                Debug.Log($"[DomainStitcher] Added sketch {sketch.sketchID}: {sketch.vertices.Count} vertices, {sketch.triangles.Count / 3} triangles");
            }

            // Step 2: Stitch boundaries between sketches
            var bodies = collection.GetBodies();
            var appendages = collection.GetAppendages();

            foreach (var appendage in appendages) {
                if (appendage.attachedToSketchID >= 0) {
                    var body = bodies.Find(b => b.sketchID == appendage.attachedToSketchID);
                    if (body != null) {
                        StitchAppendageToBody(appendage, body, sketchVertexOffset, mergedVertices, mergedTriangles);
                    }
                }
            }

            Debug.Log($"[DomainStitcher] Stitching complete: {mergedVertices.Count} vertices, {mergedTriangles.Count / 3} triangles");

            return (mergedVertices, mergedTriangles);
        }

        /// <summary>
        /// Stitch an appendage to a body along the hole boundary
        /// Creates quads connecting appendage boundary to hole edge
        /// </summary>
        private static void StitchAppendageToBody(
            SketchInfo appendage,
            SketchInfo body,
            Dictionary<int, int> vertexOffset,
            List<Vector2> mergedVertices,
            List<int> mergedTriangles) {

            Debug.Log($"[DomainStitcher] Stitching appendage {appendage.sketchID} to body {body.sketchID}");

            int appendageOffset = vertexOffset[appendage.sketchID];
            int bodyOffset = vertexOffset[body.sketchID];

            // Get appendage boundary (the closure curve part)
            List<Vector2> appendageBoundary = new List<Vector2>();
            if (appendage.attachmentCurve != null && appendage.attachmentCurve.Count > 0) {
                appendageBoundary = new List<Vector2>(appendage.attachmentCurve);
            } else {
                Debug.LogWarning($"[DomainStitcher] Appendage {appendage.sketchID} has no attachment curve!");
                return;
            }

            // Find corresponding vertices on body mesh (the hole)
            List<int> bodyHoleIndices = new List<int>();
            foreach (var boundaryPoint in appendageBoundary) {
                int closestIdx = FindClosestVertex(body.vertices, boundaryPoint);
                if (closestIdx >= 0) {
                    bodyHoleIndices.Add(closestIdx + bodyOffset);
                }
            }

            // Find corresponding vertices on appendage mesh
            List<int> appendageEdgeIndices = new List<int>();
            foreach (var boundaryPoint in appendageBoundary) {
                int closestIdx = FindClosestVertex(appendage.vertices, boundaryPoint);
                if (closestIdx >= 0) {
                    appendageEdgeIndices.Add(closestIdx + appendageOffset);
                }
            }

            // Stitch with quads
            int stitchCount = Mathf.Min(bodyHoleIndices.Count, appendageEdgeIndices.Count);
            int quadsCreated = 0;

            for (int i = 0; i < stitchCount - 1; i++) {
                int b1 = bodyHoleIndices[i];
                int b2 = bodyHoleIndices[i + 1];
                int a1 = appendageEdgeIndices[i];
                int a2 = appendageEdgeIndices[i + 1];

                // Create quad (b1, a1, a2) and (b1, a2, b2)
                mergedTriangles.Add(b1);
                mergedTriangles.Add(a1);
                mergedTriangles.Add(a2);

                mergedTriangles.Add(b1);
                mergedTriangles.Add(a2);
                mergedTriangles.Add(b2);

                quadsCreated++;
            }

            Debug.Log($"[DomainStitcher] Created {quadsCreated} quads connecting appendage {appendage.sketchID} to body {body.sketchID}");
        }

        /// <summary>
        /// Find closest vertex in a list to a target position
        /// </summary>
        private static int FindClosestVertex(List<Vector2> vertices, Vector2 target) {
            int closestIdx = -1;
            float minDist = float.MaxValue;

            for (int i = 0; i < vertices.Count; i++) {
                float dist = Vector2.Distance(vertices[i], target);
                if (dist < minDist) {
                    minDist = dist;
                    closestIdx = i;
                }
            }

            return closestIdx;
        }

        /// <summary>
        /// Create a symmetric copy of vertices (for front/back faces)
        /// </summary>
        public static List<Vector2> CreateSymmetricCopy(List<Vector2> vertices) {
            // For 2D, symmetric copy is just a duplicate
            // In 3D inflation, these will be offset in Z direction
            return new List<Vector2>(vertices);
        }

        /// <summary>
        /// Validate stitched mesh topology
        /// </summary>
        public static bool ValidateStitchedMesh(List<Vector2> vertices, List<int> triangles) {
            if (vertices == null || vertices.Count < 3) {
                Debug.LogWarning("[DomainStitcher] Too few vertices");
                return false;
            }

            if (triangles == null || triangles.Count < 3 || triangles.Count % 3 != 0) {
                Debug.LogWarning("[DomainStitcher] Invalid triangle count");
                return false;
            }

            // Check all triangle indices are valid
            foreach (var idx in triangles) {
                if (idx < 0 || idx >= vertices.Count) {
                    Debug.LogWarning($"[DomainStitcher] Invalid triangle index: {idx}");
                    return false;
                }
            }

            Debug.Log($"[DomainStitcher] Mesh validation passed: {vertices.Count} vertices, {triangles.Count / 3} triangles");
            return true;
        }
    }
}
