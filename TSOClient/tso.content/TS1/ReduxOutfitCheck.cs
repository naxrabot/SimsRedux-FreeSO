using System;
using System.Collections.Generic;

namespace FSO.Content.TS1
{
    /// <summary>
    /// SimsRedux 19.9: can this outfit (appearance + texture) be shown properly? Its appearance is known, every part
    /// it's made of has its mesh, and its texture is there. Create-a-Sim and the dressers leave out outfits that
    /// can't (they drew as a black Sim, or as a floating head and hands), and a Sim saved wearing one gets a
    /// stand-in of the same kind (ReduxAvatarFallback).
    ///
    /// It only looks at which files the game has (the file lists, the appearances' part lists read while loading,
    /// the Sims Online archives' file lists) - nothing is read or decoded - so it's cheap, and every game with the
    /// same content (the lot's content stamp) gets the same answer: the dressers' lists are built from it inside
    /// the lot, by number. Each answer is remembered (per outfit and texture) until the content is loaded again.
    /// </summary>
    public static class ReduxOutfitCheck
    {
        private static readonly Dictionary<string, bool> Cache = new Dictionary<string, bool>();
        private static readonly Dictionary<string, bool> MeshCache = new Dictionary<string, bool>();

        /// <summary>Forgets the answers (the content was loaded again).</summary>
        public static void Forget()
        {
            lock (Cache) { Cache.Clear(); MeshCache.Clear(); }
        }

        private static string NoApr(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (name.EndsWith(".apr")) name = name.Substring(0, name.Length - 4);
            return name;
        }

        /// <summary>
        /// Can this outfit be shown with this texture? (appearance with or without ".apr"; the texture as saved in
        /// the Sim, "b001fafitlgt_01"). True when this isn't a Sims 1 game.
        /// </summary>
        public static bool CanShow(string appearance, string texture)
        {
            var apr = NoApr(appearance);
            var tex = (texture ?? "").Trim().ToLowerInvariant();
            if (apr == "") return false;
            var key = apr + "|" + tex;
            bool ok;
            lock (Cache) if (Cache.TryGetValue(key, out ok)) return ok;
            try { ok = MeshesOk(apr) && TextureOk(apr, tex); }
            catch { ok = false; }
            lock (Cache) Cache[key] = ok;
            return ok;
        }

        /// <summary>Is the appearance known, with every part's mesh there? (True when this isn't a Sims 1 game.)</summary>
        public static bool MeshesOk(string appearance)
        {
            var apr = NoApr(appearance);
            if (apr == "") return false;
            bool ok;
            lock (Cache) if (MeshCache.TryGetValue(apr, out ok)) return ok;
            try { ok = ComputeMeshes(apr); }
            catch { ok = false; }
            lock (Cache) MeshCache[apr] = ok;
            return ok;
        }

        private static bool ComputeMeshes(string apr)
        {
            var content = Content.Get();
            if (content == null || !content.TS1) return true;
            // a Sims Online outfit: its parts in every skin tone are in the archives' lists
            if (ReduxTSOAvatars.IsTSOName(apr))
            {
                var set = ReduxTSOAvatars.Instance;
                return set != null && set.MeshesOk(apr);
            }
            var meshes = content.BCFGlobal?.MeshesOf(apr);
            if (meshes == null || meshes.Length == 0) return false;
            var provider = content.AvatarMeshes as TS1BMFProvider;
            if (provider == null) return true;
            foreach (var m in meshes)
                if (string.IsNullOrEmpty(m) || !provider.HasMesh(m)) return false;
            return true;
        }

        private static bool TextureOk(string apr, string tex)
        {
            var content = Content.Get();
            if (content == null || !content.TS1) return true;
            if (tex == "" || tex == "x") return false;
            if (ReduxTSOAvatars.IsTSOName(apr))
            {
                var set = ReduxTSOAvatars.Instance;
                return set != null && set.TexturesOk(apr, tex);
            }
            var provider = content.AvatarTextures as TS1AvatarTextureProvider;
            return provider == null || provider.HasTexture(tex);
        }

        // ------------------------------------------------------------------
        // the texture for a skin tone
        // ------------------------------------------------------------------

        public static readonly string[] Skins = { "lgt", "med", "drk" };

        /// <summary>
        /// The texture a Sims 1 outfit is worn with in this skin tone, from the game's texture names (lower case,
        /// without extension): the one made for this skin ("b001fafit" + "lgt" + "_..."), else one made for no skin
        /// in particular ("b001fafit_..."), else one made for another skin (light, medium, dark - the arms and legs
        /// then show that skin, but the clothes are right). Null: none at all. The name comes back as it is in the
        /// list given (with its extension, if the list has them).
        /// </summary>
        public static string SkinTexture(string appearance, string skin, IEnumerable<string> texNames)
        {
            var apr = NoApr(appearance);
            var us = apr.IndexOf('_');
            var stem = (us > 0) ? apr.Substring(0, us) : apr;
            if (stem.Length < 2 || texNames == null) return null;
            skin = (skin ?? "").ToLowerInvariant();
            string exact = null, plain = null;
            var other = new string[Skins.Length];
            foreach (var n in texNames)
            {
                if (n == null || !n.StartsWith(stem)) continue;
                var rest = n.Substring(stem.Length);
                if (skin.Length > 0 && rest.StartsWith(skin)) { exact = n; break; }
                if (plain == null && (rest.Length == 0 || rest[0] == '_' || rest[0] == '.')) plain = n;
                for (int i = 0; i < Skins.Length; i++)
                    if (other[i] == null && rest.StartsWith(Skins[i])) other[i] = n;
            }
            var found = exact ?? plain;
            if (found == null) foreach (var o in other) if (o != null) { found = o; break; }
            return found;
        }
    }
}
