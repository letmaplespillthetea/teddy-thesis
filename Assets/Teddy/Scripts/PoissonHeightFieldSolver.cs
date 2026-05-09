using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Poisson Height Field Solver
    /// Solves the Poisson equation: ∇²h = s·a·c to compute height values
    /// </summary>
    public class PoissonHeightFieldSolver {

        private float laplacianTolerance = 0.0001f;
        private int maxIterations = 200;
        private float jacobiDamping = 0.5f;

        /// <summary>
        /// Solve height fields across all domains using Poisson equation
        /// Returns height values for each vertex
        /// </summary>
        public List<float> SolveHeightFields(
            List<Vector3> vertices,
            List<int> triangles,
            List<DomainStitchingSystem.BodyPartConfig> bodyParts,
            float inflationAmount) {

            var heightFields = new List<float>(vertices.Count);
            for (int i = 0; i < vertices.Count; i++) {
                heightFields.Add(0f);
            }

            if (vertices.Count == 0 || triangles.Count == 0) {
                return heightFields;
            }

            Debug.Log($"[PoissonSolver] Solving height fields for {vertices.Count} vertices...");

            // Build Laplacian matrix
            Debug.Log("[PoissonSolver] Building Laplacian system...");
            var (laplacianMatrix, rhs) = BuildLaplacianSystem(vertices, triangles, bodyParts, inflationAmount);

            // Solve using iterative method (Jacobi or GMRES)
            Debug.Log("[PoissonSolver] Starting iterative linear solver...");
            heightFields = SolveLinearSystem(laplacianMatrix, rhs, vertices.Count);

            // Apply semi-elliptical transformation
            Debug.Log("[PoissonSolver] Applying semi-elliptical shaping...");
            ApplySemiEllipticalShaping(heightFields);

            Debug.Log("[PoissonSolver] Height field calculation complete.");
            return heightFields;
        }

        /// <summary>
        /// Build the Laplacian matrix and RHS vector for the Poisson equation
        /// ∇²h = s·a·c where:
        /// - s is +1 for front-facing, -1 for back-facing
        /// - a is 1/3 sum of incident triangle areas
        /// - c is inflation amount
        /// </summary>
        private (float[,], float[]) BuildLaplacianSystem(
            List<Vector3> vertices,
            List<int> triangles,
            List<DomainStitchingSystem.BodyPartConfig> bodyParts,
            float inflationAmount) {

            int n = vertices.Count;
            var laplacian = new float[n, n];
            var rhs = new float[n];

            // Calculate cotangent Laplacian weights
            var cotangentWeights = CalculateCotangentWeights(vertices, triangles);

            // Fill Laplacian matrix
            for (int i = 0; i < n; i++) {
                float diagonalSum = 0f;

                // Add weights from neighbors
                if (cotangentWeights.ContainsKey(i)) {
                    foreach (var (neighborIdx, weight) in cotangentWeights[i]) {
                        laplacian[i, neighborIdx] = -weight;
                        diagonalSum += weight;
                    }
                }

                laplacian[i, i] = diagonalSum;
            }

            // Apply Dirichlet boundary conditions (h = 0 at user-drawn curves)
            ApplyDirichletBoundaryConditions(laplacian, rhs, bodyParts, vertices);

            // Set RHS: s·a·c
            for (int i = 0; i < n; i++) {
                float signFactor = GetSignFactor(i, bodyParts, vertices);
                float areaTerm = CalculateVertexArea(i, triangles, vertices) / 3f;

                rhs[i] = signFactor * areaTerm * inflationAmount;
            }

            return (laplacian, rhs);
        }

        /// <summary>
        /// Calculate cotangent weights for Laplace-Beltrami operator
        /// </summary>
        private Dictionary<int, Dictionary<int, float>> CalculateCotangentWeights(
            List<Vector3> vertices,
            List<int> triangles) {

            var weights = new Dictionary<int, Dictionary<int, float>>();

            // Initialize
            for (int i = 0; i < vertices.Count; i++) {
                weights[i] = new Dictionary<int, float>();
            }

            // Process each triangle
            for (int t = 0; t < triangles.Count; t += 3) {
                int i = triangles[t];
                int j = triangles[t + 1];
                int k = triangles[t + 2];

                Vector3 vi = vertices[i];
                Vector3 vj = vertices[j];
                Vector3 vk = vertices[k];

                // Calculate cotangent of angle at each vertex
                float cotI = GetCotangent(vj - vi, vk - vi);
                float cotJ = GetCotangent(vi - vj, vk - vj);
                float cotK = GetCotangent(vi - vk, vj - vk);

                // Add weights
                AddWeight(weights, j, k, cotI / 2f);
                AddWeight(weights, k, i, cotJ / 2f);
                AddWeight(weights, i, j, cotK / 2f);
            }

            return weights;
        }

        /// <summary>
        /// Calculate cotangent of angle between two vectors
        /// </summary>
        private float GetCotangent(Vector3 a, Vector3 b) {
            float dotProduct = Vector3.Dot(a, b);
            float crossMagnitude = Vector3.Cross(a, b).magnitude;

            if (crossMagnitude < 0.0001f) return 0f;

            return dotProduct / crossMagnitude;
        }

        /// <summary>
        /// Add weight to the weight dictionary
        /// </summary>
        private void AddWeight(Dictionary<int, Dictionary<int, float>> weights, int i, int j, float weight) {
            if (!weights[i].ContainsKey(j)) {
                weights[i][j] = 0f;
            }
            weights[i][j] += weight;
        }

        /// <summary>
        /// Get sign factor for front (+1) or back (-1) facing regions
        /// </summary>
        private float GetSignFactor(int vertexIdx, List<DomainStitchingSystem.BodyPartConfig> bodyParts, List<Vector3> vertices) {
            var pos = vertices[vertexIdx];

            // Check which domain this vertex belongs to
            foreach (var part in bodyParts) {
                // Front-facing regions have positive sign
                if (IsVertexInDomain(pos, part.frontFacing)) {
                    return 1f;
                }

                // Back-facing regions have negative sign
                if (IsVertexInDomain(pos, part.backFacing)) {
                    return -1f;
                }
            }

            return 1f;  // Default to front-facing
        }

        /// <summary>
        /// Check if a vertex belongs to a domain
        /// </summary>
        private bool IsVertexInDomain(Vector3 pos, DomainStitchingSystem.StitchedDomain domain) {
            Vector2 pos2D = new Vector2(pos.x, pos.y);

            foreach (var v in domain.vertices) {
                if (Vector2.Distance(pos2D, v) < 0.01f) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Calculate total area of triangles incident to a vertex (used for area term)
        /// </summary>
        private float CalculateVertexArea(int vertexIdx, List<int> triangles, List<Vector3> vertices) {
            float area = 0f;

            for (int t = 0; t < triangles.Count; t += 3) {
                if (triangles[t] == vertexIdx || triangles[t + 1] == vertexIdx || triangles[t + 2] == vertexIdx) {
                    Vector3 v1 = vertices[triangles[t]];
                    Vector3 v2 = vertices[triangles[t + 1]];
                    Vector3 v3 = vertices[triangles[t + 2]];

                    float triArea = Vector3.Cross(v2 - v1, v3 - v1).magnitude * 0.5f;
                    area += triArea;
                }
            }

            return area;
        }

        /// <summary>
        /// Apply Dirichlet boundary conditions: h = 0 at user-drawn curves
        /// </summary>
        private void ApplyDirichletBoundaryConditions(
            float[,] laplacian,
            float[] rhs,
            List<DomainStitchingSystem.BodyPartConfig> bodyParts,
            List<Vector3> vertices) {

            for (int i = 0; i < vertices.Count; i++) {
                bool isBoundary = IsBoundaryVertex(i, bodyParts, vertices);

                if (isBoundary) {
                    // Set row to identity and RHS to 0 (h_i = 0)
                    for (int j = 0; j < vertices.Count; j++) {
                        laplacian[i, j] = 0f;
                    }
                    laplacian[i, i] = 1f;
                    rhs[i] = 0f;
                }
            }
        }

        /// <summary>
        /// Check if a vertex lies on the boundary of any domain
        /// </summary>
        private bool IsBoundaryVertex(int vertexIdx, List<DomainStitchingSystem.BodyPartConfig> bodyParts, List<Vector3> vertices) {
            var pos = vertices[vertexIdx];
            Vector2 pos2D = new Vector2(pos.x, pos.y);

            foreach (var part in bodyParts) {
                if (IsOnBoundary(pos2D, part.frontFacing.boundary)) return true;
                if (IsOnBoundary(pos2D, part.backFacing.boundary)) return true;
            }

            return false;
        }

        private bool IsOnBoundary(Vector2 pos, List<Vector2> boundary) {
            foreach (var v in boundary) {
                if (Vector2.Distance(pos, v) < 0.01f) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Solve the linear system Ax = b using iterative method
        /// </summary>
        private List<float> SolveLinearSystem(float[,] A, float[] b, int size) {
            var x = new float[size];
            var x_new = new float[size];

            // Jacobi iteration
            int finalIter = 0;
            float finalResidual = 0f;
            for (int iter = 0; iter < maxIterations; iter++) {
                finalIter = iter;
                for (int i = 0; i < size; i++) {
                    float sum = 0f;

                    for (int j = 0; j < size; j++) {
                        if (i != j) {
                            sum += A[i, j] * x[j];
                        }
                    }

                    if (Mathf.Abs(A[i, i]) > laplacianTolerance) {
                        float newValue = (b[i] - sum) / A[i, i];
                        x_new[i] = Mathf.Lerp(x[i], newValue, jacobiDamping);
                    } else {
                        x_new[i] = x[i];
                    }
                }

                // Check convergence
                float residual = 0f;
                for (int i = 0; i < size; i++) {
                    residual += Mathf.Abs(x_new[i] - x[i]);
                }
                finalResidual = residual;

                System.Array.Copy(x_new, x, size);

                if (residual < laplacianTolerance) {
                    break;
                }
            }

            Debug.Log($"[PoissonSolver] Linear solver converged in {finalIter + 1} iterations. Final residual: {finalResidual:F6}");

            return x.ToList();
        }

        /// <summary>
        /// Apply semi-elliptical shaping to height values
        /// h' = sign(h) * sqrt(|h|)
        /// </summary>
        private void ApplySemiEllipticalShaping(List<float> heightFields) {
            for (int i = 0; i < heightFields.Count; i++) {
                float h = heightFields[i];
                heightFields[i] = Mathf.Sign(h) * Mathf.Sqrt(Mathf.Abs(h));
            }
        }

        /// <summary>
        /// Smooth height fields using Laplacian smoothing
        /// </summary>
        public void SmoothHeightFields(List<float> heightFields, List<int> triangles, int smoothIterations = 5) {
            var smoothed = new List<float>(heightFields);

            for (int iter = 0; iter < smoothIterations; iter++) {
                for (int i = 0; i < heightFields.Count; i++) {
                    float sum = heightFields[i];
                    int count = 1;

                    // Find neighbors
                    for (int t = 0; t < triangles.Count; t += 3) {
                        int idx = -1;
                        int[] neighbors = new int[2];
                        int neighborCount = 0;

                        // Check if vertex i is in this triangle
                        if (triangles[t] == i) {
                            idx = 0;
                        } else if (triangles[t + 1] == i) {
                            idx = 1;
                        } else if (triangles[t + 2] == i) {
                            idx = 2;
                        }

                        if (idx >= 0) {
                            neighbors[0] = triangles[t + ((idx + 1) % 3)];
                            neighbors[1] = triangles[t + ((idx + 2) % 3)];
                            neighborCount = 2;

                            for (int n = 0; n < neighborCount; n++) {
                                sum += heightFields[neighbors[n]];
                                count++;
                            }
                        }
                    }

                    smoothed[i] = sum / count;
                }

                heightFields.Clear();
                heightFields.AddRange(smoothed);
            }
        }
    }

}
