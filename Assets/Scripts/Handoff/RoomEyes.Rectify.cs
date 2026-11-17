using System;
using UnityEngine;

namespace Windups.Handoff
{
    // F56 (2026-11-13): what is seen inside Spark's frame, straightened like a document scanner. The four corners of
    // the frame (or of what lies behind it) are given as viewport points of the camera picture; the picture inside
    // that quad is drawn out as an upright rectangle. Plain arithmetic on Frame.pixels: safe on any thread (the
    // caller runs it in Task.Run, never on the main thread).
    public sealed partial class RoomEyes
    {
        // Corners in viewport coordinates (0-1, origin bottom-left): bottom-left, bottom-right, top-right, top-left
        // of the wanted picture. The result is bottom row first, like Frame.pixels. Null when the quad is not usable.
        public static Color32[] Rectify(Color32[] pixels, int width, int height, Vector2 bl, Vector2 br, Vector2 tr, Vector2 tl, int outWidth, int outHeight)
        {
            if (pixels == null || width < 2 || height < 2 || outWidth < 2 || outHeight < 2 || pixels.Length < width * height) return null;
            // Unit square (u right, v up) -> quad, as a projective map (Heckbert).
            double x0 = bl.x, y0 = bl.y, x1 = br.x, y1 = br.y, x2 = tr.x, y2 = tr.y, x3 = tl.x, y3 = tl.y;
            double dx1 = x1 - x2, dx2 = x3 - x2, sx = x0 - x1 + x2 - x3;
            double dy1 = y1 - y2, dy2 = y3 - y2, sy = y0 - y1 + y2 - y3;
            double a, b, c, d, e, f, g, h;
            double det = dx1 * dy2 - dy1 * dx2;
            if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9) { a = x1 - x0; b = x3 - x0; c = x0; d = y1 - y0; e = y3 - y0; f = y0; g = 0; h = 0; }
            else
            {
                if (Math.Abs(det) < 1e-12) return null;
                g = (sx * dy2 - sy * dx2) / det;
                h = (dx1 * sy - dy1 * sx) / det;
                a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; c = x0;
                d = y1 - y0 + g * y1; e = y3 - y0 + h * y3; f = y0;
            }
            var result = new Color32[outWidth * outHeight];
            int maxX = width - 1, maxY = height - 1;
            for (int row = 0; row < outHeight; row++)
            {
                double v = (row + .5) / outHeight;
                for (int col = 0; col < outWidth; col++)
                {
                    double u = (col + .5) / outWidth;
                    double w = g * u + h * v + 1.0;
                    if (Math.Abs(w) < 1e-9) w = 1e-9;
                    double px = (a * u + b * v + c) / w * width - .5, py = (d * u + e * v + f) / w * height - .5;
                    if (px < 0) px = 0; else if (px > maxX) px = maxX;
                    if (py < 0) py = 0; else if (py > maxY) py = maxY;
                    int ix = (int)px, iy = (int)py;
                    int ix1 = ix < maxX ? ix + 1 : ix, iy1 = iy < maxY ? iy + 1 : iy;
                    float fx = (float)(px - ix), fy = (float)(py - iy);
                    Color32 p00 = pixels[iy * width + ix], p10 = pixels[iy * width + ix1], p01 = pixels[iy1 * width + ix], p11 = pixels[iy1 * width + ix1];
                    float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
                    result[row * outWidth + col] = new Color32(
                        (byte)(p00.r * w00 + p10.r * w10 + p01.r * w01 + p11.r * w11 + .5f),
                        (byte)(p00.g * w00 + p10.g * w10 + p01.g * w01 + p11.g * w11 + .5f),
                        (byte)(p00.b * w00 + p10.b * w10 + p01.b * w01 + p11.b * w11 + .5f), 255);
                }
            }
            return result;
        }

        // A rectangle of the picture between two viewport corners as plain pixels (bottom row first) and its size.
        public static Color32[] CropPixels(Frame frame, Vector2 a, Vector2 b, out int width, out int height)
        {
            width = height = 0;
            if (frame?.pixels == null) return null;
            int w = frame.size.x, h = frame.size.y;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, b.x) * w), 0, w - 2), x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, b.x) * w), x0 + 2, w);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, b.y) * h), 0, h - 2), y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, b.y) * h), y0 + 2, h);
            width = x1 - x0;
            height = y1 - y0;
            var crop = new Color32[width * height];
            for (int row = 0; row < height; row++) Array.Copy(frame.pixels, (y0 + row) * w + x0, crop, row * width, width);
            return crop;
        }
    }
}
