using UnityEngine;
using System.Collections.Generic;

namespace mattatz.TeddySystem {

    /// <summary>
    /// Stores information about an individual sketch
    /// Used for domain stitching to keep track of body parts vs appendages
    /// </summary>
    public class SketchInfo {
        public List<Vector2> contour;           // Original sketch points
        public bool isOpen;                     // Open (appendage) or closed (body)
        public int sketchID;                    // Unique identifier
        public SketchType type;                 // Body or Appendage
        
        // For appendages: attachment information
        public int attachedToSketchID = -1;     // Which body sketch this attaches to
        public Vector2 attachmentPoint;         // Where it attaches (on the appendage)
        public Vector2 attachmentPointOnBody;   // Where it attaches (on the body)
        public List<Vector2> attachmentCurve;   // Bp curve (closure curve for open contours)
        
        // Triangulation results
        public List<Vector2> vertices;
        public List<int> triangles;
        public List<Vector2> boundary;
        
        // Chordal axis (for skeleton)
        public Vector2[] chordalAxis;
        
        public SketchInfo(List<Vector2> contour, int id) {
            this.contour = new List<Vector2>(contour);
            this.sketchID = id;
            this.isOpen = DetectIfOpen(contour);
            this.type = isOpen ? SketchType.Appendage : SketchType.Body;
        }
        
        private bool DetectIfOpen(List<Vector2> contour) {
            if (contour.Count < 3) return false;
            float distance = Vector2.Distance(contour[0], contour[contour.Count - 1]);
            return distance > 0.1f; // Threshold for considering open
        }
    }
    
    public enum SketchType {
        Body,       // Closed contour (torso, head, etc.)
        Appendage   // Open contour (leg, arm, tail, etc.)
    }
    
    /// <summary>
    /// Manages collection of sketches for domain stitching
    /// </summary>
    public class SketchCollection {
        public List<SketchInfo> sketches = new List<SketchInfo>();
        
        public void AddSketch(List<Vector2> contour) {
            int id = sketches.Count;
            var info = new SketchInfo(contour, id);
            sketches.Add(info);
            Debug.Log($"[SketchCollection] Added sketch {id}: {info.type} ({(info.isOpen ? "open" : "closed")})");
        }
        
        public List<SketchInfo> GetBodies() {
            var bodies = new List<SketchInfo>();
            foreach (var s in sketches) {
                if (s.type == SketchType.Body) bodies.Add(s);
            }
            return bodies;
        }
        
        public List<SketchInfo> GetAppendages() {
            var appendages = new List<SketchInfo>();
            foreach (var s in sketches) {
                if (s.type == SketchType.Appendage) appendages.Add(s);
            }
            return appendages;
        }
        
        /// <summary>
        /// Detect which body each appendage attaches to
        /// </summary>
        public void DetectAttachments() {
            var bodies = GetBodies();
            var appendages = GetAppendages();
            
            Debug.Log($"[SketchCollection] Detecting attachments: {bodies.Count} bodies, {appendages.Count} appendages");
            
            foreach (var appendage in appendages) {
                // Find closest body
                float minDist = float.MaxValue;
                SketchInfo closestBody = null;
                Vector2 bestAttachPoint = Vector2.zero;
                Vector2 bestBodyPoint = Vector2.zero;
                
                // Check both endpoints of the appendage
                Vector2 start = appendage.contour[0];
                Vector2 end = appendage.contour[appendage.contour.Count - 1];
                
                foreach (var body in bodies) {
                    // Find closest point on body contour to appendage endpoints
                    foreach (var bodyPoint in body.contour) {
                        float distStart = Vector2.Distance(start, bodyPoint);
                        float distEnd = Vector2.Distance(end, bodyPoint);
                        
                        if (distStart < minDist) {
                            minDist = distStart;
                            closestBody = body;
                            bestAttachPoint = start;
                            bestBodyPoint = bodyPoint;
                        }
                        
                        if (distEnd < minDist) {
                            minDist = distEnd;
                            closestBody = body;
                            bestAttachPoint = end;
                            bestBodyPoint = bodyPoint;
                        }
                    }
                }
                
                if (closestBody != null) {
                    appendage.attachedToSketchID = closestBody.sketchID;
                    appendage.attachmentPoint = bestAttachPoint;
                    appendage.attachmentPointOnBody = bestBodyPoint;
                    
                    // Generate closure curve (Bp) for the appendage
                    appendage.attachmentCurve = GenerateClosureCurve(appendage.contour);
                    
                    Debug.Log($"[SketchCollection] Appendage {appendage.sketchID} attaches to Body {closestBody.sketchID} at distance {minDist:F3}");
                }
            }
        }
        
        /// <summary>
        /// Generate a closure curve (Bp) for an open contour
        /// Connects the two endpoints with a smooth curve
        /// </summary>
        private List<Vector2> GenerateClosureCurve(List<Vector2> openContour) {
            if (openContour.Count < 2) return new List<Vector2>();
            
            Vector2 start = openContour[0];
            Vector2 end = openContour[openContour.Count - 1];
            
            // Simple straight line for now (can be improved with Bezier curve)
            List<Vector2> curve = new List<Vector2>();
            int segments = 10;
            for (int i = 0; i <= segments; i++) {
                float t = i / (float)segments;
                Vector2 p = Vector2.Lerp(start, end, t);
                curve.Add(p);
            }
            
            return curve;
        }
        
        public void Clear() {
            sketches.Clear();
        }
        
        public int Count => sketches.Count;
    }
}
