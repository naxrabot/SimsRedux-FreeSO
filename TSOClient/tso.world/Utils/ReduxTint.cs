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
    /// Recoloured sprite pictures are made on the CPU from the original Sims 1 pixels and cached,
    /// so it works without any new shaders.
    /// </summary>
    public static class ReduxTint
    {
        public const int ModeNone = 0, ModeWhole = 1, ModeRange = 2;

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

                var px = new Color[w * h];
                for (int i = 0; i < px.Length; i++) px[i] = Apply(src[i], tint);

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
