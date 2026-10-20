using FSO.Common.Content;
using FSO.Vitaboy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.Vitaboy
{
    /// <summary>
    /// SimsRedux: what a Sim wears when their clothes or skin can't be found (a download that was taken
    /// out of the game, a uniform the game files don't have) - instead of being drawn pitch black or not
    /// at all:
    ///   - a missing texture: another texture made for the same outfit (any skin tone), else the mesh's own
    ///   - a missing outfit: one for the same occasion (else an everyday one) of the same kind (same gender, age and build)
    /// Each fallback is worked out once and remembered.
    /// </summary>
    public static class ReduxAvatarFallback
    {
        private static Dictionary<string, string> TexCache = new Dictionary<string, string>();
        private static Dictionary<string, Tuple<string, string>> AprCache = new Dictionary<string, Tuple<string, string>>();
        private static List<string> TexNames;
        private static HashSet<string> Logged = new HashSet<string>();

        /// <summary>The website sends these to the server's problem log (set by the web version).</summary>
        public static Action<string, string> ReportProblem;
        private static int Reported;
        private static readonly List<string> Pending = new List<string>();

        private static void Note(string what)
        {
            if (Logged.Count < 200 && Logged.Add(what))
            {
                Console.WriteLine("[SimsRedux] Missing Sim clothes: " + what);
                // (two reports at most - the first one, then a list of the first 15 - so a black head or
                //  outfit can be tracked down to its file without using up the page's problem reports)
                Pending.Add(what);
                if (ReportProblem != null && (Reported == 0 || (Reported == 1 && Pending.Count >= 15)))
                {
                    Reported++;
                    try { ReportProblem("missing Sim texture", string.Join("; ", Pending)); } catch { }
                    Pending.Clear();
                }
            }
        }

        private static int LoadReports;
        /// <summary>A Sim texture that wouldn't load or was lost (reported a few times, so it can be tracked down).</summary>
        public static void TextureProblem(string name, string why, int tries)
        {
            if (tries != 1 && tries != 4) return;
            var line = "texture " + (name ?? "?") + " " + why + (tries > 1 ? " (still failing)" : "");
            Console.WriteLine("[SimsRedux] Sim texture problem: " + line);
            if (ReportProblem != null && LoadReports < 4)
            {
                LoadReports++;
                try { ReportProblem("Sim texture didn't load", line); } catch { }
            }
        }

        private static readonly Dictionary<int, Microsoft.Xna.Framework.Graphics.Texture2D> Plains = new Dictionary<int, Microsoft.Xna.Framework.Graphics.Texture2D>();

        /// <summary>
        /// The last resort for a Sim's picture that can't be had: a plain one in a sensible colour (skin
        /// for heads and hands, in the right skin tone; a soft grey-blue for clothes) - never black.
        /// </summary>
        public static Microsoft.Xna.Framework.Graphics.Texture2D Plain(Microsoft.Xna.Framework.Graphics.GraphicsDevice device, string texName, string meshName)
        {
            var n = (texName ?? "").ToLowerInvariant();
            var m = (meshName ?? "").ToLowerInvariant();
            int skin = n.Contains("drk") ? 2 : n.Contains("med") ? 1 : 0;
            bool skinPart = n.StartsWith("c") || n.StartsWith("h") || m.StartsWith("c") || m.StartsWith("h") || m.Contains("hand");
            // (a Sims Online part: its heads come from the heads archive, everything else is clothes)
            if (FSO.Content.TS1.ReduxTSOAvatars.IsTSOFile(m)) skinPart = m.StartsWith(FSO.Content.TS1.ReduxTSOAvatars.HeadFilePrefix);
            int key = (skinPart ? 10 : 0) + skin;
            Microsoft.Xna.Framework.Graphics.Texture2D tex;
            if (Plains.TryGetValue(key, out tex) && tex != null && !tex.IsDisposed) return tex;
            Microsoft.Xna.Framework.Color c;
            if (skinPart) c = skin == 2 ? new Microsoft.Xna.Framework.Color(112, 76, 54) : skin == 1 ? new Microsoft.Xna.Framework.Color(196, 146, 108) : new Microsoft.Xna.Framework.Color(238, 200, 170);
            else c = new Microsoft.Xna.Framework.Color(120, 132, 160);
            try
            {
                tex = new Microsoft.Xna.Framework.Graphics.Texture2D(device, 2, 2);
                tex.SetData(new[] { c, c, c, c });
                Plains[key] = tex;
            }
            catch { tex = null; }
            return tex;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Microsoft.Xna.Framework.Graphics.Texture2D, object> CheckedFine
            = new System.Runtime.CompilerServices.ConditionalWeakTable<Microsoft.Xna.Framework.Graphics.Texture2D, object>();

        /// <summary>Is this picture completely black? (a picture that didn't come through properly)</summary>
        public static bool AllBlack(Microsoft.Xna.Framework.Graphics.Texture2D tex)
        {
            try
            {
                if (tex.Format != Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color || tex.Width * tex.Height > 1024 * 1024) return false;
                // (SimsRedux: a picture already found fine isn't read back again - every face picture and every copy
                //  of a Sim used to read all its pictures back from the graphics card)
                object seen;
                if (CheckedFine.TryGetValue(tex, out seen)) return false;
                var data = new Microsoft.Xna.Framework.Color[tex.Width * tex.Height];
                // (read back without losing the target being drawn into - on phones and in browsers a plain read
                //  unbound it, and the rest of the Sim, the other Sims and face pictures were drawn to nowhere)
                FSO.Common.Utils.TextureUtils.GetDataKeepTarget(tex.GraphicsDevice, tex, data);
                foreach (var c in data) if (c.R > 8 || c.G > 8 || c.B > 8) { try { CheckedFine.Add(tex, null); } catch { } return false; }
                if (ReportProblem != null) try { ReportProblem("black Sim texture", "A Sim texture came out completely black (" + tex.Width + "x" + tex.Height + ") - using another"); } catch { }
                return true;
            }
            catch { return false; }
        }

        private static List<string> AllTextures()
        {
            if (TexNames != null) return TexNames;
            try
            {
                var tex = FSO.Content.Content.Get().AvatarTextures as FSO.Content.TS1.TS1AvatarTextureProvider;
                TexNames = tex == null ? new List<string>() : tex.GetAllNames().Select(x =>
                {
                    var n = x.ToLowerInvariant();
                    var dot = n.LastIndexOf('.');
                    return dot > 0 ? n.Substring(0, dot) : n;
                }).Distinct().ToList();
            }
            catch { TexNames = new List<string>(); }
            return TexNames;
        }

        private static string Stem(string name)
        {
            name = (name ?? "").ToLowerInvariant();
            var i = name.IndexOf('_');
            return i > 0 ? name.Substring(0, i) : name;
        }

        private static string NoSkin(string stem)
        {
            foreach (var s in new[] { "lgt", "med", "drk" })
                if (stem.EndsWith(s)) return stem.Substring(0, stem.Length - 3);
            return stem;
        }

        private static string SkinOf(string name)
        {
            var stem = Stem(name);
            foreach (var s in new[] { "lgt", "med", "drk" }) if (stem.EndsWith(s)) return s;
            return "lgt";
        }

        /// <summary>A texture that exists, for a wanted texture that doesn't (null: nothing better found).</summary>
        public static string Texture(string wanted, string meshName, string meshTexture)
        {
            if (string.IsNullOrEmpty(wanted)) return null;
            var key = wanted + "|" + meshName;
            string found;
            if (TexCache.TryGetValue(key, out found)) return found;
            found = null;
            try
            {
                var tex = FSO.Content.Content.Get().AvatarTextures;
                if (!string.IsNullOrEmpty(meshTexture) && meshTexture != "x" && tex.Get(meshTexture) != null) found = meshTexture;
                if (found == null)
                {
                    var names = AllTextures();
                    var skin = SkinOf(wanted);
                    var stem = NoSkin(Stem(wanted));
                    var meshStem = NoSkin(Stem(meshName));
                    found = names.FirstOrDefault(n => n.StartsWith(stem + skin))
                        ?? names.FirstOrDefault(n => stem.Length > 2 && n.StartsWith(stem))
                        ?? names.FirstOrDefault(n => meshStem.Length > 2 && n.StartsWith(meshStem + skin))
                        ?? names.FirstOrDefault(n => meshStem.Length > 2 && n.StartsWith(meshStem));
                }
                Note("texture " + wanted + " -> " + (found ?? "(none)"));
            }
            catch { }
            TexCache[key] = found;
            return found;
        }

        /// <summary>
        /// An outfit (appearance name, texture) that exists, for one that doesn't: one of the same kind ("c"
        /// heads, or bodies for the same occasion - same gender/age/build), in the same skin tone.
        /// </summary>
        public static Tuple<string, string> Outfit(string appearance, string texture)
        {
            if (string.IsNullOrEmpty(appearance)) return null;
            var key = appearance + "|" + texture;
            Tuple<string, string> found;
            if (AprCache.TryGetValue(key, out found)) return found;
            found = null;
            try
            {
                var content = FSO.Content.Content.Get();
                var name = appearance.ToLowerInvariant();
                if (name.EndsWith(".apr")) name = name.Substring(0, name.Length - 4);
                // "b003fafit_casual" -> kind 'b', type "fafit"; heads "c012ma_hair" -> 'c', "ma"
                int d = 1;
                while (d < name.Length && char.IsDigit(name[d])) d++;
                var us = name.IndexOf('_');
                var type = (us > d) ? name.Substring(d, us - d) : "";
                foreach (var s in new[] { "lgt", "med", "drk" }) if (type.EndsWith(s)) type = type.Substring(0, type.Length - 3);
                // (the occasion is the first letter, as Sims 1 names them - f formal, s swimwear, l sleepwear,
                //  w outdoors, h high fashion: a stand-in for the same occasion, else an everyday one. The Sims
                //  Online's outfits are named the same way, their costumes with a "b" - see ReduxTSOAvatars)
                var kind = (name.Length > 0 && "cbfslwh".IndexOf(name[0]) >= 0) ? name[0].ToString() : "b";
                FSO.Content.TS1.TS1ClothingCollection col = null;
                List<string> list = null;
                if (type.Length >= 2 && content.BCFGlobal != null)
                    foreach (var occasion in (kind == "c" || kind == "b") ? new[] { kind } : new[] { kind, "b" })
                    {
                        if (!content.BCFGlobal.CollectionsByName.TryGetValue(occasion, out col) || col == null) continue;
                        list = col.GeneralAvatarType(type);
                        if (list.Count == 0) list = col.GeneralAvatarType(type.Substring(0, 2));
                        if (list.Count > 0) break;
                    }
                if (list != null && list.Count > 0)
                {
                    var skin = SkinOf(texture);
                    var names = AllTextures();
                    // (each missing outfit gets its own stand-in, picked by its name - the first one in the list
                    //  for everyone made every Sim with a missing head look like the same old lady)
                    int start = 0;
                    if (list.Count > 0) { uint h = 2166136261; foreach (var ch in name) h = (h ^ ch) * 16777619; start = (int)(h % (uint)list.Count); }
                    for (int k = 0; k < list.Count; k++)
                    {
                        var apr = list[(start + k) % list.Count];
                        if (content.AvatarAppearances.Get(new ContentID(apr + ".apr")) == null) continue;
                        var stem = Stem(apr);
                        var tex = names.FirstOrDefault(n => n.StartsWith(stem + skin)) ?? names.FirstOrDefault(n => n.StartsWith(stem));
                        if (tex == null) continue;
                        found = new Tuple<string, string>(apr + ".apr", tex);
                        break;
                    }
                }
                Note("outfit " + appearance + " -> " + (found?.Item1 ?? "(none)"));
            }
            catch { }
            AprCache[key] = found;
            return found;
        }
    }
}
