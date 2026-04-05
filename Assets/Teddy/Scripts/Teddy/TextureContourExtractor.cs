using UnityEngine;
using System.Collections.Generic;

namespace mattatz.TeddySystem {
    public static class TextureContourExtractor {
        public static List<Vector2> ExtractContour(Texture2D tex, float alphaThreshold = 0.5f) {
            int w = tex.width;
            int h = tex.height;
            Color32[] pixels = tex.GetPixels32();

            int startX = -1, startY = -1;
            // Scan from top-left downwards
            for (int y = h - 1; y >= 0; y--) {
                for (int x = 0; x < w; x++) {
                    if (pixels[y * w + x].a > 255 * alphaThreshold) {
                        startX = x;
                        startY = y;
                        break;
                    }
                }
                if (startX != -1) break;
            }

            if (startX == -1) return new List<Vector2>();

            var contour = new List<Vector2>();
            int curX = startX, curY = startY;
            int backX = startX - 1, backY = startY;

            // Clockwise neighbor search offsets
            int[] dx = { -1, -1,  0,  1, 1, 1, 0, -1 };
            int[] dy = {  0,  1,  1,  1, 0, -1, -1, -1 };

            int maxPoints = w * h;
            int pointsCount = 0;

            while (pointsCount < maxPoints) {
                contour.Add(new Vector2(curX, curY));

                int dBackX = backX - curX;
                int dBackY = backY - curY;
                
                int startIndex = 0;
                for(int i = 0; i < 8; i++) {
                    if (dx[i] == dBackX && dy[i] == dBackY) {
                        startIndex = i;
                        break;
                    }
                }

                int nextX = -1, nextY = -1;
                int newBackX = -1, newBackY = -1;

                for (int i = 1; i <= 8; i++) {
                    int idx = (startIndex + i) % 8;
                    int nx = curX + dx[idx];
                    int ny = curY + dy[idx];

                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && pixels[ny * w + nx].a > 255 * alphaThreshold) {
                        nextX = nx;
                        nextY = ny;
                        int bIdx = (startIndex + i - 1) % 8;
                        newBackX = curX + dx[bIdx];
                        newBackY = curY + dy[bIdx];
                        break;
                    }
                }

                if (nextX == -1) break; // Isolated pixel
                
                // Standard Moore neighborhood stopping condition
                if (nextX == startX && nextY == startY && pointsCount > 2) {
                    break;
                }

                curX = nextX;
                curY = nextY;
                backX = newBackX;
                backY = newBackY;
                pointsCount++;
            }

            // Normalize so coordinates fall within roughly [-0.5, 0.5] range
            float invMax = 1f / Mathf.Max(w, h);
            for(int i=0; i<contour.Count; i++) {
                Vector2 p = contour[i];
                p.x = (p.x - w * 0.5f) * invMax;
                p.y = (p.y - h * 0.5f) * invMax;
                contour[i] = p;
            }

            return contour;
        }
    }
}
