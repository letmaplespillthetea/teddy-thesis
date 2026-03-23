using UnityEngine;
using System.Collections.Generic;

namespace mattatz.TeddySystem {
    public class LBSSkinning {
        struct BoneWeight {
            public int boneIndex;
            public float weight;
        }

        Vector3[] restVertices;
        List<(Vector3 start, Vector3 end)> restBones;
        BoneWeight[][] vertexWeights;
        Matrix4x4[] bindPoses;

        public LBSSkinning(Vector3[] vertices, List<(Vector3 start, Vector3 end)> bones) {
            restVertices = (Vector3[])vertices.Clone();
            restBones = new List<(Vector3, Vector3)>(bones);
            int vCount = vertices.Length;
            int bCount = bones.Count;

            vertexWeights = new BoneWeight[vCount][];
            bindPoses = new Matrix4x4[bCount];

            for (int i = 0; i < bCount; i++) {
                bindPoses[i] = GetBoneMatrix(bones[i].start, bones[i].end).inverse;
            }

            for (int i = 0; i < vCount; i++) {
                Vector3 p = vertices[i];
                var weights = new List<BoneWeight>();
                float sumWeight = 0f;
                var dists = new List<(int index, float dist)>();

                for (int j = 0; j < bCount; j++) {
                    float d = DistancePointLine(p, bones[j].start, bones[j].end);
                    dists.Add((j, d));
                }

                dists.Sort((a, b) => a.dist.CompareTo(b.dist));
                int limit = Mathf.Min(4, dists.Count);

                for (int k = 0; k < limit; k++) {
                    // Inverse distance weighting. Pow(d, 4) gives smoother falloff visually similar to LBS
                    float w = 1f / (Mathf.Pow(dists[k].dist, 4f) + 0.0001f);
                    weights.Add(new BoneWeight { boneIndex = dists[k].index, weight = w });
                    sumWeight += w;
                }

                for (int k = 0; k < weights.Count; k++) {
                    var bw = weights[k];
                    bw.weight /= sumWeight;
                    weights[k] = bw;
                }
                vertexWeights[i] = weights.ToArray();
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

        float DistancePointLine(Vector3 p, Vector3 a, Vector3 b) {
            Vector3 ab = b - a;
            float sqrLen = ab.sqrMagnitude;
            if (sqrLen < 0.0001f) return Vector3.Distance(p, a);

            float t = Vector3.Dot(p - a, ab) / sqrLen;
            t = Mathf.Clamp01(t);
            Vector3 closest = a + t * ab;
            return Vector3.Distance(p, closest);
        }
    }
}
