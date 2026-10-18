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

                // the glass's sides: its left- and right-most columns, and where it starts and ends in each
                int minX = int.MaxValue, maxX = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (mask[y * w + x]) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
                if (maxX - minX < 2) return null; // (this view doesn't show the glass)
                float tl, bl, tr, br;
                Edge(mask, w, h, minX, Math.Min(maxX, minX + 1), out tl, out bl);
                Edge(mask, w, h, Math.Max(minX, maxX - 1), maxX, out tr, out br);

                var px = (Color[])src.Clone();
                float span = Math.Max(1, maxX - minX);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (!mask[i]) continue;
                        float u = (x - minX) / span;
                        float top = tl + (tr - tl) * u, bottom = bl + (br - bl) * u;
                        float v = (bottom - top) < 1 ? 0.5f : (y - top) / (bottom - top);
                        if (sprite.Flip) u = 1 - u; // (the game mirrors this view: mirror the picture back)
                        var c = Sample(pic, u, v);
                        // a touch dimmer than the screenshot (a lit screen in a room, not a sticker)
                        px[i] = new Color((byte)(c.R * 0.9f), (byte)(c.G * 0.9f), (byte)(c.B * 0.92f), src[i].A);
                    }
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

        private static void Edge(bool[] mask, int w, int h, int x0, int x1, out float top, out float bottom)
        {
            int t = int.MaxValue, b = -1;
            for (int x = x0; x <= x1; x++)
                for (int y = 0; y < h; y++)
                    if (mask[y * w + x]) { if (y < t) t = y; if (y > b) b = y; }
            if (b < 0) { t = 0; b = h - 1; }
            top = t; bottom = b;
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
