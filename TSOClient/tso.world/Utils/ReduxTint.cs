using FSO.Common.Rendering;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

using FSO.Common.IO;
namespace FSO.LotView.Utils
{
    /// <summary>
    /// SimsRedux: object colour tinting.
    ///
    /// A tint is packed into one number so it can be saved with the lot and sent in multiplayer:
    ///   bits 0-1   mode: 0 = none, 1 = whole object, 2 = only one colour
    ///   bits 2-10  new hue (0-359)
    ///   bits 11-19 the hue to change (only-one-colour mode)
    ///   bits 20-25 how similar a colour must be, in steps of 2 degrees (only-one-colour mode)
    ///   bits 26-31 strength / saturation, 0-63 (whole-object mode)
    ///
    /// Mode 3 (colour picker, whole object) uses the same bits differently:
    ///   bits 2-10  hue (0-359), bits 11-19 brightness (0-510, 255 = unchanged: darker below, lighter above),
    ///   bits 20-25 how colourful (0-63: 0 = grey/white/black), bits 26-31 strength (how much of the new colour)
    ///
    /// Recoloured sprite pictures are made on the CPU from the original Sims 1 pixels and cached,
    /// so it works without any new shaders.
    /// </summary>
    public static class ReduxTint
    {
        public const int ModeNone = 0, ModeWhole = 1, ModeRange = 2, ModeColour = 3;

        /// <summary>Mode 3: hue 0-359, colourful 0-1, brightness -1 (darker) to +1 (lighter), strength 0-1.</summary>
        public static uint PackColour(int hue, float colourful, float brightness, float strength)
        {
            uint h = (uint)(((hue % 360) + 360) % 360);
            uint b = (uint)Math.Max(0, Math.Min(510, (int)Math.Round((brightness + 1) * 255)));
            uint c = (uint)Math.Max(0, Math.Min(63, (int)Math.Round(colourful * 63)));
            uint st = (uint)Math.Max(0, Math.Min(63, (int)Math.Round(strength * 63)));
            return 3u | (h << 2) | (b << 11) | (c << 20) | (st << 26);
        }

        public static void UnpackColour(uint tint, out int hue, out float colourful, out float brightness, out float strength)
        {
            hue = (int)((tint >> 2) & 511) % 360;
            brightness = ((tint >> 11) & 511) / 255f - 1;
            colourful = ((tint >> 20) & 63) / 63f;
            strength = ((tint >> 26) & 63) / 63f;
        }

        public static uint Pack(int mode, int toHue, int fromHue, int range, float strength)
        {
            if (mode == ModeNone) return 0;
            uint m = (uint)(mode & 3);
            uint th = (uint)(((toHue % 360) + 360) % 360);
            uint fh = (uint)(((fromHue % 360) + 360) % 360);
            uint r = (uint)Math.Max(0, Math.Min(63, range / 2));
            uint st = (uint)Math.Max(0, Math.Min(63, (int)Math.Round(strength * 63)));
            return m | (th << 2) | (fh << 11) | (r << 20) | (st << 26);
        }

        public static void Unpack(uint tint, out int mode, out int toHue, out int fromHue, out int range, out float strength)
        {
            mode = (int)(tint & 3);
            toHue = (int)((tint >> 2) & 511) % 360;
            fromHue = (int)((tint >> 11) & 511) % 360;
            range = (int)((tint >> 20) & 63) * 2;
            strength = ((tint >> 26) & 63) / 63f;
        }

        /// <summary>Recolours one pixel.</summary>
        public static Color Apply(Color c, uint tint)
        {
            if (tint == 0 || c.A == 0) return c;
            if ((tint & 3) == ModeColour)
            {
                int hue; float colourful, bright, str;
                UnpackColour(tint, out hue, out colourful, out bright, out str);
                float h0, s0, v0;
                ToHSV(c, out h0, out s0, out v0);
                // the new colour, as strongly as asked (light and dark parts are kept)
                float s1 = s0 + (colourful - s0) * str;
                if (colourful > 0.02f) s1 = Math.Max(s1, colourful * str * 0.85f);
                // lighter or darker (white and black are possible)
                float v1 = bright >= 0 ? v0 + (1 - v0) * bright : v0 * (1 + bright);
                var res = FromHSV(hue, s1, v1);
                res.A = c.A;
                return res;
            }
            int mode, toHue, fromHue, range; float strength;
            Unpack(tint, out mode, out toHue, out fromHue, out range, out strength);

            float h, s, v;
            ToHSV(c, out h, out s, out v);
            if (mode == ModeWhole)
            {
                // colourise everything: keep light and dark, swap in the new colour
                h = toHue;
                s = Math.Max(s * 0.5f + strength * 0.5f, strength * 0.85f);
            }
            else if (mode == ModeRange)
            {
                // only pixels close to the chosen colour change; their small hue differences are kept
                if (s < 0.12f || v < 0.06f) return c;
                var diff = HueDiff(h, fromHue);
                if (Math.Abs(diff) > Math.Max(4, range)) return c;
                h = toHue + diff;
            }
            else return c;

            var result = FromHSV(h, s, v);
            result.A = c.A;
            return result;
        }

        /// <summary>
        /// SimsRedux: the colour tint on the object's own main colour only - a bookshelf's wood, not
        /// the books on it; a sofa's fabric, not its cushions in another colour. The picture's main
        /// colour is the one most of it is (by hue, or white/grey/black for a mostly plain object);
        /// pixels near it take the new colour, others less the further they are, details keep theirs.
        /// </summary>
        public static Color[] ApplyMain(Color[] src, uint tint)
        {
            var px = new Color[src.Length];
            var hist = new float[36];
            int chroma = 0, plain = 0;
            for (int i = 0; i < src.Length; i++)
            {
                var c = src[i];
                if (c.A < 128) continue;
                float h, s, v;
                ToHSV(c, out h, out s, out v);
                if (v < 0.12f) continue; // (deep shadow says nothing about the colour)
                if (s >= 0.18f) { hist[((int)(h / 10) % 36 + 36) % 36] += s; chroma++; }
                else plain++;
            }
            bool plainMain = plain > chroma;
            // the main hue: the strongest 30 degree band, then the average hue inside it
            float main = 0, mainS = 0.5f;
            if (!plainMain)
            {
                int best = 0; float bestW = -1;
                for (int i = 0; i < 36; i++)
                {
                    var wsum = hist[(i + 35) % 36] + hist[i] * 2 + hist[(i + 1) % 36];
                    if (wsum > bestW) { bestW = wsum; best = i; }
                }
                float sx = 0, sy = 0;
                for (int d = -2; d <= 2; d++)
                {
                    int b = (best + d + 36) % 36;
                    double a = (b * 10 + 5) * Math.PI / 180;
                    sx += (float)Math.Cos(a) * hist[b]; sy += (float)Math.Sin(a) * hist[b];
                }
                main = (float)(Math.Atan2(sy, sx) * 180 / Math.PI);
                if (main < 0) main += 360;
                // how colourful the main colour usually is (so a red book on a brown shelf isn't mistaken for it)
                var sHist = new int[20];
                int sCount = 0;
                for (int i = 0; i < src.Length; i++)
                {
                    var c = src[i];
                    if (c.A < 128) continue;
                    float h, s, v;
                    ToHSV(c, out h, out s, out v);
                    if (v < 0.12f || s < 0.18f || Math.Abs(HueDiff(h, main)) > 12) continue;
                    sHist[Math.Min(19, (int)(s * 20))]++; sCount++;
                }
                for (int b = 0, seen = 0; b < 20; b++) { seen += sHist[b]; if (seen * 2 >= sCount) { mainS = (b + 0.5f) / 20f; break; } }
            }
            for (int i = 0; i < src.Length; i++)
            {
                var c = src[i];
                if (c.A == 0) { px[i] = c; continue; }
                float h, s, v;
                ToHSV(c, out h, out s, out v);
                float w;
                if (v < 0.12f) w = 1; // shadows go with the body
                else if (plainMain) w = s < 0.22f ? 1 : (s < 0.36f ? (0.36f - s) / 0.14f : 0); // a white / grey / black object: its plain parts
                else
                {
                    var d = Math.Abs(HueDiff(h, main));
                    float hw = d <= 14 ? 1 : (d >= 30 ? 0 : (30 - d) / 16f);
                    var ds = Math.Abs(s - mainS);
                    float sw = ds <= 0.16f ? 1 : (ds >= 0.32f ? 0 : (0.32f - ds) / 0.16f);
                    w = s >= 0.18f ? hw * sw : (s <= 0.08f ? 0 : hw * (s - 0.08f) / 0.10f); // (white pages, grey bits keep theirs)
                }
                if (w <= 0) { px[i] = c; continue; }
                var t = Apply(c, tint);
                px[i] = w >= 1 ? t : new Color(
                    (byte)(c.R + (t.R - c.R) * w), (byte)(c.G + (t.G - c.G) * w), (byte)(c.B + (t.B - c.B) * w), c.A);
            }
            return px;
        }

        private static float HueDiff(float h, float target)
        {
            var d = h - target;
            while (d > 180) d -= 360;
            while (d < -180) d += 360;
            return d;
        }

        public static void ToHSV(Color c, out float h, out float s, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float d = max - min;
            v = max;
            s = (max <= 0) ? 0 : d / max;
            if (d <= 0) { h = 0; return; }
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }

        public static Color FromHSV(float h, float s, float v)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Max(0, Math.Min(1, s));
            v = Math.Max(0, Math.Min(1, v));
            float c = v * s;
            float x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            float m = v - c;
            float r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return new Color(r + m, g + m, b + m);
        }

        // ------------------------------------------------------------------
        // Recoloured sprite cache
        // ------------------------------------------------------------------

        private static Dictionary<Texture2D, Dictionary<uint, Texture2D>> Cache = new Dictionary<Texture2D, Dictionary<uint, Texture2D>>();
        private static int CleanCounter;

        /// <summary>
        /// Gives a recoloured copy of a sprite's picture (made once, then cached).
        /// On Windows (DirectX) the game throws away the original pixels once a picture is on the
        /// graphics card, so in that case the pixels are read back from the card.
        /// pending = true means the picture hasn't finished loading yet; try again a bit later.
        /// </summary>
        public static Texture2D GetTinted(GraphicsDevice gd, DGRPSprite sprite, Texture2D original, uint tint, out bool pending)
        {
            pending = false;
            if (tint == 0 || original == null || original.IsDisposed) return original;
            try
            {
                if (++CleanCounter > 2000) { CleanCounter = 0; Clean(); }

                Dictionary<uint, Texture2D> byTint;
                if (!Cache.TryGetValue(original, out byTint))
                {
                    byTint = new Dictionary<uint, Texture2D>();
                    Cache[original] = byTint;
                }
                Texture2D result;
                if (byTint.TryGetValue(tint, out result) && result != null && !result.IsDisposed) return result;

                int w = original.Width, h = original.Height;
                Color[] src = null;

                // 1. the original pixels, if the game still has them
                var frame = sprite?.GetSPR2Frame();
                if (frame != null)
                {
                    try { frame.DecodeIfRequired(false); } catch { }
                    var pd = frame.PixelData;
                    if (pd != null && pd.Length == w * h) src = (Color[])pd.Clone();
                }

                // 2. otherwise read them back from the graphics card
                if (src == null)
                {
                    if (original.Format != SurfaceFormat.Color) { Log("not a colour texture: " + original.Format); return original; }
                    src = new Color[w * h];
                    original.GetData(src);
                    bool any = false;
                    for (int i = 0; i < src.Length; i++) if (src[i].A != 0) { any = true; break; }
                    if (!any) { pending = true; return original; } // still loading
                }

                var px = ((tint & 3) == ModeColour) ? ApplyMain(src, tint) : new Color[w * h];
                if ((tint & 3) != ModeColour) for (int i = 0; i < px.Length; i++) px[i] = Apply(src[i], tint);

                result = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
                result.SetData(px);
                result.Tag = original.Tag ?? new TextureInfo(result, w, h);
                byTint[tint] = result;
                return result;
            }
            catch (Exception e)
            {
                Log("tint failed: " + e.ToString());
                return original;
            }
        }

        /// <summary>
        /// SimsRedux: throw away the recoloured pictures made for these tints (the Tint window's
        /// previews while the sliders moved - nothing shows them any more).
        /// </summary>
        public static void Forget(ICollection<uint> tints)
        {
            if (tints == null || tints.Count == 0) return;
            try
            {
                foreach (var byTint in Cache.Values)
                {
                    foreach (var t in tints)
                    {
                        Texture2D tex;
                        if (byTint.TryGetValue(t, out tex))
                        {
                            byTint.Remove(t);
                            try { if (tex != null && !tex.IsDisposed) tex.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (Exception e) { Log("forget failed: " + e.Message); }
        }

        /// <summary>Forget recoloured pictures whose originals are gone.</summary>
        private static void Clean()
        {
            var dead = new List<Texture2D>();
            foreach (var pair in Cache)
            {
                if (pair.Key.IsDisposed)
                {
                    dead.Add(pair.Key);
                    foreach (var t in pair.Value.Values) t?.Dispose();
                }
            }
            foreach (var d in dead) Cache.Remove(d);
        }

        private static int LogCount;
        public static void Log(string text)
        {
            Console.WriteLine("[SimsRedux] " + text);
            if (LogCount++ > 50) return;
            try
            {
                var dir = FSO.Common.FSOEnvironment.UserDir;
                if (string.IsNullOrEmpty(dir)) return;
                FSOFile.AppendAllText(System.IO.Path.Combine(dir, "tint_debug.log"), DateTime.Now + " " + text + "\r\n");
            }
            catch { }
        }
    }
}
