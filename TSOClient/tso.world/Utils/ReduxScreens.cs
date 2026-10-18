using FSO.Common.Rendering;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// SimsRedux: pictures on a computer's monitor (Twisted Metal 2), painted into the computer itself.
    ///
    /// Sims 1 draws a computer's screen as its own little picture on top of the computer ("screen
    /// overlay": the web, a game, or off), one for every camera side and zoom. Comparing the "off" one
    /// with the others shows exactly which pixels are the glass in each view; a screenshot is bent onto
    /// those pixels (its left and right edges follow the glass's sides, the top and bottom follow its
    /// slant), so it sits on the monitor from every side and zoom, and the Sim's head still goes in front.
    ///
    /// The screenshots come from the game (SetPictures). An object shows one when its ObjectComponent's
    /// ReduxScreen is set (0, 1, 2... = which one; -1 = its own screens).
    /// </summary>
    public static class ReduxScreens
    {
        public class Picture { public int Width, Height; public Color[] Pixels; }
        private static List<Picture> Pictures = new List<Picture>();

        /// <summary>The screenshots to show (raw colours, any size).</summary>
        public static void SetPictures(List<Picture> pictures)
        {
            Pictures = pictures ?? new List<Picture>();
            Cache.Clear();
        }

        public static int Count { get { return Pictures.Count; } }

        private class Overlay { public bool IsScreen; public SPR2 Off; public List<SPR2> Games = new List<SPR2>(); }
        private static Dictionary<IffFile, Dictionary<uint, Overlay>> Files = new Dictionary<IffFile, Dictionary<uint, Overlay>>();
        private static Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        private static Dictionary<string, bool[]> Masks = new Dictionary<string, bool[]>();

        private static Dictionary<uint, Overlay> Scan(IffFile iff)
        {
            Dictionary<uint, Overlay> map;
            if (Files.TryGetValue(iff, out map)) return map;
            map = new Dictionary<uint, Overlay>();
            try
            {
                var all = iff.List<SPR2>() ?? new List<SPR2>();
                var screens = new List<SPR2>();
                foreach (var s in all)
                {
                    var l = (s.ChunkLabel ?? "").ToLowerInvariant();
                    if (l.Contains("screen") && (l.Contains("overlay") || l.Contains("game") || l.Contains("web"))) screens.Add(s);
                }
                // the screens are in groups (one per computer in the file, each with its own "off")
                foreach (var s in screens)
                {
                    var l = (s.ChunkLabel ?? "").ToLowerInvariant();
                    if (l.Contains("off")) continue;
                    // the "off" picture nearest in number (same computer), with the same frames
                    SPR2 off = null; int best = int.MaxValue;
                    foreach (var o in screens)
                    {
                        if (!(o.ChunkLabel ?? "").ToLowerInvariant().Contains("off")) continue;
                        if (o.Frames == null || s.Frames == null || o.Frames.Length != s.Frames.Length) continue;
                        var d = Math.Abs(o.ChunkID - s.ChunkID);
                        if (d < best) { best = d; off = o; }
                    }
                    var ov = new Overlay { IsScreen = true, Off = off };
                    foreach (var g in screens)
                    {
                        if ((g.ChunkLabel ?? "").ToLowerInvariant().Contains("off")) continue;
                        if (off != null && Math.Abs(g.ChunkID - off.ChunkID) > 10) continue;
                        if (g.Frames != null && s.Frames != null && g.Frames.Length == s.Frames.Length) ov.Games.Add(g);
                    }
                    if (ov.Games.Count == 0) ov.Games.Add(s);
                    map[s.ChunkID] = ov;
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] monitor screens: " + e.Message); }
            Files[iff] = map;
            return map;
        }

        /// <summary>Does this file have screen pictures we can paint over (otherwise: the floating picture)?</summary>
        public static bool HasScreens(IffFile iff)
        {
            return iff != null && Scan(iff).Count > 0;
        }

        private static Color[] Pixels(SPR2Frame f)
        {
            if (f == null) return null;
            try { f.DecodeIfRequired(false); } catch { }
            return f.PixelData;
        }

        /// <summary>The glass in this view: pixels that change between the screen being off and showing something.</summary>
        private static bool[] Mask(IffFile iff, Overlay ov, uint frameIndex, int w, int h)
        {
            var key = (iff.Filename ?? "") + "#" + ov.Games[0].ChunkID + "#" + frameIndex;
            bool[] mask;
            if (Masks.TryGetValue(key, out mask)) return mask;
            mask = new bool[w * h];
            var off = (ov.Off != null && frameIndex < ov.Off.Frames.Length) ? Pixels(ov.Off.Frames[frameIndex]) : null;
            foreach (var g in ov.Games)
            {
                if (frameIndex >= g.Frames.Length) continue;
                var fr = g.Frames[frameIndex];
                if (fr.Width != w || fr.Height != h) continue;
                var px = Pixels(fr);
                if (px == null) continue;
                for (int i = 0; i < px.Length && i < mask.Length; i++)
                {
                    if (px[i].A == 0) continue;
                    if (off == null || off.Length != px.Length) mask[i] = true;
                    else if (off[i].PackedValue != px[i].PackedValue) mask[i] = true;
                }
            }
            // in some views "off" is only the bezel, with see-through glass inside it: that inside is glass too
            if (off != null && off.Length == w * h)
            {
                var outside = new bool[w * h];
                var stack = new Stack<int>();
                for (int x = 0; x < w; x++) { Seed(off, outside, stack, x); Seed(off, outside, stack, (h - 1) * w + x); }
                for (int y = 0; y < h; y++) { Seed(off, outside, stack, y * w); Seed(off, outside, stack, y * w + w - 1); }
                while (stack.Count > 0)
                {
                    int j = stack.Pop(), jx = j % w, jy = j / w;
                    if (jx > 0) Seed(off, outside, stack, j - 1);
                    if (jx < w - 1) Seed(off, outside, stack, j + 1);
                    if (jy > 0) Seed(off, outside, stack, j - w);
                    if (jy < h - 1) Seed(off, outside, stack, j + w);
                }
                for (int i = 0; i < mask.Length; i++) if (off[i].A == 0 && !outside[i]) mask[i] = true;
            }
            // (a few glass pixels happen to be the same colour on and off: fill each row between its ends)
            for (int y = 0; y < h; y++)
            {
                int a = -1, b = -1;
                for (int x = 0; x < w; x++) if (mask[y * w + x]) { if (a < 0) a = x; b = x; }
                for (int x = a + 1; x < b; x++) mask[y * w + x] = true;
            }
            Masks[key] = mask;
            return mask;
        }

        /// <summary>
        /// The picture to draw for this sprite while the object shows screenshot number "picture"
        /// (null: it isn't the screen, or there's nothing to show).
        /// </summary>
        public static Texture2D Get(GraphicsDevice gd, DGRPSprite sprite, Texture2D original, int picture)
        {
            if (picture < 0 || Pictures.Count == 0 || sprite == null || original == null) return null;
            try
            {
                var iff = sprite.ReduxIff;
                if (iff == null) return null;
                var map = Scan(iff);
                Overlay ov;
                if (!map.TryGetValue(sprite.SpriteID, out ov)) return null;
                var spr = iff.Get<SPR2>((ushort)sprite.SpriteID);
                if (spr == null || sprite.SpriteFrameIndex >= spr.Frames.Length) return null;
                var frame = spr.Frames[sprite.SpriteFrameIndex];
                int w = frame.Width, h = frame.Height;
                if (w <= 2 || h <= 2) return null;
                var pic = Pictures[picture % Pictures.Count];
                var key = (iff.Filename ?? "") + "#" + sprite.SpriteID + "#" + sprite.SpriteFrameIndex + "#" + (picture % Pictures.Count) + (sprite.Flip ? "f" : "");
                Texture2D tex;
                if (Cache.TryGetValue(key, out tex) && tex != null && !tex.IsDisposed) return tex;

                var src = Pixels(frame);
                if (src == null || src.Length != w * h) return null;
                var mask = Mask(iff, ov, sprite.SpriteFrameIndex, w, h);

                // only the glass: the biggest solid patch of changed pixels (a stray pixel elsewhere in the
                // picture stretched the old outline past the monitor), with its gaps filled
                var glass = Glass(mask, w, h);
                if (glass == null) return null; // (this view doesn't show the glass)

                // where each row and column of the glass starts and ends: the screenshot is fitted to them,
                // so it follows the glass's slant from every side
                var rowMin = new int[h]; var rowMax = new int[h];
                var colMin = new int[w]; var colMax = new int[w];
                for (int y = 0; y < h; y++) { rowMin[y] = int.MaxValue; rowMax[y] = -1; }
                for (int x = 0; x < w; x++) { colMin[x] = int.MaxValue; colMax[x] = -1; }
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (glass[y * w + x])
                        {
                            if (x < rowMin[y]) rowMin[y] = x; if (x > rowMax[y]) rowMax[y] = x;
                            if (y < colMin[x]) colMin[x] = y; if (y > colMax[x]) colMax[x] = y;
                        }

                var px = (Color[])src.Clone();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (!glass[i]) continue;
                        float rw = Math.Max(1, rowMax[y] - rowMin[y]), ch = Math.Max(1, colMax[x] - colMin[x]);
                        float u = (x - rowMin[y]) / rw, v = (y - colMin[x]) / ch;
                        if (sprite.Flip) u = 1 - u; // (the game mirrors this view: mirror the picture back)
                        // the average of the screenshot's pixels this one covers (a small screen, a big picture)
                        var c = Area(pic, u, v, 1f / (rw + 1), 1f / (ch + 1));
                        // a touch dimmer than the screenshot (a lit screen in a room, not a sticker)
                        px[i] = new Color((byte)(c.R * 0.9f), (byte)(c.G * 0.9f), (byte)(c.B * 0.92f), (byte)255);
                    }
                tex = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
                tex.SetData(px);
                tex.Tag = original.Tag ?? new TextureInfo(tex, w, h);
                Cache[key] = tex;
                return tex;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] monitor picture: " + e.Message);
                return null;
            }
        }

        private static void Seed(Color[] off, bool[] outside, Stack<int> stack, int i)
        {
            if (outside[i] || off[i].A != 0) return;
            outside[i] = true;
            stack.Push(i);
        }

        /// <summary>The glass: the biggest connected patch of the mask, with the holes in its rows and columns filled.</summary>
        private static bool[] Glass(bool[] mask, int w, int h)
        {
            var label = new int[w * h];
            int best = 0, bestCount = 0, next = 0;
            var stack = new Stack<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || label[i] != 0) continue;
                int id = ++next, count = 0;
                label[i] = id; stack.Push(i);
                while (stack.Count > 0)
                {
                    int j = stack.Pop(); count++;
                    int jx = j % w, jy = j / w;
                    if (jx > 0 && mask[j - 1] && label[j - 1] == 0) { label[j - 1] = id; stack.Push(j - 1); }
                    if (jx < w - 1 && mask[j + 1] && label[j + 1] == 0) { label[j + 1] = id; stack.Push(j + 1); }
                    if (jy > 0 && mask[j - w] && label[j - w] == 0) { label[j - w] = id; stack.Push(j - w); }
                    if (jy < h - 1 && mask[j + w] && label[j + w] == 0) { label[j + w] = id; stack.Push(j + w); }
                }
                if (count > bestCount) { bestCount = count; best = id; }
            }
            if (bestCount < 6) return null;
            var g = new bool[w * h];
            for (int i = 0; i < g.Length; i++) g[i] = label[i] == best;
            // fill each row and each column between its ends (glass pixels that look the same on and off)
            for (int y = 0; y < h; y++)
            {
                int a = -1, b = -1;
                for (int x = 0; x < w; x++) if (g[y * w + x]) { if (a < 0) a = x; b = x; }
                for (int x = a + 1; x < b; x++) g[y * w + x] = true;
            }
            for (int x = 0; x < w; x++)
            {
                int a = -1, b = -1;
                for (int y = 0; y < h; y++) if (g[y * w + x]) { if (a < 0) a = y; b = y; }
                for (int y = a + 1; y < b; y++) g[y * w + x] = true;
            }
            return g;
        }

        private static void Edge(bool[] mask, int w, int h, int x0, int x1, out float top, out float bottom)
        {
            int t = int.MaxValue, b = -1;
            for (int x = x0; x <= x1; x++)
                for (int y = 0; y < h; y++)
                    if (mask[y * w + x]) { if (y < t) t = y; if (y > b) b = y; }
            if (b < 0) { t = 0; b = h - 1; }
            top = t; bottom = b;
        }

        /// <summary>The average colour of the picture over a small area (u, v the middle; du, dv its size, 0-1).</summary>
        private static Color Area(Picture p, float u, float v, float du, float dv)
        {
            int x0 = (int)Math.Floor((u - du / 2) * p.Width), x1 = (int)Math.Ceiling((u + du / 2) * p.Width);
            int y0 = (int)Math.Floor((v - dv / 2) * p.Height), y1 = (int)Math.Ceiling((v + dv / 2) * p.Height);
            x0 = Math.Max(0, Math.Min(p.Width - 1, x0)); x1 = Math.Max(x0 + 1, Math.Min(p.Width, x1));
            y0 = Math.Max(0, Math.Min(p.Height - 1, y0)); y1 = Math.Max(y0 + 1, Math.Min(p.Height, y1));
            long r = 0, g = 0, b = 0; int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = p.Pixels[y * p.Width + x];
                    r += c.R; g += c.G; b += c.B; n++;
                }
            if (n == 0) return Sample(p, u, v);
            return new Color((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)255);
        }

        private static Color Sample(Picture p, float u, float v)
        {
            u = Math.Max(0, Math.Min(1, u)); v = Math.Max(0, Math.Min(1, v));
            // (a little averaging: the glass is small, the screenshot isn't)
            float fx = u * (p.Width - 1), fy = v * (p.Height - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(p.Width - 1, x0 + 1), y1 = Math.Min(p.Height - 1, y0 + 1);
            float ax = fx - x0, ay = fy - y0;
            var a = p.Pixels[y0 * p.Width + x0]; var b = p.Pixels[y0 * p.Width + x1];
            var c = p.Pixels[y1 * p.Width + x0]; var d = p.Pixels[y1 * p.Width + x1];
            float r = (a.R * (1 - ax) + b.R * ax) * (1 - ay) + (c.R * (1 - ax) + d.R * ax) * ay;
            float g = (a.G * (1 - ax) + b.G * ax) * (1 - ay) + (c.G * (1 - ax) + d.G * ax) * ay;
            float bl = (a.B * (1 - ax) + b.B * ax) * (1 - ay) + (c.B * (1 - ax) + d.B * ax) * ay;
            return new Color((byte)r, (byte)g, (byte)bl, (byte)255);
        }
    }
}
