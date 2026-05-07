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

namespace mattatz.TeddySystem.Example {

	public enum OperationMode {
		Default,
		Draw,
		Move,
		MoveJoint,
		LassoJoint,
		WaitingForInflation
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

		[Header("Domain Stitching (Advanced)")]
		[SerializeField] bool useDomainStitching = false;
		[SerializeField, Range(0.1f, 2f)] float domainInflationAmount = 1.0f;
		[SerializeField] bool smoothHeightFields = true;

		OperationMode mode;

		Teddy teddy;
		List<Vector2> points;
		List<Puppet> puppets = new List<Puppet>();

		// Domain Stitching System
		DomainStitchingSystem stitchingSystem;
		List<List<Vector2>> multiPartContours = new List<List<Vector2>>();  // For domain stitching

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

		void CreateColorWheel() {
			int size = 200;
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
							selected.Select();
							if (isEditMode) {
								selected.OnTextureClicked(hit, selectedEditColor);
							}
							startPoint = hit.point;
							origin = selected.transform.position;
							mode = OperationMode.Move;
						} else if (!isLassoMode) {
							// Only allow sketch-drawing when enabled AND lasso is NOT armed
							if (isDrawingEnabled && !isAnimationMode) {
								activePuppet = null;
								isEditMode = false;
								Clear();
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
					Clear();
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

				if (Input.GetMouseButtonUp(0) && activePuppet.isRecordingAnim) {
					activePuppet.StopRecordingAnimation();
				}
			}
		}

		void Build () {
			if(points.Count < 3) return;

			points = Utils2D.Constrain(points, threshold);
			if(points.Count < 3) return;

			if (useDomainStitching) {
				BuildWithDomainStitching();
			} else {
				BuildTraditional();
			}
		}

		/// <summary>
		/// Traditional mesh building (original Teddy method)
		/// </summary>
		void BuildTraditional () {
			teddy = new Teddy(points);
			var mesh = teddy.Build(MeshSmoothingMethod.HC, 2, 0.2f, 0.75f);
			var bones = teddy.GetSkeletonBones();

			CreatePuppet(mesh, bones);
		}

		/// <summary>
		/// Build mesh using Domain Stitching Algorithm
		/// Supports multiple body parts with proper mesh closure
		/// </summary>
		void BuildWithDomainStitching () {
			// Initialize or clear the stitching system
			stitchingSystem = new DomainStitchingSystem();

			// For now, treat the drawn contour as a single part
			// In future: support multi-part drawings (e.g., separate body and limb strokes)
			multiPartContours.Clear();
			multiPartContours.Add(new List<Vector2>(points));

			// Determine if the contour is open or closed
			List<bool> isOpenList = new List<bool>();
			foreach (var contour in multiPartContours) {
				float endDistance = Vector2.Distance(contour[0], contour[contour.Count - 1]);
				isOpenList.Add(endDistance > 0.1f);
			}

			// Initialize stitching system with contours
			stitchingSystem.InitializeFromContours(multiPartContours, isOpenList);

			// Generate stitched mesh
			var mesh = stitchingSystem.GenerateStitchedMesh(domainInflationAmount, smoothHeightFields);

			if (mesh != null) {
				// For domain stitching, we don't have skeleton bones yet
				// This can be added by skeletal extraction from the generated mesh
				CreatePuppet(mesh, new List<(Vector3, Vector3)>());

				// Log statistics
				var (vCount, tCount, dCount) = stitchingSystem.GetMeshStats();
				Debug.Log($"[Domain Stitching] Vertices: {vCount}, Triangles: {tCount}, Domains: {dCount}");
			}
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
				lineMat.SetColor("_Color", Color.white);
				lineMat.SetPass(0);
				GL.Begin(GL.LINES);
				for(int i = 0, n = points.Count - 1; i < n; i++) {
					GL.Vertex(points[i]); GL.Vertex(points[i + 1]);
				}
				GL.End();
				GL.PopMatrix();
			}

			// Draw lasso outline in screen space
			if (mode == OperationMode.LassoJoint && lassoPoints != null && lassoPoints.Count > 1) {
				GL.PushMatrix();
				GL.LoadPixelMatrix();
				lineMat.SetColor("_Color", new Color(1f, 1f, 0f, 0.9f));
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
			style.fontSize = 20;
			
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

			// ── Domain Stitching Controls ────────────────────────────────────
			GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
			labelStyle.fontSize = 14;
			labelStyle.normal.textColor = Color.white;

			GUI.Box(new Rect(Screen.width - 210, 10, 200, isDrawingEnabled ? 140 : 115), GUIContent.none);
			GUI.Label(new Rect(Screen.width - 200, 15, 180, 20), "─ Domain Stitching ─", labelStyle);

			bool prevStitching = useDomainStitching;
			useDomainStitching = GUI.Toggle(new Rect(Screen.width - 200, 38, 20, 20), useDomainStitching, "Enabled");

			if (prevStitching != useDomainStitching) {
				Debug.Log($"Domain Stitching: {(useDomainStitching ? "ON" : "OFF")}");
			}

			GUI.Label(new Rect(Screen.width - 200, 62, 180, 16), "Inflation: " + domainInflationAmount.ToString("F2"), labelStyle);
			domainInflationAmount = GUI.HorizontalSlider(new Rect(Screen.width - 200, 82, 180, 18), domainInflationAmount, 0.1f, 2f);

			GUI.enabled = useDomainStitching;
			smoothHeightFields = GUI.Toggle(new Rect(Screen.width - 200, 105, 20, 20), smoothHeightFields, "Smooth");
			GUI.enabled = true;

			// ── Normal play mode right panel ────────────────────────────────────
			if (!isEditMode && !isRigEditMode && !isAnimationMode) {
				if (GUI.Button(new Rect(Screen.width - 210, 160, 200, 50), "Edit Mode", style)) {
					isEditMode = true;
					if (activePuppet != null) activePuppet.StartEditMode();
				}

				// Lasso Joints button (only in normal play mode, not Edit Mode)
				GUI.enabled = (puppets.Count > 0);
				if (GUI.Button(new Rect(Screen.width - 210, 215, 200, 45), isLassoMode ? "▶ Drawing Lasso..." : "Lasso Joints", style)) {
					if (!isLassoMode) {
						isLassoMode = true;
						lassoPoints.Clear();
						mode = OperationMode.LassoJoint;
					}
				}
				if (GUI.Button(new Rect(Screen.width - 210, 265, 200, 45), "Reset Joints", style)) {
					foreach (var p in puppets) {
						if (p != null) p.ResetLasso();
					}
					showRegionPanel = false;
					if (mode == OperationMode.LassoJoint) {
						lassoPoints.Clear();
						lassoStarted = false;
						isLassoMode  = false;
						mode = OperationMode.Default;
					}
				}

				// ── Edit Rig button ─────────────────────────────────────────────
				GUI.enabled = (activePuppet != null);
				if (GUI.Button(new Rect(Screen.width - 210, 315, 200, 45), "Edit Rig", style)) {
					isRigEditMode    = true;
					rigEditPuppet    = activePuppet;
					rigSelectedJoint = -1;
					rigConnectMode   = false;
					rigConnectFirst  = -1;
				}
				
				if (GUI.Button(new Rect(Screen.width - 210, 365, 200, 45), "Anim Mode", style)) {
					isAnimationMode = true;
				}
				GUI.enabled = true;

				// ── Region Physics panel ────────────────────────────────────────
				if (showRegionPanel && regionPuppet != null && selectedRegion >= 0) {
					float px = Screen.width - 210;
					float py = 318f;
					Rect panelRect = new Rect(px - 5, py - 4, 213, 120);

					// Close when clicking OUTSIDE the panel
					Event ev = Event.current;
					if (ev.type == EventType.MouseDown && !panelRect.Contains(ev.mousePosition)) {
						showRegionPanel = false;
					}

					GUIStyle lbl = new GUIStyle(GUI.skin.label);
					lbl.fontSize = 16;
					lbl.normal.textColor = Color.white;

					GUI.Box(panelRect, GUIContent.none);
					GUI.Label(new Rect(px, py, 200, 22),
						$"Region {selectedRegion} Physics", lbl);

					// Stiffness — live apply
					GUI.Label(new Rect(px, py + 26, 200, 20),
						$"Stiffness: {regionEditStiffness:F2}", lbl);
					float newS = GUI.HorizontalSlider(
						new Rect(px, py + 46, 200, 18), regionEditStiffness, 0f, 1f);
					if (!Mathf.Approximately(newS, regionEditStiffness)) {
						regionEditStiffness = newS;
						regionPuppet.SetRegionParams(selectedRegion, regionEditStiffness, regionEditDamping);
					}

					// Damping — live apply
					GUI.Label(new Rect(px, py + 68, 200, 20),
						$"Damping:   {regionEditDamping:F2}", lbl);
					float newD = GUI.HorizontalSlider(
						new Rect(px, py + 88, 200, 18), regionEditDamping, 0f, 1f);
					if (!Mathf.Approximately(newD, regionEditDamping)) {
						regionEditDamping = newD;
						regionPuppet.SetRegionParams(selectedRegion, regionEditStiffness, regionEditDamping);
					}
				}

				GUI.enabled = true;
			} else if (isRigEditMode) {
				// ── Rig Edit panel ────────────────────────────────────────────────
				float rx = Screen.width - 210;
				GUIStyle lbl = new GUIStyle(GUI.skin.label);
				lbl.fontSize = 16;
				lbl.normal.textColor = Color.white;

				GUI.Box(new Rect(rx - 5, 5, 215, 280), GUIContent.none);
				GUI.Label(new Rect(rx, 10, 200, 24), "─── Edit Rig ───", lbl);

				string jointInfo = rigSelectedJoint >= 0
					? $"Selected: Joint {rigSelectedJoint}"
					: "Click a joint to select";
				GUI.Label(new Rect(rx, 38, 200, 22), jointInfo, lbl);

				if (rigConnectMode)
					GUI.Label(new Rect(rx, 60, 200, 22), "▶ Pick 2nd joint...", lbl);

				// Delete selected joint
				GUI.enabled = rigSelectedJoint >= 0 && !rigConnectMode;
				if (GUI.Button(new Rect(rx, 90, 200, 44), "Delete Joint", style)) {
					rigEditPuppet.RemoveJoint(rigSelectedJoint);
					rigSelectedJoint = -1;
				}

				// Connect two joints → new bone
				GUI.enabled = rigSelectedJoint >= 0;
				if (!rigConnectMode) {
					if (GUI.Button(new Rect(rx, 140, 200, 44), "Connect →", style)) {
						rigConnectFirst = rigSelectedJoint;
						rigConnectMode  = true;
					}
				} else {
					if (GUI.Button(new Rect(rx, 140, 200, 44), "Cancel Connect", style)) {
						rigConnectMode  = false;
						rigConnectFirst = -1;
					}
				}

				// Hint
				GUI.enabled = true;
				GUIStyle hint = new GUIStyle(GUI.skin.label);
				hint.fontSize = 13;
				hint.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
				hint.wordWrap = true;
				GUI.Label(new Rect(rx, 192, 200, 50),
					"Drag joint to move.\nClick empty space to deselect.", hint);

				// Done
				if (GUI.Button(new Rect(rx, 245, 200, 44), "Done", style)) {
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
				
				if (GUI.Button(new Rect(rx, 40, 200, 44), "Exit Anim Mode", style)) {
					isAnimationMode = false;
				}

				if (activePuppet != null) {
					if (GUI.Button(new Rect(rx, 90, 200, 44), "Clear Anims", style)) {
						activePuppet.ClearAnimation();
					}
					GUI.Label(new Rect(rx, 140, 200, 24), "Frames: " + activePuppet.animMaxFrames, lbl);
					GUI.Label(new Rect(rx, 170, 200, 24), "Current: " + activePuppet.animCurrentFrame, lbl);
					GUI.Label(new Rect(rx, 210, 200, 60), "Click & drag a joint\nto record motion.", lbl);
				}
			} else {
				if (GUI.Button(new Rect(Screen.width - 210, 10, 200, 40), "Done", style)) {
					isEditMode = false;
				}
				if (GUI.Button(new Rect(Screen.width - 210, 55, 95, 40), "Apply", style)) {
					if (activePuppet != null) activePuppet.ApplyColorToLastClick(selectedEditColor);
				}
				if (GUI.Button(new Rect(Screen.width - 105, 55, 95, 40), "Cancel", style)) {
					isEditMode = false;
					if (activePuppet != null) activePuppet.CancelEditMode();
				}
				
				if (colorWheel == null) CreateColorWheel();
				Rect wheelRect = new Rect(Screen.width - 210, 100, 200, 200);
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
							if (activePuppet != null) {
								activePuppet.isPreviewingColor = true;
								activePuppet.UpdatePreview(selectedEditColor);
							}
						}
						e.Use();
					}
				}

				float newSlider = GUI.HorizontalSlider(new Rect(Screen.width - 210, 310, 200, 20), selectedValue, 0f, 1f);
				if (newSlider != selectedValue) {
					selectedValue = newSlider;
					Color.RGBToHSV(selectedEditColor, out float h, out float s, out float curV);
					selectedEditColor = Color.HSVToRGB(h, s, selectedValue);
					if (activePuppet != null) {
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
			GUI.enabled = true;

			if (GUI.Button(new Rect(10, 10, 200, 50), "Mode: " + (isDrawingEnabled ? "DRAWING" : "VIEW ONLY"), style)) {
				isDrawingEnabled = !isDrawingEnabled;
			}

			if (GUI.Button(new Rect(10, 70, 200, 50), "Gravity: " + (enableGravity ? "ON" : "OFF (Float)"), style)) {
				enableGravity = !enableGravity;
				foreach (var p in puppets) {
					if (p != null) {
						var body = p.GetComponent<Rigidbody>();
						if (body != null) body.useGravity = enableGravity;
						p.gravity = enableGravity ? 9.81f : 0f;
					}
				}
			}

			if (GUI.Button(new Rect(10, 130, 200, 40), "Reset Rotation", style)) {
				currentRotation = Vector3.zero;
			}

			GUI.Label(new Rect(10, 180, 200, 20), "Rotate X: " + currentRotation.x.ToString("F0"), style);
			currentRotation.x = GUI.HorizontalSlider(new Rect(10, 205, 200, 20), currentRotation.x, 0f, 360f);

			GUI.Label(new Rect(10, 230, 200, 20), "Rotate Y: " + currentRotation.y.ToString("F0"), style);
			currentRotation.y = GUI.HorizontalSlider(new Rect(10, 255, 200, 20), currentRotation.y, 0f, 360f);

			GUI.Label(new Rect(10, 280, 200, 20), "Rotate Z: " + currentRotation.z.ToString("F0"), style);
			currentRotation.z = GUI.HorizontalSlider(new Rect(10, 305, 200, 20), currentRotation.z, 0f, 360f);

#if UNITY_EDITOR
			if (GUI.Button(new Rect(10, 335, 200, 50), "Import PNG", style)) {
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
				float bh = 70f;
				float bx = (Screen.width - bw) / 2f;
				float by = Screen.height - 180f;

				GUIStyle inflateStyle = new GUIStyle(style);
				inflateStyle.fontSize = 26;
				inflateStyle.fontStyle = FontStyle.Bold;
				inflateStyle.normal.textColor = new Color(0.2f, 1f, 0.2f); // Bright green

				if (GUI.Button(new Rect(bx, by, bw, bh), "INFLATE", inflateStyle)) {
					Build();
					mode = OperationMode.Default;
				}

				if (GUI.Button(new Rect(bx, by + bh + 15, bw, bh * 0.7f), "Cancel Sketch", style)) {
					Clear();
					mode = OperationMode.Default;
				}
			}
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

	}

}
