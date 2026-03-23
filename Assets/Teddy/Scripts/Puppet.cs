using UnityEngine;
using Random = UnityEngine.Random;

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

namespace mattatz.TeddySystem.Example {

	[RequireComponent (typeof(Rigidbody), typeof(MeshFilter), typeof(MeshRenderer)) ]
	[RequireComponent (typeof(MeshCollider)) ]
	public class Puppet : MonoBehaviour {

		Rigidbody body {
			get {
				if(_body == null) {
					_body = GetComponent<Rigidbody>();
				}
				return _body;
			}
		}

		MeshFilter filter {
			get {
				if(_filter == null) {
					_filter = GetComponent<MeshFilter>();
				}
				return _filter;
			}
		}

		MeshCollider col {
			get {
				if(_collider == null) {
					_collider = GetComponent<MeshCollider>();
				}
				return _collider;
			}
		}

		public bool showSkeleton = true;
		public Color skeletonColor = Color.red;
		public float simplifyDistance = 0.05f;
		public float jointRadius = 5f;

		public bool enablePhysics = true;
		public float shapeStiffness = 0.2f;
		public float damping = 0.1f;
		public float gravity = 0f;

		public int draggingJoint = -1;
		public float dragZ = 0f;

		List<(Vector3, Vector3)> skeletonBones;
		List<Vector3> joints;
		List<Vector2Int> boneIndices;
		List<float> restLengths;
		List<Vector3> restLocalPositions;
		List<Vector3> worldJoints;
		List<Vector3> prevWorldJoints;
		HarmonicSkinning skinning;

		[SerializeField] List<Color> colors;

		Rigidbody _body;
		MeshFilter _filter;
		MeshCollider _collider;

		void Start () {
			var rnd = GetComponent<MeshRenderer>();
			MaterialPropertyBlock block = new MaterialPropertyBlock();
			rnd.GetPropertyBlock(block);
			block.SetColor("_Color", colors[Random.Range(0, colors.Count)]);

			rnd.SetPropertyBlock(block);
		}

		void Update () {
			if (joints == null || skinning == null) return;
			
			if (enablePhysics) {
				float dt = Time.deltaTime;
				if (dt > 0.1f) dt = 0.1f;

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

					Vector3 targetWorld = transform.TransformPoint(restLocalPositions[i]);
					worldJoints[i] = Vector3.Lerp(nextPos, targetWorld, shapeStiffness);
				}

				if (restLengths != null && boneIndices != null) {
					int iterations = 30; // lower for performance on multiple puppets
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

				for(int i = 0; i < joints.Count; i++) {
					joints[i] = transform.InverseTransformPoint(worldJoints[i]);
				}

				var currentBones = new List<(Vector3, Vector3)>();
				foreach (var bi in boneIndices) {
					currentBones.Add((joints[bi.x], joints[bi.y]));
				}
				skeletonBones = currentBones;
				
				Mesh mesh = filter.sharedMesh;
				mesh.vertices = skinning.Deform(skeletonBones);
				mesh.RecalculateNormals();
				mesh.RecalculateBounds();
			} else {
				for(int i=0; i<joints.Count; i++) {
					if (i != draggingJoint) worldJoints[i] = transform.TransformPoint(restLocalPositions[i]);
					joints[i] = restLocalPositions[i];
				}
			}
		}

		public bool TryPickJoint(Camera cam, Vector2 mousePos, float pixelRadius, out int jointIndex) {
			jointIndex = -1;
			if (joints == null || joints.Count == 0) return false;

			float minDist = pixelRadius;
			for (int i = 0; i < joints.Count; i++) {
				Vector3 screenPos = cam.WorldToScreenPoint(transform.TransformPoint(joints[i]));
				float dist = Vector2.Distance(mousePos, new Vector2(screenPos.x, screenPos.y));
				if (dist < minDist) {
					minDist = dist;
					jointIndex = i;
					dragZ = screenPos.z;
				}
			}
			return jointIndex != -1;
		}

		public void MoveJoint(Vector3 mp) {
			if (draggingJoint != -1 && worldJoints != null) {
				worldJoints[draggingJoint] = mp;
			}
		}

		public void Ignore () {
			col.enabled = false;
		}

		public void Select () {
			body.isKinematic = true;
		}

		public void Unselect () {
			body.isKinematic = false;
		}

		public void SetupSkeleton(List<(Vector3, Vector3)> bones) {
			skeletonBones = SimplifyBones(bones);
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

			skinning = new HarmonicSkinning(filter.sharedMesh.vertices, filter.sharedMesh.triangles, skeletonBones);
		}

		public void SetMesh (Mesh mesh) {
			body.mass = mesh.bounds.size.magnitude;
			filter.sharedMesh = mesh;

			if(mesh.triangles.Length > 255 * 3) {
				var oVertices = mesh.vertices.ToList();
				var oTriangles = mesh.triangles.ToList();
				int count = oTriangles.Count / 3;

				var vertices = new List<Vector3>();
				var triangles = new List<int>();

				for(int i = 0; i < 85; i++) {
					int idx = Random.Range(0, count) * 3;
					int a = oTriangles[idx], b = oTriangles[idx + 1], c = oTriangles[idx + 2];
					int vCount = vertices.Count;
					vertices.Add(oVertices[a]); vertices.Add(oVertices[b]); vertices.Add(oVertices[c]);
					triangles.Add(vCount); triangles.Add(vCount + 1); triangles.Add(vCount + 2);

					oTriangles.RemoveAt(idx + 2);
					oTriangles.RemoveAt(idx + 1);
					oTriangles.RemoveAt(idx);
					count -= 3;
				}	

				var colliderMesh = new Mesh();
				colliderMesh.vertices = vertices.ToArray();
				colliderMesh.SetTriangles(triangles.ToArray(), 0);
				col.sharedMesh = colliderMesh;
			} else {
				col.sharedMesh = mesh;
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

		static Material lineMaterial;
		static void CreateLineMaterial() {
			if (!lineMaterial) {
				Shader shader = Shader.Find("Hidden/Internal-Colored");
				lineMaterial = new Material(shader);
				lineMaterial.hideFlags = HideFlags.HideAndDontSave;
				lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
				lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
				lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
				lineMaterial.SetInt("_ZWrite", 0);
			}
		}

		void OnGUI() {
			if (Event.current.type != EventType.Repaint || !showSkeleton || skeletonBones == null) return;
			var cam = Camera.main;
			if (cam == null) return;

			CreateLineMaterial();
			lineMaterial.SetPass(0);

			GL.PushMatrix();
			GL.LoadPixelMatrix();

			GL.Begin(GL.LINES);
			GL.Color(skeletonColor);
			foreach (var (start, end) in skeletonBones) {
				Vector3 ws = cam.WorldToScreenPoint(transform.TransformPoint(start));
				Vector3 we = cam.WorldToScreenPoint(transform.TransformPoint(end));
				if (ws.z < 0f || we.z < 0f) continue;
				GL.Vertex3(ws.x, Screen.height - ws.y, 0f);
				GL.Vertex3(we.x, Screen.height - we.y, 0f);
			}
			GL.End();

			GL.Begin(GL.TRIANGLES);
			GL.Color(skeletonColor);
			if (joints != null) {
				for (int i = 0; i < joints.Count; i++) {
					Vector3 ws = cam.WorldToScreenPoint(transform.TransformPoint(joints[i]));
					if (ws.z < 0f) continue;
					
					if (i == draggingJoint) {
						GL.End();
						lineMaterial.SetColor("_Color", Color.white);
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
						DrawFilledDisc(ws.x, Screen.height - ws.y, jointRadius * 1.5f, 16);
						GL.End();
						lineMaterial.SetColor("_Color", skeletonColor);
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
					} else {
						DrawFilledDisc(ws.x, Screen.height - ws.y, jointRadius, 16);
					}
				}
			}
			GL.End();
			GL.PopMatrix();
		}

		void DrawFilledDisc(float cx, float cy, float r, int segments) {
			float step = 2f * Mathf.PI / segments;
			for (int i = 0; i < segments; i++) {
				float a0 = i * step;
				float a1 = (i + 1) * step;
				GL.Vertex3(cx, cy, 0f);
				GL.Vertex3(cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, 0f);
				GL.Vertex3(cx + Mathf.Cos(a1) * r, cy + Mathf.Sin(a1) * r, 0f);
			}
		}

	}


}

