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
		public float jointRadius = 2f;

		public bool enablePhysics = true;
		public float shapeStiffness = 0.2f;
		public float damping = 0.1f;
		public float gravity = 0f;

		public int draggingJoint = -1;
		public float dragZ = 0f;
		public int rigEditSelectedJoint = -1; // highlighted yellow in Rig Edit mode

		List<(Vector3, Vector3)> skeletonBones;
		List<Vector3> joints;
		List<Vector2Int> boneIndices;
		List<float> restLengths;
		List<Vector3> restLocalPositions;
		List<Vector3> worldJoints;
		List<Vector3> prevWorldJoints;
		HarmonicSkinning skinning;

		// Lasso Joints: indices of joints that are pinned (non-interactable)
		HashSet<int> pinnedJoints   = new HashSet<int>();
		// Joints that have been explicitly included by at least one lasso draw.
		// A joint is pinned only if it has NEVER been inside any lasso.
		HashSet<int> lassoIncluded  = new HashSet<int>();
		// Per-region physics. Each ApplyLasso() call creates a new region.
		int[]        jointRegion;           // -1 = no region (uses global params)
		List<float>  regionStiffness = new List<float>();
		List<float>  regionDamping   = new List<float>();

		[SerializeField] List<Color> colors;

		Rigidbody _body;
		MeshFilter _filter;
		MeshCollider _collider;

		bool isColorInitialized = false;
		Color puppetColor;
		public float textureUserScale = 1f;
		public Texture2D mainTexture;

		public void InitColor() {
			if (isColorInitialized) return;
			isColorInitialized = true;

			var rnd = GetComponent<MeshRenderer>();
			MaterialPropertyBlock block = new MaterialPropertyBlock();
			rnd.GetPropertyBlock(block);
			puppetColor = colors[Random.Range(0, colors.Count)];
			block.SetColor("_Color", puppetColor);
			rnd.SetPropertyBlock(block);
		}

		void Start () {
			InitColor();
		}

		public void ApplyTextureFront(Texture2D tex, float userScale, int w, int h) {
			InitColor();
			mainTexture = tex;
			textureUserScale = userScale;

			tex.SetPixel(0, 0, puppetColor);
			tex.Apply();

			Mesh mesh = filter.sharedMesh;
			Vector2[] uvs = new Vector2[mesh.vertexCount];
			float maxDim = Mathf.Max(w, h);

			for (int i = 0; i < mesh.vertexCount; i++) {
				Vector3 v = mesh.vertices[i];
				Vector3 n = mesh.normals[i];

				if (n.z < -0.1f) {
					float Vx = v.x / userScale;
					float Vy = v.y / userScale;
					float u = (Vx * maxDim + w * 0.5f) / w;
					float v_uv = (Vy * maxDim + h * 0.5f) / h;
					uvs[i] = new Vector2(u, v_uv);
				} else {
					uvs[i] = new Vector2(0f, 0f);
				}
			}
			mesh.uv = uvs;

			var rnd = GetComponent<MeshRenderer>();
			MaterialPropertyBlock block = new MaterialPropertyBlock();
			rnd.GetPropertyBlock(block);
			block.SetTexture("_MainTex", tex);
			block.SetColor("_Color", Color.white);
			rnd.SetPropertyBlock(block);
		}

		void Update () {
			if (joints == null || skinning == null) return;
			
			if (enablePhysics) {
				float dt = Time.deltaTime;
				if (dt > 0.1f) dt = 0.1f;

				for(int i = 0; i < worldJoints.Count; i++) {
					// Pinned joints are frozen at their rest position – skip all dynamics
					if (pinnedJoints.Contains(i)) {
						worldJoints[i] = transform.TransformPoint(restLocalPositions[i]);
						prevWorldJoints[i] = worldJoints[i];
						continue;
					}

					if (i == draggingJoint) {
						prevWorldJoints[i] = worldJoints[i];
						continue;
					}

					float jDamp  = (jointRegion != null && i < jointRegion.Length && jointRegion[i] >= 0)
						? regionDamping[jointRegion[i]] : damping;
					float jStiff = (jointRegion != null && i < jointRegion.Length && jointRegion[i] >= 0)
						? regionStiffness[jointRegion[i]] : shapeStiffness;

					Vector3 vel = (worldJoints[i] - prevWorldJoints[i]) / dt;
					vel.y -= gravity * dt;
					vel *= (1f - jDamp);

					prevWorldJoints[i] = worldJoints[i];
					Vector3 nextPos = worldJoints[i] + vel * dt;

					Vector3 targetWorld = transform.TransformPoint(restLocalPositions[i]);
					worldJoints[i] = Vector3.Lerp(nextPos, targetWorld, jStiff);
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
								bool pin0c = pinnedJoints.Contains(i0);
								bool pin1c = pinnedJoints.Contains(i1);
								if (pin0c & pin1c) {
									// both anchored - nothing moves
								} else if (pin0c) {
									worldJoints[i1] -= offset * 2f;
								} else if (pin1c) {
									worldJoints[i0] += offset * 2f;
								} else {
									worldJoints[i0] += offset;
									worldJoints[i1] -= offset;
								}
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

		public int lastClickX = -1;
		public int lastClickY = -1;
		public Color32[] backupPixels;
		public Color32[] appliedPixels;
		public Color32[] currentWorkingPixels;
		public List<int> previewRegionIndices;
		public List<int> boundaryIndices;
		public bool isPreviewingColor = false;

		public void StartEditMode() {
			if (mainTexture != null) {
				backupPixels = mainTexture.GetPixels32();
				appliedPixels = mainTexture.GetPixels32();
				currentWorkingPixels = mainTexture.GetPixels32();
			}
			previewRegionIndices = new List<int>();
			lastClickX = -1; lastClickY = -1;
		}

		public void CancelEditMode() {
			if (mainTexture != null && backupPixels != null) {
				mainTexture.SetPixels32(backupPixels);
				mainTexture.Apply();
				backupPixels = null;
				appliedPixels = null;
				currentWorkingPixels = null;
				previewRegionIndices = null;
				boundaryIndices = null;
				isPreviewingColor = false;
			}
		}

		public void ResetEditMode() {
			if (mainTexture != null && backupPixels != null) {
				Array.Copy(backupPixels, appliedPixels, backupPixels.Length);
				Array.Copy(backupPixels, currentWorkingPixels, backupPixels.Length);
				mainTexture.SetPixels32(currentWorkingPixels);
				mainTexture.Apply();
				if (previewRegionIndices != null) previewRegionIndices.Clear();
				if (boundaryIndices != null) boundaryIndices.Clear();
				isPreviewingColor = false;
				lastClickX = -1; lastClickY = -1;
			}
		}

		public void ApplyColorToLastClick(Color32 newColor) { 
			if (mainTexture == null || previewRegionIndices == null || previewRegionIndices.Count == 0) return;
			// Commit only the filled color, NOT the orange boundary outline!
			foreach (int idx in previewRegionIndices) {
				appliedPixels[idx] = newColor; // This ignores the orange boundary if it was in currentWorkingPixels
			}
			Array.Copy(appliedPixels, currentWorkingPixels, currentWorkingPixels.Length);
			mainTexture.SetPixels32(currentWorkingPixels);
			mainTexture.Apply();
			
			previewRegionIndices.Clear();
			if (boundaryIndices != null) boundaryIndices.Clear();
			isPreviewingColor = false;
			lastClickX = -1; lastClickY = -1;
		}

		public void UpdatePreview(Color32 newColor) {
			if (mainTexture == null || previewRegionIndices == null || previewRegionIndices.Count == 0 || currentWorkingPixels == null) return;
			
			foreach(int idx in previewRegionIndices) {
				currentWorkingPixels[idx] = isPreviewingColor ? newColor : appliedPixels[idx];
			}
			
			if (!isPreviewingColor && boundaryIndices != null) {
				Color32 orangeBorder = new Color32(255, 140, 0, 255);
				foreach(int idx in boundaryIndices) {
					currentWorkingPixels[idx] = orangeBorder;
				}
			}
			
			mainTexture.SetPixels32(currentWorkingPixels);
			mainTexture.Apply();
		}

		public void OnTextureClicked(RaycastHit hit, Color32 currentColor) {
			if (mainTexture == null) return;

			Vector3 v = transform.InverseTransformPoint(hit.point);
			int w = mainTexture.width;
			int h = mainTexture.height;
			float maxDim = Mathf.Max(w, h);
			
			float Vx = v.x / textureUserScale;
			float Vy = v.y / textureUserScale;
			float u = (Vx * maxDim + w * 0.5f) / w;
			float v_uv = (Vy * maxDim + h * 0.5f) / h;
			
			int px = Mathf.FloorToInt(u * w);
			int py = Mathf.FloorToInt(v_uv * h);
			
			px = Mathf.Clamp(px, 0, w - 1);
			py = Mathf.Clamp(py, 0, h - 1);
			
			lastClickX = px;
			lastClickY = py;

			if (appliedPixels != null && currentWorkingPixels != null) {
				Array.Copy(appliedPixels, currentWorkingPixels, appliedPixels.Length);
			}

			int targetIndex = py * w + px;
			Color32 targetColor = currentWorkingPixels[targetIndex];
			string hex = ColorUtility.ToHtmlStringRGBA(targetColor);
			
			if (previewRegionIndices == null) previewRegionIndices = new List<int>();
			previewRegionIndices.Clear();

			bool[] visited = new bool[w * h];
			Queue<Vector2Int> q = new Queue<Vector2Int>();
			q.Enqueue(new Vector2Int(px, py));
			visited[targetIndex] = true;
			previewRegionIndices.Add(targetIndex);
			
			while (q.Count > 0) {
				Vector2Int p = q.Dequeue();
				
				Vector2Int[] dirs = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
				foreach (var d in dirs) {
					int nx = p.x + d.x;
					int ny = p.y + d.y;
					if (nx >= 0 && nx < w && ny >= 0 && ny < h) {
						int nIndex = ny * w + nx;
						if (!visited[nIndex]) {
							Color32 c = currentWorkingPixels[nIndex];
							int diffR = Mathf.Abs(c.r - targetColor.r);
							int diffG = Mathf.Abs(c.g - targetColor.g);
							int diffB = Mathf.Abs(c.b - targetColor.b);
							int diffA = Mathf.Abs(c.a - targetColor.a);
							
							if (diffR <= 10 && diffG <= 10 && diffB <= 10 && diffA <= 10) {
								visited[nIndex] = true;
								previewRegionIndices.Add(nIndex);
								q.Enqueue(new Vector2Int(nx, ny));
							}
						}
					}
				}
			}
			
			boundaryIndices = new List<int>();
			HashSet<int> regionSet = new HashSet<int>(previewRegionIndices);
			foreach(int idx in previewRegionIndices) {
				int x = idx % w;
				int y = idx / w;
				bool isBoundary = false;
				
				if (x > 0 && !regionSet.Contains(y * w + (x - 1))) isBoundary = true;
				if (x < w - 1 && !regionSet.Contains(y * w + (x + 1))) isBoundary = true;
				if (y > 0 && !regionSet.Contains((y - 1) * w + x)) isBoundary = true;
				if (y < h - 1 && !regionSet.Contains((y + 1) * w + x)) isBoundary = true;
				if (x == 0 || x == w - 1 || y == 0 || y == h - 1) isBoundary = true;
				
				if (isBoundary) boundaryIndices.Add(idx);
			}

			Debug.Log($"Hex: #{hex}, Same Color Area Count: {previewRegionIndices.Count}");
			isPreviewingColor = false;
			UpdatePreview(currentColor);
		}

		public bool TryPickJoint(Camera cam, Vector2 mousePos, float pixelRadius, out int jointIndex) {
			jointIndex = -1;
			if (joints == null || joints.Count == 0) return false;

			float minDist = pixelRadius;
			for (int i = 0; i < joints.Count; i++) {
				// Skip pinned (gray) joints – they cannot be dragged
				if (pinnedJoints.Contains(i)) continue;

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

		/// <summary>
		/// Pick the nearest joint regardless of pinned state (for Rig Edit mode).
		/// </summary>
		public bool TryPickJointAny(Camera cam, Vector3 mousePos, float pixelRadius, out int jointIndex) {
			jointIndex = -1;
			if (joints == null || joints.Count == 0) return false;
			float minDist = pixelRadius;
			for (int i = 0; i < joints.Count; i++) {
				Vector3 screenPos = cam.WorldToScreenPoint(transform.TransformPoint(joints[i]));
				float dist = Vector2.Distance(mousePos, new Vector2(screenPos.x, screenPos.y));
				if (dist < minDist) { minDist = dist; jointIndex = i; dragZ = screenPos.z; }
			}
			return jointIndex != -1;
		}

		/// <summary>
		/// Remove a joint, auto-bridging its neighbours so the chain stays
		/// connected, then rebuild skinning.
		/// </summary>
		public void RemoveJoint(int idx) {
			if (joints == null || idx < 0 || idx >= joints.Count) return;

			// 1. Collect neighbour indices connected through this joint
			var neighbours = new List<int>();
			foreach (var bi in boneIndices) {
				if (bi.x == idx && !neighbours.Contains(bi.y)) neighbours.Add(bi.y);
				if (bi.y == idx && !neighbours.Contains(bi.x)) neighbours.Add(bi.x);
			}

			// 2. Remove bones touching this joint
			for (int i = boneIndices.Count - 1; i >= 0; i--) {
				if (boneIndices[i].x == idx || boneIndices[i].y == idx) {
					boneIndices.RemoveAt(i);
					restLengths.RemoveAt(i);
				}
			}

			// 3. Remap neighbour indices that shift after removal
			for (int i = 0; i < neighbours.Count; i++)
				if (neighbours[i] > idx) neighbours[i]--;

			// 4. Remap ALL remaining boneIndices > idx down by 1
			for (int i = 0; i < boneIndices.Count; i++) {
				int x = boneIndices[i].x > idx ? boneIndices[i].x - 1 : boneIndices[i].x;
				int y = boneIndices[i].y > idx ? boneIndices[i].y - 1 : boneIndices[i].y;
				boneIndices[i] = new Vector2Int(x, y);
			}

			// 5. Remove the joint data
			joints.RemoveAt(idx);
			restLocalPositions.RemoveAt(idx);
			worldJoints.RemoveAt(idx);
			prevWorldJoints.RemoveAt(idx);

			// 6. Rebuild jointRegion (simple sequential copy, skip removed index)
			if (jointRegion != null) {
				var newRegion = new int[joints.Count];
				int dst = 0;
				for (int i = 0; i < jointRegion.Length && dst < newRegion.Length; i++) {
					if (i == idx) continue;
					newRegion[dst++] = jointRegion[i];
				}
				jointRegion = newRegion;
			}

			// 7. Remap pinnedJoints
			pinnedJoints.RemoveWhere(j => j == idx);
			var newPinned = new HashSet<int>();
			foreach (var j in pinnedJoints) newPinned.Add(j > idx ? j - 1 : j);
			pinnedJoints = newPinned;

			draggingJoint = -1;
			rigEditSelectedJoint = -1;

			// 8. Bridge neighbours: connect each pair so the chain stays intact
			for (int a = 0; a < neighbours.Count; a++) {
				for (int b = a + 1; b < neighbours.Count; b++) {
					int na = neighbours[a], nb = neighbours[b];
					if (na < 0 || nb < 0 || na >= joints.Count || nb >= joints.Count) continue;
					bool exists = false;
					foreach (var bi in boneIndices)
						if ((bi.x == na && bi.y == nb) || (bi.x == nb && bi.y == na)) { exists = true; break; }
					if (!exists) {
						boneIndices.Add(new Vector2Int(na, nb));
						restLengths.Add(Vector3.Distance(joints[na], joints[nb]));
					}
				}
			}

			RebuildSkeletonBones();
		}

		/// <summary>
		/// Add a bone (edge) between two existing joints and rebuild skinning.
		/// </summary>
		public void AddBone(int i0, int i1) {
			if (joints == null || i0 < 0 || i1 < 0 || i0 >= joints.Count || i1 >= joints.Count || i0 == i1) return;
			// Check if bone already exists
			foreach (var bi in boneIndices)
				if ((bi.x == i0 && bi.y == i1) || (bi.x == i1 && bi.y == i0)) return;
			boneIndices.Add(new Vector2Int(i0, i1));
			restLengths.Add(Vector3.Distance(joints[i0], joints[i1]));
			RebuildSkeletonBones();
		}

		/// <summary>
		/// Sync skeletonBones list from boneIndices+joints, then rebuild HarmonicSkinning.
		/// </summary>
		void RebuildSkeletonBones() {
			skeletonBones = new List<(Vector3, Vector3)>();
			foreach (var bi in boneIndices)
				skeletonBones.Add((joints[bi.x], joints[bi.y]));
			if (filter.sharedMesh != null)
				skinning = new HarmonicSkinning(filter.sharedMesh.vertices, filter.sharedMesh.triangles, skeletonBones);
		}

		/// <summary>Returns the joint local-space position, or Vector3.zero if invalid.</summary>
		public Vector3 GetJointLocalPos(int idx) {
			if (joints == null || idx < 0 || idx >= joints.Count) return Vector3.zero;
			return joints[idx];
		}

		/// <summary>Total number of joints.</summary>
		public int JointCount => joints == null ? 0 : joints.Count;

		public void Ignore () {
			col.enabled = false;
		}

		public void Select () {
			body.isKinematic = true;
		}

		public void Unselect () {
			body.isKinematic = false;
		}

		/// <summary>
		/// Apply a screen-space lasso polygon. Returns the index of the new region created.
		/// Joints inside → assigned to this region (interactable, red).
		/// Joints outside AND not in any prior region → pinned (gray, frozen).
		/// Physics params default to global values; caller can update via SetRegionParams().
		/// </summary>
		public int ApplyLasso(Camera cam, List<Vector2> lassoGUI) {
			if (joints == null) return -1;
			// Create a fresh region with the current global defaults
			int newRegion = regionStiffness.Count;
			regionStiffness.Add(shapeStiffness);
			regionDamping.Add(damping);

			for (int i = 0; i < joints.Count; i++) {
				Vector3 screenPos = cam.WorldToScreenPoint(transform.TransformPoint(joints[i]));
				Vector2 guiPos = new Vector2(screenPos.x, Screen.height - screenPos.y);
				if (PointInPolygon(guiPos, lassoGUI)) {
					// Inside: mark included, unpin, assign to new region
					lassoIncluded.Add(i);
					pinnedJoints.Remove(i);
					if (jointRegion != null) jointRegion[i] = newRegion;
				} else if (!lassoIncluded.Contains(i)) {
					// Outside and never freed: pin
					pinnedJoints.Add(i);
					if (jointRegion != null) jointRegion[i] = -1;
				}
				// else: already in a prior region — leave untouched
			}
			return newRegion;
		}

		/// <summary>Returns the region index the joint belongs to (-1 = no region / global params).</summary>
		public int GetJointRegion(int jointIndex) {
			if (jointRegion == null || jointIndex < 0 || jointIndex >= jointRegion.Length) return -1;
			return jointRegion[jointIndex];
		}

		/// <summary>Returns stiffness and damping for a given region (falls back to global if invalid).</summary>
		public (float stiffness, float damping) GetRegionParams(int region) {
			if (region < 0 || region >= regionStiffness.Count)
				return (shapeStiffness, damping);
			return (regionStiffness[region], regionDamping[region]);
		}

		/// <summary>Update physics params for an existing region.</summary>
		public void SetRegionParams(int region, float stiffness, float damp) {
			if (region < 0 || region >= regionStiffness.Count) return;
			regionStiffness[region] = stiffness;
			regionDamping[region]   = damp;
		}

		/// <summary>Unpin all joints, clear all regions and lasso history.</summary>
		public void ResetLasso() {
			pinnedJoints.Clear();
			lassoIncluded.Clear();
			regionStiffness.Clear();
			regionDamping.Clear();
			if (jointRegion != null)
				for (int i = 0; i < jointRegion.Length; i++) jointRegion[i] = -1;
		}

		/// <summary>Even-odd ray cast point-in-polygon test (2-D screen space).</summary>
		static bool PointInPolygon(Vector2 point, List<Vector2> polygon) {
			int n = polygon.Count;
			bool inside = false;
			for (int i = 0, j = n - 1; i < n; j = i++) {
				Vector2 vi = polygon[i];
				Vector2 vj = polygon[j];
				if (((vi.y > point.y) != (vj.y > point.y)) &&
					(point.x < (vj.x - vi.x) * (point.y - vi.y) / (vj.y - vi.y) + vi.x)) {
					inside = !inside;
				}
			}
			return inside;
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

			// Initialise per-joint region tracking (all -1 = global params)
			jointRegion = new int[joints.Count];
			for (int i = 0; i < joints.Count; i++) jointRegion[i] = -1;
			regionStiffness.Clear();
			regionDamping.Clear();

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

					bool isPinned = pinnedJoints.Contains(i);

					if (i == rigEditSelectedJoint && i != draggingJoint) {
						// Yellow highlight for selected joint in Rig Edit mode
						GL.End();
						lineMaterial.SetColor("_Color", Color.yellow);
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
						DrawFilledDisc(ws.x, Screen.height - ws.y, jointRadius * 1.8f, 16);
						GL.End();
						lineMaterial.SetColor("_Color", skeletonColor);
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
					} else if (i == draggingJoint) {
						GL.End();
						lineMaterial.SetColor("_Color", Color.white);
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
						DrawFilledDisc(ws.x, Screen.height - ws.y, jointRadius * 1.5f, 16);
						GL.End();
						lineMaterial.SetColor("_Color", skeletonColor);
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
					} else if (isPinned) {
						// Gray pinned joint
						GL.End();
						lineMaterial.SetColor("_Color", new Color(0.45f, 0.45f, 0.45f, 1f));
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
						DrawFilledDisc(ws.x, Screen.height - ws.y, jointRadius, 16);
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