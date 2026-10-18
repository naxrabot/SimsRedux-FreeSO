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
    ///   - a missing outfit: the first everyday outfit of the same kind (same gender, age and build)
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
        /// An outfit (appearance name, texture) that exists, for one that doesn't: the first of the same
        /// kind ("b" bodies or "c" heads, same gender/age/build), in the same skin tone.
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
                var kind = (name.Length > 0 && (name[0] == 'c' || name[0] == 'b')) ? name[0].ToString() : "b";
                FSO.Content.TS1.TS1ClothingCollection col;
                if (type.Length >= 2 && content.BCFGlobal != null && content.BCFGlobal.CollectionsByName.TryGetValue(kind, out col))
                {
                    var skin = SkinOf(texture);
                    var list = col.GeneralAvatarType(type);
                    if (list.Count == 0) list = col.GeneralAvatarType(type.Substring(0, 2));
                    var names = AllTextures();
                    foreach (var apr in list)
                    {
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
