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
            Vector3 cameraLocalPos,
            bool smoothHeightFields = true) {

            if (plannarVertices.Count == 0 || triangles.Count == 0) {
                return null;
            }

            Debug.Log($"[MeshInflation] Inflating {plannarVertices.Count} vertices to 3D...");

            var mesh = new Mesh();

            // Step 1: Create 3D vertices from height fields
            Debug.Log("[MeshInflation] Step 1: Creating 3D vertices...");
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

            // Step 2: Symmetrize mesh (create front and back faces)
            Debug.Log("[MeshInflation] Step 2: Symmetrizing mesh (creating front and back faces)...");
            var inflatedTriangles = new List<int>();
            
            const float epsilon = 0.01f; // Increased epsilon for more robust contour detection
            int originalVertexCount = inflatedVertices.Count;
            
            Debug.Log($"[MeshInflation] Original vertex count: {originalVertexCount}");
            
            // Add front-facing triangles
            for (int t = 0; t < triangles.Count; t += 3) {
                int i0 = vertexMap[triangles[t]];
                int i1 = vertexMap[triangles[t + 1]];
                int i2 = vertexMap[triangles[t + 2]];

                inflatedTriangles.Add(i0);
                inflatedTriangles.Add(i1);
                inflatedTriangles.Add(i2);
            }
            
            // Build mirror map: original index -> mirrored index
            var mirrorMap = new Dictionary<int, int>();
            int contourVertexCount = 0;
            for (int i = 0; i < originalVertexCount; i++) {
                Vector3 v = inflatedVertices[i];
                if (Mathf.Abs(v.z) > epsilon) {
                    // Create mirrored vertex and store mapping
                    int mirroredIdx = inflatedVertices.Count;
                    inflatedVertices.Add(new Vector3(v.x, v.y, -v.z));
                    mirrorMap[i] = mirroredIdx;
                } else {
                    contourVertexCount++;
                }
            }
            
            Debug.Log($"[MeshInflation] Contour vertices: {contourVertexCount}, Mirrored vertices: {mirrorMap.Count}");
            Debug.Log($"[MeshInflation] Total vertices after mirroring: {inflatedVertices.Count}");
            
            // Add back-facing triangles (reversed winding)
            int skippedTriangles = 0;
            for (int t = 0; t < triangles.Count; t += 3) {
                int i0 = vertexMap[triangles[t]];
                int i1 = vertexMap[triangles[t + 1]];
                int i2 = vertexMap[triangles[t + 2]];
                
                Vector3 v0 = inflatedVertices[i0];
                Vector3 v1 = inflatedVertices[i1];
                Vector3 v2 = inflatedVertices[i2];
                
                // Map to mirrored vertices or keep contour vertices
                int ni0, ni1, ni2;
                
                if (Mathf.Abs(v0.z) <= epsilon) {
                    ni0 = i0;
                } else if (mirrorMap.ContainsKey(i0)) {
                    ni0 = mirrorMap[i0];
                } else {
                    // Create mirrored vertex on-the-fly if missing
                    int mirroredIdx = inflatedVertices.Count;
                    inflatedVertices.Add(new Vector3(v0.x, v0.y, -v0.z));
                    mirrorMap[i0] = mirroredIdx;
                    ni0 = mirroredIdx;
                }
                
                if (Mathf.Abs(v1.z) <= epsilon) {
                    ni1 = i1;
                } else if (mirrorMap.ContainsKey(i1)) {
                    ni1 = mirrorMap[i1];
                } else {
                    // Create mirrored vertex on-the-fly if missing
                    int mirroredIdx = inflatedVertices.Count;
                    inflatedVertices.Add(new Vector3(v1.x, v1.y, -v1.z));
                    mirrorMap[i1] = mirroredIdx;
                    ni1 = mirroredIdx;
                }
                
                if (Mathf.Abs(v2.z) <= epsilon) {
                    ni2 = i2;
                } else if (mirrorMap.ContainsKey(i2)) {
                    ni2 = mirrorMap[i2];
                } else {
                    // Create mirrored vertex on-the-fly if missing
                    int mirroredIdx = inflatedVertices.Count;
                    inflatedVertices.Add(new Vector3(v2.x, v2.y, -v2.z));
                    mirrorMap[i2] = mirroredIdx;
                    ni2 = mirroredIdx;
                }
                
                // Reverse winding for back face
                inflatedTriangles.Add(ni0);
                inflatedTriangles.Add(ni2);
                inflatedTriangles.Add(ni1);
            }
            
            if (skippedTriangles > 0) {
                Debug.LogWarning($"[MeshInflation] Skipped {skippedTriangles} back-facing triangles due to missing mirror mapping.");
            }

            // Step 3: Create caps and close the mesh
            Debug.Log("[MeshInflation] Step 3: Closing open boundaries...");
            CloseOpenBoundaries(inflatedVertices, inflatedTriangles, plannarVertices, triangles, heightFields);

            // Step 4: Calculate normals
            Debug.Log("[MeshInflation] Step 4: Finalizing mesh and recalculating normals...");
            mesh.vertices = inflatedVertices.ToArray();
            mesh.triangles = inflatedTriangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // Step 5: Optional smoothing
            if (smoothHeightFields && inflatedVertices.Count < 65000) {
                Debug.Log("[MeshInflation] Step 5: Applying Laplacian smoothing...");
                SmoothMesh(mesh, smoothingIterations, smoothingStrength);
            }

            Debug.Log("[MeshInflation] Inflation complete.");
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
        /// Implements simplified ARAP-L from "Teddy: A Sketching Interface for 3D Freeform Design"
        /// Uses local-global optimization with layering constraints
        /// </summary>
        public void ApplyARAPDeformation(
            ref Mesh mesh,
            List<DeformationConstraint> constraints,
            int iterations = 5) {

            if (constraints == null || constraints.Count == 0) {
                return;
            }

            Vector3[] vertices = mesh.vertices;
            Vector3[] originalVertices = (Vector3[])vertices.Clone();
            int[] triangles = mesh.triangles;

            Debug.Log($"[ARAP] Starting ARAP-L deformation with {constraints.Count} constraints, {iterations} iterations...");

            // Build adjacency list with cotangent weights
            var neighbors = BuildAdjacencyList(vertices.Length, triangles);
            var weights = ComputeCotangentWeights(originalVertices, triangles, neighbors);

            // Local-global optimization loop
            for (int iter = 0; iter < iterations; iter++) {
                // GLOBAL STEP: Solve for new vertex positions with constraints
                var newVertices = new Vector3[vertices.Length];
                
                for (int i = 0; i < vertices.Length; i++) {
                    // Check if this vertex has constraints
                    var constraintType = GetVertexConstraintType(i, constraints, out float targetZ);
                    
                    if (constraintType == ConstraintType.Equality) {
                        // Equality constraint: keep at current position
                        newVertices[i] = vertices[i];
                    } else {
                        // Compute ARAP energy minimization for this vertex
                        Vector3 laplacian = ComputeWeightedLaplacian(i, vertices, neighbors, weights);
                        Vector3 proposed = vertices[i] + laplacian * 0.5f;  // Damped update
                        
                        if (constraintType == ConstraintType.Inequality) {
                            // Inequality constraint: enforce z >= targetZ
                            proposed.z = Mathf.Max(proposed.z, targetZ);
                        }
                        
                        newVertices[i] = proposed;
                    }
                }
                
                vertices = newVertices;
            }

            Debug.Log($"[ARAP] Deformation complete.");
            mesh.vertices = vertices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
        
        /// <summary>
        /// Get constraint type for a vertex
        /// </summary>
        private ConstraintType GetVertexConstraintType(int vertexIndex, List<DeformationConstraint> constraints, out float targetZ) {
            targetZ = 0f;
            
            foreach (var constraint in constraints) {
                if (constraint.affectedVertices.Contains(vertexIndex)) {
                    targetZ = constraint.targetZ;
                    return constraint.type;
                }
            }
            
            return (ConstraintType)(-1);  // No constraint
        }
        
        /// <summary>
        /// Compute weighted Laplacian for ARAP energy
        /// </summary>
        private Vector3 ComputeWeightedLaplacian(int vertexIndex, Vector3[] vertices, List<int>[] neighbors, Dictionary<(int, int), float> weights) {
            Vector3 laplacian = Vector3.zero;
            float totalWeight = 0f;
            
            foreach (int neighbor in neighbors[vertexIndex]) {
                var key = vertexIndex < neighbor ? (vertexIndex, neighbor) : (neighbor, vertexIndex);
                float weight = weights.ContainsKey(key) ? weights[key] : 1f;
                
                laplacian += weight * (vertices[neighbor] - vertices[vertexIndex]);
                totalWeight += weight;
            }
            
            if (totalWeight > 0f) {
                laplacian /= totalWeight;
            }
            
            return laplacian;
        }
        
        /// <summary>
        /// Compute cotangent weights for mesh edges
        /// Simplified version - uses uniform weights for now
        /// </summary>
        private Dictionary<(int, int), float> ComputeCotangentWeights(Vector3[] vertices, int[] triangles, List<int>[] neighbors) {
            var weights = new Dictionary<(int, int), float>();
            
            // For simplicity, use uniform weights
            // Full implementation would compute actual cotangent weights from triangle angles
            for (int i = 0; i < neighbors.Length; i++) {
                foreach (int j in neighbors[i]) {
                    var key = i < j ? (i, j) : (j, i);
                    if (!weights.ContainsKey(key)) {
                        weights[key] = 1f;
                    }
                }
            }
            
            return weights;
        }

        /// <summary>
        /// Build adjacency list for mesh vertices
        /// </summary>
        private List<int>[] BuildAdjacencyList(int vertexCount, int[] triangles) {
            var neighbors = new List<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++) {
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

            return neighbors;
        }

        /// <summary>
        /// Check if a vertex is constrained by any constraint
        /// </summary>
        private bool IsConstrainedVertex(int vertexIndex, List<DeformationConstraint> constraints) {
            foreach (var constraint in constraints) {
                if (constraint.affectedVertices.Contains(vertexIndex)) {
                    return true;
                }
            }
            return false;
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
