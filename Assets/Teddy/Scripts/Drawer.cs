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
		MoveJoint
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

		OperationMode mode;

		Teddy teddy;
		List<Vector2> points;
		List<Puppet> puppets = new List<Puppet>();

		Camera cam;
		float screenZ = 0f;

		Puppet selected;
		Puppet activePuppet;
		bool isEditMode = false;
		Vector3 origin;
		Vector3 startPoint;

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

		void Start () {
			cam = Camera.main;
			screenZ = Mathf.Abs(cam.transform.position.z - transform.position.z);

			points = new List<Vector2>();
			points = JsonUtility.FromJson<JsonSerialization<Vector2>>(json.text).ToList();
			Build();
		}

		void Update () {
			foreach(var p in puppets) {
				if (p == null) continue;
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
			bool isOverLeftUI = new Rect(10, 10, 200, 110).Contains(guiMouse);
			bool isOverRightUI = new Rect(Screen.width - 220, 10, 220, isEditMode ? 400 : 80).Contains(guiMouse);
			bool isOverUI = isOverLeftUI || isOverRightUI;

			switch(mode) {

			case OperationMode.Default:

				if(Input.GetMouseButtonDown(0) && !isOverUI) {
					var ray = cam.ScreenPointToRay(screen);
					RaycastHit hit;

					bool jointPicked = false;
					foreach (var p in puppets) {
						if (p == null) continue;
						int jIndex;
						if (p.TryPickJoint(cam, screen, 20f, out jIndex)) {
							selected = p;
							activePuppet = p;
							selected.draggingJoint = jIndex;
							mode = OperationMode.MoveJoint;
							jointPicked = true;
							break;
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
						} else {
							activePuppet = null;
							isEditMode = false;
							Clear();
							mode = OperationMode.Draw;
						}
					}
				}

				break;

			case OperationMode.Draw:
				if(Input.GetMouseButtonUp(0)) {
					Build();
					mode = OperationMode.Default;
				} else {
					var p = cam.ScreenToWorldPoint(screen);
					p = transform.InverseTransformPoint(p);
					var p2D = new Vector2(p.x, p.y);
					if(points.Count <= 0 || Vector2.Distance(p2D, points.Last()) > threshold) {
						points.Add(p2D);
					}
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
					if (selected != null) selected.draggingJoint = -1;
					selected = null;
					mode = OperationMode.Default;
				} else {
					if (selected != null) {
						Vector3 mp = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, selected.dragZ));
						selected.MoveJoint(mp);
					}
				}

				break;

			}

		}

		void Build () {
			if(points.Count < 3) return;

			points = Utils2D.Constrain(points, threshold);
			if(points.Count < 3) return;

			teddy = new Teddy(points);
			var mesh = teddy.Build(MeshSmoothingMethod.HC, 2, 0.2f, 0.75f);
			var bones = teddy.GetSkeletonBones();

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
				Destroy(puppet.gameObject, 10f);
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
			
			GUI.enabled = (activePuppet != null);
			if (!isEditMode) {
				if (GUI.Button(new Rect(Screen.width - 210, 10, 200, 50), "Edit Mode", style)) {
					isEditMode = true;
					if (activePuppet != null) activePuppet.StartEditMode();
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

				GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
				labelStyle.alignment = TextAnchor.MiddleCenter;
				Color textColor = (selectedEditColor.r * 0.299f + selectedEditColor.g * 0.587f + selectedEditColor.b * 0.114f) > 0.5f ? Color.black : Color.white;
				labelStyle.normal.textColor = textColor;
				GUI.Label(new Rect(Screen.width - 210, 340, 200, 40), "#" + ColorUtility.ToHtmlStringRGB(selectedEditColor), labelStyle);
			}
			GUI.enabled = true;

			if (GUI.Button(new Rect(10, 10, 200, 50), "Gravity: " + (enableGravity ? "ON" : "OFF (Float)"), style)) {
				enableGravity = !enableGravity;
				foreach (var p in puppets) {
					if (p != null) {
						var body = p.GetComponent<Rigidbody>();
						if (body != null) body.useGravity = enableGravity;
						p.gravity = enableGravity ? 9.81f : 0f;
					}
				}
			}
#if UNITY_EDITOR
			if (GUI.Button(new Rect(10, 70, 200, 50), "Import PNG", style)) {
				string path = EditorUtility.OpenFilePanel("Select PNG Image", "", "png");
				if (!string.IsNullOrEmpty(path)) {
					byte[] bytes = File.ReadAllBytes(path);
					Texture2D tex = new Texture2D(2, 2);
					tex.LoadImage(bytes);
					
					var pixelContour = TextureContourExtractor.ExtractContour(tex);
					if (pixelContour.Count > 3) {
						Clear();
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
