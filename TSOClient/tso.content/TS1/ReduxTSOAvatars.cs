using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FSO.Common.IO;
using FSO.Content.Codecs;
using FSO.Content.Model;
using FSO.Files.FAR3;
using FSO.Vitaboy;

namespace FSO.Content.TS1
{
    /// <summary>
    /// SimsRedux (Sims Online import, stage 2): The Sims Online's adult clothes and heads, worn by Sims 1 Sims.
    ///
    /// They come from TSO's avatardata/bodies and avatardata/heads archives (FAR3 .dat files: collections ->
    /// purchasables -> outfits -> one appearance per skin tone -> bindings -> mesh + texture). The archives are
    /// opened through the game's file layer (FSOFile) and only their file lists are read up front; a mesh or
    /// texture is read when a Sim wears it and kept for a while (timed caches), so the browser never loads a
    /// whole .dat (textures.dat is 71 MB). The small lists (collections, outfits, appearances, bindings) are
    /// read once while loading and those archives are closed again.
    ///
    /// The Sims 1 side only knows outfits by name, so each TSO outfit gets a stable Sims 1-style name that is
    /// saved in character files as text, like any other outfit:
    ///   bodies  {o}{nnn}{f|m}a{fit|fat|skn}_xtso{key}  texture {o}{nnn}{f|m}a{build}{lgt|med|drk}_xtso{key}
    ///   heads   c{nnn}{f|m}a_xtso{key}                  texture c{nnn}{f|m}a{lgt|med|drk}_xtso{key}
    /// (o: the occasion, as Sims 1 names its outfits - f formal, s swimwear, l sleepwear, b everyday and costumes;
    /// nnn: TSO's own number for it; key: 8 hex digits worked out from TSO's own name for the outfit, so the name
    /// never changes and never clashes with a Sims 1 or downloaded one). It follows the Sims 1 pattern on purpose:
    /// the body type is where Create-a-Sim, the wardrobe and the occasion outfits look for it, and a game without
    /// these files gives the Sim a Sims 1 stand-in of the same kind and occasion (ReduxAvatarFallback; a costume
    /// gets an everyday one) while keeping the name.
    /// The few TSO outfits made in more than one body size (the same textures and meshes for fit, fat and skinny)
    /// share one key and number, so Create-a-Sim shows them once with its size buttons, like the Sims 1 ones. The
    /// names 13.5 gave them (all "b", one key per size) are still found (Find), so Sims saved then keep them.
    /// The skin tone travels in the texture name (as in Sims 1): Avatar.AddAppearance swaps in the appearance
    /// made for that skin tone (ForSkin), since a TSO outfit's parts can change with the skin.
    ///
    /// Inside, meshes and textures are named "xtsob~" (bodies archive) or "xtsoh~" (heads archive) + TSO's file
    /// name; those names only live in memory. The hands stay the Sims 1 ones, in the Sim's skin tone.
    /// A mesh that uses a bone the Sims 1 skeleton doesn't have is left out (and logged); a broken archive leaves
    /// out its half (bodies or heads), the rest still loads.
    /// </summary>
    public class ReduxTSOAvatars
    {
        /// <summary>The loaded set (null: no Sims Online clothes this time).</summary>
        public static ReduxTSOAvatars Instance;

        /// <summary>Marks a Sims Online outfit's appearance and texture names ("b113faskn_xtso1a2b3c4d").</summary>
        public const string Marker = "_xtso";
        public const string BodyFilePrefix = "xtsob~";
        public const string HeadFilePrefix = "xtsoh~";
        public static readonly string[] Skins = { "lgt", "med", "drk" };
        /// <summary>The Create-a-Sim occasion of a costume (a Sims 1 Sim keeps one, as person string 35 - see
        /// VMSuitProvider). Its name starts with "b", so without these files it gets an everyday stand-in.</summary>
        public const char CostumeKind = 'x';

        /// <summary>One mesh of an outfit with its texture (both "xtsob~"/"xtsoh~" names) and the bone it hangs off.</summary>
        public class Part
        {
            public string Bone;
            public string Mesh;
            public string Texture;
        }

        public class TSOOutfit
        {
            /// <summary>TSO's own name for it ("fab113_ct__tragclown").</summary>
            public string TsoName;
            /// <summary>The TSO collection it was found in first ("costumes_female").</summary>
            public string Collection;
            /// <summary>Every TSO collection it's in.</summary>
            public List<string> Collections = new List<string>();
            public bool Head;
            /// <summary>'f' or 'm'.</summary>
            public char Gender;
            /// <summary>The Create-a-Sim occasion: 'b' everyday, 'f' formal, 's' swimwear, 'l' sleepwear,
            /// CostumeKind costume ('c' for heads).</summary>
            public char Kind;
            /// <summary>fit, fat or skn (bodies; "" for heads).</summary>
            public string Build;
            /// <summary>Its Sims 1-style appearance name (no ".apr").</summary>
            public string Name;
            /// <summary>The name 13.5 gave it, when that was different (still found by Find).</summary>
            public string OldName;
            /// <summary>The same outfit in the other body sizes (including this one; null: it only comes in one).</summary>
            public List<TSOOutfit> Sizes;
            internal string Number;
            /// <summary>Its parts for each skin tone (lgt, med, drk).</summary>
            public Part[][] Parts = new Part[3][];
            /// <summary>The part whose texture changes with the skin tone (what the texture name stands for).</summary>
            public int Primary;
            internal Appearance[] Aprs = new Appearance[3];

            /// <summary>Its texture name for a skin tone ("b113fasknmed_xtso1a2b3c4d") - what character files save.</summary>
            public string TextureName(string skin)
            {
                var us = Name.IndexOf('_');
                return Name.Insert(us, SkinIndex(skin) >= 0 ? skin.ToLowerInvariant() : "lgt");
            }
        }

        public int ArchiveCount;
        public long ArchiveBytes;
        public int MeshCount, TextureCount, Skipped;

        private readonly Dictionary<string, TSOOutfit> ByName = new Dictionary<string, TSOOutfit>();
        /// <summary>The names 13.5 gave outfits that are named differently now (they're in saved Sims).</summary>
        private readonly Dictionary<string, TSOOutfit> OldNames = new Dictionary<string, TSOOutfit>();
        private readonly List<TSOOutfit> All = new List<TSOOutfit>();
        private readonly Dictionary<string, Tuple<Far3, Far3.Entry>> MeshEntries = new Dictionary<string, Tuple<Far3, Far3.Entry>>();
        private readonly Dictionary<string, Tuple<Far3, Far3.Entry>> TexEntries = new Dictionary<string, Tuple<Far3, Far3.Entry>>();
        private readonly List<Far3> Open = new List<Far3>();
        private FSO.Common.Utils.TimedReferenceCache<string, Mesh> MeshCache = new FSO.Common.Utils.TimedReferenceCache<string, Mesh>();
        private FSO.Common.Utils.TimedReferenceCache<string, ITextureRef> TexCache = new FSO.Common.Utils.TimedReferenceCache<string, ITextureRef>();
        private readonly HashSet<string> Failed = new HashSet<string>();
        private HashSet<string> AdultBones;
        private bool AdultBonesTried;

        // ------------------------------------------------------------------
        // names
        // ------------------------------------------------------------------

        /// <summary>Is this a Sims Online outfit's appearance or texture name (with or without ".apr")?</summary>
        public static bool IsTSOName(string name)
        {
            return name != null && name.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) > 0;
        }

        /// <summary>Is this one of the in-memory mesh/texture names ("xtsob~...", "xtsoh~...")?</summary>
        public static bool IsTSOFile(string name)
        {
            return name != null && (name.StartsWith(BodyFilePrefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(HeadFilePrefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>0 lgt, 1 med, 2 drk, -1 something else.</summary>
        public static int SkinIndex(string skin)
        {
            return skin == null ? -1 : Array.IndexOf(Skins, skin.ToLowerInvariant());
        }

        /// <summary>The skin tone in a texture name ("b113fasknmed_xtso.." -> 1), -1 if it has none.</summary>
        public static int SkinOfTexture(string tex)
        {
            if (tex == null) return -1;
            var us = tex.IndexOf('_');
            if (us < 3) return -1;
            return SkinIndex(tex.Substring(us - 3, 3));
        }

        private static string Lower(string name)
        {
            return (name ?? "").Replace('\\', '/').ToLowerInvariant();
        }

        private static string NoExt(string name, string ext)
        {
            name = Lower(name);
            if (ext != null && name.EndsWith(ext)) return name.Substring(0, name.Length - ext.Length);
            return name;
        }

        private static string Key(string tsoName)
        {
            uint h = 2166136261;
            foreach (var ch in tsoName) h = (h ^ ch) * 16777619;
            return h.ToString("x8");
        }

        // ------------------------------------------------------------------
        // what the rest of the game asks for
        // ------------------------------------------------------------------

        /// <summary>The outfits for Create-a-Sim: heads or bodies, 'f' or 'm' (adults only; TSO has no children).</summary>
        public List<TSOOutfit> List(bool head, char gender)
        {
            return All.Where(x => x.Head == head && x.Gender == gender).ToList();
        }

        public int Count { get { return All.Count; } }

        /// <summary>
        /// The outfits a dresser offers a Sim for one occasion ('b' everyday, 'f', 's', 'l'): grown-ups only, the
        /// Sim's gender and build, in loading order. Only what the content stamp covers decides it (the archives'
        /// file lists), so a lot worker and its players get the same list.
        /// </summary>
        public List<TSOOutfit> ForDresser(char gender, string build, char kind)
        {
            build = (build ?? "").ToLowerInvariant();
            return All.Where(x => !x.Head && x.Gender == gender && x.Build == build && x.Kind == kind).ToList();
        }

        public TSOOutfit Find(string name)
        {
            TSOOutfit o;
            name = NoExt(name, ".apr");
            if (ByName.TryGetValue(name, out o)) return o;
            return OldNames.TryGetValue(name, out o) ? o : null;
        }

        /// <summary>The first letter of a TSO outfit's name for an occasion (what a stand-in is picked by).</summary>
        public static char NameLetter(char kind)
        {
            return (kind == 'f' || kind == 's' || kind == 'l') ? kind : 'b';
        }

        /// <summary>The appearance by its Sims 1-style name (light skin; ForSkin gives the others). Null: not known.</summary>
        public Appearance GetAppearance(string name)
        {
            var o = Find(name);
            return o == null ? null : AppearanceFor(o, 0);
        }

        private Appearance AppearanceFor(TSOOutfit o, int skin)
        {
            if (skin < 0 || skin > 2) skin = 0;
            lock (o)
            {
                var apr = o.Aprs[skin];
                if (apr != null) return apr;
                apr = new Appearance
                {
                    Name = o.Name,
                    Bindings = o.Parts[skin].Select(p => new AppearanceBinding
                    {
                        RealBinding = new Binding { Bone = p.Bone, MeshName = p.Mesh, TextureName = p.Texture }
                    }).ToArray()
                };
                o.Aprs[skin] = apr;
                return apr;
            }
        }

        /// <summary>
        /// A Sims Online appearance in the skin tone its texture name says (Avatar.AddAppearance asks this for every
        /// appearance it puts on): null for anything else, which is then used as it is.
        /// </summary>
        public static Appearance ForSkin(Appearance apr, string texture)
        {
            var set = Instance;
            if (set == null || apr == null || !IsTSOName(apr.Name)) return null;
            var o = set.Find(apr.Name);
            if (o == null) return null;
            return set.AppearanceFor(o, Math.Max(0, SkinOfTexture(texture)));
        }

        /// <summary>A mesh by its in-memory name ("xtsob~fabb001fafit_01-pelvis-body"). Null: missing or left out.</summary>
        public Mesh GetMesh(string name)
        {
            name = NoExt(name, ".mesh");
            Tuple<Far3, Far3.Entry> where;
            if (!MeshEntries.TryGetValue(name, out where)) return null;
            lock (Failed) if (Failed.Contains(name)) return null;
            return MeshCache.GetOrAdd(name, k =>
            {
                try
                {
                    Mesh mesh;
                    using (var str = new MemoryStream(where.Item1.Read(where.Item2), false)) mesh = (Mesh)new MeshCodec().GenDecode(str);
                    var odd = UnknownBones(mesh);
                    if (odd != null)
                    {
                        Fail(k, "uses bones the Sims 1 skeleton doesn't have (" + odd + ") - left out");
                        return null;
                    }
                    return mesh;
                }
                catch (Exception e)
                {
                    Fail(k, "couldn't be read: " + e.Message);
                    return null;
                }
            });
        }

        /// <summary>A texture: an in-memory name ("xtsob~cr_dw_b007fafitlgt_x"), or an outfit's texture name (its main part).</summary>
        public ITextureRef GetTexture(string name)
        {
            name = Lower(name);
            if (IsTSOName(name) && !IsTSOFile(name))
            {
                // an outfit's texture name: the main part's texture in that skin tone
                var us = name.IndexOf('_');
                var skin = SkinOfTexture(name);
                if (skin < 0) return null;
                var o = Find(name.Remove(us - 3, 3));
                if (o == null) return null;
                var parts = o.Parts[skin];
                if (parts.Length == 0) return null;
                name = parts[Math.Min(o.Primary, parts.Length - 1)].Texture;
            }
            var dot = name.LastIndexOf('.');
            if (dot > name.IndexOf('~')) name = name.Substring(0, dot);
            Tuple<Far3, Far3.Entry> where;
            if (!TexEntries.TryGetValue(name, out where)) return null;
            lock (Failed) if (Failed.Contains(name)) return null;
            return TexCache.GetOrAdd(name, k =>
            {
                try
                {
                    using (var str = new MemoryStream(where.Item1.Read(where.Item2), false))
                        return (ITextureRef)new TextureCodec().GenDecode(str);
                }
                catch (Exception e)
                {
                    Fail(k, "couldn't be read: " + e.Message);
                    return null;
                }
            });
        }

        private int Reported;
        private void Fail(string name, string why)
        {
            lock (Failed) { if (!Failed.Add(name)) return; Reported++; }
            Console.WriteLine("[SimsRedux] Sims Online " + name + " " + why);
            // (the first few go to the problem log - a damaged archive mustn't fill it)
            if (Reported <= 3) try { FSO.Content.Framework.FAR1Provider<object>.ReportProblem?.Invoke("Sims Online clothes", name + " " + why); } catch { }
        }

        /// <summary>The bones a mesh moves with that the Sims 1 adult skeleton doesn't have ("" none: null).</summary>
        private string UnknownBones(Mesh mesh)
        {
            if (!AdultBonesTried)
            {
                AdultBonesTried = true;
                try
                {
                    var skel = Content.Get()?.AvatarSkeletons?.Get("adult.skel");
                    if (skel?.Bones != null) AdultBones = new HashSet<string>(skel.Bones.Select(b => (b.Name ?? "").ToUpperInvariant()));
                }
                catch { }
                if (AdultBones == null) Console.WriteLine("[SimsRedux] Sims Online clothes: no Sims 1 adult skeleton to check their bones against");
            }
            if (AdultBones == null || mesh.BoneBindings == null) return null;
            var odd = mesh.BoneBindings
                .Where(b => (b.RealVertexCount > 0 || b.BlendVertexCount > 0) && !AdultBones.Contains((b.BoneName ?? "").ToUpperInvariant()))
                .Select(b => b.BoneName).Distinct().ToList();
            return odd.Count == 0 ? null : string.Join(", ", odd);
        }

        // ------------------------------------------------------------------
        // loading
        // ------------------------------------------------------------------

        /// <summary>Forgets the loaded set (no Sims Online clothes).</summary>
        public static ReduxTSOAvatars Unload()
        {
            var old = Instance;
            Instance = null;
            if (old != null) old.Close();
            return null;
        }

        /// <summary>Loads them from the Sims Online folder (Content.TSOBasePath). Null: none there.</summary>
        public static ReduxTSOAvatars Load()
        {
            Unload();
            var set = new ReduxTSOAvatars();
            try
            {
                set.LoadPart("bodies", BodyFilePrefix, false);
                set.LoadPart("heads", HeadFilePrefix, true);
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] Sims Online clothes couldn't be loaded: " + e.Message);
                set.Close();
                return null;
            }
            Console.WriteLine("[SimsRedux] Sims Online clothes: " + set.All.Count(x => !x.Head) + " outfits and " + set.All.Count(x => x.Head) + " heads ("
                + set.MeshCount + " meshes, " + set.TextureCount + " textures, " + set.ArchiveCount + " archives; " + set.Skipped + " left out)");
            if (set.All.Count == 0) { set.Close(); return null; }
            Instance = set;
            return set;
        }

        private void Close()
        {
            foreach (var f in Open) f.Dispose();
            Open.Clear();
        }

        // collections and the occasion their outfits are for ('\0': not used). Costumes: TSO's costumes, old
        // world, sci-fi, togas, Vaudeville and the old west, Vegas, and its work uniforms (dressing-up and
        // role-play clothes - a Sims 1 Sim already changes into their own job's uniform for work). Everything
        // else - TSO's everyday clothes and its sportswear (jerseys, softball and track kit) - is everyday wear.
        private static char OccasionOf(string collection, bool head)
        {
            if (collection.Contains("internal") || collection.StartsWith("naked")) return '\0';
            if (head) return 'c';
            if (collection.StartsWith("formalwear") || collection.StartsWith("wedding")) return 'f';
            if (collection.StartsWith("swimwear")) return 's';
            if (collection.StartsWith("sleepwear")) return 'l';
            foreach (var c in CostumeCollections) if (collection.StartsWith(c)) return CostumeKind;
            return 'b';
        }

        private static readonly string[] CostumeCollections = { "costumes", "oldworld", "scifi", "toga", "vaudwest", "vegas", "uniforms" };

        // which occasion wins when an outfit is in more than one collection
        private static int Rank(char kind)
        {
            return kind == 'b' ? 0 : kind == CostumeKind ? 1 : 2;
        }

        // costumes TSO keeps with its everyday clothes ("ea_..." collections): those it names as costumes itself
        // ("fab710_ct", "fab110_ct-luau-01" - its House Party ones), and the unmistakable ones (superheroes,
        // knights, animal suits, robots, a legionary, Cleopatra, a mime...)
        private static readonly Regex CostumeNameRx = new Regex(
            "^[mf]ab\\d{3}_(ct([_-]|$)|hero|ironhero|caped|capeggplant|trident|supersim$|cleo$|legio$|quelknight|infoverlord$|hypnotist$|robot$|bears$|polarbear$|mnst$|purpgorilla$|mime_)",
            RegexOptions.Compiled);

        /// <summary>Is this TSO outfit name (TSO's own, "fab113_ct__tragclown") a costume by its name alone?</summary>
        public static bool IsCostumeName(string tsoName)
        {
            return tsoName != null && CostumeNameRx.IsMatch(tsoName.ToLowerInvariant());
        }

        // a TSO file name without its number and body size ("xtsob~ct_uf_b852fafitlgt_military_01" -> "ct_uf_*_military_01")
        private static readonly Regex SizeRx = new Regex("b\\d{3}[mf]a(fit|fat|skn)(lgt|med|drk)?", RegexOptions.Compiled);
        private static string NoSize(string file)
        {
            file = file ?? "";
            var t = file.IndexOf('~');
            if (t >= 0) file = file.Substring(t + 1);
            return SizeRx.Replace(file, "*");
        }

        /// <summary>
        /// TSO made a few outfits in more than one body size: the same meshes and textures (apart from the number
        /// and size in their file names) for fit, fat and skinny, as separate outfits ("fab851_uf__military_01" fat,
        /// "fab852_..." fit, "fab853_..." skinny). They get one key (from those shared file names) and the lowest
        /// number, so their names only differ in the size - what Create-a-Sim groups by. Only when each size is
        /// there once, for the same gender and occasion.
        /// </summary>
        private static void SizeGroups(List<TSOOutfit> outfits)
        {
            var groups = outfits.GroupBy(o => o.Gender + "|" + o.Kind + "|"
                + string.Join("+", o.Parts[0].Select(p => NoSize(p.Mesh) + "=" + NoSize(p.Texture)))).ToList();
            foreach (var g in groups)
            {
                var list = g.ToList();
                if (list.Count < 2 || !g.Key.Contains("*") || list.Select(x => x.Build).Distinct().Count() != list.Count) continue;
                var key = Key("size|" + g.Key);
                var number = list.Select(x => x.Number).OrderBy(x => x, StringComparer.Ordinal).First();
                foreach (var o in list)
                {
                    o.Number = number;
                    o.Name = NameLetter(o.Kind) + number + o.Gender + "a" + o.Build + Marker + key;
                    o.Sizes = list;
                }
            }
        }

        // the order they're listed in: TSO's everyday clothes first, then the rest
        private static int OrderOf(string collection)
        {
            if (collection.StartsWith("ea_")) return 0;
            if (collection.StartsWith("daywear")) return 1;
            return 2;
        }

        private static readonly Regex BuildRx = new Regex("b\\d{3}[mf]a(fit|fat|skn)", RegexOptions.Compiled);
        private static readonly Regex NumberRx = new Regex("^[mf]a[bh](\\d{3})", RegexOptions.Compiled);

        private void LoadPart(string part, string prefix, bool head)
        {
            var subs = new[] { "collections", "purchasables", "outfits", "appearances", "bindings", "meshes", "textures" };
            var fars = new Dictionary<string, Far3>();
            foreach (var sub in subs)
            {
                var rel = "avatardata/" + part + "/" + sub + "/" + sub + ".dat";
                var path = Content.TSOPath(rel);
                try
                {
                    if (!FSOFile.Exists(path)) throw new FileNotFoundException("missing");
                    fars[sub] = new Far3(path, rel);
                }
                catch (Exception e)
                {
                    Console.WriteLine("[SimsRedux] Sims Online " + part + " left out: " + rel + ": " + e.Message);
                    try { FSO.Content.Framework.FAR1Provider<object>.ReportProblem?.Invoke("broken Sims Online file", rel + ": " + e.Message); } catch { }
                    foreach (var f in fars.Values) f.Dispose();
                    return;
                }
            }

            var newOutfits = new List<TSOOutfit>();
            try
            {
                var meshes = fars["meshes"];
                var textures = fars["textures"];
                var meshNames = new Dictionary<ulong, string>();
                var texNames = new Dictionary<ulong, string>();
                foreach (var e in meshes.Entries)
                {
                    var n = prefix + NoExt(e.Name, ".mesh");
                    meshNames[e.ID] = n;
                    if (!MeshEntries.ContainsKey(n)) MeshEntries[n] = Tuple.Create(meshes, e);
                }
                foreach (var e in textures.Entries)
                {
                    var n = Lower(e.Name);
                    var dot = n.LastIndexOf('.');
                    if (dot > 0) n = n.Substring(0, dot);
                    n = prefix + n;
                    texNames[e.ID] = n;
                    if (!TexEntries.ContainsKey(n)) TexEntries[n] = Tuple.Create(textures, e);
                }

                // collections -> purchasables -> outfits
                var found = new Dictionary<ulong, TSOOutfit>();
                var oftIDs = new Dictionary<TSOOutfit, Outfit>();
                var cols = fars["collections"].Entries.Select((e, i) => new { e, i, name = NoExt(e.Name, ".col") })
                    .OrderBy(x => OrderOf(x.name)).ThenBy(x => x.i).ToList();
                foreach (var c in cols)
                {
                    var kind = OccasionOf(c.name, head);
                    if (kind == '\0') continue;
                    Collection col;
                    try { col = new Collection(fars["collections"].Read(c.e)); }
                    catch (Exception e) { Console.WriteLine("[SimsRedux] Sims Online collection " + c.name + " skipped: " + e.Message); continue; }
                    foreach (var item in col)
                    {
                        try
                        {
                            var po = fars["purchasables"].Get(item.TypeID, item.FileID);
                            if (po == null) { Skipped++; continue; }
                            var pur = new PurchasableOutfit();
                            using (var str = new MemoryStream(fars["purchasables"].Read(po), false)) pur.Read(str);
                            Far3.Entry oe;
                            if (!fars["outfits"].ById.TryGetValue(pur.OutfitID, out oe)) { Skipped++; continue; }
                            TSOOutfit known;
                            if (found.TryGetValue(oe.ID, out known))
                            {
                                // in more than one collection: an occasion (formal, swimwear, sleepwear) wins over
                                // costume, and that over everyday
                                if (!known.Collections.Contains(c.name)) known.Collections.Add(c.name);
                                if (Rank(kind) > Rank(known.Kind)) known.Kind = kind;
                                continue;
                            }
                            var tsoName = NoExt(oe.Name, ".oft");
                            // adults only, men and women (TSO's own names: fab/mab bodies, fah/mah heads)
                            if (tsoName.Length < 4 || (tsoName[0] != 'f' && tsoName[0] != 'm') || tsoName[1] != 'a') continue;
                            var oft = new Outfit();
                            using (var str = new MemoryStream(fars["outfits"].Read(oe), false)) oft.Read(str);
                            var o = new TSOOutfit { TsoName = tsoName, Collection = c.name, Head = head, Gender = tsoName[0], Kind = kind };
                            o.Collections.Add(c.name);
                            found[oe.ID] = o;
                            oftIDs[o] = oft;
                            newOutfits.Add(o);
                        }
                        catch (Exception e) { Skipped++; Console.WriteLine("[SimsRedux] Sims Online outfit skipped (" + c.name + "): " + e.Message); }
                    }
                }

                // outfits -> one appearance per skin tone -> bindings -> mesh + texture
                var ready = new List<TSOOutfit>();
                foreach (var o in newOutfits)
                {
                    var oft = oftIDs[o];
                    var aprs = new[]
                    {
                        Tuple.Create(oft.LightAppearanceTypeID, oft.LightAppearanceFileID),
                        Tuple.Create(oft.MediumAppearanceTypeID, oft.MediumAppearanceFileID),
                        Tuple.Create(oft.DarkAppearanceTypeID, oft.DarkAppearanceFileID)
                    };
                    for (int s = 0; s < 3; s++) o.Parts[s] = ReadAppearance(fars, aprs[s].Item1, aprs[s].Item2, meshNames, texNames);
                    var any = o.Parts.FirstOrDefault(p => p != null && p.Length > 0);
                    if (any == null) { Skipped++; Console.WriteLine("[SimsRedux] Sims Online outfit " + o.TsoName + " left out: nothing of it could be found"); continue; }
                    for (int s = 0; s < 3; s++) if (o.Parts[s] == null || o.Parts[s].Length == 0) o.Parts[s] = any; // (a skin tone it doesn't come in)

                    // the part that changes with the skin tone (else the first)
                    o.Primary = 0;
                    if (o.Parts[0].Length == o.Parts[1].Length && o.Parts[0].Length == o.Parts[2].Length)
                        for (int i = 0; i < o.Parts[0].Length; i++)
                            if (o.Parts[0][i].Texture != o.Parts[1][i].Texture || o.Parts[0][i].Texture != o.Parts[2][i].Texture) { o.Primary = i; break; }

                    var main = o.Parts[0][o.Primary];
                    var num = NumberRx.Match(o.TsoName);
                    o.Number = num.Success ? num.Groups[1].Value : "900";
                    // (costumes TSO keeps with its everyday clothes)
                    if (!head && o.Kind == 'b' && IsCostumeName(o.TsoName)) o.Kind = CostumeKind;
                    if (head)
                    {
                        o.Build = "";
                        o.Name = "c" + o.Number + o.Gender + "a" + Marker + Key(o.TsoName);
                    }
                    else
                    {
                        var b = BuildRx.Match(main.Texture);
                        if (!b.Success) b = BuildRx.Match(main.Mesh);
                        o.Build = b.Success ? b.Groups[1].Value : "fit";
                        o.Name = NameLetter(o.Kind) + o.Number + o.Gender + "a" + o.Build + Marker + Key(o.TsoName);
                        o.OldName = "b" + o.Number + o.Gender + "a" + o.Build + Marker + Key(o.TsoName);
                    }
                    ready.Add(o);
                }

                // the same outfit in other body sizes: one key and number for all of them (see SizeGroups)
                if (!head) SizeGroups(ready);

                foreach (var o in ready)
                {
                    if (ByName.ContainsKey(o.Name))
                    {
                        Skipped++;
                        Console.WriteLine("[SimsRedux] Sims Online outfit " + o.TsoName + " left out: its name " + o.Name + " is taken by " + ByName[o.Name].TsoName);
                        continue;
                    }
                    ByName[o.Name] = o;
                    All.Add(o);
                }
                // (the names 13.5 saved, for those named differently now)
                foreach (var o in ready)
                    if (o.OldName != null && o.OldName != o.Name && ByName.ContainsKey(o.Name) && ByName[o.Name] == o
                        && !ByName.ContainsKey(o.OldName) && !OldNames.ContainsKey(o.OldName))
                        OldNames[o.OldName] = o;
            }
            finally
            {
                // only the meshes and textures are read later; the lists are done with
                foreach (var kv in fars)
                    if (kv.Key != "meshes" && kv.Key != "textures") kv.Value.Dispose();
            }

            Open.Add(fars["meshes"]);
            Open.Add(fars["textures"]);
            MeshCount += fars["meshes"].Entries.Count;
            TextureCount += fars["textures"].Entries.Count;
            foreach (var f in fars.Values)
            {
                ArchiveCount++;
                ArchiveBytes += f.Length;
            }
        }

        private Part[] ReadAppearance(Dictionary<string, Far3> fars, uint type, uint file, Dictionary<ulong, string> meshNames, Dictionary<ulong, string> texNames)
        {
            var ae = fars["appearances"].Get(type, file);
            if (ae == null) return null;
            var apr = new Appearance();
            try { using (var str = new MemoryStream(fars["appearances"].Read(ae), false)) apr.Read(str); }
            catch { return null; }
            var parts = new List<Part>();
            foreach (var ab in apr.Bindings ?? new AppearanceBinding[0])
            {
                try
                {
                    var be = fars["bindings"].Get(ab.TypeID, ab.FileID);
                    if (be == null) continue;
                    var bnd = new Binding();
                    using (var str = new MemoryStream(fars["bindings"].Read(be), false)) bnd.Read(str);
                    string mesh, tex;
                    if (!meshNames.TryGetValue(Far3.MakeID(bnd.MeshTypeID, bnd.MeshFileID), out mesh)) continue;
                    if (!texNames.TryGetValue(Far3.MakeID(bnd.TextureTypeID, bnd.TextureFileID), out tex)) continue;
                    parts.Add(new Part { Bone = bnd.Bone, Mesh = mesh, Texture = tex });
                }
                catch { }
            }
            return parts.ToArray();
        }

        /// <summary>
        /// A FAR3 archive (TSO's .dat files), read through FSOFile a piece at a time: its file list is read when it's
        /// opened, each file when it's asked for. (Not FAR3Archive: that one silently gives up while another archive
        /// is being read, and stops at the first two files with the same number.)
        /// </summary>
        internal class Far3 : IDisposable
        {
            public class Entry
            {
                public string Name;
                public uint TypeID, FileID;
                public ulong ID;
                public uint Offset, Size;
                public bool Compressed;
            }

            public readonly string Rel;
            public readonly long Length;
            public readonly List<Entry> Entries = new List<Entry>();
            public readonly Dictionary<ulong, Entry> ById = new Dictionary<ulong, Entry>();
            private Stream Str;
            private BinaryReader Reader;
            private const int MaxFile = 32 * 1024 * 1024;

            public static ulong MakeID(uint type, uint file) { return ((ulong)file << 32) | type; }

            public Far3(string path, string rel)
            {
                Rel = rel;
                Str = FSOFile.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    Reader = new BinaryReader(Str);
                    Length = Str.Length;
                    var magic = Encoding.ASCII.GetString(Reader.ReadBytes(8));
                    var version = Reader.ReadUInt32();
                    if (magic != "FAR!byAZ" || version != 3) throw new InvalidDataException("not a FAR3 archive");
                    var manifest = Reader.ReadUInt32();
                    if (manifest < 16 || manifest + 4 > Length) throw new InvalidDataException("its file list is outside the file (cut short?)");
                    Str.Seek(manifest, SeekOrigin.Begin);
                    var count = Reader.ReadUInt32();
                    if (count > (Length - manifest) / 24) throw new InvalidDataException("its file list is cut short");
                    for (uint i = 0; i < count; i++)
                    {
                        var e = new Entry();
                        e.Size = Reader.ReadUInt32();
                        Reader.ReadBytes(3); // (compressed size)
                        Reader.ReadByte();   // (data type)
                        e.Offset = Reader.ReadUInt32();
                        e.Compressed = Reader.ReadByte() == 1;
                        Reader.ReadByte();   // (access number)
                        var nameLength = Reader.ReadUInt16();
                        e.TypeID = Reader.ReadUInt32();
                        e.FileID = Reader.ReadUInt32();
                        e.Name = Encoding.ASCII.GetString(Reader.ReadBytes(nameLength));
                        e.ID = MakeID(e.TypeID, e.FileID);
                        Entries.Add(e);
                        if (!ById.ContainsKey(e.ID)) ById[e.ID] = e;
                    }
                }
                catch
                {
                    Str.Dispose();
                    throw;
                }
            }

            public Entry Get(uint type, uint file)
            {
                Entry e;
                return ById.TryGetValue(MakeID(type, file), out e) ? e : null;
            }

            public byte[] Read(Entry e)
            {
                lock (this)
                {
                    if (Str == null) throw new ObjectDisposedException(Rel);
                    if (e.Offset >= Length || e.Size > MaxFile) throw new InvalidDataException("bad file " + e.Name);
                    Str.Seek(e.Offset, SeekOrigin.Begin);
                    if (e.Compressed)
                    {
                        // (9 bytes, the packed size, the RefPack id 0xFB10, the unpacked size in 3 bytes, then the data)
                        Reader.ReadBytes(9);
                        var packed = Reader.ReadUInt32();
                        var id = Reader.ReadUInt16();
                        if (id == 0xFB10)
                        {
                            var b = Reader.ReadBytes(3);
                            var size = (uint)((b[0] << 16) | (b[1] << 8) | b[2]);
                            if (packed > Length - e.Offset || size > MaxFile) throw new InvalidDataException("bad packed file " + e.Name);
                            var dec = new Decompresser { CompressedSize = packed, DecompressedSize = size };
                            var data = dec.Decompress(Reader.ReadBytes((int)packed));
                            if (data == null || data.Length != size) throw new InvalidDataException("couldn't unpack " + e.Name);
                            return data;
                        }
                        Str.Seek(e.Offset, SeekOrigin.Begin);
                    }
                    var raw = Reader.ReadBytes((int)e.Size);
                    if (raw.Length != e.Size) throw new EndOfStreamException(e.Name + " is cut short");
                    return raw;
                }
            }

            public void Dispose()
            {
                lock (this)
                {
                    try { Str?.Dispose(); } catch { }
                    Str = null;
                }
            }
        }
    }
}
