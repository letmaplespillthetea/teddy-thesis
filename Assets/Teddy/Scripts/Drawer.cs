using UnityEngine;

using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

using mattatz.Utils;
using mattatz.Triangulation2DSystem;
using mattatz.TeddySystem;

namespace mattatz.TeddySystem.Example {

	public enum OperationMode {
		Default,
		Draw,
		Move,
		MoveJoint,
		LassoJoint,
		WaitingForInflation,
		DrawOnSurface
	};

	public class Drawer : MonoBehaviour {

		[SerializeField, Range(0.2f, 1.5f)] float threshold = 1.0f;
		[SerializeField] GameObject prefab;
		[SerializeField] GameObject floor;
		[SerializeField] Material lineMat;
		[SerializeField] TextAsset json;

		[Header("Skeleton Appearance")]
		[SerializeField] bool showSkeleton = true;
		[SerializeField] Color skeletonColor = Color.red;
		[SerializeField, Tooltip("Merge bones shorter than this distance")] float simplifyDistance = 0.05f;
		[SerializeField, Range(2f, 16f)] float jointRadius = 2f;

		[Header("Mass Spring Physics")]
		[SerializeField] bool enableGravity = false;
		[SerializeField] bool enablePhysics = true;
		[SerializeField, Range(0f, 1f)] float shapeStiffness = 0.2f;
		[SerializeField, Range(0f, 1f)] float damping = 0.1f;

		[SerializeField, Range(0.1f, 2f)] float inflationAmount = 1.0f;
		[SerializeField] bool smoothHeightFields = true;

		[Header("Domain Stitching (Advanced)")]
		[SerializeField] bool useDomainStitching = false;
		[SerializeField] bool useFullPipeline = false; // Enable Phases 2-5
		[SerializeField] bool useARAPLayering = false; // Enable Phase 5 (ARAP-L)

		OperationMode mode;

		Teddy teddy;
		List<Vector2> points;
		List<Puppet> puppets = new List<Puppet>();

		// Domain Stitching System
		DomainStitchingSystem stitchingSystem;
		List<List<Vector2>> multiPartContours = new List<List<Vector2>>();  // Accumulated sketches for refinement/stitching
		SketchCollection sketchCollection = new SketchCollection();          // Phase 2: Individual sketch info

		Camera cam;
		float screenZ = 0f;

		Puppet selected;
		Puppet activePuppet;
		bool isEditMode = false;
		Vector3 origin;
		Vector3 startPoint;

		// Lasso Joints state
		bool isLassoMode = false;
		bool lassoStarted = false;
		List<Vector2> lassoPoints = new List<Vector2>();

		// ── Rig Edit mode state ───────────────────────────────────────────────
		bool isRigEditMode = false;
		Puppet rigEditPuppet;       // puppet being edited
		int  rigSelectedJoint = -1; // joint highlighted in yellow
		bool rigConnectMode = false; // waiting for second joint to connect
		int  rigConnectFirst = -1;  // first joint picked for Connect

		// Region Physics panel state (shown after lasso or when clicking a joint)
		bool     showRegionPanel    = false;
		Puppet   regionPuppet;
		int      selectedRegion     = -1;
		float    regionEditStiffness = 0.2f;
		float    regionEditDamping   = 0.1f;

		// For click-vs-drag detection in MoveJoint mode
		Vector2 dragStartMousePos;
		
		// --- Animation Mode ---
		public bool isAnimationMode = false;
		public bool isAnimDragValid = false;

		Texture2D colorWheel;
		public Color selectedEditColor = Color.red;
		float selectedValue = 1f;

		// --- Surface Drawing Mode ---
		private float brushSize = 15f;
		private Puppet surfaceDrawingTarget = null;
		private bool isSurfaceDrawing = false;
		private bool isEraser = false; // Toggle for erasing

		void CreateColorWheel() {
			int size = 150; // Smaller size
			colorWheel = new Texture2D(size, size);
			Vector2 center = new Vector2(size / 2f, size / 2f);
			float radius = size / 2f;

			for (int y = 0; y < size; y++) {
				for (int x = 0; x < size; x++) {
					Vector2 pos = new Vector2(x, y);
					float dist = Vector2.Distance(center, pos);
					if (dist > radius) {
						colorWheel.SetPixel(x, y, Color.clear);
					} else {
						float angle = Mathf.Atan2(y - center.y, x - center.x);
						float hue = (angle + Mathf.PI) / (2f * Mathf.PI);
						float sat = dist / radius;
						Color c = Color.HSVToRGB(hue, sat, 1f);
						colorWheel.SetPixel(x, y, c);
					}
				}
			}
			colorWheel.Apply();
		}

		public bool isDrawingEnabled = true;
		public Vector3 currentRotation = Vector3.zero;

		void Start () {
			cam = Camera.main;
			if (cam != null) {
				cam.backgroundColor = Color.white;
				cam.clearFlags = CameraClearFlags.SolidColor;
			}
			screenZ = Mathf.Abs(cam.transform.position.z - transform.position.z);

			points = new List<Vector2>();
			// Start with an empty scene instead of loading the duck
			// points = JsonUtility.FromJson<JsonSerialization<Vector2>>(json.text).ToList();
			// Build();
		}

		void Update () {
			// Apply rotation from UI sliders
			transform.localEulerAngles = currentRotation;

			// Sync selected joint highlight to puppet in Rig Edit mode
			if (isRigEditMode && rigEditPuppet != null)
				rigEditPuppet.rigEditSelectedJoint = rigSelectedJoint;
			else if (!isRigEditMode && rigEditPuppet != null) {
				rigEditPuppet.rigEditSelectedJoint = -1;
				rigEditPuppet = null;
			}

			// Sync animation mode highlights
			if (!isAnimationMode && activePuppet != null && activePuppet.animSelectedJoint >= 0) {
				activePuppet.StopRecordingAnimation();
			}

			foreach(var p in puppets) {
				if (p == null) continue;
				p.isAnimationMode = isAnimationMode;
				p.showSkeleton = showSkeleton;
				p.skeletonColor = skeletonColor;
				p.jointRadius = jointRadius;
				p.enablePhysics = enablePhysics;
				p.shapeStiffness = shapeStiffness;
				p.damping = damping;
			}

			var bottom = cam.ViewportToWorldPoint(new Vector3(0.5f, 0f, screenZ));
			floor.transform.position = bottom;

			var screen = Input.mousePosition;
			screen.z = screenZ;

			Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
			// Area for the new UI controls
			bool isOverLeftUI  = new Rect(10, 10, 200, 350).Contains(guiMouse);
			// Right panel: buttons + region panel (220 × 320 when region panel is visible)
			bool isOverRightUI = new Rect(Screen.width - 220, 10, 220,
				isEditMode ? 400 : (showRegionPanel ? 320 : 280)).Contains(guiMouse);
			
			// Inflation buttons in center bottom
			bool isOverCenterUI = false;
			if (mode == OperationMode.WaitingForInflation) {
				isOverCenterUI = new Rect((Screen.width - 240) / 2f, Screen.height - 180f, 240, 140).Contains(guiMouse);
			}

			bool isOverUI = isOverLeftUI || isOverRightUI || isOverCenterUI;

			switch(mode) {

			case OperationMode.Default:

				if(Input.GetMouseButtonDown(0) && !isOverUI) {
					var ray = cam.ScreenPointToRay(screen);
					RaycastHit hit;

					bool jointPicked = false;
					if (!isEditMode && !isAnimationMode) {
						foreach (var p in puppets) {
							if (p == null) continue;
							int jIndex;
							if (p.TryPickJoint(cam, screen, 20f, out jIndex)) {
								selected = p;
								activePuppet = p;
								selected.draggingJoint = jIndex;
								dragStartMousePos = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
								mode = OperationMode.MoveJoint;
								jointPicked = true;
								break;
							}
						}
					}

					if (!jointPicked) {
						if(Physics.Raycast(ray.origin, ray.direction, out hit, float.MaxValue)) {
							startPoint = cam.ScreenToWorldPoint(screen);
							selected = hit.collider.GetComponent<Puppet>();
							activePuppet = selected;
							
							// If in Animation Mode, we only want to interact with joints/points, NOT move the whole model
							if (isAnimationMode) {
								selected.Select(); // Ensure kinematic
								// Don't change mode to OperationMode.Move
							} else {
								selected.Select();
								if (isEditMode) {
									selected.OnTextureClicked(hit, selectedEditColor);
								}
								startPoint = hit.point;
								origin = selected.transform.position;
								mode = OperationMode.Move;
							}
						} else if (!isLassoMode) {
							// Only allow sketch-drawing when enabled AND lasso is NOT armed
							if (isDrawingEnabled && !isAnimationMode) {
								activePuppet = null;
								isEditMode = false;
								// Clear(); // Don't clear multiPartContours here, just the current points
								points.Clear();
								mode = OperationMode.Draw;
							}
						}
					}
				}

				break;

			case OperationMode.Draw:
				if(Input.GetMouseButtonUp(0)) {
					if (points.Count > 3) {
						mode = OperationMode.WaitingForInflation;
					} else {
						points.Clear();
						mode = OperationMode.Default;
					}
				} else {
					var p = cam.ScreenToWorldPoint(screen);
					p = transform.InverseTransformPoint(p);
					var p2D = new Vector2(p.x, p.y);
					if(points.Count <= 0 || Vector2.Distance(p2D, points.Last()) > threshold) {
						points.Add(p2D);
					}
				}
				break;

			case OperationMode.WaitingForInflation:
				if(Input.GetMouseButtonDown(0) && !isOverUI) {
					// If clicking background while waiting, start a new drawing
					// Clear(); 
					points.Clear();
					mode = OperationMode.Draw;
				}
				break;

			case OperationMode.Move:

				if(Input.GetMouseButtonUp(0)) {
					selected.Unselect();
					selected = null;

					mode = OperationMode.Default;
				} else {
					var currentPoint = cam.ScreenToWorldPoint(screen);
					var offset = currentPoint - startPoint;
					selected.transform.position = origin + offset;
				}

				break;

			case OperationMode.MoveJoint:

				if (Input.GetMouseButtonUp(0)) {
					if (selected != null) {
						int jIdx = selected.draggingJoint;
						selected.draggingJoint = -1;

						// Detect click vs drag: if mouse barely moved, treat as a region-select click
						float moved = Vector2.Distance(
							new Vector2(Input.mousePosition.x, Input.mousePosition.y),
							dragStartMousePos);
						if (moved < 8f && jIdx >= 0) {
							int region = selected.GetJointRegion(jIdx);
							if (region >= 0) {
								var (rs, rd) = selected.GetRegionParams(region);
								regionEditStiffness = rs;
								regionEditDamping   = rd;
								selectedRegion  = region;
								regionPuppet    = selected;
								showRegionPanel = true;
							}
						}
					}
					selected = null;
					mode = OperationMode.Default;
				} else {
					if (selected != null) {
						Vector3 mp = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, selected.dragZ));
						selected.MoveJoint(mp);
					}
				}

				break;

			case OperationMode.LassoJoint:

				if (!lassoStarted) {
					if (Input.GetMouseButtonDown(0) && !isOverUI) {
						lassoStarted = true;
						lassoPoints.Clear();
					}
				} else {
					if (Input.GetMouseButtonUp(0)) {
						// Geometry is committed — apply, then open the physics panel
						if (lassoPoints.Count > 2) {
							regionPuppet = null;
							foreach (var p in puppets) {
								if (p == null) continue;
								int newRegion = p.ApplyLasso(cam, lassoPoints);
								if (newRegion >= 0 && regionPuppet == null) {
									// Auto-open the panel for the first puppet that got a region
									var (rs, rd) = p.GetRegionParams(newRegion);
									regionEditStiffness = rs;
									regionEditDamping   = rd;
									selectedRegion  = newRegion;
									regionPuppet    = p;
									showRegionPanel = true;
								}
							}
						}
						lassoPoints.Clear();
						lassoStarted = false;
						isLassoMode  = false;
						mode = OperationMode.Default;
					} else if (Input.GetMouseButton(0)) {
						Vector2 mp2 = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
						if (lassoPoints.Count == 0 || Vector2.Distance(mp2, lassoPoints[lassoPoints.Count - 1]) > 5f)
							lassoPoints.Add(mp2);
					}
				}

				break;

			case OperationMode.DrawOnSurface:
				if (Input.GetMouseButtonDown(0) && !isOverUI) {
					var ray = cam.ScreenPointToRay(screen);
					RaycastHit hit;
					if (Physics.Raycast(ray.origin, ray.direction, out hit, float.MaxValue)) {
						var targetPuppet = hit.collider.GetComponent<Puppet>();
						if (targetPuppet != null) {
							surfaceDrawingTarget = targetPuppet;
							surfaceDrawingTarget.isBeingPainted = true;
							isSurfaceDrawing = true;
							Color32 paintColor = isEraser ? (Color32)Color.white : (Color32)selectedEditColor;
							surfaceDrawingTarget.PaintOnSurface(hit, paintColor, brushSize);
							Debug.Log($"[DrawSurface] Started {(isEraser ? "erasing" : "painting")} on {hit.collider.gameObject.name}");
						}
					}
				} else if (Input.GetMouseButton(0) && isSurfaceDrawing && surfaceDrawingTarget != null) {
					// Continue painting along the stroke continuously
					var ray = cam.ScreenPointToRay(screen);
					RaycastHit hit;
					if (Physics.Raycast(ray.origin, ray.direction, out hit, float.MaxValue)) {
						if (hit.collider.GetComponent<Puppet>() == surfaceDrawingTarget) {
							Color32 paintColor = isEraser ? (Color32)Color.white : (Color32)selectedEditColor;
							surfaceDrawingTarget.PaintOnSurface(hit, paintColor, brushSize);
						}
					}
				} else if (Input.GetMouseButtonUp(0)) {
					if (surfaceDrawingTarget != null) {
						surfaceDrawingTarget.isBeingPainted = false;
					}
					isSurfaceDrawing = false;
					surfaceDrawingTarget = null;
					Debug.Log("[DrawSurface] Stopped painting");
				}
				break;

			}

			// ── Rig Edit mode input (runs independently of OperationMode) ────────
			if (isRigEditMode && rigEditPuppet != null) {
				Vector3 mouseScreen = Input.mousePosition;
				mouseScreen.z = screenZ;

				if (Input.GetMouseButtonDown(0) && !isOverUI) {
					int picked = -1;
					rigEditPuppet.TryPickJointAny(cam, Input.mousePosition, 24f, out picked);

					if (rigConnectMode) {
						// Second click in connect mode
						if (picked >= 0 && picked != rigConnectFirst) {
							rigEditPuppet.AddBone(rigConnectFirst, picked);
							rigConnectMode = false;
							rigConnectFirst = -1;
							rigSelectedJoint = picked;
						} else if (picked < 0) {
							// Clicked empty space → cancel connect
							rigConnectMode = false;
							rigConnectFirst = -1;
						}
					} else {
						rigSelectedJoint = picked; // select or deselect
						if (picked >= 0) {
							rigEditPuppet.draggingJoint = picked;
						}
					}
				}

				if (Input.GetMouseButton(0) && rigEditPuppet.draggingJoint >= 0) {
					Vector3 mp = cam.ScreenToWorldPoint(
						new Vector3(mouseScreen.x, mouseScreen.y, rigEditPuppet.dragZ));
					rigEditPuppet.MoveJoint(mp);
				}

				if (Input.GetMouseButtonUp(0)) {
					rigEditPuppet.draggingJoint = -1;
				}
			}
			
			// ── Animation mode input ────────
			if (isAnimationMode && activePuppet != null) {
				Vector3 mouseScreen = Input.mousePosition;
				mouseScreen.z = screenZ;

				if (Input.GetMouseButtonDown(0)) {
					if (isOverUI) {
						isAnimDragValid = false;
					} else {
						isAnimDragValid = true;
						int pickedJoint = -1;
						int pickedBlue = -1;
						
						if (activePuppet.TryPickJointAny(cam, Input.mousePosition, 12f, out pickedJoint)) {
							activePuppet.animSelectedJoint = pickedJoint;
							activePuppet.animSelectedBluePoint = -1;
							dragStartMousePos = new Vector2(mouseScreen.x, mouseScreen.y);
						} else if (activePuppet.TryPickBluePoint(cam, Input.mousePosition, 12f, out pickedBlue)) {
							activePuppet.animSelectedBluePoint = pickedBlue;
							activePuppet.animSelectedJoint = -1;
							dragStartMousePos = new Vector2(mouseScreen.x, mouseScreen.y);
						} else {
							if (activePuppet.animSelectedJoint >= 0 || activePuppet.animSelectedBluePoint >= 0) {
								activePuppet.StartRecordingAnimation();
								Vector3 mp = cam.ScreenToWorldPoint(new Vector3(mouseScreen.x, mouseScreen.y, activePuppet.dragZ));
								activePuppet.MoveJoint(mp);
							} else {
								Ray ray = cam.ScreenPointToRay(mouseScreen);
								RaycastHit hit;
								if (Physics.Raycast(ray, out hit)) {
									if (hit.collider.gameObject == activePuppet.gameObject) {
										int bIdx = activePuppet.CreateBluePoint(hit);
										activePuppet.animSelectedBluePoint = bIdx;
										activePuppet.animSelectedJoint = -1;
										activePuppet.dragZ = cam.WorldToScreenPoint(hit.point).z;
										dragStartMousePos = new Vector2(mouseScreen.x, mouseScreen.y);
									}
								}
							}
						}
					}
				}

				if (isAnimDragValid && Input.GetMouseButton(0) && (activePuppet.animSelectedJoint >= 0 || activePuppet.animSelectedBluePoint >= 0)) {
					if (!activePuppet.isRecordingAnim) {
						if (Vector2.Distance(new Vector2(mouseScreen.x, mouseScreen.y), dragStartMousePos) > 4f) {
							activePuppet.StartRecordingAnimation();
						}
					}
					
					if (activePuppet.isRecordingAnim) {
						Vector3 mp = cam.ScreenToWorldPoint(new Vector3(mouseScreen.x, mouseScreen.y, activePuppet.dragZ));
						activePuppet.MoveJoint(mp);
					}
				}

				if (Input.GetMouseButtonUp(0)) {
					if (activePuppet != null) {
						if (activePuppet.isRecordingAnim) {
							activePuppet.StopRecordingAnimation();
						}
						activePuppet.Unselect();
					}
				}
			}
		}

	void Build () {
		if (points.Count < 3 && multiPartContours.Count == 0) return;

		if (points.Count >= 3) {
			points = Utils2D.Constrain(points, threshold);
		}

		// Choose build method based on mode
		if (useDomainStitching && (multiPartContours.Count > 0 || points.Count > 3)) {
			BuildWithDomainStitching();
		} else {
			BuildTraditional();
		}

		// Save current session sketches to collection before clearing
		foreach (var contour in multiPartContours) {
			sketchCollection.AddSketch(contour);
		}
		if (points.Count > 3) {
			sketchCollection.AddSketch(new List<Vector2>(points));
		}

		// Print 4 sample points for legs as requested
		var savedAppendages = sketchCollection.GetAppendages();
		foreach (var app in savedAppendages) {
			string samples = "";
			for (int j = 0; j < Mathf.Min(4, app.contour.Count); j++) {
				samples += $"({app.contour[j].x:F3}, {app.contour[j].y:F3}) ";
			}
			Debug.Log($"[Drawer] Leg Sketch Sample Points (XY): {samples}");
		}

		// Post-process: Push appendages (legs/arms) forward in Z
		if (activePuppet != null) {
			PushAppendagesForward(activePuppet, sketchCollection);
		}

		ClearAll();
	}

	/// <summary>
	/// Offsets the Z position of mesh vertices that belong to appendage sketches.
	/// This pushes limbs (legs, arms) forward for better depth layering.
	/// </summary>
	void PushAppendagesForward(Puppet puppet, SketchCollection collection) {
		var appendages = collection.GetAppendages();
		if (appendages.Count == 0) return;

		MeshFilter mf = puppet.GetComponent<MeshFilter>();
		if (mf == null || mf.sharedMesh == null) return;

		Mesh mesh = mf.sharedMesh;
		Vector3[] vertices = mesh.vertices;
		float zOffset = 0.4f; // Push forward amount

		bool modified = false;
		foreach (var app in appendages) {
			for (int i = 0; i < vertices.Length; i++) {
				Vector2 p2D = new Vector2(vertices[i].x, vertices[i].y);
				if (IsPointInPolygon(p2D, app.contour)) {
					vertices[i].z += zOffset;
					modified = true;
				}
			}
		}

		if (modified) {
			mesh.vertices = vertices;
			mesh.RecalculateNormals();
			mesh.RecalculateBounds();
			
			// Update collider if it exists
			MeshCollider mc = puppet.GetComponent<MeshCollider>();
			if (mc != null) mc.sharedMesh = mesh;
		}
	}

	/// <summary>
	/// Helper for point-in-polygon test (ray casting algorithm)
	/// </summary>
	bool IsPointInPolygon(Vector2 p, List<Vector2> poly) {
		int n = poly.Count;
		bool inside = false;
		for (int i = 0, j = n - 1; i < n; j = i++) {
			if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
				(p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)) {
				inside = !inside;
			}
		}
		return inside;
	}

		/// <summary>
		/// Traditional mesh building (original Teddy method)
		/// </summary>
		void BuildTraditional () {
			if (points.Count < 3) return;

			teddy = new Teddy(points);
			var mesh = teddy.Build(smoothHeightFields ? MeshSmoothingMethod.HC : MeshSmoothingMethod.None, 2, 0.2f, 0.75f, inflationAmount);
			var bones = teddy.GetSkeletonBones();

			CreatePuppet(mesh, bones);
		}

	/// <summary>
	/// Build mesh using Domain Stitching Algorithm
	/// Phase 1: Extract outer contour
	/// Phase 2: Keep individual sketch info for body/appendage classification
	/// Phase 3: Triangulate sketches and create holes
	/// </summary>
void BuildWithDomainStitching () {
	Debug.Log("[Drawer] Domain Stitching: Starting...");
	
	try {
		// Collect all contours
		var contoursToBuild = new List<List<Vector2>>();
		foreach (var c in multiPartContours) contoursToBuild.Add(new List<Vector2>(c));
		if (points.Count > 3) contoursToBuild.Add(new List<Vector2>(points));

		if (contoursToBuild.Count == 0) {
			Debug.LogWarning("[Domain Stitching] No contours to build!");
			return;
		}

		Debug.Log($"[Domain Stitching] Processing {contoursToBuild.Count} contours");

		// SIMPLIFIED APPROACH: Just use outer contour extraction (Phase 1)
		// Skip complex stitching for now to avoid freeze
		Debug.Log("[Phase 1] Extracting outer contour...");
		List<Vector2> outerContour = ExtractOuterContour(contoursToBuild);
		
		if (outerContour == null || outerContour.Count < 3) {
			Debug.LogError("[Phase 1] Failed to extract outer contour! Falling back to traditional build.");
			BuildTraditional();
			return;
		}

		Debug.Log($"[Phase 1] Extracted outer contour with {outerContour.Count} points");
		
		// Build traditional Teddy mesh with the outer contour
		teddy = new Teddy(outerContour);
		var mesh = teddy.Build(smoothHeightFields ? MeshSmoothingMethod.HC : MeshSmoothingMethod.None, 2, 0.2f, 0.75f, inflationAmount);
		var bones = teddy.GetSkeletonBones();

		CreatePuppet(mesh, bones);
		
		Debug.Log("[Domain Stitching] Complete - built mesh from outer contour");
		
	} catch (System.Exception ex) {
		Debug.LogError($"[Domain Stitching] Exception: {ex.Message}\n{ex.StackTrace}");
		Debug.LogError("[Domain Stitching] Falling back to traditional build");
		
		// Fallback to traditional build
		if (points.Count >= 3) {
			BuildTraditional();
		}
	}
}

	/// <summary>
	/// Extract boundary from a 2D mesh (vertices + triangles)
	/// Finds all edges that belong to only one triangle (boundary edges)
	/// </summary>
	List<Vector2> ExtractBoundaryFromMesh(List<Vector2> vertices, List<int> triangles) {
		if (vertices == null || triangles == null || triangles.Count < 3) {
			Debug.LogWarning("[ExtractBoundary] Invalid mesh data");
			return null;
		}

		Debug.Log($"[ExtractBoundary] Processing {vertices.Count} vertices, {triangles.Count / 3} triangles");

		// Count edge occurrences (boundary edges appear only once)
		Dictionary<(int, int), int> edgeCount = new Dictionary<(int, int), int>();

		for (int i = 0; i < triangles.Count; i += 3) {
			int v0 = triangles[i];
			int v1 = triangles[i + 1];
			int v2 = triangles[i + 2];

			// Validate indices
			if (v0 < 0 || v0 >= vertices.Count || v1 < 0 || v1 >= vertices.Count || v2 < 0 || v2 >= vertices.Count) {
				Debug.LogWarning($"[ExtractBoundary] Invalid triangle indices: {v0}, {v1}, {v2}");
				continue;
			}

			// Add 3 edges (order matters: use min,max to normalize)
			AddEdge(edgeCount, v0, v1);
			AddEdge(edgeCount, v1, v2);
			AddEdge(edgeCount, v2, v0);
		}

		// Find boundary edges (count == 1)
		List<(int, int)> boundaryEdges = new List<(int, int)>();
		foreach (var kvp in edgeCount) {
			if (kvp.Value == 1) {
				boundaryEdges.Add(kvp.Key);
			}
		}

		if (boundaryEdges.Count == 0) {
			Debug.LogWarning("[ExtractBoundary] No boundary edges found!");
			return null;
		}

		Debug.Log($"[ExtractBoundary] Found {boundaryEdges.Count} boundary edges");

		// Sort edges to form a continuous loop
		List<int> boundaryIndices = new List<int>();
		var edgeDict = new Dictionary<int, List<int>>();
		foreach (var (a, b) in boundaryEdges) {
			if (!edgeDict.ContainsKey(a)) edgeDict[a] = new List<int>();
			if (!edgeDict.ContainsKey(b)) edgeDict[b] = new List<int>();
			edgeDict[a].Add(b);
			edgeDict[b].Add(a);
		}

		// Start from any boundary vertex
		int current = boundaryEdges[0].Item1;
		int start = current;
		boundaryIndices.Add(current);

		int maxIterations = vertices.Count * 2; // Safety limit
		int iterations = 0;

		while (iterations < maxIterations) {
			iterations++;

			if (!edgeDict.ContainsKey(current)) {
				Debug.LogWarning($"[ExtractBoundary] Current vertex {current} not in edge dict!");
				break;
			}

			var neighbors = edgeDict[current];
			int next = -1;
			foreach (var n in neighbors) {
				if (boundaryIndices.Count == 1 || n != boundaryIndices[boundaryIndices.Count - 2]) {
					next = n;
					break;
				}
			}

			if (next < 0) {
				Debug.LogWarning($"[ExtractBoundary] No next vertex found at iteration {iterations}");
				break;
			}

			if (next == start) {
				Debug.Log($"[ExtractBoundary] Loop closed at iteration {iterations}");
				break;
			}

			boundaryIndices.Add(next);
			current = next;
		}

		if (iterations >= maxIterations) {
			Debug.LogError($"[ExtractBoundary] Hit max iterations ({maxIterations})! Possible infinite loop.");
		}

		// Convert indices to positions
		List<Vector2> boundary = new List<Vector2>();
		foreach (var idx in boundaryIndices) {
			if (idx >= 0 && idx < vertices.Count) {
				boundary.Add(vertices[idx]);
			}
		}

		Debug.Log($"[ExtractBoundary] Extracted {boundary.Count} boundary points from {vertices.Count} vertices");
		return boundary;
	}

	void AddEdge(Dictionary<(int, int), int> dict, int a, int b) {
		var key = a < b ? (a, b) : (b, a);
		if (!dict.ContainsKey(key)) dict[key] = 0;
		dict[key]++;
	}

	/// <summary>
	/// Phase 1: Extract outer contour from multiple sketches via rasterization
	/// </summary>
	List<Vector2> ExtractOuterContour(List<List<Vector2>> contours) {
		if (contours == null || contours.Count == 0) return null;

		Debug.Log($"[ExtractOuterContour] Processing {contours.Count} contours");

		// 1. Find bounding box
		float minX = float.MaxValue, minY = float.MaxValue;
		float maxX = float.MinValue, maxY = float.MinValue;
		foreach (var c in contours) {
			foreach (var p in c) {
				minX = Mathf.Min(minX, p.x);
				minY = Mathf.Min(minY, p.y);
				maxX = Mathf.Max(maxX, p.x);
				maxY = Mathf.Max(maxY, p.y);
			}
		}

		// Add padding
		float width = maxX - minX;
		float height = maxY - minY;
		float margin = Mathf.Max(width, height) * 0.15f; // Increased padding
		minX -= margin; minY -= margin; maxX += margin; maxY += margin;
		width = maxX - minX; height = maxY - minY;

		Debug.Log($"[ExtractOuterContour] Bounds: ({minX:F2}, {minY:F2}) to ({maxX:F2}, {maxY:F2})");

		// 2. Setup Rasterization with higher resolution
		int res = 1024; // Increased from 512 for better quality
		RenderTexture rt = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.Default);
		RenderTexture.active = rt;
		GL.Clear(true, true, Color.clear);

		// Use a simple material to draw filled polygons
		Material mat = new Material(Shader.Find("Hidden/Internal-Colored"));
		mat.SetPass(0);

		GL.PushMatrix();
		GL.LoadPixelMatrix(0, res, 0, res);
		GL.Begin(GL.TRIANGLES);
		GL.Color(Color.white);

		// Rasterize all contours
		int triangleCount = 0;
		foreach (var c in contours) {
			try {
				var polygon = Polygon2D.Contour(c.ToArray());
				var triangulation = new Triangulation2D(polygon, 0f);
				foreach (var t in triangulation.Triangles) {
					Vector2 p0 = MapToRT(t.a.Coordinate, minX, minY, width, height, res);
					Vector2 p1 = MapToRT(t.b.Coordinate, minX, minY, width, height, res);
					Vector2 p2 = MapToRT(t.c.Coordinate, minX, minY, width, height, res);
					GL.Vertex3(p0.x, p0.y, 0);
					GL.Vertex3(p1.x, p1.y, 0);
					GL.Vertex3(p2.x, p2.y, 0);
					triangleCount++;
				}
			} catch (System.Exception ex) {
				Debug.LogWarning($"[ExtractOuterContour] Failed to triangulate contour: {ex.Message}");
			}
		}
		GL.End();
		GL.PopMatrix();

		Debug.Log($"[ExtractOuterContour] Rasterized {triangleCount} triangles");

		// 3. Read pixels and extract contour
		Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
		tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
		tex.Apply();

		var rawContour = TextureContourExtractor.ExtractContour(tex);
		
		Debug.Log($"[ExtractOuterContour] Raw contour: {rawContour.Count} points");

		// 4. Transform back to world coordinates
		List<Vector2> result = new List<Vector2>();
		foreach (var p in rawContour) {
			float px = (p.x * res) + res * 0.5f;
			float py = (p.y * res) + res * 0.5f;
			
			float wx = minX + (px / (float)res) * width;
			float wy = minY + (py / (float)res) * height;
			result.Add(new Vector2(wx, wy));
		}

		// 5. Simplify, Smooth and ensure closure (balanced for speed)
		int originalCount = result.Count;
		result = SketchCleaner.Clean(result, threshold * 1.5f); // More aggressive for speed
		Debug.Log($"[ExtractOuterContour] After clean: {result.Count} points (from {originalCount})");
		
		result = SketchCleaner.Smooth(result, 2);
		Debug.Log($"[ExtractOuterContour] After smooth: {result.Count} points");
		
		result = SketchCleaner.EnsureClosure(result, threshold);
		Debug.Log($"[ExtractOuterContour] Final: {result.Count} points");

		// Cleanup
		RenderTexture.active = null;
		RenderTexture.ReleaseTemporary(rt);
		Destroy(tex);
		Destroy(mat);

		return result;
	}

		/// <summary>
		/// Create a puppet from mesh and bones
		/// </summary>
		void CreatePuppet(Mesh mesh, List<(Vector3, Vector3)> bones) {
			var go = Instantiate(prefab);
			go.transform.parent = transform;
			go.transform.localPosition = Vector3.zero;
			go.transform.localRotation = Quaternion.identity;

			var puppet = go.GetComponent<Puppet>();
			puppet.GetComponent<Rigidbody>().useGravity = enableGravity;
			puppet.gravity = enableGravity ? 9.81f : 0f;
			puppet.showSkeleton = showSkeleton;
			puppet.skeletonColor = skeletonColor;
			puppet.simplifyDistance = simplifyDistance;
			puppet.jointRadius = jointRadius;
			puppet.enablePhysics = enablePhysics;
			puppet.shapeStiffness = shapeStiffness;
			puppet.damping = damping;

			puppet.SetMesh(mesh);
			puppet.SetupSkeleton(bones);
			puppets.Add(puppet);
			activePuppet = puppet;
		}

		void Clear () {
			points.Clear();
		}

		void ClearAll() {
			points.Clear();
			multiPartContours.Clear();
			// Keep only appendages for later display
			if (sketchCollection != null) {
				var appendages = sketchCollection.GetAppendages();
				sketchCollection.Clear();
				foreach (var app in appendages) {
					sketchCollection.sketches.Add(app);
				}
			}
		}

		public void Save () {
			LocalStorage.SaveList<Vector2>(points, "points.json");
		}

		public void Reset () {
			puppets.ForEach(puppet => {
				puppet.Ignore();
				Destroy(puppet.gameObject);
			});
			puppets.Clear();
		}

		void OnDrawGizmos () {
			if(points != null) {
				Gizmos.matrix = transform.localToWorldMatrix;
				Gizmos.color = Color.white;
				points.ForEach(p => {
					Gizmos.DrawSphere(p, 0.02f);
				});
			}
		}

		void OnRenderObject () {

			if(points != null) {
				GL.PushMatrix();
				GL.MultMatrix (transform.localToWorldMatrix);
				lineMat.SetColor("_Color", Color.black);
				lineMat.SetPass(0);
				GL.Begin(GL.LINES);
				for(int i = 0, n = points.Count - 1; i < n; i++) {
					GL.Vertex(points[i]); GL.Vertex(points[i + 1]);
				}
				GL.End();
				
				// Draw accumulated parts
				lineMat.SetColor("_Color", new Color(0f, 0f, 0f, 0.4f));
				lineMat.SetPass(0);
				GL.Begin(GL.LINES);
				foreach (var contour in multiPartContours) {
					for (int i = 0, n = contour.Count - 1; i < n; i++) {
						GL.Vertex(contour[i]); GL.Vertex(contour[i + 1]);
					}
				}
				GL.End();

				// Draw saved appendages from previous builds
				lineMat.SetColor("_Color", new Color(0.2f, 0.8f, 1f, 0.6f)); // Light blue for saved appendages
				lineMat.SetPass(0);
				GL.Begin(GL.LINES);
				foreach (var sketch in sketchCollection.GetAppendages()) {
					for (int i = 0, n = sketch.contour.Count - 1; i < n; i++) {
						GL.Vertex(sketch.contour[i]); GL.Vertex(sketch.contour[i + 1]);
					}
				}
				GL.End();
				
				GL.PopMatrix();
			}

			// Draw lasso outline in screen space
			if (mode == OperationMode.LassoJoint && lassoPoints != null && lassoPoints.Count > 1) {
				GL.PushMatrix();
				GL.LoadPixelMatrix();
				lineMat.SetColor("_Color", new Color(0f, 0.5f, 1f, 0.9f)); // Blue lasso for better visibility on white
				lineMat.SetPass(0);
				GL.Begin(GL.LINES);
				for (int i = 0; i < lassoPoints.Count - 1; i++) {
					// lassoPoints are stored in GUI coords (y flipped), convert back to GL pixel coords
					Vector2 a = lassoPoints[i];
					Vector2 b = lassoPoints[i + 1];
					GL.Vertex3(a.x, Screen.height - a.y, 0f);
					GL.Vertex3(b.x, Screen.height - b.y, 0f);
				}
				// Close the loop
				Vector2 first = lassoPoints[0];
				Vector2 last  = lassoPoints[lassoPoints.Count - 1];
				GL.Vertex3(last.x,  Screen.height - last.y,  0f);
				GL.Vertex3(first.x, Screen.height - first.y, 0f);
				GL.End();
				GL.PopMatrix();
			}

		}

		void OnGUI() {
			GUIStyle style = new GUIStyle(GUI.skin.button);
			style.fontSize = 9;
			
			if (activePuppet != null) {
				var renderer = activePuppet.GetComponent<MeshRenderer>();
				if (renderer != null) {
					Bounds b = renderer.bounds;
					Vector3[] corners = new Vector3[8];
					corners[0] = new Vector3(b.min.x, b.min.y, b.min.z);
					corners[1] = new Vector3(b.max.x, b.min.y, b.min.z);
					corners[2] = new Vector3(b.min.x, b.max.y, b.min.z);
					corners[3] = new Vector3(b.max.x, b.max.y, b.min.z);
					corners[4] = new Vector3(b.min.x, b.min.y, b.max.z);
					corners[5] = new Vector3(b.max.x, b.min.y, b.max.z);
					corners[6] = new Vector3(b.min.x, b.max.y, b.max.z);
					corners[7] = new Vector3(b.max.x, b.max.y, b.max.z);

					float minX = float.MaxValue, minY = float.MaxValue;
					float maxX = float.MinValue, maxY = float.MinValue;
					foreach (var c in corners) {
						Vector3 s = Camera.main.WorldToScreenPoint(c);
						if (s.x < minX) minX = s.x;
						if (s.x > maxX) maxX = s.x;
						float sy = Screen.height - s.y;
						if (sy < minY) minY = sy;
						if (sy > maxY) maxY = sy;
					}

					DrawRect(new Rect(minX, minY, maxX - minX, maxY - minY), 2, Color.yellow);
				}
			}

			// ── Mesh Generation Controls ────────────────────────────────────
			GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
			labelStyle.fontSize = 14;
			labelStyle.normal.textColor = Color.black;

			GUI.Box(new Rect(Screen.width - 210, 10, 200, 140), GUIContent.none);
			GUI.Label(new Rect(Screen.width - 200, 15, 180, 20), "─ Mesh Generation ─", labelStyle);

			GUI.Label(new Rect(Screen.width - 200, 38, 180, 16), "Inflation: " + inflationAmount.ToString("F2"), labelStyle);
			inflationAmount = GUI.HorizontalSlider(new Rect(Screen.width - 200, 58, 180, 18), inflationAmount, 0.1f, 2f);
			smoothHeightFields = GUI.Toggle(new Rect(Screen.width - 200, 80, 180, 20), smoothHeightFields, " Smooth Mesh");

			bool prevStitching = useDomainStitching;
			useDomainStitching = GUI.Toggle(new Rect(Screen.width - 200, 105, 180, 20), useDomainStitching, " Domain Stitching");
			if (prevStitching != useDomainStitching) {
				Debug.Log($"Domain Stitching: {(useDomainStitching ? "ON" : "OFF")}");
			}

			// ── Contextual Controls ──────────────────────────────────────────
			if (isRigEditMode) {
				float rx = Screen.width - 210;
				GUIStyle lbl = new GUIStyle(GUI.skin.label);
				lbl.fontSize = 16;
				lbl.normal.textColor = Color.white;
				GUI.Box(new Rect(rx - 5, 5, 215, 250), GUIContent.none);
				GUI.Label(new Rect(rx, 10, 200, 24), "── Rig Edit Mode ──", lbl);
				
				if (rigSelectedJoint >= 0) {
					if (GUI.Button(new Rect(rx, 35, 87, 19), (rigConnectMode ? "Cancel Link" : "Connect"), style)) {
						if (rigConnectMode) {
							rigConnectMode = false;
							rigConnectFirst = -1;
						} else {
							rigConnectMode = true;
							rigConnectFirst = rigSelectedJoint;
						}
					}
					if (GUI.Button(new Rect(rx, 59, 87, 19), "Remove", style)) {
						rigEditPuppet.RemoveJoint(rigSelectedJoint);
						rigSelectedJoint = -1;
					}
				}

				if (GUI.Button(new Rect(rx, 93, 87, 19), "Done", style)) {
					isRigEditMode    = false;
					rigEditPuppet    = null;
					rigSelectedJoint = -1;
					rigConnectMode   = false;
					rigConnectFirst  = -1;
				}
			} else if (isAnimationMode) {
				float rx = Screen.width - 210;
				GUIStyle lbl = new GUIStyle(GUI.skin.label);
				lbl.fontSize = 16;
				lbl.normal.textColor = Color.white;
				GUI.Box(new Rect(rx - 5, 5, 215, 280), GUIContent.none);
				GUI.Label(new Rect(rx, 10, 200, 24), "── Anim Mode ──", lbl);
				
				if (GUI.Button(new Rect(rx, 35, 87, 19), "Exit Anim Mode", style)) {
					isAnimationMode = false;
				}

				if (activePuppet != null) {
					if (GUI.Button(new Rect(rx, 59, 87, 19), "Clear Anims", style)) {
						activePuppet.ClearAnimation();
					}
					GUI.Label(new Rect(rx, 140, 200, 24), "Frames: " + activePuppet.animMaxFrames, lbl);
					GUI.Label(new Rect(rx, 170, 200, 24), "Current: " + activePuppet.animCurrentFrame, lbl);
				}
			} else if (isEditMode || mode == OperationMode.DrawOnSurface) {
				DrawColorEditor(style);
			} else {
				// ── Normal play mode right panel ─────────────────────────────
				float startY = 160;

				if (GUI.Button(new Rect(Screen.width - 210, startY, 87, 22), "Edit Mode", style)) {
					isEditMode = true;
					if (activePuppet != null) activePuppet.StartEditMode();
				}

				if (GUI.Button(new Rect(Screen.width - 105, startY, 87, 22), "Draw Surf", style)) {
					mode = OperationMode.DrawOnSurface;
				}
				startY += 26;
				
				if (GUI.Button(new Rect(Screen.width - 210, startY, 87, 22), "Rig Mode", style)) {
					if (activePuppet != null) {
						isRigEditMode = true;
						rigEditPuppet = activePuppet;
					}
				}

				if (GUI.Button(new Rect(Screen.width - 105, startY, 87, 22), (isLassoMode ? "Cancel" : "Physics"), style)) {
					isLassoMode = !isLassoMode;
					mode = isLassoMode ? OperationMode.LassoJoint : OperationMode.Default;
				}
				startY += 26;

				if (GUI.Button(new Rect(Screen.width - 210, startY, 87, 22), "Anim Mode", style)) {
					isAnimationMode = true;
				}
				
				if (GUI.Button(new Rect(Screen.width - 105, startY, 87, 22), "Reset All", style)) {
					foreach (var p in puppets) if (p != null) p.ResetLasso();
					showRegionPanel = false;
					mode = OperationMode.Default;
					isLassoMode = false;
				}
			}
			GUI.enabled = true;

			// ── Region Physics panel (Overlays contextual panel if active) ────────────────
			if (showRegionPanel && regionPuppet != null && selectedRegion >= 0) {
				float px = Screen.width - 210;
				float py = 318f;
				Rect panelRect = new Rect(px - 5, py - 4, 213, 120);

				if (Event.current.type == EventType.MouseDown && !panelRect.Contains(Event.current.mousePosition)) {
					showRegionPanel = false;
				}

				GUIStyle lbl = new GUIStyle(GUI.skin.label);
				lbl.fontSize = 16;
				lbl.normal.textColor = Color.black;

				GUI.Box(panelRect, GUIContent.none);
				GUI.Label(new Rect(px, py, 200, 22), $"Region {selectedRegion} Physics", lbl);

				GUI.Label(new Rect(px, py + 26, 200, 20), $"Stiffness: {regionEditStiffness:F2}", lbl);
				regionEditStiffness = GUI.HorizontalSlider(new Rect(px, py + 46, 200, 18), regionEditStiffness, 0f, 1f);

				GUI.Label(new Rect(px, py + 68, 200, 20), $"Damping:   {regionEditDamping:F2}", lbl);
				regionEditDamping = GUI.HorizontalSlider(new Rect(px, py + 88, 200, 18), regionEditDamping, 0f, 1f);
				
				regionPuppet.SetRegionParams(selectedRegion, regionEditStiffness, regionEditDamping);
			}
			GUI.enabled = true;

			if (GUI.Button(new Rect(10, 10, 87, 22), (isDrawingEnabled ? "DRAW" : "VIEW"), style)) {
				isDrawingEnabled = !isDrawingEnabled;
			}

			if (GUI.Button(new Rect(10, 37, 87, 22), "Gravity: " + (enableGravity ? "ON" : "OFF"), style)) {
				enableGravity = !enableGravity;
				foreach (var p in puppets) {
					if (p != null) {
						var body = p.GetComponent<Rigidbody>();
						if (body != null) body.useGravity = enableGravity;
						p.gravity = enableGravity ? 9.81f : 0f;
					}
				}
			}

			if (GUI.Button(new Rect(10, 64, 87, 17), "Reset Rot", style)) {
				currentRotation = Vector3.zero;
			}

			GUI.Label(new Rect(10, 180, 200, 20), "Rotate X: " + currentRotation.x.ToString("F0"), style);
			currentRotation.x = GUI.HorizontalSlider(new Rect(10, 205, 200, 20), currentRotation.x, 0f, 360f);

			GUI.Label(new Rect(10, 230, 200, 20), "Rotate Y: " + currentRotation.y.ToString("F0"), style);
			currentRotation.y = GUI.HorizontalSlider(new Rect(10, 255, 200, 20), currentRotation.y, 0f, 360f);

			GUI.Label(new Rect(10, 280, 200, 20), "Rotate Z: " + currentRotation.z.ToString("F0"), style);
			currentRotation.z = GUI.HorizontalSlider(new Rect(10, 305, 200, 20), currentRotation.z, 0f, 360f);

#if UNITY_EDITOR
			if (GUI.Button(new Rect(10, 86, 87, 22), "Import", style)) {
				string path = EditorUtility.OpenFilePanel("Select PNG Image", "", "png");
				if (!string.IsNullOrEmpty(path)) {
					byte[] bytes = File.ReadAllBytes(path);
					Texture2D tex = new Texture2D(2, 2);
					tex.LoadImage(bytes);
					
					var pixelContour = TextureContourExtractor.ExtractContour(tex);
					if (pixelContour.Count > 3) {
						Reset(); // Reset clears the scene of old models before building the new one
						float userScale = cam.orthographic ? 
							(2f * cam.orthographicSize * 0.6f) : 
							(2f * screenZ * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.6f);

						foreach (var p in pixelContour) {
							points.Add(p * userScale);
						}
						
						Build();

						if (puppets.Count > 0) {
							var puppet = puppets.Last();
							puppet.ApplyTextureFront(tex, userScale, tex.width, tex.height);
						}
					} else {
						Destroy(tex);
					}
				}
			}
#endif

			// ── Waiting for Inflation UI ───────────────────────────────────────
			if (mode == OperationMode.WaitingForInflation) {
				float bw = 240f;
				float bh = 140f;
				float bx = (Screen.width - bw) / 2f;
				float by = Screen.height - 180f;

				GUI.Box(new Rect(bx - 5, by - 5, bw + 10, bh + 10), "Sketch Session");

				GUIStyle inflateStyle = new GUIStyle(style);
				inflateStyle.fontSize = 11;
				inflateStyle.fontStyle = FontStyle.Bold;
				inflateStyle.normal.textColor = new Color(0.2f, 1f, 0.2f); // Bright green

				if (GUI.Button(new Rect(bx, by + 10, 110, 30), "INFLATE", inflateStyle)) {
					Build();
					mode = OperationMode.Default;
				}

				if (GUI.Button(new Rect(bx + 120, by + 10, 110, 30), "ADD PART", style)) {
					if (points.Count > 3) {
						var contour = new List<Vector2>(points);
						multiPartContours.Add(contour);
						// We don't add to sketchCollection here yet, we'll do it in Build() or Keep all in sync
						points.Clear();
						mode = OperationMode.Draw;
					}
				}

				if (GUI.Button(new Rect(bx, by + 45, 110, 30), "REFINE", style)) {
					RefineSketches();
				}

				if (GUI.Button(new Rect(bx + 120, by + 45, 110, 30), "CLEAR ALL", style)) {
					ClearAll();
					mode = OperationMode.Default;
				}

				if (GUI.Button(new Rect(bx, by + 80, 230, 21), "Cancel Current", style)) {
					points.Clear();
					mode = OperationMode.Default;
				}
			}
		}

		/// <summary>
		/// Merges all drawn sketches into a single outer contour (Raster-based approach)
		/// </summary>
		void RefineSketches() {
			var contours = new List<List<Vector2>>(multiPartContours);
			if (points.Count > 3) contours.Add(new List<Vector2>(points));

			if (contours.Count == 0) return;

			// 1. Find bounding box
			float minX = float.MaxValue, minY = float.MaxValue;
			float maxX = float.MinValue, maxY = float.MinValue;
			foreach (var c in contours) {
				foreach (var p in c) {
					minX = Mathf.Min(minX, p.x);
					minY = Mathf.Min(minY, p.y);
					maxX = Mathf.Max(maxX, p.x);
					maxY = Mathf.Max(maxY, p.y);
				}
			}

			// Add padding
			float width = maxX - minX;
			float height = maxY - minY;
			float margin = Mathf.Max(width, height) * 0.1f;
			minX -= margin; minY -= margin; maxX += margin; maxY += margin;
			width = maxX - minX; height = maxY - minY;

			// 2. Setup Rasterization
			int res = 512;
			RenderTexture rt = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.Default);
			RenderTexture.active = rt;
			GL.Clear(true, true, Color.clear);

			// Use a simple material to draw filled polygons
			Material mat = new Material(Shader.Find("Hidden/Internal-Colored"));
			mat.SetPass(0);

			GL.PushMatrix();
			GL.LoadPixelMatrix(0, res, 0, res); // Map to RT pixels
			GL.Begin(GL.TRIANGLES);
			GL.Color(Color.white);

			foreach (var c in contours) {
				// Simple triangulation for concave polygons (Teddy's triangulation is better but this is for raster)
				var polygon = Polygon2D.Contour(c.ToArray());
				var triangulation = new Triangulation2D(polygon, 0f);
				foreach (var t in triangulation.Triangles) {
					Vector2 p0 = MapToRT(t.a.Coordinate, minX, minY, width, height, res);
					Vector2 p1 = MapToRT(t.b.Coordinate, minX, minY, width, height, res);
					Vector2 p2 = MapToRT(t.c.Coordinate, minX, minY, width, height, res);
					GL.Vertex3(p0.x, p0.y, 0);
					GL.Vertex3(p1.x, p1.y, 0);
					GL.Vertex3(p2.x, p2.y, 0);
				}
			}
			GL.End();
			GL.PopMatrix();

			// 3. Read pixels and extract contour
			Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
			tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
			tex.Apply();

			var rawContour = TextureContourExtractor.ExtractContour(tex);
			
			// 4. Transform back to world coordinates
			List<Vector2> refinedResult = new List<Vector2>();
			foreach (var p in rawContour) {
				float px = (p.x * res) + res * 0.5f;
				float py = (p.y * res) + res * 0.5f;
				
				float wx = minX + (px / (float)res) * width;
				float wy = minY + (py / (float)res) * height;
				refinedResult.Add(new Vector2(wx, wy));
			}

			// 5. Simplify, Smooth and ensure closure
			refinedResult = SketchCleaner.Clean(refinedResult, threshold);
			refinedResult = SketchCleaner.Smooth(refinedResult, 3);
			refinedResult = SketchCleaner.EnsureClosure(refinedResult, threshold);

			RenderTexture.active = null;
			RenderTexture.ReleaseTemporary(rt);
			Destroy(tex);
			Destroy(mat);

			// 6. Set as the current active sketch (Auto-pass logic)
			points = refinedResult;
			multiPartContours.Clear();
			
			Debug.Log($"[Refine] Merged {contours.Count} sketches into 1 contour with {points.Count} points.");
		}

		Vector2 MapToRT(Vector2 p, float minX, float minY, float w, float h, int res) {
			float x = (p.x - minX) / w * res;
			float y = (p.y - minY) / h * res;
			return new Vector2(x, y);
		}

		void DrawRect(Rect rect, int thickness, Color color) {
			Color prev = GUI.color;
			GUI.color = color;
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
			GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
			GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height - thickness, rect.width, thickness), Texture2D.whiteTexture);
			GUI.DrawTexture(new Rect(rect.x + rect.width - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
			GUI.color = prev;
		}

		void DrawColorEditor(GUIStyle style) {
			if (GUI.Button(new Rect(Screen.width - 210, 10, 87, 17), "Done", style)) {
				isEditMode = false;
				if (mode == OperationMode.DrawOnSurface) mode = OperationMode.Default;
			}

			// --- Brush & Eraser Controls ---
			if (mode == OperationMode.DrawOnSurface || isEditMode) {
				float ctrlY = 280; // Start higher due to smaller wheel
				
				GUIStyle lblStyle = new GUIStyle(GUI.skin.label);
				lblStyle.normal.textColor = Color.black;
				
				if (mode == OperationMode.DrawOnSurface) {
					GUI.Label(new Rect(Screen.width - 210, ctrlY, 200, 20), "Brush Size: " + brushSize.ToString("F1"), lblStyle);
					brushSize = GUI.HorizontalSlider(new Rect(Screen.width - 210, ctrlY + 22, 200, 20), brushSize, 1f, 100f);
					ctrlY += 50;
				}

				// Eraser Toggle
				Color prevBgc = GUI.backgroundColor;
				if (isEraser) GUI.backgroundColor = Color.cyan;
				if (GUI.Button(new Rect(Screen.width - 210, ctrlY, 200, 30), isEraser ? "ERASER: ON" : "ERASER: OFF", style)) {
					isEraser = !isEraser;
				}
				GUI.backgroundColor = prevBgc;
				ctrlY += 35;

				if (GUI.Button(new Rect(Screen.width - 210, ctrlY, 200, 25), "Clear All Paint", style)) {
					if (activePuppet != null) activePuppet.ClearSurfacePaint();
				}
			}

			if (isEditMode) {
				if (GUI.Button(new Rect(Screen.width - 210, 32, 41, 17), "Apply", style)) {
					if (activePuppet != null) activePuppet.ApplyColorToLastClick(selectedEditColor);
				}
				if (GUI.Button(new Rect(Screen.width - 105, 32, 41, 17), "Cancel", style)) {
					isEditMode = false;
					if (activePuppet != null) activePuppet.CancelEditMode();
				}
			}
			
			if (colorWheel == null || colorWheel.width != 150) CreateColorWheel();
			Rect wheelRect = new Rect(Screen.width - 185, 80, 150, 150); // Smaller and centered in the 200px panel
			GUI.DrawTexture(wheelRect, colorWheel);
			
			Color.RGBToHSV(selectedEditColor, out float wheelH, out float wheelS, out float _);
			float pCx = wheelRect.width / 2f;
			float pCy = wheelRect.height / 2f;
			float pDist = wheelS * pCx;
			float pAngle = wheelH * 2f * Mathf.PI - Mathf.PI;
			float pointerX = wheelRect.x + pCx + Mathf.Cos(pAngle) * pDist;
			float pointerY = wheelRect.y + pCy - Mathf.Sin(pAngle) * pDist;
			
			Color prevGUIColor = GUI.color;
			GUI.color = Color.black;
			GUI.DrawTexture(new Rect(pointerX - 4, pointerY - 4, 8, 8), Texture2D.whiteTexture);
			GUI.color = Color.white;
			GUI.DrawTexture(new Rect(pointerX - 2, pointerY - 2, 4, 4), Texture2D.whiteTexture);
			GUI.color = prevGUIColor;

			Event e = Event.current;
			if (e.isMouse && e.button == 0 && wheelRect.Contains(e.mousePosition)) {
				if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) {
					Vector2 localPos = e.mousePosition - new Vector2(wheelRect.x, wheelRect.y);
					float cx = wheelRect.width / 2f;
					float cy = wheelRect.height / 2f;
					float dist = Vector2.Distance(localPos, new Vector2(cx, cy));
					if (dist <= cx) {
						float angle = Mathf.Atan2(-(localPos.y - cy), localPos.x - cx);
						float hue = (angle + Mathf.PI) / (2f * Mathf.PI);
						float sat = dist / cx;
						selectedEditColor = Color.HSVToRGB(hue, sat, selectedValue);
						if (activePuppet != null && isEditMode) {
							activePuppet.isPreviewingColor = true;
							activePuppet.UpdatePreview(selectedEditColor);
						}
					}
					e.Use();
				}
			}

			float newSlider = GUI.HorizontalSlider(new Rect(Screen.width - 210, 250, 200, 20), selectedValue, 0f, 1f);
			if (newSlider != selectedValue) {
				selectedValue = newSlider;
				Color.RGBToHSV(selectedEditColor, out float h, out float s, out float curV);
				selectedEditColor = Color.HSVToRGB(h, s, selectedValue);
				if (activePuppet != null && isEditMode) {
					activePuppet.isPreviewingColor = true;
					activePuppet.UpdatePreview(selectedEditColor);
				}
			}

			Color prevColor = GUI.color;
			GUI.color = selectedEditColor;
			GUI.DrawTexture(new Rect(Screen.width - 210, 340, 200, 40), Texture2D.whiteTexture);
			GUI.color = prevColor;

			GUIStyle colorLabelStyle = new GUIStyle(GUI.skin.label);
			colorLabelStyle.alignment = TextAnchor.MiddleCenter;
			Color textColor = (selectedEditColor.r * 0.299f + selectedEditColor.g * 0.587f + selectedEditColor.b * 0.114f) > 0.5f ? Color.black : Color.white;
			colorLabelStyle.normal.textColor = textColor;
			GUI.Label(new Rect(Screen.width - 210, 340, 200, 40), "#" + ColorUtility.ToHtmlStringRGB(selectedEditColor), colorLabelStyle);
		}

	}

}
