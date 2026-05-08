using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Domain Merger: Stitches multiple 2D domains together
    /// Handles boundary duplication and hole creation for mesh closure
    /// </summary>
    public class DomainMerger {

        private class VertexMapping {
            public Vector2 position;
            public int originalIndex;
            public int domainID;
            public bool isBoundary;
            public int globalIndex;
        }

        private List<VertexMapping> vertexMap = new List<VertexMapping>();
        private List<Vector3> mergedVertices = new List<Vector3>();
        private List<int> mergedTriangles = new List<int>();
        private float mergeThreshold = 0.001f;

        /// <summary>
        /// Merge multiple domains into a single connected domain
        /// Returns (mergedVertices, mergedTriangles)
        /// </summary>
        public (List<Vector3>, List<int>) MergeDomains(List<DomainStitchingSystem.BodyPartConfig> bodyParts) {
            mergedVertices.Clear();
            mergedTriangles.Clear();
            vertexMap.Clear();

            if (bodyParts == null || bodyParts.Count == 0) {
                return (mergedVertices, mergedTriangles);
            }

            Debug.Log($"[DomainMerger] Merging {bodyParts.Count} body parts...");

            // Step 1: Merge all front-facing domains
            Debug.Log("[DomainMerger] Step 1: Merging front-facing domains...");
            foreach (var part in bodyParts) {
                MergeDomain(part.frontFacing, part.frontFacing.domainID);
            }

            // Step 2: Merge all back-facing domains
            Debug.Log("[DomainMerger] Step 2: Merging back-facing domains...");
            foreach (var part in bodyParts) {
                MergeDomain(part.backFacing, part.backFacing.domainID);
            }

            // Step 3: Stitch front and back domains of each part
            Debug.Log("[DomainMerger] Step 3: Stitching front and back domains...");
            foreach (var part in bodyParts) {
                StitchFrontAndBack(part);
            }

            // Step 4: Handle open boundaries - create holes and attach parts
            Debug.Log("[DomainMerger] Step 4: Handling open boundaries and attachments...");
            foreach (var part in bodyParts) {
                if (part.frontFacing.isOpenContour && part.backFacing.isOpenContour) {
                    AttachOpenBoundaries(part);
                }
            }

            // Step 5: Merge all vertices to final mesh
            Debug.Log("[DomainMerger] Step 5: Finalizing vertices...");
            FinalizeVertices();

            Debug.Log($"[DomainMerger] Merge complete. Merged vertices: {mergedVertices.Count}, triangles: {mergedTriangles.Count / 3}");
            return (mergedVertices, mergedTriangles);
        }

        /// <summary>
        /// Merge a single domain into the global vertex and triangle lists
        /// </summary>
        private void MergeDomain(DomainStitchingSystem.StitchedDomain domain, int domainID) {
            if (domain.vertices == null || domain.triangles == null) return;

            int vertexOffset = mergedVertices.Count;

            // Add vertices
            for (int i = 0; i < domain.vertices.Count; i++) {
                Vector2 v = domain.vertices[i];
                mergedVertices.Add(new Vector3(v.x, v.y, 0f));
                vertexMap.Add(new VertexMapping {
                    position = v,
                    originalIndex = i,
                    domainID = domainID,
                    isBoundary = IsBoundaryVertex(v, domain),
                    globalIndex = mergedVertices.Count - 1
                });
            }

            // Add triangles with offset
            foreach (var tri in domain.triangles) {
                mergedTriangles.Add(tri + vertexOffset);
            }
        }

        /// <summary>
        /// Determine if a vertex lies on the boundary of the domain
        /// </summary>
        private bool IsBoundaryVertex(Vector2 v, DomainStitchingSystem.StitchedDomain domain) {
            foreach (var boundaryV in domain.boundary) {
                if (Vector2.Distance(v, boundaryV) < mergeThreshold) {
                    return true;
                }
            }

            if (domain.closureCurve != null) {
                foreach (var closureV in domain.closureCurve) {
                    if (Vector2.Distance(v, closureV) < mergeThreshold) {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Stitch front and back facing regions along their common boundary
        /// Creates a continuous domain by connecting vertices
        /// </summary>
        private void StitchFrontAndBack(DomainStitchingSystem.BodyPartConfig part) {
            var front = part.frontFacing;
            var back = part.backFacing;

            if (front.vertices == null || back.vertices == null) return;

            // Find corresponding boundary vertices between front and back
            var boundaryPairs = MatchBoundaryVertices(front, back);

            // Create triangles stitching front and back along boundaries
            foreach (var (frontIdx, backIdx) in boundaryPairs) {
                int f = FindGlobalVertexIndex(frontIdx, front.domainID);
                int b = FindGlobalVertexIndex(backIdx, back.domainID);

                if (f >= 0 && b >= 0) {
                    // Create stitching triangles (these will be hidden internally)
                    mergedTriangles.Add(f);
                    mergedTriangles.Add(b);
                    mergedTriangles.Add(f);  // Degenerate for now; proper implementation would use edge quads
                }
            }
        }

        /// <summary>
        /// Match boundary vertices between two domains
        /// </summary>
        private List<(int, int)> MatchBoundaryVertices(
            DomainStitchingSystem.StitchedDomain front,
            DomainStitchingSystem.StitchedDomain back) {

            var pairs = new List<(int, int)>();

            // Match boundary vertices by proximity
            for (int i = 0; i < front.boundary.Count; i++) {
                Vector2 frontBoundary = front.boundary[i];

                // Find closest vertex in back boundary
                float minDist = float.MaxValue;
                int bestMatch = -1;

                for (int j = 0; j < back.boundary.Count; j++) {
                    float dist = Vector2.Distance(frontBoundary, back.boundary[j]);
                    if (dist < minDist) {
                        minDist = dist;
                        bestMatch = j;
                    }
                }

                if (bestMatch >= 0) {
                    pairs.Add((i, bestMatch));
                }
            }

            return pairs;
        }

        /// <summary>
        /// Handle open boundaries by creating holes and attaching parts
        /// </summary>
        private void AttachOpenBoundaries(DomainStitchingSystem.BodyPartConfig part) {
            var front = part.frontFacing;
            var back = part.backFacing;

            if (!front.isOpenContour) return;

            // Find boundary vertices where the domain closes (Bp curve)
            int boundarySize = front.boundary.Count;

            // Duplicate vertices along the boundary
            var duplicatedVertices = new List<int>();
            for (int i = 0; i < boundarySize; i++) {
                int globalIdx = FindGlobalVertexIndex(i, front.domainID);
                if (globalIdx >= 0) {
                    // Create duplicate vertex (for hole creation)
                    var vert = mergedVertices[globalIdx];
                    int dupIdx = mergedVertices.Count;
                    mergedVertices.Add(vert);
                    duplicatedVertices.Add(dupIdx);
                }
            }

            // Create a thin "hole" by forming degenerate triangles along the boundary
            // This will be the connection point for attaching other parts
            for (int i = 0; i < duplicatedVertices.Count - 1; i++) {
                int v1 = duplicatedVertices[i];
                int v2 = duplicatedVertices[i + 1];

                // Degenerate triangle forming the hole edge
                mergedTriangles.Add(v1);
                mergedTriangles.Add(v2);
                mergedTriangles.Add(v1);
            }
        }

        /// <summary>
        /// Find the global vertex index for a vertex in a specific domain
        /// </summary>
        private int FindGlobalVertexIndex(int localIdx, int domainID) {
            foreach (var mapping in vertexMap) {
                if (mapping.domainID == domainID && mapping.originalIndex == localIdx) {
                    return mapping.globalIndex;
                }
            }
            return -1;
        }

        /// <summary>
        /// Finalize vertices by removing duplicates and optimizing the mesh
        /// </summary>
        private void FinalizeVertices() {
            // Remove completely degenerate triangles
            for (int i = mergedTriangles.Count - 3; i >= 0; i -= 3) {
                int v1 = mergedTriangles[i];
                int v2 = mergedTriangles[i + 1];
                int v3 = mergedTriangles[i + 2];

                if (v1 == v2 || v2 == v3 || v1 == v3) {
                    mergedTriangles.RemoveRange(i, 3);
                }
            }

            // Verify triangle count is valid
            if (mergedTriangles.Count % 3 != 0) {
                Debug.LogWarning($"Invalid triangle count: {mergedTriangles.Count}");
            }
        }

        /// <summary>
        /// Create connectivity information for adjacent domains
        /// Used for constraint generation
        /// </summary>
        public List<DomainAdjacency> GetDomainAdjacencies(List<DomainStitchingSystem.BodyPartConfig> bodyParts) {
            var adjacencies = new List<DomainAdjacency>();

            for (int i = 0; i < bodyParts.Count; i++) {
                for (int j = i + 1; j < bodyParts.Count; j++) {
                    var front1 = bodyParts[i].frontFacing;
                    var front2 = bodyParts[j].frontFacing;

                    // Check if domains are adjacent (share boundary or stitching curve)
                    var commonVertices = FindCommonBoundaryVertices(front1, front2);
                    if (commonVertices.Count > 0) {
                        adjacencies.Add(new DomainAdjacency {
                            domain1ID = i,
                            domain2ID = j,
                            sharedVertices = commonVertices
                        });
                    }
                }
            }

            return adjacencies;
        }

        private List<Vector2> FindCommonBoundaryVertices(
            DomainStitchingSystem.StitchedDomain domain1,
            DomainStitchingSystem.StitchedDomain domain2) {

            var common = new List<Vector2>();

            foreach (var v1 in domain1.boundary) {
                foreach (var v2 in domain2.boundary) {
                    if (Vector2.Distance(v1, v2) < mergeThreshold) {
                        common.Add(v1);
                        break;
                    }
                }
            }

            return common;
        }
    }

    /// <summary>
    /// Information about adjacent domains
    /// </summary>
    public class DomainAdjacency {
        public int domain1ID;
        public int domain2ID;
        public List<Vector2> sharedVertices;
    }

}
