using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

using mattatz.Utils;
using mattatz.Triangulation2DSystem;
using mattatz.MeshSmoothingSystem;

namespace mattatz.TeddySystem.Example {

	public class Demo : MonoBehaviour {

		[SerializeField] string fileName = "duck";
		[SerializeField] bool debug = false;
		[SerializeField] Material lineMat = null;

		[SerializeField] int smoothingTimes = 5;
		[SerializeField, Range(0f, 1f)] float smoothingAlpha = 0.25f, smoothingBeta = 0.5f;

		[Header("Skeleton")]
		[SerializeField] bool showSkeleton = true;
		[SerializeField] Color skeletonColor = Color.red;
		[SerializeField, Tooltip("Merge bones shorter than this distance to reduce joints (e.g. 0.05)")] 
		float simplifyDistance = 0.05f;

		Teddy teddy;
		List<Segment2D> contour;
		List<(Vector3, Vector3)> skeletonBones;

		// ── Drag state ──────────────────────────────────────────────────────────
		bool   dragging;
		Vector3 dragOffset;

		int draggingJoint = -1;
		float dragZ;

		[Header("Mass Spring Physics")]
		[SerializeField] bool enablePhysics = true;
		[SerializeField, Range(0f, 1f)] float shapeStiffness = 0.2f;
		[SerializeField, Range(0f, 1f)] float damping = 0.1f;
		[SerializeField] float gravity = 0f;

		List<Vector3> joints;
		List<Vector2Int> boneIndices;
		List<float> restLengths;
		
		List<Vector3> restLocalPositions;
		List<Vector3> worldJoints;
		List<Vector3> prevWorldJoints;

		HarmonicSkinning skinning;

		void Start () {
			if(debug) {
				var points = LocalStorage.LoadList<Vector2>(fileName + ".json");
				teddy = new Teddy(points);
				contour = BuildContourSegments(teddy.triangulation);
				GetComponent<MeshFilter>().sharedMesh = teddy.Build(MeshSmoothingMethod.HC, smoothingTimes, smoothingAlpha, smoothingBeta);

				// Build skeleton after Build() so heightTable is fully populated
				skeletonBones = teddy.GetSkeletonBones();
				skeletonBones = SimplifyBones(skeletonBones);
				LogSkeleton(skeletonBones);

				// Extract unique joints and bone connections for skinning
				joints = new List<Vector3>();
				boneIndices = new List<Vector2Int>();
				
				foreach (var b in skeletonBones) {
					int i0 = joints.IndexOf(b.Item1);
					if (i0 == -1) { i0 = joints.Count; joints.Add(b.Item1); }
					
					int i1 = joints.IndexOf(b.Item2);
					if (i1 == -1) { i1 = joints.Count; joints.Add(b.Item2); }
					
					boneIndices.Add(new Vector2Int(i0, i1));
				}

				restLengths = new List<float>();
				foreach (var bi in boneIndices) {
					restLengths.Add(Vector3.Distance(joints[bi.x], joints[bi.y]));
				}

				restLocalPositions = new List<Vector3>(joints);
				worldJoints = new List<Vector3>();
				prevWorldJoints = new List<Vector3>();
				foreach (var j in joints) {
					Vector3 w = transform.TransformPoint(j);
					worldJoints.Add(w);
					prevWorldJoints.Add(w);
				}

				Mesh filterMesh = GetComponent<MeshFilter>().sharedMesh;
				skinning = new HarmonicSkinning(filterMesh.vertices, filterMesh.triangles, skeletonBones);
			}
		}

		// ── Drag model or joint in Game View ─────────────────────────────────────────────
		void Update () {
			var cam = Camera.main;
			if (cam == null) return;

			if (Input.GetMouseButtonDown(0)) {
				Vector2 mousePos = Input.mousePosition;
				int closestJoint = -1;
				float minDist = 20f; // 20 pixels radius for joint picking

				if (joints != null && joints.Count > 0) {
					for (int i = 0; i < joints.Count; i++) {
						Vector3 screenPos = cam.WorldToScreenPoint(transform.TransformPoint(joints[i]));
						float dist = Vector2.Distance(mousePos, new Vector2(screenPos.x, screenPos.y));
						if (dist < minDist) {
							minDist = dist;
							closestJoint = i;
						}
					}
				}

				if (closestJoint != -1) {
					draggingJoint = closestJoint;
					dragZ = cam.WorldToScreenPoint(transform.TransformPoint(joints[closestJoint])).z;
					dragging = false;
				} else {
					Vector3 mp = cam.ScreenToWorldPoint(
						new Vector3(Input.mousePosition.x, Input.mousePosition.y, cam.WorldToScreenPoint(transform.position).z));
					dragOffset = transform.position - mp;
					dragging   = true;
				}
			}

			if (Input.GetMouseButtonUp(0)) {
				draggingJoint = -1;
				dragging = false;
			}

			if (draggingJoint != -1 || dragging || enablePhysics) {
				if (draggingJoint != -1) {
					Vector3 mp = cam.ScreenToWorldPoint(
						new Vector3(Input.mousePosition.x, Input.mousePosition.y, dragZ));
					worldJoints[draggingJoint] = mp;
				} else if (dragging) {
					Vector3 mp = cam.ScreenToWorldPoint(
						new Vector3(Input.mousePosition.x, Input.mousePosition.y, cam.WorldToScreenPoint(transform.position).z));
					transform.position = mp + dragOffset;
				}

				// Physics Step
				if (enablePhysics && joints != null) {
					float dt = Time.deltaTime;
					if (dt > 0.1f) dt = 0.1f; // clamp delta

					// 1. Verlet Integration & Shape Matching
					for(int i = 0; i < worldJoints.Count; i++) {
						if (i == draggingJoint) {
							prevWorldJoints[i] = worldJoints[i];
							continue;
						}

						Vector3 vel = (worldJoints[i] - prevWorldJoints[i]) / dt;
						vel.y -= gravity * dt;
						vel *= (1f - damping);

						prevWorldJoints[i] = worldJoints[i];
						Vector3 nextPos = worldJoints[i] + vel * dt;

						// Shape Match pulls towards current transform
						Vector3 targetWorld = transform.TransformPoint(restLocalPositions[i]);
						worldJoints[i] = Vector3.Lerp(nextPos, targetWorld, shapeStiffness);
					}
				} else if (!enablePhysics && joints != null) {
					// Update world joints exactly if physics off
					for(int i=0; i<joints.Count; i++) {
						if (i != draggingJoint) worldJoints[i] = transform.TransformPoint(restLocalPositions[i]);
					}
				}

				// 2. PBD Solver to maintain bone lengths
				if (restLengths != null && boneIndices != null) {
					int iterations = 50;
					for (int it = 0; it < iterations; it++) {
						for (int i = 0; i < boneIndices.Count; i++) {
							int i0 = boneIndices[i].x;
							int i1 = boneIndices[i].y;
							
							Vector3 p0 = worldJoints[i0];
							Vector3 p1 = worldJoints[i1];
							Vector3 delta = p1 - p0;
							float currentLen = delta.magnitude;
							if (currentLen < 0.0001f) continue;
							
							float diff = (currentLen - restLengths[i]) / currentLen;
							Vector3 offset = delta * 0.5f * diff;
							
							if (i0 == draggingJoint) {
								worldJoints[i1] -= offset * 2f;
							} else if (i1 == draggingJoint) {
								worldJoints[i0] += offset * 2f;
							} else {
								worldJoints[i0] += offset;
								worldJoints[i1] -= offset;
							}
						}
					}
				}

				// Sync back to local joints for Skinning
				if (joints != null) {
					for(int i = 0; i < joints.Count; i++) {
						joints[i] = transform.InverseTransformPoint(worldJoints[i]);
					}
				}

				// Rebuild skeleton bones from moved joints
				var currentBones = new List<(Vector3, Vector3)>();
				if (boneIndices != null && joints != null) {
					foreach (var bi in boneIndices) {
						currentBones.Add((joints[bi.x], joints[bi.y]));
					}
					skeletonBones = currentBones;
				}

				// Deform mesh
				if (skinning != null && skeletonBones != null) {
					Mesh mesh = GetComponent<MeshFilter>().sharedMesh;
					mesh.vertices = skinning.Deform(skeletonBones);
					mesh.RecalculateNormals();
					mesh.RecalculateBounds();
				}
			} else {
				// Not interacting and no physics: keep updating worldJoints to match transform if moving in scene view
				if (joints != null && restLocalPositions != null) {
					for(int i=0; i<joints.Count; i++) {
						worldJoints[i] = transform.TransformPoint(restLocalPositions[i]);
						joints[i] = restLocalPositions[i];
					}
				}
			}
		}

		List<(Vector3, Vector3)> SimplifyBones(List<(Vector3, Vector3)> bones) {
			if (simplifyDistance <= 0f) return bones;

			var jlist = new List<Vector3>();
			var edges = new List<Vector2Int>();
			foreach (var b in bones) {
				int i0 = jlist.IndexOf(b.Item1);
				if (i0 == -1) { i0 = jlist.Count; jlist.Add(b.Item1); }
				int i1 = jlist.IndexOf(b.Item2);
				if (i1 == -1) { i1 = jlist.Count; jlist.Add(b.Item2); }
				edges.Add(new Vector2Int(i0, i1));
			}

			bool merged = true;
			while (merged) {
				merged = false;
				for (int i = 0; i < edges.Count; i++) {
					int i0 = edges[i].x;
					int i1 = edges[i].y;
					if (i0 == i1) continue;
					
					if (Vector3.Distance(jlist[i0], jlist[i1]) < simplifyDistance) {
						// Merge i1 into i0 at midpoint
						Vector3 mid = (jlist[i0] + jlist[i1]) * 0.5f;
						jlist[i0] = mid;
						
						for (int j = 0; j < edges.Count; j++) {
							var e = edges[j];
							if (e.x == i1) e.x = i0;
							if (e.y == i1) e.y = i0;
							edges[j] = e;
						}
						merged = true;
						break;
					}
				}
				edges.RemoveAll(e => e.x == e.y);
			}

			var result = new List<(Vector3, Vector3)>();
			foreach (var e in edges) {
				result.Add((jlist[e.x], jlist[e.y]));
			}
			return result;
		}

		void LogSkeleton(List<(Vector3, Vector3)> bones) {
			if (bones == null || bones.Count == 0) {
				Debug.LogWarning("[Skeleton] No bones found!");
				return;
			}

			Debug.Log($"[Skeleton] Total bones: {bones.Count}");
			for (int i = 0; i < bones.Count; i++) {
				var (start, end) = bones[i];
				Vector3 mid    = (start + end) * 0.5f;
				float   length = Vector3.Distance(start, end);
				Debug.Log($"  Bone[{i,2}]  start=({start.x:F2}, {start.y:F2})  end=({end.x:F2}, {end.y:F2})  mid=({mid.x:F2}, {mid.y:F2})  len={length:F3}");
			}
		}

		List<Segment2D> BuildContourSegments (Triangulation2D triangulation) {
			var triangles = teddy.triangulation.Triangles;

			var contour = new List<Segment2D>();

			var table = new Dictionary<Segment2D, HashSet<Triangle2D>>();
			for(int i = 0, n = triangles.Length; i < n; i++) {
				var t = triangles[i];
				if(!table.ContainsKey(t.s0)) table.Add(t.s0, new HashSet<Triangle2D>());
				if(!table.ContainsKey(t.s1)) table.Add(t.s1, new HashSet<Triangle2D>());
				if(!table.ContainsKey(t.s2)) table.Add(t.s2, new HashSet<Triangle2D>());

				table[t.s0].Add(t);
				table[t.s1].Add(t);
				table[t.s2].Add(t);
			}

			contour = table.Keys.ToList().FindAll(s => {
				return table[s].Count == 1;
			}).ToList();

			return contour;
		}

		void OnDrawGizmos () {
			// Contour
			if (contour != null) {
				Gizmos.color = Color.yellow;
				contour.ForEach(s => Gizmos.DrawLine(
					transform.TransformPoint(s.a.Coordinate),
					transform.TransformPoint(s.b.Coordinate)
				));
			}

			// Skeleton — visible in Scene View
			if (showSkeleton && skeletonBones != null) {
				float gizmoR = jointRadius * 0.01f; // scale pixel radius to world units

				Gizmos.color = skeletonColor;
				foreach (var (start, end) in skeletonBones) {
					Vector3 ws = transform.TransformPoint(start);
					Vector3 we = transform.TransformPoint(end);
					Gizmos.DrawLine(ws, we);
				}

				// Collect unique joints then draw filled spheres
				var joints = new HashSet<Vector3>();
				foreach (var (start, end) in skeletonBones) {
					joints.Add(transform.TransformPoint(start));
					joints.Add(transform.TransformPoint(end));
				}
				foreach (var j in joints)
					Gizmos.DrawSphere(j, gizmoR);
			}
		}

		void OnRenderObject () {
			if(teddy == null) return;

			if(teddy.triangulation != null) {
				lineMat.SetColor("_Color", Color.black);
				DrawTriangles(teddy.triangulation.Triangles);
			}

		}

		[SerializeField, Range(2f, 16f)] float jointRadius = 5f;

		void OnGUI () {
			if (Event.current.type != EventType.Repaint) return;
			if (!showSkeleton || skeletonBones == null || skeletonBones.Count == 0) return;

			var cam = Camera.main;
			if (cam == null || lineMat == null) return;

			lineMat.SetColor("_Color", skeletonColor);
			lineMat.SetPass(0);

			GL.PushMatrix();
			GL.LoadPixelMatrix();

			// ── Bone lines ──────────────────────────────────────────────────────
			var screenJoints = new HashSet<Vector2>();

			GL.Begin(GL.LINES);
			foreach (var (start, end) in skeletonBones) {
				Vector3 ws = cam.WorldToScreenPoint(transform.TransformPoint(start));
				Vector3 we = cam.WorldToScreenPoint(transform.TransformPoint(end));
				if (ws.z < 0f || we.z < 0f) continue;

				float sx = ws.x, sy = Screen.height - ws.y;
				float ex = we.x, ey = Screen.height - we.y;

				GL.Vertex3(sx, sy, 0f);
				GL.Vertex3(ex, ey, 0f);

				screenJoints.Add(new Vector2(sx, sy));
				screenJoints.Add(new Vector2(ex, ey));
			}
			GL.End();

			// ── Filled joint discs ───────────────────────────────────────────────
			GL.Begin(GL.TRIANGLES);
			if (joints != null) {
				for (int i = 0; i < joints.Count; i++) {
					Vector3 ws = cam.WorldToScreenPoint(transform.TransformPoint(joints[i]));
					if (ws.z < 0f) continue;
					float sx = ws.x, sy = Screen.height - ws.y;

					if (i == draggingJoint) {
						GL.End();
						lineMat.SetColor("_Color", Color.white);
						lineMat.SetPass(0);
						GL.Begin(GL.TRIANGLES);
						DrawFilledDisc(sx, sy, jointRadius * 1.5f, 16);
						GL.End();
						lineMat.SetColor("_Color", skeletonColor);
						lineMat.SetPass(0);
						GL.Begin(GL.TRIANGLES);
					} else {
						DrawFilledDisc(sx, sy, jointRadius, 16);
					}
				}
			}
			GL.End();

			GL.PopMatrix();
		}

		// Solid filled disc via triangle fan — must be called inside GL.Begin(GL.TRIANGLES)
		void DrawFilledDisc (float cx, float cy, float r, int segments) {
			float step = 2f * Mathf.PI / segments;
			for (int i = 0; i < segments; i++) {
				float a0 = i       * step;
				float a1 = (i + 1) * step;
				GL.Vertex3(cx,                    cy,                    0f); // center
				GL.Vertex3(cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, 0f);
				GL.Vertex3(cx + Mathf.Cos(a1) * r, cy + Mathf.Sin(a1) * r, 0f);
			}
		}

		void DrawTriangles (Triangle2D[] triangles) {
			GL.PushMatrix();
			GL.MultMatrix (transform.localToWorldMatrix);

			lineMat.SetPass(0);

			GL.Begin(GL.LINES);

			for(int i = 0, n = triangles.Length; i < n; i++) {
				var t = triangles[i];
				GL.Vertex(t.s0.a.Coordinate); GL.Vertex(t.s0.b.Coordinate);
				GL.Vertex(t.s1.a.Coordinate); GL.Vertex(t.s1.b.Coordinate);
				GL.Vertex(t.s2.a.Coordinate); GL.Vertex(t.s2.b.Coordinate);
			}

			GL.End();
			GL.PopMatrix();
		}



	}

}
