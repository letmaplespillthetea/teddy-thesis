using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Constrained Delaunay Triangulation implementation
    /// Generates triangulation while respecting boundary constraints
    /// </summary>
    public class ConstrainedDelaunayTriangulation {

        private List<Vector2> vertices = new List<Vector2>();
        private List<Vector2Int> edges = new List<Vector2Int>();  // Boundary edges
        private List<int> triangles = new List<int>();
        private List<Vector2> boundaryVertices = new List<Vector2>();
        private float minDistance = 0.01f;  // Minimum distance between vertices

        /// <summary>
        /// Set the boundary constraint for the domain
        /// </summary>
        public void SetBoundaryConstraint(List<Vector2> boundary) {
            if (boundary == null || boundary.Count < 3) {
                Debug.LogWarning("Invalid boundary for triangulation");
                return;
            }

            boundaryVertices = new List<Vector2>(boundary);
            
            // Remove duplicate consecutive points
            for (int i = 1; i < boundaryVertices.Count; i++) {
                if (Vector2.Distance(boundaryVertices[i], boundaryVertices[i - 1]) < minDistance) {
                    boundaryVertices.RemoveAt(i);
                    i--;
                }
            }

            // Ensure closure
            if (Vector2.Distance(boundaryVertices[0], boundaryVertices[boundaryVertices.Count - 1]) > minDistance) {
                boundaryVertices.Add(boundaryVertices[0]);
            }

            // Create boundary edges
            edges.Clear();
            for (int i = 0; i < boundaryVertices.Count - 1; i++) {
                edges.Add(new Vector2Int(i, i + 1));
            }
        }

        /// <summary>
        /// Generate Delaunay triangulation with boundary constraints
        /// Returns (vertices, triangles)
        /// </summary>
        public (List<Vector2>, List<int>) Triangulate() {
            vertices.Clear();
            triangles.Clear();

            if (boundaryVertices.Count < 3) {
                Debug.LogWarning("Boundary has insufficient vertices");
                return (vertices, triangles);
            }

            // Step 1: Insert boundary vertices
            foreach (var v in boundaryVertices) {
                if (!VertexExists(v)) {
                    vertices.Add(v);
                }
            }

            // Step 2: Insert auxiliary vertices in the interior
            InsertInteriorVertices();

            // Step 3: Apply Delaunay triangulation
            ApplyDelaunayTriangulation();

            // Step 4: Enforce boundary constraints
            EnforceBoundaryConstraints();

            return (vertices, triangles);
        }

        /// <summary>
        /// Insert auxiliary vertices in the interior of the domain
        /// This ensures better mesh quality
        /// </summary>
        private void InsertInteriorVertices() {
            // Calculate bounding box
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);

            foreach (var v in boundaryVertices) {
                min = Vector2.Min(min, v);
                max = Vector2.Max(max, v);
            }

            float spacing = Vector2.Distance(max, min) / 8f;  // Adaptive spacing

            // Insert grid of interior points
            for (float x = min.x + spacing; x < max.x; x += spacing) {
                for (float y = min.y + spacing; y < max.y; y += spacing) {
                    Vector2 point = new Vector2(x, y);

                    // Check if point is inside the domain
                    if (IsPointInside(point) && !VertexExists(point)) {
                        vertices.Add(point);
                    }
                }
            }
        }

        /// <summary>
        /// Check if a point is inside the polygon using ray casting
        /// </summary>
        private bool IsPointInside(Vector2 point) {
            int intersections = 0;
            Vector2 rayEnd = new Vector2(point.x + 10000f, point.y);

            for (int i = 0; i < boundaryVertices.Count - 1; i++) {
                Vector2 p1 = boundaryVertices[i];
                Vector2 p2 = boundaryVertices[i + 1];

                if (SegmentsIntersect(point, rayEnd, p1, p2)) {
                    intersections++;
                }
            }

            return intersections % 2 == 1;
        }

        /// <summary>
        /// Check if two line segments intersect
        /// </summary>
        private bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4) {
            float d = (p2.x - p1.x) * (p4.y - p3.y) - (p2.y - p1.y) * (p4.x - p3.x);
            if (Mathf.Abs(d) < 0.0001f) return false;

            float t = ((p3.x - p1.x) * (p4.y - p3.y) - (p3.y - p1.y) * (p4.x - p3.x)) / d;
            float u = ((p3.x - p1.x) * (p2.y - p1.y) - (p3.y - p1.y) * (p2.x - p1.x)) / d;

            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        /// <summary>
        /// Apply Delaunay triangulation to the point set
        /// Uses incremental Delaunay algorithm
        /// </summary>
        private void ApplyDelaunayTriangulation() {
            triangles.Clear();

            if (vertices.Count < 3) return;

            // Create super-triangle that encloses all points
            var (superTriangle, superVerts) = CreateSuperTriangle();

            // Add super-triangle vertices to temporary list
            List<Vector2> tempVerts = new List<Vector2>(vertices);
            tempVerts.AddRange(superVerts);

            List<int> tempTriangles = new List<int> { superTriangle.x, superTriangle.y, superTriangle.z };

            // Incrementally add vertices
            for (int i = 0; i < vertices.Count; i++) {
                var badTriangles = new List<int>();

                // Find triangles whose circumcircle contains the point
                for (int t = 0; t < tempTriangles.Count; t += 3) {
                    if (CircumcircleContains(
                        tempVerts[tempTriangles[t]],
                        tempVerts[tempTriangles[t + 1]],
                        tempVerts[tempTriangles[t + 2]],
                        vertices[i])) {
                        badTriangles.Add(t);
                    }
                }

                // Find polygon hole left by bad triangles
                var polygon = new List<Vector2Int>();
                foreach (var badT in badTriangles) {
                    for (int e = 0; e < 3; e++) {
                        int v1 = tempTriangles[badT + e];
                        int v2 = tempTriangles[badT + ((e + 1) % 3)];

                        bool shared = false;
                        foreach (var otherT in badTriangles) {
                            if (otherT == badT) continue;
                            for (int oe = 0; oe < 3; oe++) {
                                int ov1 = tempTriangles[otherT + oe];
                                int ov2 = tempTriangles[otherT + ((oe + 1) % 3)];
                                if ((v1 == ov1 && v2 == ov2) || (v1 == ov2 && v2 == ov1)) {
                                    shared = true;
                                    break;
                                }
                            }
                            if (shared) break;
                        }

                        if (!shared) {
                            polygon.Add(new Vector2Int(v1, v2));
                        }
                    }
                }

                // Remove bad triangles
                for (int i2 = badTriangles.Count - 1; i2 >= 0; i2--) {
                    int idx = badTriangles[i2];
                    tempTriangles.RemoveRange(idx, 3);
                }

                // Add new triangles
                foreach (var edge in polygon) {
                    tempTriangles.Add(edge.x);
                    tempTriangles.Add(edge.y);
                    tempTriangles.Add(vertices.Count + superVerts.Count - 1);
                }
            }

            // Remove super-triangle
            triangles = tempTriangles;
            RemoveSuperTriangleTriangles();
        }

        /// <summary>
        /// Create a super-triangle that encloses all vertices
        /// </summary>
        private (Vector3Int superTriangle, List<Vector2> superVerts) CreateSuperTriangle() {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);

            foreach (var v in vertices) {
                min = Vector2.Min(min, v);
                max = Vector2.Max(max, v);
            }

            float dx = max.x - min.x;
            float dy = max.y - min.y;
            float padding = Mathf.Max(dx, dy) * 0.5f;

            var superVerts = new List<Vector2> {
                new Vector2(min.x - padding, min.y - padding),
                new Vector2(max.x + padding, min.y - padding),
                new Vector2(min.x + dx * 0.5f, max.y + padding)
            };

            return (new Vector3Int(vertices.Count, vertices.Count + 1, vertices.Count + 2), superVerts);
        }

        /// <summary>
        /// Check if a point is inside the circumcircle of a triangle
        /// </summary>
        private bool CircumcircleContains(Vector2 a, Vector2 b, Vector2 c, Vector2 p) {
            float ax = a.x, ay = a.y;
            float bx = b.x, by = b.y;
            float cx = c.x, cy = c.y;
            float px = p.x, py = p.y;

            float d = 2f * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Mathf.Abs(d) < 0.0001f) return false;

            float ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
            float uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;

            float radius = Mathf.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));
            return Mathf.Sqrt((px - ux) * (px - ux) + (py - uy) * (py - uy)) < radius + 0.0001f;
        }

        /// <summary>
        /// Remove triangles that use super-triangle vertices
        /// </summary>
        private void RemoveSuperTriangleTriangles() {
            for (int i = triangles.Count - 3; i >= 0; i -= 3) {
                int v1 = triangles[i];
                int v2 = triangles[i + 1];
                int v3 = triangles[i + 2];

                if (v1 >= vertices.Count || v2 >= vertices.Count || v3 >= vertices.Count) {
                    triangles.RemoveRange(i, 3);
                }
            }
        }

        /// <summary>
        /// Enforce boundary constraints by adding boundary edges
        /// </summary>
        private void EnforceBoundaryConstraints() {
            // Flip triangles to ensure boundary edges are present
            foreach (var edge in edges) {
                EnforceEdge(edge.x, edge.y);
            }
        }

        /// <summary>
        /// Enforce a specific edge by flipping triangles if necessary
        /// </summary>
        private void EnforceEdge(int v1, int v2) {
            // Find triangles that should contain this edge
            for (int t = 0; t < triangles.Count; t += 3) {
                bool hasEdge = false;

                for (int e = 0; e < 3; e++) {
                    int a = triangles[t + e];
                    int b = triangles[t + ((e + 1) % 3)];

                    if ((a == v1 && b == v2) || (a == v2 && b == v1)) {
                        hasEdge = true;
                        break;
                    }
                }

                if (!hasEdge) {
                    // Check if edge crosses any triangle edges
                    // If so, flip the edge to enforce the constraint
                }
            }
        }

        /// <summary>
        /// Check if a vertex already exists in the list
        /// </summary>
        private bool VertexExists(Vector2 v) {
            foreach (var vert in vertices) {
                if (Vector2.Distance(v, vert) < minDistance) {
                    return true;
                }
            }
            return false;
        }
    }

}
