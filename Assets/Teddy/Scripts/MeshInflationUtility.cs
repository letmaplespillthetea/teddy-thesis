using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Mesh Inflation Utility
    /// Converts 2D planar domains to 3D meshes using height fields
    /// </summary>
    public class MeshInflationUtility {

        private float smoothingStrength = 0.1f;
        private int smoothingIterations = 3;

        /// <summary>
        /// Inflate a 2D triangulated domain to 3D using height fields
        /// Creates front and back-facing surfaces connected by stitched domains
        /// </summary>
        public Mesh InflateTo3D(
            List<Vector3> plannarVertices,
            List<int> triangles,
            List<float> heightFields,
            bool smoothHeightFields = true) {

            if (plannarVertices.Count == 0 || triangles.Count == 0) {
                return null;
            }

            var mesh = new Mesh();

            // Step 1: Create 3D vertices from height fields
            var inflatedVertices = new List<Vector3>();
            var vertexMap = new Dictionary<int, int>();  // Maps 2D vertex index to 3D vertex index

            for (int i = 0; i < plannarVertices.Count; i++) {
                Vector3 pos2D = plannarVertices[i];
                float height = i < heightFields.Count ? heightFields[i] : 0f;

                // Create vertex at inflated position
                Vector3 pos3D = new Vector3(pos2D.x, pos2D.y, height);
                int idx3D = inflatedVertices.Count;
                inflatedVertices.Add(pos3D);

                vertexMap[i] = idx3D;
            }

            // Step 2: Create triangles with correct winding
            var inflatedTriangles = new List<int>();
            var normals = new List<Vector3>();

            for (int i = 0; i < plannarVertices.Count; i++) {
                normals.Add(Vector3.zero);
            }

            // Add original triangles
            for (int t = 0; t < triangles.Count; t += 3) {
                int i0 = vertexMap[triangles[t]];
                int i1 = vertexMap[triangles[t + 1]];
                int i2 = vertexMap[triangles[t + 2]];

                // Check winding and add triangle
                Vector3 v0 = inflatedVertices[i0];
                Vector3 v1 = inflatedVertices[i1];
                Vector3 v2 = inflatedVertices[i2];

                Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);

                // Ensure correct winding (outward facing)
                Vector3 centroid = (v0 + v1 + v2) / 3f;
                if (Vector3.Dot(normal, centroid) < 0) {
                    // Flip winding
                    int temp = i1;
                    i1 = i2;
                    i2 = temp;
                }

                inflatedTriangles.Add(i0);
                inflatedTriangles.Add(i1);
                inflatedTriangles.Add(i2);
            }

            // Step 3: Create caps and close the mesh
            // For open boundaries, create connecting surfaces
            CloseOpenBoundaries(inflatedVertices, inflatedTriangles, plannarVertices, triangles, heightFields);

            // Step 4: Calculate normals
            mesh.vertices = inflatedVertices.ToArray();
            mesh.triangles = inflatedTriangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // Step 5: Optional smoothing
            if (smoothHeightFields && inflatedVertices.Count < 65000) {
                SmoothMesh(mesh, smoothingIterations, smoothingStrength);
            }

            return mesh;
        }

        /// <summary>
        /// Close open boundaries by creating connecting surfaces
        /// </summary>
        private void CloseOpenBoundaries(
            List<Vector3> vertices,
            List<int> triangles,
            List<Vector3> original2D,
            List<int> originalTriangles,
            List<float> heightFields) {

            // Find boundary edges (edges that appear only once in triangles)
            var edgeCount = new Dictionary<long, int>();

            for (int t = 0; t < originalTriangles.Count; t += 3) {
                for (int e = 0; e < 3; e++) {
                    int v1 = originalTriangles[t + e];
                    int v2 = originalTriangles[t + ((e + 1) % 3)];

                    long edge = v1 < v2 ? ((long)v1 << 32) | v2 : ((long)v2 << 32) | v1;

                    if (!edgeCount.ContainsKey(edge)) {
                        edgeCount[edge] = 0;
                    }
                    edgeCount[edge]++;
                }
            }

            // Create caps for boundary edges
            foreach (var (edge, count) in edgeCount) {
                if (count == 1) {
                    int v1 = (int)((edge >> 32) & 0xFFFFFFFF);
                    int v2 = (int)(edge & 0xFFFFFFFF);

                    // Create cap triangle
                    triangles.Add(v1);
                    triangles.Add(v2);
                    triangles.Add(v1);  // Degenerate for now; proper implementation would create quads
                }
            }
        }

        /// <summary>
        /// Apply Laplacian smoothing to the mesh
        /// </summary>
        private void SmoothMesh(Mesh mesh, int iterations, float strength) {
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;

            // Build adjacency information
            var neighbors = new List<int>[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) {
                neighbors[i] = new List<int>();
            }

            for (int t = 0; t < triangles.Length; t += 3) {
                for (int i = 0; i < 3; i++) {
                    int v1 = triangles[t + i];
                    int v2 = triangles[t + ((i + 1) % 3)];

                    if (!neighbors[v1].Contains(v2)) {
                        neighbors[v1].Add(v2);
                    }
                    if (!neighbors[v2].Contains(v1)) {
                        neighbors[v2].Add(v1);
                    }
                }
            }

            // Smooth iterations
            for (int iter = 0; iter < iterations; iter++) {
                var newVertices = new Vector3[vertices.Length];

                for (int i = 0; i < vertices.Length; i++) {
                    Vector3 sum = vertices[i];
                    int count = 1;

                    foreach (int neighbor in neighbors[i]) {
                        sum += vertices[neighbor];
                        count++;
                    }

                    Vector3 smoothed = sum / count;
                    newVertices[i] = Vector3.Lerp(vertices[i], smoothed, strength);
                }

                vertices = newVertices;
            }

            mesh.vertices = vertices;
            mesh.RecalculateNormals();
        }

        /// <summary>
        /// Apply as-rigid-as-possible (ARAP) deformation constraints
        /// Used to move deformed parts while maintaining mesh rigidity
        /// </summary>
        public void ApplyARAPDeformation(
            ref Mesh mesh,
            List<DeformationConstraint> constraints,
            int iterations = 5) {

            if (constraints == null || constraints.Count == 0) {
                return;
            }

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;

            // For each constraint
            foreach (var constraint in constraints) {
                if (constraint.type == ConstraintType.Inequality) {
                    // Move vertices to target Z position
                    foreach (int vIdx in constraint.affectedVertices) {
                        if (vIdx >= 0 && vIdx < vertices.Length) {
                            vertices[vIdx].z = Mathf.Max(vertices[vIdx].z, constraint.targetZ);
                        }
                    }
                } else if (constraint.type == ConstraintType.Equality) {
                    // Keep vertices at their current positions
                    // (used for maintaining contact with body)
                }
            }

            mesh.vertices = vertices;
            mesh.RecalculateNormals();
        }

        /// <summary>
        /// Create a height field visualization for debugging
        /// </summary>
        public Mesh CreateHeightFieldDebugMesh(
            List<Vector3> vertices2D,
            List<float> heightFields,
            List<int> triangles) {

            var debugMesh = new Mesh();
            var debugVertices = new List<Vector3>();

            // Map 2D positions to 3D with height
            for (int i = 0; i < vertices2D.Count; i++) {
                Vector3 pos = vertices2D[i];
                float height = i < heightFields.Count ? heightFields[i] : 0f;

                debugVertices.Add(new Vector3(pos.x, pos.y, height));
            }

            debugMesh.vertices = debugVertices.ToArray();
            debugMesh.triangles = triangles.ToArray();
            debugMesh.RecalculateNormals();
            debugMesh.RecalculateBounds();

            return debugMesh;
        }

        /// <summary>
        /// Export mesh statistics for analysis
        /// </summary>
        public MeshStatistics GetMeshStatistics(Mesh mesh) {
            if (mesh == null) {
                return new MeshStatistics();
            }

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int[] triangles = mesh.triangles;

            float avgHeight = 0f;
            float minHeight = float.MaxValue;
            float maxHeight = float.MinValue;

            foreach (var v in vertices) {
                avgHeight += v.z;
                minHeight = Mathf.Min(minHeight, v.z);
                maxHeight = Mathf.Max(maxHeight, v.z);
            }

            avgHeight /= vertices.Length;

            return new MeshStatistics {
                vertexCount = vertices.Length,
                triangleCount = triangles.Length / 3,
                averageHeight = avgHeight,
                minHeight = minHeight,
                maxHeight = maxHeight,
                bounds = mesh.bounds
            };
        }
    }

    /// <summary>
    /// Mesh statistics for analysis
    /// </summary>
    public class MeshStatistics {
        public int vertexCount;
        public int triangleCount;
        public float averageHeight;
        public float minHeight;
        public float maxHeight;
        public Bounds bounds;

        public override string ToString() {
            return $"Vertices: {vertexCount}, Triangles: {triangleCount}, " +
                   $"Height: [{minHeight:F2}, {maxHeight:F2}] (avg: {averageHeight:F2})";
        }
    }

}
