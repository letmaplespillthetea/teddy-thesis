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
        public List<bool> triangleIsAppendage = new List<bool>();
        private float mergeThreshold = 0.01f;  // Increased from 0.001f to find more matching vertices

        /// <summary>
        /// Merge multiple domains into a single connected domain
        /// Returns (mergedVertices, mergedTriangles)
        /// </summary>
        public (List<Vector3>, List<int>) MergeDomains(List<DomainStitchingSystem.BodyPartConfig> bodyParts) {
            mergedVertices.Clear();
            mergedTriangles.Clear();
            triangleIsAppendage.Clear();
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
            // Back-facing domains (odd domainID) need reversed winding order
            bool isBackFacing = (domainID % 2) == 1;
            
            if (isBackFacing) {
                // Reverse winding order for back-facing triangles
                for (int t = 0; t < domain.triangles.Count; t += 3) {
                    mergedTriangles.Add(domain.triangles[t] + vertexOffset);
                    mergedTriangles.Add(domain.triangles[t + 2] + vertexOffset);  // Swap t+1 and t+2
                    mergedTriangles.Add(domain.triangles[t + 1] + vertexOffset);
                    triangleIsAppendage.Add(domain.isAppendage);
                }
            } else {
                // Keep normal winding order for front-facing triangles
                for (int t = 0; t < domain.triangles.Count; t += 3) {
                    mergedTriangles.Add(domain.triangles[t] + vertexOffset);
                    mergedTriangles.Add(domain.triangles[t + 1] + vertexOffset);
                    mergedTriangles.Add(domain.triangles[t + 2] + vertexOffset);
                    triangleIsAppendage.Add(domain.isAppendage);
                }
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

            // Build complete contour including boundary and closure curve
            var frontContour = new List<Vector2>(front.boundary);
            if (front.closureCurve != null && front.closureCurve.Count > 0) {
                frontContour.AddRange(front.closureCurve);
            }
            
            var backContour = new List<Vector2>(back.boundary);
            if (back.closureCurve != null && back.closureCurve.Count > 0) {
                backContour.AddRange(back.closureCurve);
            }

            // Stitch along the complete contour
            int count = Mathf.Min(frontContour.Count, backContour.Count);
            int stitchedQuads = 0;
            int skippedQuads = 0;
            
            for (int i = 0; i < count; i++) {
                int next = (i + 1) % count;

                // Find global indices for the contour vertices
                int f1 = FindVertexByPosition(front, frontContour[i]);
                int f2 = FindVertexByPosition(front, frontContour[next]);
                int b1 = FindVertexByPosition(back, backContour[i]);
                int b2 = FindVertexByPosition(back, backContour[next]);

                if (f1 >= 0 && f2 >= 0 && b1 >= 0 && b2 >= 0) {
                    // Create side quad (f1, b1, b2) and (f1, b2, f2)
                    mergedTriangles.Add(f1);
                    mergedTriangles.Add(b1);
                    mergedTriangles.Add(b2);
                    triangleIsAppendage.Add(part.frontFacing.isAppendage);

                    mergedTriangles.Add(f1);
                    mergedTriangles.Add(b2);
                    mergedTriangles.Add(f2);
                    triangleIsAppendage.Add(part.frontFacing.isAppendage);
                    
                    stitchedQuads++;
                } else {
                    skippedQuads++;
                    if (skippedQuads <= 5) {  // Only log first 5 to avoid spam
                        Debug.LogWarning($"[DomainMerger] Skipped quad {i}: f1={f1}, f2={f2}, b1={b1}, b2={b2} " +
                                       $"(front pos: {frontContour[i]}, back pos: {backContour[i]})");
                    }
                }
            }
            
            Debug.Log($"[DomainMerger] Stitched {stitchedQuads} quads, skipped {skippedQuads} quads for part {part.partID} (contour size: {count})");
        }

        private int FindVertexByPosition(DomainStitchingSystem.StitchedDomain domain, Vector2 pos) {
            // First try exact match
            float bestDist = float.MaxValue;
            int bestIdx = -1;
            
            for (int i = 0; i < domain.vertices.Count; i++) {
                float dist = Vector2.Distance(domain.vertices[i], pos);
                if (dist < bestDist) {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            
            if (bestDist < mergeThreshold) {
                return FindGlobalVertexIndex(bestIdx, domain.domainID);
            }
            
            // If no match found, create a new vertex at this position
            Debug.LogWarning($"[DomainMerger] No vertex found for position {pos} in domain {domain.domainID}, creating new vertex. Best distance was {bestDist}");
            int newIdx = mergedVertices.Count;
            mergedVertices.Add(new Vector3(pos.x, pos.y, 0f));
            return newIdx;
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

            // Attach back-facing boundary to front-facing hole
            for (int i = 0; i < duplicatedVertices.Count - 1; i++) {
                int f1 = FindGlobalVertexIndex(i, front.domainID);
                int f2 = FindGlobalVertexIndex(i + 1, front.domainID);
                int b1 = duplicatedVertices[i];
                int b2 = duplicatedVertices[i + 1];

                if (f1 >= 0 && f2 >= 0) {
                    mergedTriangles.Add(f1);
                    mergedTriangles.Add(b1);
                    mergedTriangles.Add(b2);
                    triangleIsAppendage.Add(part.frontFacing.isAppendage);

                    mergedTriangles.Add(f1);
                    mergedTriangles.Add(b2);
                    mergedTriangles.Add(f2);
                    triangleIsAppendage.Add(part.frontFacing.isAppendage);
                }
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
                    triangleIsAppendage.RemoveAt(i / 3);
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
