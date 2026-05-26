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
		DrawOnSurface,
		Erase
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
		[SerializeField, Tooltip("Old shape-matching stiffness (kept for compatibility, unused by Fast MS solver)"), Range(0f, 1f)]
		float shapeStiffness = 0.2f;
		[SerializeField, Tooltip("Spring stiffness k for Fast Mass-Spring solver.\nEffective = h²·k (h=1/60). Try 1000–50000.")]
		float springStiffness = 5000f;
		[SerializeField, Range(0f, 1f)] float damping = 0.1f;

		[SerializeField, Range(0.1f, 2f)] float inflationAmount = 1.0f;
		[SerializeField] bool smoothHeightFields = true;

		[SerializeField] bool useDomainStitching = true;

		OperationMode mode;

		Teddy teddy;
		List<Vector2> points;
		List<Puppet> puppets = new List<Puppet>();

		// Domain Stitching System
		DomainStitchingSystem stitchingSystem;
		List<List<Vector2>> multiPartContours = new List<List<Vector2>>();  // Accumulated sketches for refinement/stitching

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

		// Bresenham sketch overlay
		Texture2D sketchOverlayTex;
		Color32[] sketchPixels;
		bool sketchTextureDirty = true;

		struct SketchSnapshot {
			public List<Vector2> pts;
			public List<List<Vector2>> contours;
			public OperationMode opMode;

			public SketchSnapshot(List<Vector2> pts, List<List<Vector2>> contours, OperationMode opMode) {
				this.pts = new List<Vector2>(pts);
				this.contours = new List<List<Vector2>>();
				foreach (var c in contours) {
					this.contours.Add(new List<Vector2>(c));
				}
				this.opMode = opMode;
			}
		}

		List<SketchSnapshot> undoStack = new List<SketchSnapshot>();

		// For click-vs-drag detection in MoveJoint mode
		Vector2 dragStartMousePos;
		
		// --- Animation Mode ---
		public bool isAnimationMode = false;
		[Header("Animation Path Settings")]
		[SerializeField] Color animPathColor = new Color(0.6f, 0.6f, 0.6f, 0.4f);
		[SerializeField] Color animBluePathColor = new Color(0.4f, 0.4f, 0.8f, 0.4f);
		[SerializeField, Range(0.05f, 5f)] float animPathBrushSize = 0.5f;
		
		// --- Color History ---
		List<Color> colorHistory = new List<Color>();
		void AddColorToHistory(Color c) {
			// Don't add gray/white defaults if they are too generic? No, user wants applied colors.
			// Remove if already exists to move it to front
			for(int i = 0; i < colorHistory.Count; i++) {
				if (ColorEquals(colorHistory[i], c)) {
					colorHistory.RemoveAt(i);
					break;
				}
			}
			colorHistory.Insert(0, c);
			if (colorHistory.Count > 6) colorHistory.RemoveAt(6);
		}
		bool ColorEquals(Color a, Color b) {
			return Mathf.Abs(a.r-b.r) < 0.001f && Mathf.Abs(a.g-b.g) < 0.001f && Mathf.Abs(a.b-b.b) < 0.001f;
		}
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
			// Handle Ctrl+Z Undo
			if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.Z)) {
				PerformUndo();
			}

			// Keyboard shortcuts for interaction modes
			if (Input.GetKeyDown(KeyCode.E) && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) {
				isEditMode = !isEditMode;
				isAnimationMode = false;
				isRigEditMode = false;
				mode = OperationMode.Default;
				if (isEditMode && activePuppet != null) activePuppet.StartEditMode();
				else if (!isEditMode && activePuppet != null) activePuppet.CancelEditMode();
			}
			if (Input.GetKeyDown(KeyCode.P)) {
				if (mode == OperationMode.DrawOnSurface) {
					mode = OperationMode.Default;
				} else {
					mode = OperationMode.DrawOnSurface;
					isEditMode = false;
					isAnimationMode = false;
					isRigEditMode = false;
				}
			}
			if (Input.GetKeyDown(KeyCode.R)) {
				if (isRigEditMode) {
					isRigEditMode = false;
					rigEditPuppet = null;
				} else if (activePuppet != null) {
					isRigEditMode = true;
					rigEditPuppet = activePuppet;
					isEditMode = false;
					isAnimationMode = false;
					mode = OperationMode.Default;
				}
			}
			if (Input.GetKeyDown(KeyCode.L)) {
				isLassoMode = !isLassoMode;
				mode = isLassoMode ? OperationMode.LassoJoint : OperationMode.Default;
			}
			if (Input.GetKeyDown(KeyCode.A)) {
				isAnimationMode = !isAnimationMode;
				isEditMode = false;
				isRigEditMode = false;
				mode = OperationMode.Default;
			}
			if (Input.GetKeyDown(KeyCode.G)) {
				enableGravity = !enableGravity;
				foreach (var p in puppets) {
					if (p != null) {
						var body = p.GetComponent<Rigidbody>();
						if (body != null) body.useGravity = enableGravity;
						p.gravity = enableGravity ? 9.81f : 0f;
					}
				}
			}
			if (Input.GetKeyDown(KeyCode.M)) {
				showSkeleton = !showSkeleton;
			}

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
				p.springStiffness = springStiffness;  // Fast Mass-Spring stiffness
				p.damping = damping;
				p.animPathColor = animPathColor;
				p.animBluePathColor = animBluePathColor;
				p.animPathBrushSize = animPathBrushSize;
			}

			var bottom = cam.ViewportToWorldPoint(new Vector3(0.5f, 0f, screenZ));
			floor.transform.position = bottom;

			var screen = Input.mousePosition;
			screen.z = screenZ;

			Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
			// Area for the new UI controls
			bool isOverLeftUI  = new Rect(10, 10, 200, 500).Contains(guiMouse);
			
			// Right panel: dynamically calculate height based on active panels
			float rightPanelHeight = 270; // Base (Mesh Gen + Interaction Modes)
			if (isEditMode || mode == OperationMode.DrawOnSurface) rightPanelHeight += 400;
			else if (isRigEditMode) rightPanelHeight += 150;
			else if (isAnimationMode) rightPanelHeight += 200;
			if (showRegionPanel) rightPanelHeight += 130;

			// Right UI Area for blocking clicks
			bool isOverRightUI = new Rect(Screen.width - 220, 0, 220, Screen.height).Contains(guiMouse);
			
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
							if (isDrawingEnabled && !isAnimationMode && mode != OperationMode.Erase) {
								activePuppet = null;
								isEditMode = false;
								PushUndoState(); // Save state before starting to draw
								// Clear(); // Don't clear multiPartContours here, just the current points
								points.Clear();
								sketchTextureDirty = true;
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
						sketchTextureDirty = true;
						mode = OperationMode.Default;
					}
				} else {
					var p = cam.ScreenToWorldPoint(screen);
					p = transform.InverseTransformPoint(p);
					var p2D = new Vector2(p.x, p.y);
					if(points.Count <= 0 || Vector2.Distance(p2D, points.Last()) > threshold) {
						points.Add(p2D);
						sketchTextureDirty = true;
					}
				}
				break;

			case OperationMode.WaitingForInflation:
				if(Input.GetMouseButtonDown(0) && !isOverUI) {
					// If clicking background while waiting, start a new drawing
					PushUndoState(); // Save state before starting to draw
					points.Clear();
					sketchTextureDirty = true;
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

			case OperationMode.Erase:
				if (Input.GetMouseButtonDown(0) && !isOverUI) {
					PushUndoState();
				} else if (Input.GetMouseButton(0) && !isOverUI) {
					var p = cam.ScreenToWorldPoint(screen);
					p = transform.InverseTransformPoint(p);
					var p2D = new Vector2(p.x, p.y);
					float eraserRadius = 0.5f;
					bool modified = false;

					if (points != null && points.Count > 0) {
						var newParts = SplitContourByEraser(points, p2D, eraserRadius, ref modified);
						if (newParts.Count > 0) {
							points = newParts[newParts.Count - 1];
							for(int i = 0; i < newParts.Count - 1; i++) multiPartContours.Add(newParts[i]);
						} else if (modified) {
							points.Clear();
						}
					}

					if (multiPartContours != null && multiPartContours.Count > 0) {
						List<List<Vector2>> newMultiParts = new List<List<Vector2>>();
						for(int c = 0; c < multiPartContours.Count; c++) {
							var segments = SplitContourByEraser(multiPartContours[c], p2D, eraserRadius, ref modified);
							newMultiParts.AddRange(segments);
						}
						if (modified) multiPartContours = newMultiParts;
					}

					if (modified) {
						sketchTextureDirty = true;
					}
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

		ClearAll();
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

		// 2. Setup CPU Rasterization using Bresenham's line algorithm
		int res = 1024;
		Color32[] pixels = new Color32[res * res];
		Color32 white = new Color32(255, 255, 255, 255);

		// Rasterize all contours as outlines on CPU
		foreach (var c in contours) {
			for (int i = 0; i < c.Count; i++) {
				Vector2 p0 = MapToRT(c[i], minX, minY, width, height, res);
				Vector2 p1 = MapToRT(c[(i + 1) % c.Count], minX, minY, width, height, res);
				DrawLineBresenham(pixels, res, res, p0, p1, white, 5); // 5px thickness for clean Moore tracing
			}
		}

		Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
		tex.SetPixels32(pixels);
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
		Destroy(tex);

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
			sketchTextureDirty = true;
		}

		void ClearAll() {
			points.Clear();
			multiPartContours.Clear();
			sketchTextureDirty = true;
		}

		public void Save () {
			LocalStorage.SaveList<Vector2>(points, "points.json");
		}

		void PushUndoState() {
			undoStack.Add(new SketchSnapshot(points, multiPartContours, mode));
			if (undoStack.Count > 10) undoStack.RemoveAt(0);
		}

		void PerformUndo() {
			if (undoStack.Count == 0) {
				points.Clear();
				multiPartContours.Clear();
				mode = OperationMode.Default;
				sketchTextureDirty = true;
				return;
			}
			var state = undoStack[undoStack.Count - 1];
			undoStack.RemoveAt(undoStack.Count - 1);
			points = state.pts;
			multiPartContours = state.contours;
			mode = state.opMode;
			sketchTextureDirty = true;
		}

		public void Reset () {
			puppets.ForEach(puppet => {
				puppet.Ignore();
				Destroy(puppet.gameObject);
			});
			puppets.Clear();
			ClearAll(); // Clear all sketch lines when resetting
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

		List<List<Vector2>> SplitContourByEraser(List<Vector2> contour, Vector2 p2D, float radius, ref bool modified) {
			List<List<Vector2>> segments = new List<List<Vector2>>();
			List<Vector2> currentPart = new List<Vector2>();
			for(int i = 0; i < contour.Count; i++) {
				if (Vector2.Distance(p2D, contour[i]) > radius) {
					currentPart.Add(contour[i]);
				} else {
					modified = true;
					if (currentPart.Count > 0) {
						if (currentPart.Count > 1) segments.Add(currentPart);
						currentPart = new List<Vector2>();
					}
				}
			}
			if (currentPart.Count > 1) {
				segments.Add(currentPart);
			}
			return segments;
		}

		void OnRenderObject () {

			if (mode == OperationMode.Erase) {
				var screen = Input.mousePosition;
				screen.z = screenZ;
				var p = cam.ScreenToWorldPoint(screen);
				p = transform.InverseTransformPoint(p);
				
				lineMat.SetColor("_Color", new Color(1f, 0.2f, 0.2f, 0.8f));
				lineMat.SetPass(0);
				GL.Begin(GL.LINES);
				int segs = 32;
				float r = 0.5f;
				for(int i = 0; i <= segs; i++) {
					float a1 = i * Mathf.PI * 2f / segs;
					float a2 = (i+1) * Mathf.PI * 2f / segs;
					GL.Vertex3(p.x + Mathf.Cos(a1)*r, p.y + Mathf.Sin(a1)*r, p.z);
					GL.Vertex3(p.x + Mathf.Cos(a2)*r, p.y + Mathf.Sin(a2)*r, p.z);
				}
				GL.End();
			}

			// Sketch GL vector lines replaced with CPU Bresenham texture overlay in OnGUI

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
			if ((points != null && points.Count > 0) || multiPartContours.Count > 0) {
				RenderSketchWithBresenham();
				if (sketchOverlayTex != null) {
					var prevGUIColor = GUI.color;
					GUI.color = Color.white;
					GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), sketchOverlayTex);
					GUI.color = prevGUIColor;
				}
			}

			Color _bg = GUI.backgroundColor;
			// Set global button background to dark grey/black matching the reference
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
			
			GUIStyle style = new GUIStyle(GUI.skin.button);
			style.fontSize = 9;
			style.normal.textColor = Color.white;
			style.hover.textColor = Color.white;
			style.active.textColor = Color.white;
			
			GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
			labelStyle.fontSize = 11;
			labelStyle.normal.textColor = Color.white;

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

			// ── Left Side Panels ─────────────────────────────────────────────
			
			// -- General Panel (Y:5, h:230) --
			DrawPanel(new Rect(5, 5, 145, 230), "─ General ─");
			
			if (isDrawingEnabled && mode != OperationMode.Erase) GUI.backgroundColor = new Color(0.15f, 0.55f, 0.9f); // Blue when active
			if (GUI.Button(new Rect(15, 30, 60, 24), "DRAW", style)) {
				isDrawingEnabled = !isDrawingEnabled;
				if (mode == OperationMode.Erase) mode = OperationMode.Default;
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			if (mode == OperationMode.Erase) GUI.backgroundColor = new Color(0.9f, 0.2f, 0.2f); // Red when active
			if (GUI.Button(new Rect(80, 30, 60, 24), "ERASE", style)) {
				mode = (mode == OperationMode.Erase) ? OperationMode.Default : OperationMode.Erase;
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			if (GUI.Button(new Rect(15, 65, 125, 22), "[G] Grav: " + (enableGravity ? "ON" : "OFF"), style)) {
				enableGravity = !enableGravity;
				foreach (var p in puppets) {
					if (p != null) {
						var body = p.GetComponent<Rigidbody>();
						if (body != null) body.useGravity = enableGravity;
						p.gravity = enableGravity ? 9.81f : 0f;
					}
				}
			}

			if (GUI.Button(new Rect(15, 95, 125, 22), "[~] Reset Rot", style)) {
				currentRotation = Vector3.zero;
			}

			if (GUI.Button(new Rect(15, 125, 125, 22), "[M] Show Mesh", style)) {
				showSkeleton = !showSkeleton;
			}

			if (GUI.Button(new Rect(15, 155, 125, 22), "<< Undo (Ctrl+Z)", style)) {
				PerformUndo();
			}

			GUI.backgroundColor = new Color(0.8f, 0.3f, 0.3f);
			if (GUI.Button(new Rect(15, 185, 125, 24), "XX Clear Sketch", style)) {
				PushUndoState();
				ClearAll();
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			// -- Transform Panel (Y:245, h:160) --
			DrawPanel(new Rect(5, 245, 145, 160), "─ Transform ─");
			GUIStyle rotLbl = new GUIStyle(GUI.skin.label);
			rotLbl.fontSize = 10;
			rotLbl.normal.textColor = Color.white;
			GUI.Label(new Rect(15, 265, 125, 16), "Rotate X: " + currentRotation.x.ToString("F0"), rotLbl);
			currentRotation.x = GUI.HorizontalSlider(new Rect(15, 281, 125, 18), currentRotation.x, 0f, 360f);
			GUI.Label(new Rect(15, 301, 125, 16), "Rotate Y: " + currentRotation.y.ToString("F0"), rotLbl);
			currentRotation.y = GUI.HorizontalSlider(new Rect(15, 317, 125, 18), currentRotation.y, 0f, 360f);
			GUI.Label(new Rect(15, 337, 125, 16), "Rotate Z: " + currentRotation.z.ToString("F0"), rotLbl);
			currentRotation.z = GUI.HorizontalSlider(new Rect(15, 353, 125, 18), currentRotation.z, 0f, 360f);

			// -- Files Panel (Y:415, h:82) --
			DrawPanel(new Rect(5, 415, 145, 82), "─ Files ─");
#if UNITY_EDITOR
			if (GUI.Button(new Rect(15, 435, 125, 22), "[+] Import PNG", style)) {
				string path = EditorUtility.OpenFilePanel("Select PNG Image", "", "png");
				if (!string.IsNullOrEmpty(path)) {
					byte[] bytes = File.ReadAllBytes(path);
					Texture2D tex = new Texture2D(2, 2);
					tex.LoadImage(bytes);
					
					var pixelContour = TextureContourExtractor.ExtractContour(tex);
					if (pixelContour.Count > 3) {
						Reset();
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

			if (GUI.Button(new Rect(15, 463, 125, 22), "[-] Export GLB", style)) {
				if (puppets.Count > 0) {
					Puppet pup = activePuppet != null ? activePuppet : puppets.Last();
					string path = UnityEditor.EditorUtility.SaveFilePanel("Export GLB", "", "puppet.glb", "glb");
					if (!string.IsNullOrEmpty(path)) {
						var mf = pup.GetComponent<MeshFilter>();
						var frames = pup.GenerateAnimationFrames();
						GLBExporter.ExportAnimation(path, mf.sharedMesh, pup.mainTexture, frames, 60f);
					}
				}
			}
#endif

			// ── Right Side Panels ────────────────────────────────────────────

			// -- Mesh Generation Panel (always shown) --
			DrawPanel(new Rect(Screen.width - 210, 5, 200, 135), "─ Mesh Generation ─");
			GUI.Label(new Rect(Screen.width - 200, 30, 180, 16), "Inflation: " + inflationAmount.ToString("F2"), labelStyle);
			inflationAmount = GUI.HorizontalSlider(new Rect(Screen.width - 200, 50, 180, 18), inflationAmount, 0.1f, 2f);
			smoothHeightFields = GUI.Toggle(new Rect(Screen.width - 200, 75, 180, 20), smoothHeightFields, " Smooth Mesh");
			showSkeleton = GUI.Toggle(new Rect(Screen.width - 200, 100, 180, 20), showSkeleton, " Show Skeleton");

			// -- Interaction Modes Panel (always shown) --
			DrawPanel(new Rect(Screen.width - 210, 150, 200, 115), "─ Interaction Modes ─");
			
			// Highlight current mode button by changing background color
			if (isEditMode) GUI.backgroundColor = new Color(0.2f, 0.6f, 1f, 1f);
			if (GUI.Button(new Rect(Screen.width - 200, 175, 85, 22), "[E] Edit", style)) {
				isEditMode = !isEditMode;
				isAnimationMode = false;
				isRigEditMode = false;
				mode = OperationMode.Default;
				if (isEditMode && activePuppet != null) activePuppet.StartEditMode();
				else if (!isEditMode && activePuppet != null) activePuppet.CancelEditMode();
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			if (mode == OperationMode.DrawOnSurface) GUI.backgroundColor = new Color(0.2f, 0.6f, 1f, 1f);
			if (GUI.Button(new Rect(Screen.width - 105, 175, 85, 22), "[P] Paint", style)) {
				if (mode == OperationMode.DrawOnSurface) {
					mode = OperationMode.Default;
				} else {
					mode = OperationMode.DrawOnSurface;
					isEditMode = false;
					isAnimationMode = false;
					isRigEditMode = false;
				}
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			if (isRigEditMode) GUI.backgroundColor = new Color(0.2f, 0.6f, 1f, 1f);
			if (GUI.Button(new Rect(Screen.width - 200, 202, 85, 22), "[R] Rig", style)) {
				if (isRigEditMode) {
					isRigEditMode = false;
					rigEditPuppet = null;
				} else if (activePuppet != null) {
					isRigEditMode = true;
					rigEditPuppet = activePuppet;
					isEditMode = false;
					isAnimationMode = false;
					mode = OperationMode.Default;
				}
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			if (isLassoMode) GUI.backgroundColor = new Color(0.2f, 0.6f, 1f, 1f);
			if (GUI.Button(new Rect(Screen.width - 105, 202, 85, 22), "[L] Phys", style)) {
				isLassoMode = !isLassoMode;
				mode = isLassoMode ? OperationMode.LassoJoint : OperationMode.Default;
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			if (isAnimationMode) GUI.backgroundColor = new Color(0.2f, 0.6f, 1f, 1f);
			if (GUI.Button(new Rect(Screen.width - 200, 229, 180, 22), "[A] Anim", style)) {
				isAnimationMode = !isAnimationMode;
				isEditMode = false;
				isRigEditMode = false;
				mode = OperationMode.Default;
			}
			GUI.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

			// -- Contextual Panels --
			if (isRigEditMode) {
				// Rig Mode Panel
				float rx = Screen.width - 210;
				float ry = 270;
				DrawPanel(new Rect(rx, ry, 200, 120), "─ Rig Edit Mode ─");
				if (rigSelectedJoint >= 0) {
					if (GUI.Button(new Rect(rx + 10, ry + 30, 85, 22), (rigConnectMode ? "Cancel Link" : "Connect"), style)) {
						if (rigConnectMode) {
							rigConnectMode = false;
							rigConnectFirst = -1;
						} else {
							rigConnectMode = true;
							rigConnectFirst = rigSelectedJoint;
						}
					}
					if (GUI.Button(new Rect(rx + 105, ry + 30, 85, 22), "Remove", style)) {
						rigEditPuppet.RemoveJoint(rigSelectedJoint);
						rigSelectedJoint = -1;
					}
				}
				if (GUI.Button(new Rect(rx + 10, ry + 60, 180, 24), "Done", style)) {
					isRigEditMode    = false;
					rigEditPuppet    = null;
					rigSelectedJoint = -1;
					rigConnectMode   = false;
					rigConnectFirst  = -1;
				}
			} else if (isAnimationMode) {
				// Anim Mode Panel
				float rx = Screen.width - 210;
				float ry = 270;
				DrawPanel(new Rect(rx, ry, 200, 210), "─ Anim Mode ─");
				if (GUI.Button(new Rect(rx + 10, ry + 30, 180, 22), "Exit Anim Mode", style)) isAnimationMode = false;
				
				GUIStyle animLbl = new GUIStyle(GUI.skin.label);
				animLbl.fontSize = 11; animLbl.normal.textColor = Color.white;

				if (activePuppet != null) {
					if (GUI.Button(new Rect(rx + 10, ry + 60, 180, 22), "Clear Anims", style)) activePuppet.ClearAnimation();
					GUI.Label(new Rect(rx + 10, ry + 90, 180, 20), "Frames: " + activePuppet.animMaxFrames, animLbl);
					GUI.Label(new Rect(rx + 10, ry + 110, 180, 20), "Current: " + activePuppet.animCurrentFrame, animLbl);
				}

				GUI.Label(new Rect(rx + 10, ry + 135, 180, 20), "Brush Size: " + animPathBrushSize.ToString("F2"), animLbl);
				animPathBrushSize = GUI.HorizontalSlider(new Rect(rx + 10, ry + 155, 180, 18), animPathBrushSize, 0.05f, 5f);
			} else if (isEditMode || mode == OperationMode.DrawOnSurface) {
				// Surface Drawing Panel
				DrawColorEditor(style);
			}

			// -- Region Physics Panel --
			if (showRegionPanel && regionPuppet != null && selectedRegion >= 0) {
				float px = Screen.width - 210;
				float py = (isEditMode || mode == OperationMode.DrawOnSurface || isAnimationMode || isRigEditMode) ? 720f : 270f;
				Rect panelRect = new Rect(px, py, 200, 120);

				if (Event.current.type == EventType.MouseDown && !panelRect.Contains(Event.current.mousePosition)) {
					showRegionPanel = false;
				}

				DrawPanel(panelRect, $"Region {selectedRegion} Physics");

				GUIStyle regionLbl = new GUIStyle(GUI.skin.label);
				regionLbl.fontSize = 10;
				regionLbl.normal.textColor = Color.white;

				GUI.Label(new Rect(px + 10, py + 30, 180, 20), $"Stiffness: {regionEditStiffness:F2}", regionLbl);
				regionEditStiffness = GUI.HorizontalSlider(new Rect(px + 10, py + 50, 180, 18), regionEditStiffness, 0f, 1f);

				GUI.Label(new Rect(px + 10, py + 72, 180, 20), $"Damping:   {regionEditDamping:F2}", regionLbl);
				regionEditDamping = GUI.HorizontalSlider(new Rect(px + 10, py + 92, 180, 18), regionEditDamping, 0f, 1f);
				
				regionPuppet.SetRegionParams(selectedRegion, regionEditStiffness, regionEditDamping);
			}
			
			GUI.backgroundColor = _bg;

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

			// 2. Setup CPU Rasterization using Bresenham's line algorithm
			int res = 512;
			Color32[] pixels = new Color32[res * res];
			Color32 white = new Color32(255, 255, 255, 255);

			foreach (var c in contours) {
				for (int i = 0; i < c.Count; i++) {
					Vector2 p0 = MapToRT(c[i], minX, minY, width, height, res);
					Vector2 p1 = MapToRT(c[(i + 1) % c.Count], minX, minY, width, height, res);
					DrawLineBresenham(pixels, res, res, p0, p1, white, 5);
				}
			}

			Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
			tex.SetPixels32(pixels);
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

			Destroy(tex);

			// 6. Set as the current active sketch (Auto-pass logic)
			points = refinedResult;
			multiPartContours.Clear();
			sketchTextureDirty = true;
			
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

		void DrawPanel(Rect rect, string title) {
			Color prevColor = GUI.color;
			
			// 1. Draw solid dark grey background
			GUI.color = new Color(0.12f, 0.12f, 0.12f, 0.95f);
			GUI.DrawTexture(rect, Texture2D.whiteTexture);
			
			// 2. Draw a blue line at the very top of the panel (2px thick)
			GUI.color = new Color(0f, 0.5f, 1f, 1f); // Vibrant blue
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 2), Texture2D.whiteTexture);
			
			// 3. Draw a thin white border around the panel
			GUI.color = new Color(0.3f, 0.3f, 0.3f, 1f);
			GUI.DrawTexture(new Rect(rect.x, rect.y + 2, rect.width, 1), Texture2D.whiteTexture); // top border
			GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height - 1, rect.width, 1), Texture2D.whiteTexture); // bottom
			GUI.DrawTexture(new Rect(rect.x, rect.y + 2, 1, rect.height - 3), Texture2D.whiteTexture); // left
			GUI.DrawTexture(new Rect(rect.x + rect.width - 1, rect.y + 2, 1, rect.height - 3), Texture2D.whiteTexture); // right
			
			GUI.color = prevColor;

			GUIStyle headerStyle = new GUIStyle(GUI.skin.label);
			headerStyle.fontSize = 11;
			headerStyle.fontStyle = FontStyle.Bold;
			headerStyle.alignment = TextAnchor.UpperCenter;
			headerStyle.normal.textColor = Color.white;
			GUI.Label(new Rect(rect.x, rect.y + 5, rect.width, 20), title, headerStyle);
		}

		void DrawLineBresenham(Color32[] pixels, int w, int h, Vector2 p0, Vector2 p1, Color32 color, int thickness = 5) {
			int x0 = Mathf.RoundToInt(p0.x);
			int y0 = Mathf.RoundToInt(p0.y);
			int x1 = Mathf.RoundToInt(p1.x);
			int y1 = Mathf.RoundToInt(p1.y);

			int dx = Mathf.Abs(x1 - x0);
			int dy = Mathf.Abs(y1 - y0);
			int sx = x0 < x1 ? 1 : -1;
			int sy = y0 < y1 ? 1 : -1;
			int err = dx - dy;

			while (true) {
				for (int ty = -thickness / 2; ty <= thickness / 2; ty++) {
					for (int tx = -thickness / 2; tx <= thickness / 2; tx++) {
						int px = x0 + tx;
						int py = y0 + ty;
						if (px >= 0 && px < w && py >= 0 && py < h) {
							pixels[py * w + px] = color;
						}
					}
				}

				if (x0 == x1 && y0 == y1) break;
				int e2 = 2 * err;
				if (e2 > -dy) {
					err -= dy;
					x0 += sx;
				}
				if (e2 < dx) {
					err += dx;
					y0 += sy;
				}
			}
		}

		void RenderSketchWithBresenham() {
			int w = Screen.width;
			int h = Screen.height;
			
			if (sketchOverlayTex == null || sketchOverlayTex.width != w || sketchOverlayTex.height != h) {
				if (sketchOverlayTex != null) {
					Destroy(sketchOverlayTex);
				}
				sketchOverlayTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
				sketchPixels = new Color32[w * h];
				sketchTextureDirty = true;
			}

			if (sketchTextureDirty) {
				System.Array.Clear(sketchPixels, 0, sketchPixels.Length);

				Color32 strokeColor = new Color32(0, 0, 0, 255); // Black for active sketch
				Color32 accumColor = new Color32(0, 0, 0, 255);  // Fully opaque black for accumulated sketches

				// 1. Draw accumulated multipart contours
				foreach (var contour in multiPartContours) {
					if (contour.Count < 2) continue;
					for (int i = 0; i < contour.Count - 1; i++) {
						Vector3 w0 = transform.TransformPoint(contour[i]);
						Vector3 w1 = transform.TransformPoint(contour[i + 1]);
						Vector3 s0 = cam.WorldToScreenPoint(w0);
						Vector3 s1 = cam.WorldToScreenPoint(w1);
						DrawLineBresenham(sketchPixels, w, h, s0, s1, accumColor, 4);
					}
				}

				// 2. Draw current active points
				if (points != null && points.Count > 1) {
					for (int i = 0; i < points.Count - 1; i++) {
						Vector3 w0 = transform.TransformPoint(points[i]);
						Vector3 w1 = transform.TransformPoint(points[i + 1]);
						Vector3 s0 = cam.WorldToScreenPoint(w0);
						Vector3 s1 = cam.WorldToScreenPoint(w1);
						DrawLineBresenham(sketchPixels, w, h, s0, s1, strokeColor, 4);
					}
				}

				sketchOverlayTex.SetPixels32(sketchPixels);
				sketchOverlayTex.Apply();
				sketchTextureDirty = false;
			}
		}

		void OnDestroy() {
			if (sketchOverlayTex != null) {
				Destroy(sketchOverlayTex);
			}
		}

		void DrawColorEditor(GUIStyle style) {
			float startY = 270;
			DrawPanel(new Rect(Screen.width - 210, startY, 200, 440), "─ Surface Drawing ─");
			
			// Done / Exit button
			if (GUI.Button(new Rect(Screen.width - 200, startY + 30, 180, 24), "Done", style)) {
				isEditMode = false;
				if (mode == OperationMode.DrawOnSurface) mode = OperationMode.Default;
				if (activePuppet != null) activePuppet.CommitEditMode();
			}

			// Color wheel
			if (colorWheel == null || colorWheel.width != 150) CreateColorWheel();
			Rect wheelRect = new Rect(Screen.width - 185, startY + 65, 150, 150);
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

			GUIStyle textLblStyle = new GUIStyle(GUI.skin.label);
			textLblStyle.normal.textColor = Color.white;
			textLblStyle.fontSize = 10;

			// Brightness slider
			GUI.Label(new Rect(Screen.width - 200, startY + 225, 180, 16), "Brightness (Value)", textLblStyle);
			float newSlider = GUI.HorizontalSlider(new Rect(Screen.width - 200, startY + 242, 180, 18), selectedValue, 0f, 1f);
			if (newSlider != selectedValue) {
				selectedValue = newSlider;
				Color.RGBToHSV(selectedEditColor, out float h, out float s, out float curV);
				selectedEditColor = Color.HSVToRGB(h, s, selectedValue);
				if (activePuppet != null && isEditMode) {
					activePuppet.isPreviewingColor = true;
					activePuppet.UpdatePreview(selectedEditColor);
				}
			}

			// Hex display
			Color prevColor = GUI.color;
			GUI.color = selectedEditColor;
			GUI.DrawTexture(new Rect(Screen.width - 200, startY + 265, 80, 24), Texture2D.whiteTexture);
			GUI.color = prevColor;

			GUIStyle colorLabelStyle = new GUIStyle(GUI.skin.label);
			colorLabelStyle.alignment = TextAnchor.MiddleCenter;
			Color textColor = (selectedEditColor.r * 0.299f + selectedEditColor.g * 0.587f + selectedEditColor.b * 0.114f) > 0.5f ? Color.black : Color.white;
			colorLabelStyle.normal.textColor = textColor;
			colorLabelStyle.fontSize = 10;
			GUI.Label(new Rect(Screen.width - 200, startY + 265, 80, 24), "#" + ColorUtility.ToHtmlStringRGB(selectedEditColor), colorLabelStyle);

			// Color history / swatches (draw 6 small squares from left-over history)
			float swatchX = Screen.width - 110;
			float swatchY = startY + 265;
			for (int sIdx = 0; sIdx < 6; sIdx++) {
				Color sColor = (colorHistory != null && sIdx < colorHistory.Count) ? colorHistory[sIdx] : Color.white;
				GUI.color = sColor;
				Rect sRect = new Rect(swatchX + (sIdx % 3) * 30, swatchY + (sIdx / 3) * 13, 25, 10);
				if (GUI.Button(sRect, GUIContent.none)) {
					selectedEditColor = sColor;
					Color.RGBToHSV(selectedEditColor, out float h, out float s, out float v);
					selectedValue = v;
					if (activePuppet != null && isEditMode) {
						activePuppet.isPreviewingColor = true;
						activePuppet.UpdatePreview(selectedEditColor);
					}
				}
			}
			GUI.color = prevColor;

			// Brush Size (only if Paint Mode)
			if (mode == OperationMode.DrawOnSurface) {
				GUI.Label(new Rect(Screen.width - 200, startY + 300, 180, 16), "Brush Size: " + brushSize.ToString("F1"), textLblStyle);
				brushSize = GUI.HorizontalSlider(new Rect(Screen.width - 200, startY + 318, 180, 18), brushSize, 1f, 100f);
			} else if (isEditMode) {
				// Apply / Cancel for Edit Mode
				if (GUI.Button(new Rect(Screen.width - 200, startY + 300, 85, 22), "Apply", style)) {
					if (activePuppet != null) {
						activePuppet.ApplyColorToLastClick(selectedEditColor);
						AddColorToHistory(selectedEditColor);
					}
				}
				if (GUI.Button(new Rect(Screen.width - 105, startY + 300, 85, 22), "Cancel", style)) {
					isEditMode = false;
					if (activePuppet != null) activePuppet.CancelEditMode();
				}
			}

			// Clear paint button
			if (GUI.Button(new Rect(Screen.width - 200, startY + 350, 180, 24), "Clear All Paint", style)) {
				if (activePuppet != null) activePuppet.ClearSurfacePaint();
			}
		}

	}

}