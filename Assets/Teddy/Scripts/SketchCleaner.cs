using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace mattatz.TeddySystem {

    public static class SketchCleaner {

        /// <summary>
        /// Clean and simplify a user-drawn sketch
        /// </summary>
        public static List<Vector2> Clean(List<Vector2> points, float threshold = 1.0f) {
            if (points == null || points.Count < 2) return points;

            // 1. Remove duplicate consecutive points
            List<Vector2> cleaned = new List<Vector2>();
            cleaned.Add(points[0]);
            for (int i = 1; i < points.Count; i++) {
                if (Vector2.Distance(points[i], cleaned.Last()) > threshold * 0.5f) {
                    cleaned.Add(points[i]);
                }
            }

            // 2. Simplify using Ramer-Douglas-Peucker (optional, but good for performance)
            // For now, just use basic distance thresholding
            
            // 3. Handle self-intersections (Teddy usually handles this in triangulator, but clean is better)

            return cleaned;
        }

        /// <summary>
        /// Ensure a contour is properly closed if endpoints are close
        /// </summary>
        public static List<Vector2> EnsureClosure(List<Vector2> points, float closeThreshold = 5.0f) {
            if (points == null || points.Count < 3) return points;

            float dist = Vector2.Distance(points[0], points.Last());
            if (dist < closeThreshold) {
                points[points.Count - 1] = points[0];
            }

            return points;
        }

        /// <summary>
        /// Smooth a contour by moving points towards their neighbors (Laplacian smoothing)
        /// </summary>
        public static List<Vector2> Smooth(List<Vector2> points, int iterations = 3) {
            if (points == null || points.Count < 3) return points;
            
            List<Vector2> smoothed = new List<Vector2>(points);
            for (int iter = 0; iter < iterations; iter++) {
                List<Vector2> nextIter = new List<Vector2>(smoothed.Count);
                for (int i = 0; i < smoothed.Count; i++) {
                    int prevIdx = (i - 1 + smoothed.Count) % smoothed.Count;
                    int nextIdx = (i + 1) % smoothed.Count;
                    
                    // Simple average smoothing
                    Vector2 p = smoothed[i] * 0.5f + (smoothed[prevIdx] + smoothed[nextIdx]) * 0.25f;
                    nextIter.Add(p);
                }
                smoothed = nextIter;
            }
            return smoothed;
        }
    }
}
