using UnityEngine;
using System.Collections.Generic;

namespace mattatz.TeddySystem {
    public class HarmonicSkinning {
        struct BoneWeight {
            public int boneIndex;
            public float weight;
        }

        Vector3[] restVertices;
        Matrix4x4[] bindPoses;
        BoneWeight[][] vertexWeights;

        public HarmonicSkinning(Vector3[] vertices, int[] triangles, List<(Vector3 start, Vector3 end)> bones) {
            restVertices = (Vector3[])vertices.Clone();
            int vCount = vertices.Length;
            int bCount = bones.Count;

            bindPoses = new Matrix4x4[bCount];
            for (int i = 0; i < bCount; i++) {
                bindPoses[i] = GetBoneMatrix(bones[i].start, bones[i].end).inverse;
            }

            // Build adjacency list for Laplacian smoothing
            var adj = new List<int>[vCount];
            for (int i = 0; i < vCount; i++) adj[i] = new List<int>();

            for (int i = 0; i < triangles.Length; i += 3) {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];
                
                if (!adj[v0].Contains(v1)) adj[v0].Add(v1);
                if (!adj[v0].Contains(v2)) adj[v0].Add(v2);
                
                if (!adj[v1].Contains(v0)) adj[v1].Add(v0);
                if (!adj[v1].Contains(v2)) adj[v1].Add(v2);

                if (!adj[v2].Contains(v0)) adj[v2].Add(v0);
                if (!adj[v2].Contains(v1)) adj[v2].Add(v1);
            }

            // Initialize weights matrix and identify fixed (Dirichlet) boundary vertices
            float[,] W = new float[vCount, bCount];
            bool[] isFixed = new bool[vCount];

            for (int i = 0; i < vCount; i++) {
                float minDist = float.MaxValue;
                int minBone = -1;
                for (int j = 0; j < bCount; j++) {
                    // Use 2D distance to find spine vertices (ignoring Z thickness)
                    float d = DistancePointLine2D(vertices[i], bones[j].start, bones[j].end);
                    if (d < minDist) {
                        minDist = d;
                        minBone = j;
                    }
                }

                // If a vertex lies along the 2D spine, lock its weight to the nearest bone
                if (minDist < 0.03f) {
                    isFixed[i] = true;
                    W[i, minBone] = 1.0f;
                }
            }

            // Fallback: make sure every bone has at least one fixed vertex so it can diffuse properly
            for (int j = 0; j < bCount; j++) {
                float minDist = float.MaxValue;
                int bestV = -1;
                for (int i = 0; i < vCount; i++) {
                    float d = DistancePointLine2D(vertices[i], bones[j].start, bones[j].end);
                    if (d < minDist) { minDist = d; bestV = i; }
                }
                if (bestV != -1 && !isFixed[bestV]) {
                    isFixed[bestV] = true;
                    for (int b = 0; b < bCount; b++) W[bestV, b] = 0f;
                    W[bestV, j] = 1.0f;
                }
            }

            // Laplacian Diffusion (Harmonic Weights) - typically 50-100 iterations is enough for small meshes
            int diffusionIterations = 100;
            float[,] W_new = new float[vCount, bCount];

            for (int iter = 0; iter < diffusionIterations; iter++) {
                for (int i = 0; i < vCount; i++) {
                    if (isFixed[i]) {
                        for (int b = 0; b < bCount; b++) W_new[i, b] = W[i, b];
                        continue;
                    }

                    int nC = adj[i].Count;
                    if (nC == 0) continue;

                    for (int b = 0; b < bCount; b++) {
                        float sum = 0f;
                        foreach (int neighbor in adj[i]) {
                            sum += W[neighbor, b];
                        }
                        W_new[i, b] = sum / nC;
                    }
                }
                // Swap matrices
                var temp = W;
                W = W_new;
                W_new = temp;
            }

            // Extract non-zero weights per vertex (compress)
            vertexWeights = new BoneWeight[vCount][];
            for (int i = 0; i < vCount; i++) {
                var wList = new List<BoneWeight>();
                float sum = 0f;
                for (int b = 0; b < bCount; b++) {
                    if (W[i, b] > 0.001f) {
                        wList.Add(new BoneWeight { boneIndex = b, weight = W[i, b] });
                        sum += W[i, b];
                    }
                }
                
                // Normalize to handle minor precision errors
                if (sum > 0f) {
                    for(int k=0; k<wList.Count; k++) {
                        var bw = wList[k];
                        bw.weight /= sum;
                        wList[k] = bw;
                    }
                }
                vertexWeights[i] = wList.ToArray();
            }
        }

        public Vector3[] Deform(List<(Vector3 start, Vector3 end)> currentBones) {
            Vector3[] newVertices = new Vector3[restVertices.Length];
            Matrix4x4[] currentPoses = new Matrix4x4[currentBones.Count];

            for (int i = 0; i < currentBones.Count; i++) {
                currentPoses[i] = GetBoneMatrix(currentBones[i].start, currentBones[i].end) * bindPoses[i];
            }

            for (int i = 0; i < restVertices.Length; i++) {
                Vector3 v = restVertices[i];
                Vector3 newV = Vector3.zero;

                foreach (var bw in vertexWeights[i]) {
                    newV += currentPoses[bw.boneIndex].MultiplyPoint3x4(v) * bw.weight;
                }
                newVertices[i] = newV;
            }
            return newVertices;
        }

        Matrix4x4 GetBoneMatrix(Vector3 start, Vector3 end) {
            Vector3 dir = end - start;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.right;

            Vector3 forward = Vector3.forward;
            Vector3 up = Vector3.Cross(forward, dir).normalized;
            if (up.sqrMagnitude < 0.0001f) up = Vector3.up;

            Quaternion rot = Quaternion.LookRotation(forward, up);
            return Matrix4x4.TRS(start, rot, Vector3.one);
        }

        float DistancePointLine2D(Vector3 p, Vector3 a, Vector3 b) {
            Vector2 p2 = new Vector2(p.x, p.y);
            Vector2 a2 = new Vector2(a.x, a.y);
            Vector2 b2 = new Vector2(b.x, b.y);

            Vector2 ab = b2 - a2;
            float sqrLen = ab.sqrMagnitude;
            if (sqrLen < 0.0001f) return Vector2.Distance(p2, a2);

            float t = Vector2.Dot(p2 - a2, ab) / sqrLen;
            t = Mathf.Clamp01(t);
            Vector2 closest = a2 + t * ab;
            return Vector2.Distance(p2, closest);
        }
    }
}
