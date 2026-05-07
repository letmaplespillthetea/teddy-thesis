using UnityEngine;
using System.Collections.Generic;
using System.Linq;

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
                isOpenContour = isOpen
            };

            // Back-facing region (mirror)
            config.backFacing = new StitchedDomain {
                domainID = partID * 2 + 1,
                boundary = new List<Vector2>(contour),
                isOpenContour = isOpen
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
        public Mesh GenerateStitchedMesh(float inflationAmount = 1.0f, bool smoothHeightFields = true) {
            if (bodyParts.Count == 0) return null;

            // Step 1: Generate Delaunay triangulations for each domain
            foreach (var part in bodyParts) {
                TriangulateDomain(part.frontFacing, inflationAmount);
                TriangulateDomain(part.backFacing, -inflationAmount);
            }

            // Step 2: Merge domains with stitching
            var domainMerger = new DomainMerger();
            (stitchedVertices, stitchedTriangles) = domainMerger.MergeDomains(bodyParts);

            // Step 3: Compute height fields via Poisson equation
            var heightSolver = new PoissonHeightFieldSolver();
            List<float> heightFields = heightSolver.SolveHeightFields(
                stitchedVertices.Cast<Vector3>().ToList(),
                stitchedTriangles,
                bodyParts,
                inflationAmount
            );

            // Step 4: Inflate to 3D mesh
            var inflater = new MeshInflationUtility();
            Mesh finalMesh = inflater.InflateTo3D(
                stitchedVertices.Cast<Vector3>().ToList(),
                stitchedTriangles,
                heightFields,
                smoothHeightFields
            );

            return finalMesh;
        }

        /// <summary>
        /// Triangulate a single domain using constrained Delaunay triangulation
        /// </summary>
        private void TriangulateDomain(StitchedDomain domain, float inflationSign) {
            var triangulator = new ConstrainedDelaunayTriangulation();

            // Combine boundary curves
            var boundaryVertices = new List<Vector2>(domain.boundary);
            if (domain.closureCurve != null) {
                boundaryVertices.AddRange(domain.closureCurve);
            }

            // Constrain triangulation by the boundary
            triangulator.SetBoundaryConstraint(boundaryVertices);

            // Generate triangulation
            (domain.vertices, domain.triangles) = triangulator.Triangulate();

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

            foreach (var part in bodyParts) {
                if (part.frontFacing.isOpenContour) {
                    // Inequality constraint: front half moves in front of body
                    constraints.Add(new DeformationConstraint {
                        type = ConstraintType.Inequality,
                        targetZ = 1.0f,  // Move forward
                        affectedVertices = GetBoundaryVertices(part.frontFacing)
                    });

                    // Equality constraint: back half aligns with body cavity
                    constraints.Add(new DeformationConstraint {
                        type = ConstraintType.Equality,
                        affectedVertices = GetBoundaryVertices(part.backFacing)
                    });
                }
            }

            return constraints;
        }

        private List<int> GetBoundaryVertices(StitchedDomain domain) {
            var boundary = new List<int>();
            int boundarySize = domain.boundary.Count;

            for (int i = 0; i < boundarySize; i++) {
                boundary.Add(i);
            }

            return boundary;
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
