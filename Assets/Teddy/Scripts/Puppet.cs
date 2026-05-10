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

		// --- Animation Mode ---
		public bool isAnimationMode = false;
		public Dictionary<int, List<Vector3>> recordedMotions = new Dictionary<int, List<Vector3>>();
		public bool isAnimationPlaying = false;
		public class BluePoint {
			public int v0, v1, v2;
			public Vector3 barycentric;
			public int attachedJoint;
		}
		public List<BluePoint> bluePoints = new List<BluePoint>();
		public int animSelectedBluePoint = -1;
		public Dictionary<int, List<Vector3>> blueRecordedMotions = new Dictionary<int, List<Vector3>>();

		public int animMaxFrames = 0;
		public int animCurrentFrame = 0;
		public int animSelectedJoint = -1;
		public bool isRecordingAnim = false;

		public Vector3 GetBluePointWorldPos(int bIdx) {
			if (bIdx < 0 || bIdx >= bluePoints.Count) return Vector3.zero;
			BluePoint bp = bluePoints[bIdx];
			Vector3[] verts = GetComponent<MeshFilter>().sharedMesh.vertices;
			Vector3 localPos = bp.barycentric.x * verts[bp.v0] + bp.barycentric.y * verts[bp.v1] + bp.barycentric.z * verts[bp.v2];
			return transform.TransformPoint(localPos);
		}

		public int CreateBluePoint(RaycastHit hit) {
			BluePoint bp = new BluePoint();
			Vector3 worldHit = hit.point;
			Vector3 localHit = transform.InverseTransformPoint(worldHit);
			
			int[] triangles = GetComponent<MeshFilter>().sharedMesh.triangles;
			Vector3[] verts = GetComponent<MeshFilter>().sharedMesh.vertices;
			
			bool found = false;
			for (int i=0; i<triangles.Length; i+=3) {
				int t0 = triangles[i];
				int t1 = triangles[i+1];
				int t2 = triangles[i+2];
				Vector3 p0 = verts[t0];
				Vector3 p1 = verts[t1];
				Vector3 p2 = verts[t2];
				
				Vector2 v0 = new Vector2(p1.x - p0.x, p1.y - p0.y);
				Vector2 v1 = new Vector2(p2.x - p0.x, p2.y - p0.y);
				Vector2 v2 = new Vector2(localHit.x - p0.x, localHit.y - p0.y);
				
				float d00 = Vector2.Dot(v0, v0);
				float d01 = Vector2.Dot(v0, v1);
				float d11 = Vector2.Dot(v1, v1);
				float d20 = Vector2.Dot(v2, v0);
				float d21 = Vector2.Dot(v2, v1);
				
				float denom = d00 * d11 - d01 * d01;
				if (Mathf.Abs(denom) < 1e-6f) continue;
				
				float v = (d11 * d20 - d01 * d21) / denom;
				float w = (d00 * d21 - d01 * d20) / denom;
				float u = 1.0f - v - w;
				
				if (u >= -0.01f && v >= -0.01f && w >= -0.01f) {
					bp.v0 = t0; bp.v1 = t1; bp.v2 = t2;
					u = Mathf.Clamp01(u); v = Mathf.Clamp01(v); w = Mathf.Clamp01(w);
					float sum = u + v + w;
					bp.barycentric = new Vector3(u/sum, v/sum, w/sum);
					found = true;
					break;
				}
			}
			
			if (!found) {
				int bestV = 0; float bestVDist = float.MaxValue;
				for (int i=0; i<verts.Length; i++) {
					float d = Vector2.Distance(new Vector2(verts[i].x, verts[i].y), new Vector2(localHit.x, localHit.y));
					if (d < bestVDist) { bestVDist = d; bestV = i; }
				}
				bp.v0 = bestV; bp.v1 = bestV; bp.v2 = bestV;
				bp.barycentric = new Vector3(1, 0, 0);
			}
			
			int bestJ = 0; float bestJDist = float.MaxValue;
			for (int i=0; i<worldJoints.Count; i++) {
				float d = Vector3.Distance(worldJoints[i], worldHit);
				if (d < bestJDist) { bestJDist = d; bestJ = i; }
			}
			bp.attachedJoint = bestJ;
			
			bluePoints.Add(bp);
			dragOffsetWorld = worldHit - worldJoints[bestJ];
			return bluePoints.Count - 1;
		}

		public bool TryPickBluePoint(Camera cam, Vector3 screenPos, float thresholdRadius, out int pickedIndex) {
			pickedIndex = -1;
			float minDist = float.MaxValue;
			for (int i = 0; i < bluePoints.Count; i++) {
				Vector3 wPos = GetBluePointWorldPos(i);
				Vector3 sPos = cam.WorldToScreenPoint(wPos);
				if (sPos.z < 0f) continue;
				float dist = Vector2.Distance(new Vector2(screenPos.x, screenPos.y), new Vector2(sPos.x, sPos.y));
				if (dist <= thresholdRadius && dist < minDist) {
					minDist = dist;
					pickedIndex = i;
					dragZ = sPos.z;
				}
			}
			if (pickedIndex >= 0) {
				Vector3 mouseWorld = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, dragZ));
				dragOffsetWorld = mouseWorld - worldJoints[bluePoints[pickedIndex].attachedJoint];
			}
			return pickedIndex >= 0;
		}

		public void StartRecordingAnimation() {
			if (animSelectedJoint >= 0) {
				draggingJoint = animSelectedJoint;
				recordedMotions[animSelectedJoint] = new List<Vector3>();
			} else if (animSelectedBluePoint >= 0) {
				draggingJoint = bluePoints[animSelectedBluePoint].attachedJoint;
				recordedMotions[draggingJoint] = new List<Vector3>();
				blueRecordedMotions[animSelectedBluePoint] = new List<Vector3>();
			} else return;

			isAnimationPlaying = true;
			isRecordingAnim = true;
			if (animMaxFrames > 0) animCurrentFrame = 0;
		}

		public void StopRecordingAnimation() {
			if (!isRecordingAnim) return;
			isRecordingAnim = false;
			draggingJoint = -1;

			if (animSelectedJoint >= 0) {
				int picked = animSelectedJoint;
				if (animMaxFrames == 0) {
					animMaxFrames = recordedMotions[picked].Count;
				} else {
					if (recordedMotions[picked].Count == 0) {
						recordedMotions[picked].Add(worldJoints[picked]);
					}
					Vector3 lastPos = recordedMotions[picked].Last();
					while (recordedMotions[picked].Count < animMaxFrames) {
						recordedMotions[picked].Add(lastPos);
					}
				}
			} else if (animSelectedBluePoint >= 0) {
				int picked = bluePoints[animSelectedBluePoint].attachedJoint;
				int bIdx = animSelectedBluePoint;
				if (animMaxFrames == 0) {
					animMaxFrames = recordedMotions[picked].Count;
				} else {
					if (recordedMotions[picked].Count == 0) {
						recordedMotions[picked].Add(worldJoints[picked]);
						blueRecordedMotions[bIdx].Add(GetBluePointWorldPos(bIdx));
					}
					Vector3 lastPos = recordedMotions[picked].Last();
					Vector3 lastBP = blueRecordedMotions[bIdx].Last();
					while (recordedMotions[picked].Count < animMaxFrames) {
						recordedMotions[picked].Add(lastPos);
						blueRecordedMotions[bIdx].Add(lastBP);
					}
				}
			}
		}

		public void ClearAnimation() {
			recordedMotions.Clear();
			blueRecordedMotions.Clear();
			bluePoints.Clear();
			isAnimationPlaying = false;
			animMaxFrames = 0;
			animCurrentFrame = 0;
			animSelectedJoint = -1;
			animSelectedBluePoint = -1;
			isRecordingAnim = false;
		}

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
			
			puppetColor = (colors != null && colors.Count > 0) ? colors[Random.Range(0, colors.Count)] : Color.white;
			
			// Only set color if the shader has a _Color property
			if (rnd.sharedMaterial != null && rnd.sharedMaterial.HasProperty("_Color")) {
				block.SetColor("_Color", puppetColor);
			}
			
			// Sync existing material properties to the block so they stay adjustable
			if (rnd.sharedMaterial != null) {
				if(rnd.sharedMaterial.HasProperty("_MainTex_ST")) block.SetVector("_MainTex_ST", rnd.sharedMaterial.GetVector("_MainTex_ST"));
				if(rnd.sharedMaterial.HasProperty("_DisplacementParams")) block.SetVector("_DisplacementParams", rnd.sharedMaterial.GetVector("_DisplacementParams"));
				if(rnd.sharedMaterial.HasProperty("_ToonParams")) block.SetVector("_ToonParams", rnd.sharedMaterial.GetVector("_ToonParams"));
			}
			
			rnd.SetPropertyBlock(block);
		}

		public void SetColor(Color c) {
			puppetColor = c;
			var rnd = GetComponent<MeshRenderer>();
			MaterialPropertyBlock block = new MaterialPropertyBlock();
			rnd.GetPropertyBlock(block);
			block.SetColor("_Color", c);
			rnd.SetPropertyBlock(block);
		}

		void Start () {
			InitColor();
		}

		void UpdateDominantColor(Color32[] pixels, Texture2D tex) {
			Dictionary<int, int> counts = new Dictionary<int, int>();
			Color32 dominant = puppetColor;
			int maxCount = 0;
			for (int i = 1; i < pixels.Length; i++) { // skip 0
				Color32 c = pixels[i];
				if (c.a < 10) continue; // ignore transparent
				int key = (c.r << 24) | (c.g << 16) | (c.b << 8) | c.a;
				if (counts.ContainsKey(key)) {
					counts[key]++;
				} else {
					counts[key] = 1;
				}
				if (counts[key] > maxCount) {
					maxCount = counts[key];
					dominant = c;
				}
			}
			pixels[0] = dominant;
			if (tex != null) {
				tex.SetPixel(0, 0, dominant);
			}
		}

		public void ApplyTextureFront(Texture2D tex, float userScale, int w, int h) {
			InitColor();
			mainTexture = tex;
			textureUserScale = userScale;

			Color32[] initialPixels = tex.GetPixels32();
			UpdateDominantColor(initialPixels, tex);
			tex.Apply();

			Mesh mesh = filter.sharedMesh;
			Vector2[] uvs = new Vector2[mesh.vertexCount];
			float maxDim = Mathf.Max(w, h);

			for (int i = 0; i < mesh.vertexCount; i++) {
				Vector3 v = mesh.vertices[i];
				
				if (v.z <= 0.01f) {
					// Front and Seam
					float Vx = v.x / userScale;
					float Vy = v.y / userScale;
					float u = (Vx * maxDim + w * 0.5f) / w;
					float v_uv = (Vy * maxDim + h * 0.5f) / h;
					uvs[i] = new Vector2(u, v_uv);
				} else {
					// Back: mapped to (0,0) where the dominant color will be stored
					uvs[i] = new Vector2(0f, 0f);
				}
			}
			mesh.uv = uvs;

			var rnd = GetComponent<MeshRenderer>();
			MaterialPropertyBlock block = new MaterialPropertyBlock();
			rnd.GetPropertyBlock(block);
			block.SetTexture("_MainTex", tex);
			block.SetColor("_Color", Color.white);

			// Sync Tiling/Offset from material if it exists
			if (rnd.sharedMaterial != null && rnd.sharedMaterial.HasProperty("_MainTex_ST")) {
				block.SetVector("_MainTex_ST", rnd.sharedMaterial.GetVector("_MainTex_ST"));
			}

			rnd.SetPropertyBlock(block);
		}

		void Update () {
			if (joints == null || skinning == null) return;
			
			if (enablePhysics) {
				float dt = Time.deltaTime;
				if (dt > 0.1f) dt = 0.1f;

				if (isAnimationPlaying) {
					animCurrentFrame++;
					if (animMaxFrames > 0 && animCurrentFrame >= animMaxFrames) {
						animCurrentFrame = 0;
						if (isRecordingAnim) {
							StopRecordingAnimation();
						}
					}
				}

				if (isRecordingAnim) {
					if (animSelectedJoint >= 0) {
						recordedMotions[animSelectedJoint].Add(worldJoints[animSelectedJoint]);
					} else if (animSelectedBluePoint >= 0) {
						int jIdx = bluePoints[animSelectedBluePoint].attachedJoint;
						recordedMotions[jIdx].Add(worldJoints[jIdx]);
						blueRecordedMotions[animSelectedBluePoint].Add(GetBluePointWorldPos(animSelectedBluePoint));
					}
				}

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

					if (isAnimationPlaying && recordedMotions.ContainsKey(i) && (!isRecordingAnim || i != animSelectedJoint)) {
						int frame = animCurrentFrame;
						if (frame >= recordedMotions[i].Count) frame = recordedMotions[i].Count - 1;
						worldJoints[i] = recordedMotions[i][frame];
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
			
			UpdateDominantColor(appliedPixels, mainTexture);

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
			if (currentWorkingPixels == null || currentWorkingPixels.Length == 0) StartEditMode();

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

		public Vector3 dragOffsetWorld;

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
			if (jointIndex >= 0) {
				Vector3 mouseWorld = cam.ScreenToWorldPoint(new Vector3(mousePos.x, mousePos.y, dragZ));
				dragOffsetWorld = mouseWorld - worldJoints[jointIndex];
			}
			return jointIndex != -1;
		}

		public void MoveJoint(Vector3 targetWorld) {
			if (animSelectedJoint >= 0) {
				if (animSelectedJoint < worldJoints.Count) {
					worldJoints[animSelectedJoint] = targetWorld - dragOffsetWorld;
				}
			} else if (animSelectedBluePoint >= 0) {
				BluePoint bp = bluePoints[animSelectedBluePoint];
				worldJoints[bp.attachedJoint] = targetWorld - dragOffsetWorld;
			} else if (draggingJoint >= 0 && draggingJoint < joints.Count) {
				worldJoints[draggingJoint] = targetWorld - dragOffsetWorld;
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
			if (jointIndex >= 0) {
				Vector3 mouseWorld = cam.ScreenToWorldPoint(new Vector3(mousePos.x, mousePos.y, dragZ));
				dragOffsetWorld = mouseWorld - worldJoints[jointIndex];
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
			
			// Generate default Planar UVs if missing or empty
			if (mesh.uv == null || mesh.uv.Length == 0) {
				Vector3[] vertices = mesh.vertices;
				Vector2[] uvs = new Vector2[vertices.Length];
				Bounds bounds = mesh.bounds;
				for (int i = 0; i < vertices.Length; i++) {
					// Map X/Y coordinates to 0-1 range based on bounds
					float u = (vertices[i].x - bounds.min.x) / bounds.size.x;
					float v = (vertices[i].y - bounds.min.y) / bounds.size.y;
					uvs[i] = new Vector2(u, v);
				}
				mesh.uv = uvs;
			}

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
			if (!isAnimationMode) {
				foreach (var (start, end) in skeletonBones) {
					Vector3 ws = cam.WorldToScreenPoint(transform.TransformPoint(start));
					Vector3 we = cam.WorldToScreenPoint(transform.TransformPoint(end));
					if (ws.z < 0f || we.z < 0f) continue;
					GL.Vertex3(ws.x, Screen.height - ws.y, 0f);
					GL.Vertex3(we.x, Screen.height - we.y, 0f);
				}
			}
			
			// Draw Animation Paths
			if (isAnimationMode && recordedMotions != null && recordedMotions.Count > 0) {
				GL.End();
				lineMaterial.SetColor("_Color", new Color(0.2f, 0.2f, 0.2f, 0.8f)); // Dark gray curves
				lineMaterial.SetPass(0);
				GL.Begin(GL.LINES);
				foreach (var kvp in recordedMotions) {
					var path = kvp.Value;
					for (int i = 0; i < path.Count - 1; i++) {
						Vector3 p0 = cam.WorldToScreenPoint(path[i]);
						Vector3 p1 = cam.WorldToScreenPoint(path[i + 1]);
						if (p0.z > 0f && p1.z > 0f) {
							GL.Vertex3(p0.x, Screen.height - p0.y, 0f);
							GL.Vertex3(p1.x, Screen.height - p1.y, 0f);
						}
					}
					if (path.Count > 1 && animMaxFrames > 0 && (!isRecordingAnim || kvp.Key != animSelectedJoint)) {
						Vector3 p0 = cam.WorldToScreenPoint(path[path.Count - 1]);
						Vector3 p1 = cam.WorldToScreenPoint(path[0]);
						if (p0.z > 0f && p1.z > 0f) {
							GL.Vertex3(p0.x, Screen.height - p0.y, 0f);
							GL.Vertex3(p1.x, Screen.height - p1.y, 0f);
						}
					}
				}
			}
			
			// Draw Blue Animation Paths
			if (isAnimationMode && blueRecordedMotions != null && blueRecordedMotions.Count > 0) {
				GL.End();
				lineMaterial.SetColor("_Color", new Color(0.2f, 0.2f, 0.8f, 0.8f)); // Dark blue curves
				lineMaterial.SetPass(0);
				GL.Begin(GL.LINES);
				foreach (var kvp in blueRecordedMotions) {
					var path = kvp.Value;
					for (int i = 0; i < path.Count - 1; i++) {
						Vector3 p0 = cam.WorldToScreenPoint(path[i]);
						Vector3 p1 = cam.WorldToScreenPoint(path[i + 1]);
						if (p0.z > 0f && p1.z > 0f) {
							GL.Vertex3(p0.x, Screen.height - p0.y, 0f);
							GL.Vertex3(p1.x, Screen.height - p1.y, 0f);
						}
					}
					if (path.Count > 1 && animMaxFrames > 0 && (!isRecordingAnim || kvp.Key != animSelectedBluePoint)) {
						Vector3 p0 = cam.WorldToScreenPoint(path[path.Count - 1]);
						Vector3 p1 = cam.WorldToScreenPoint(path[0]);
						if (p0.z > 0f && p1.z > 0f) {
							GL.Vertex3(p0.x, Screen.height - p0.y, 0f);
							GL.Vertex3(p1.x, Screen.height - p1.y, 0f);
						}
					}
				}
			}
			
			GL.End();

			// Draw Blue points
			if (isAnimationMode && bluePoints != null) {
				for (int i = 0; i < bluePoints.Count; i++) {
					Vector3 wPos = GetBluePointWorldPos(i);
					Vector3 ws = cam.WorldToScreenPoint(wPos);
					if (ws.z < 0f) continue;
					bool isControlPoint = blueRecordedMotions.ContainsKey(i) || i == animSelectedBluePoint;
					GL.End();
					lineMaterial.SetColor("_Color", isControlPoint ? Color.blue : new Color(0.4f, 0.4f, 0.9f, 0.5f));
					lineMaterial.SetPass(0);
					GL.Begin(GL.TRIANGLES);
					DrawFilledDisc(ws.x, Screen.height - ws.y, isControlPoint ? jointRadius * 1.5f : jointRadius * 0.7f, 16);
				}
			}

			GL.Begin(GL.TRIANGLES);
			GL.Color(skeletonColor);
			if (worldJoints != null) {
				for (int i = 0; i < worldJoints.Count; i++) {
					Vector3 ws = cam.WorldToScreenPoint(worldJoints[i]);
					if (ws.z < 0f) continue;

					if (isAnimationMode) {
						bool isControlPoint = recordedMotions.ContainsKey(i) || i == animSelectedJoint;
						GL.End();
						lineMaterial.SetColor("_Color", isControlPoint ? Color.red : new Color(0.6f, 0.6f, 0.6f, 0.4f));
						lineMaterial.SetPass(0);
						GL.Begin(GL.TRIANGLES);
						DrawFilledDisc(ws.x, Screen.height - ws.y, isControlPoint ? jointRadius * 1.5f : jointRadius * 0.7f, 16);
						continue;
					}

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
						lineMaterial.SetColor("_Color", Color.yellow);
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