using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FSO.Common.IO;
using FSO.Common.Utils;
using FSO.Content.Interfaces;
using FSO.Files.FAR1;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.Formats.OTF;

namespace FSO.Content.TS1
{
    /// <summary>
    /// SimsRedux stage 3: The Sims Online's buy and build objects, in Sims 1 mode.
    ///
    /// Only the objects on the Stage3 list are brought in: things you can buy in The Sims Online that have no
    /// interactions at all (no pie menu, from their own tree table, their group leader's or their semi-global's)
    /// and whose scripts - their own, their semi-global's and TSO's global ones - use nothing The Sims Online's
    /// servers did (no money, online jobs, plugins or inventory, no TSO-only calls). That's decorations,
    /// paintings, sculptures, plants and trees, windows, columns, awnings and fences. The list is fixed here
    /// (not worked out when the game starts), so every game and every lot worker loads exactly the same objects.
    ///
    /// Where they come from (all read through the game's file layer, so the browser reads a piece at a time):
    ///   objectdata/objects/objiff.far - the objects (their scripts and catalog text). Only the listed files are
    ///       read while the game starts (about 1.2 MB); an object is read again when it's first used.
    ///   objectdata/objects/objspf1..9.far - their pictures. Never read whole: only an object's own .spf entry,
    ///       when the object is first shown or used, kept for a while (a timed cache) like the game's other files.
    ///   objectdata/objects/objotf.far - their tuning (small).
    ///   objectdata/globals - TSO's own global scripts and semi-globals. TSO objects always run against these
    ///       (never the Sims 1 ones - see GameIffResource.ReduxGlobal), loaded the first time one is needed.
    ///   packingslips/catalog.xml - The Sims Online's prices.
    ///
    /// A GUID the Sims 1 game or a shipped mod already uses is left out (theirs wins); one only a player's own
    /// Downloads use goes to the Sims Online object, so every game and lot worker ends up with the same objects.
    /// Objects are filed under FilePrefix + their file name ("~tso/paintings2.iff"), so they never share a
    /// file name with a Sims 1 object.
    /// </summary>
    public class ReduxTSOObjects
    {
        public const string FilePrefix = "~tso/";

        /// <summary>
        /// The objects brought in (stage 3): "file:GUID,GUID;..." - each GUID is a Buy or Build catalog object (a
        /// multi-tile object's leader brings its parts with it). Made from the stage 3 analysis of objiff.far.
        /// </summary>
        private const string Stage3 =
            "awnings:360CDFB2,362A97B8;awnings3:29D3125F,29FB5A55;awningthatch:369037E1;clockoffice:CFC9FD08;" +
            "clockpost:35A3DF98,35BD9792;column2:4B78BF0E,6F93A84A,A8AD8714,A8C59F83;column3:35971BB9,35A34B24,35B5032E;" +
            "column5:35C702C5;columnarch:38D0DF9E;columnarchaquaduct:3B8937E4;columnarchbeach:3A2737BA;columnarchcarnival:3A99379B;" +
            "columnarchmoroccan:218E9FCF;columnarchromantiled:345E3754;columnarchscifi:2913A1E9;columnarchstone:259AACA5;" +
            "columnarchunleashed:266F98BA,27B7D0B0;columns4:3BAA97E2,3BD4DFE8;cone:B123A8E1;constructionhorse:60688651;" +
            "curtainsunleashed:2815D0E3,28CD98E9;dancefloor:D481CEE5;fenceflowersnow:34689D77,3468D57D;fenceoffice:04A88C61;" +
            "fences:354A1785,36FE2161;fenceshd:310197C6,3115C75B,317DDFCC;fencesstonevacation:3AE59DE0,3AE5D5EA;" +
            "filingcabinet:16F2C4A6;moosehead:62B6F3C1;" +
            "paintings2:1F15AF7E,5880D445,5D0BBDB6,855CDE99,8D70F7B3,A11D2875,A13F39CF,A1594E42,A1BD4C80,A1D5048A,A2666952,DC2DD727;" +
            "paintings3:2E7F37EA,34ACF253,417E691C,5EDB929D,5FB93288,89173B24,9293B59E,94FD43DC;" +
            "paintings4:1149EBCD,1154994A,16AED8D8;paintings5:8276148D,829E5C87,82BC0C1A;paintings7:2A49CCAB;" +
            "plantboxfence:342BA855,3473E05F;plants2:154522AF,15460FCA;plantshanginghd:3A42A8A4,3BA4E0AE;" +
            "sculptures2:029C9043,342A5E00,375DD48B,4AE4F77C,877FECA1,95EFE015,A478D407,A805E814,B48A1144,DAC39EA9,EFF1FDB0;" +
            "sculptures3:01F02564,1E6D621B,37BFA925,B4B333E4,E7D374D8,EC3B09C0;sculptures4:C76DB5C1,C7A3FDCB;" +
            "sculptures5:895E839E,895ECB94,8998815C,89CBB913,89EAA2EF;sculptures6:3A8D2542,3ABB3DD5;sculptureweddingtopiary:4BD3B0A4;" +
            "shrubs2:0EB692FB;shrubs3:215FBF2E,2163F6F7,263FAA5F,26939210,26B189EC,26DDDDD2,26F5CC68;" +
            "shrubs4:3DABFA03,3DC5B209,3E1BF8C1,3E49AA9E,3E65D5A4;stopsign:38EAE0DD;" +
            "topiaries:E482787C,E48328E1,E48C2A23,E48D0746,E4FB60EB;treecurb:315EA148,3698E942;" +
            "trees3:21F127AF,2FF38F53,374B44F3,440087F3,51474702,56040A7B,563504AC,56D56301,5F421524,65627AE4,70E7B9A5,7EB57E89,8BA56284,8C7F5607,C99AC74E,D297087F,D8F649FF,D9BD21CD,E36B9622,E87D6B95,F693720B;" +
            "trees4:365ACF98,378AB380,37A2A8CA;treeshd:658E9089,66BD805A,6779ED37;weddingflowers:97FEE127;weddingqueueropes:83684654;" +
            "windows2:0C04CA02,0D14B885,0D4CE6FD,0EDE95BB,7F0ABD26,862CCC31,8C44BA58,912CBED4,D158B046;windows3:79E2BB28;" +
            "windows4:15717E80,2A262D32,B53903AA,CB790E58,D0A244E4,D0B754E0,F88E6AAC;windows5:323AF637,EE72ED45;" +
            "windows6:5E50C870,5EDDBAF4,79BBDF2F,CA658DEB";

        /// <summary>What was loaded (for the online content stamp): the archives and their bytes.</summary>
        public int ArchiveCount;
        public long ArchiveBytes;
        /// <summary>How many objects (OBJDs) were brought in, and how many of them are in Buy or Build mode.</summary>
        public int ObjectCount, CatalogCount;

        private class Ref { public FAR1Archive Archive; public FarEntry Entry; }
        private readonly Dictionary<string, Ref> Iffs = new Dictionary<string, Ref>();
        private readonly Dictionary<string, Ref> Spfs = new Dictionary<string, Ref>();
        private readonly Dictionary<string, Ref> Otfs = new Dictionary<string, Ref>();
        private readonly TimedReferenceCache<string, IffFile> IffCache = new TimedReferenceCache<string, IffFile>();
        private readonly TimedReferenceCache<string, IffFile> SpfCache = new TimedReferenceCache<string, IffFile>();
        private readonly Dictionary<uint, uint> Prices = new Dictionary<uint, uint>();
        /// <summary>The semi-globals the loaded objects name (lower case, no .iff).</summary>
        private readonly HashSet<string> SemiGlobalNames = new HashSet<string>();

        private readonly object GlobalLock = new object();
        private GameGlobal TSOGlobal;
        private readonly Dictionary<string, GameGlobal> TSOSemiGlobals = new Dictionary<string, GameGlobal>();

        private Content ContentManager;

        private ReduxTSOObjects(Content content) { ContentManager = content; }

        /// <summary>The listed files and their catalog GUIDs.</summary>
        public static Dictionary<string, HashSet<uint>> List()
        {
            var result = new Dictionary<string, HashSet<uint>>();
            foreach (var part in Stage3.Split(';'))
            {
                var colon = part.IndexOf(':');
                if (colon <= 0) continue;
                var set = new HashSet<uint>();
                foreach (var g in part.Substring(colon + 1).Split(','))
                {
                    uint guid;
                    if (uint.TryParse(g, System.Globalization.NumberStyles.HexNumber, null, out guid)) set.Add(guid);
                }
                result[part.Substring(0, colon).ToLowerInvariant() + ".iff"] = set;
            }
            return result;
        }

        /// <summary>
        /// Opens the archives (only their file lists are read) and the prices. Null if the objects archive isn't
        /// there (the rest of The Sims Online's content still loads).
        /// </summary>
        public static ReduxTSOObjects Open(Content content)
        {
            var objIff = Content.TSOPath("objectdata/objects/objiff.far");
            if (!FSOFile.Exists(objIff)) { Console.WriteLine("[SimsRedux] Sims Online: no objectdata/objects/objiff.far - no objects"); return null; }
            // (its objects only ever run against TSO's own globals - without them, none are loaded)
            if (!FSOFile.Exists(Content.TSOPath("objectdata/globals/global.iff"))) { Console.WriteLine("[SimsRedux] Sims Online: no objectdata/globals/global.iff - no objects"); return null; }
            var result = new ReduxTSOObjects(content);
            if (!result.AddArchive("objectdata/objects/objiff.far", ".iff", result.Iffs)) return null;
            result.AddArchive("objectdata/objects/objotf.far", ".otf", result.Otfs);
            for (int i = 1; i <= 9; i++) result.AddArchive("objectdata/objects/objspf" + i + ".far", ".spf", result.Spfs);
            result.ReadPrices();
            return result;
        }

        private bool AddArchive(string rel, string ext, Dictionary<string, Ref> into)
        {
            var path = Content.TSOPath(rel);
            try
            {
                if (!FSOFile.Exists(path)) { Console.WriteLine("[SimsRedux] Sims Online: no " + rel); return false; }
                var archive = new FAR1Archive(path, true);
                foreach (var entry in archive.GetAllFarEntries())
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Filename) || entry.DataOffset < 0 || entry.DataLength <= 0) continue;
                    var name = entry.Filename.Replace('\\', '/');
                    name = name.Substring(name.LastIndexOf('/') + 1).ToLowerInvariant();
                    if (!name.EndsWith(ext) || into.ContainsKey(name)) continue; // (the first archive's copy wins)
                    into[name] = new Ref { Archive = archive, Entry = entry };
                }
                long size = 0;
                try { size = archive.ArchiveLength; } catch { }
                ArchiveCount++;
                ArchiveBytes += size;
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] skipped a broken Sims Online archive: " + rel + ": " + e.Message);
                try { Framework.FAR1Provider<IffFile>.ReportProblem?.Invoke("broken Sims Online file", rel + ": " + e.Message); } catch { }
                return false;
            }
        }

        private static readonly Regex PriceRow = new Regex("<P g=\"0x([0-9A-Fa-f]+)\" s=\"(-?\\d+)\" p=\"(\\d+)\"");

        private void ReadPrices()
        {
            try
            {
                var path = Content.TSOPath("packingslips/catalog.xml");
                if (!FSOFile.Exists(path)) return;
                foreach (Match m in PriceRow.Matches(FSOFile.ReadAllText(path)))
                {
                    uint guid, price;
                    if (uint.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out guid)
                        && uint.TryParse(m.Groups[3].Value, out price)) Prices[guid] = price;
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] Sims Online prices: " + e.Message); }
        }

        /// <summary>One object found in the listed files, and whether it goes in the catalog.</summary>
        public class Found
        {
            public string FileName;     // FilePrefix + the file's name
            public OBJD Obj;
            public bool Catalog;
            public sbyte Category;
            public uint Price;
            public string Name;         // its catalog name (CTSS), or its label
        }

        /// <summary>
        /// Reads the listed files and returns their objects: each listed GUID, and a multi-tile object's parts
        /// (same file, same master ID). A whole group is left out if any of its GUIDs is taken (`taken`).
        /// </summary>
        public List<Found> Collect(Func<uint, bool> taken)
        {
            var result = new List<Found>();
            int clashes = 0, missing = 0;
            foreach (var file in List())
            {
                IffFile iff = null;
                try { iff = ReadIff(file.Key); }
                catch (Exception e) { Console.WriteLine("[SimsRedux] skipped a damaged Sims Online object file (" + file.Key + "): " + e.Message); }
                if (iff == null) { missing++; continue; }
                iff.MarkThrowaway();
                var objds = iff.List<OBJD>() ?? new List<OBJD>();
                var glob = iff.List<GLOB>();
                if (glob != null && glob.Count > 0 && !string.IsNullOrEmpty(glob[0].Name)) SemiGlobalNames.Add(SemiName(glob[0].Name));
                foreach (var guid in file.Value)
                {
                    var lead = objds.Find(x => x.GUID == guid);
                    if (lead == null) { missing++; continue; }
                    var group = new List<OBJD> { lead };
                    if (lead.MasterID != 0)
                        foreach (var o in objds)
                            if (o != lead && o.MasterID == lead.MasterID) group.Add(o);
                    if (group.Exists(x => taken(x.GUID) || x.ObjectType == OBJDType.Person)) { clashes++; continue; }
                    foreach (var o in group)
                    {
                        var found = new Found { FileName = FilePrefix + file.Key, Obj = o, Name = o.ChunkLabel };
                        if (o == lead)
                        {
                            found.Catalog = true;
                            // (the same categories as the Sims 1 objects: 0-7 Buy, 8+ Build. TSO's own object
                            // fences (build type 8) go with the columns (13), which Build mode shows with the walls.)
                            if (o.FunctionFlags > 0) found.Category = (sbyte)Math.Log(o.FunctionFlags, 2);
                            else found.Category = (sbyte)((o.BuildModeType == 8 ? 6 : o.BuildModeType) + 7);
                            uint price;
                            found.Price = Prices.TryGetValue(o.GUID, out price) ? price : o.Price;
                            try
                            {
                                var name = iff.Get<CTSS>(o.CatalogStringsID)?.GetString(0);
                                if (!string.IsNullOrWhiteSpace(name)) found.Name = name.Trim();
                            }
                            catch { }
                            if (found.Category < 0 || found.Category >= 30) found.Catalog = false;
                        }
                        result.Add(found);
                    }
                }
            }
            ObjectCount = result.Count;
            CatalogCount = result.FindAll(x => x.Catalog).Count;
            Console.WriteLine("[SimsRedux] Sims Online objects: " + ObjectCount + " (" + CatalogCount + " in Buy and Build mode)"
                + (clashes > 0 ? ", " + clashes + " left out (their GUID is taken)" : "") + (missing > 0 ? ", " + missing + " not found" : ""));
            return result;
        }

        /// <summary>The semi-globals the loaded objects use - with global.iff, part of the content stamp.</summary>
        public void AddGlobalsToStamp()
        {
            var names = new List<string> { "global" };
            names.AddRange(SemiGlobalNames);
            foreach (var n in names)
            {
                try
                {
                    var path = Content.TSOPath("objectdata/globals/" + n + ".iff");
                    if (!FSOFile.Exists(path)) continue;
                    using (var s = FSOFile.OpenRead(path)) { ArchiveCount++; ArchiveBytes += s.Length; }
                }
                catch { }
            }
        }

        private static string SemiName(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (name.EndsWith(".iff")) name = name.Substring(0, name.Length - 4);
            return name;
        }

        private IffFile ReadEntry(Ref r, string name)
        {
            var data = r.Archive.GetEntry(r.Entry);
            if (data == null) return null;
            var iff = new IffFile();
            using (var stream = new MemoryStream(data, false)) iff.Read(stream);
            iff.SetFilename(name);
            return iff;
        }

        private IffFile ReadIff(string file)
        {
            Ref r;
            return Iffs.TryGetValue(file, out r) ? ReadEntry(r, file) : null;
        }

        /// <summary>The object file (cached for a while).</summary>
        public IffFile GetIff(string file)
        {
            Ref r;
            if (!Iffs.TryGetValue(file, out r)) return null;
            var result = IffCache.GetOrAdd(file, (k) => ReadEntry(r, k));
            if (result != null && result.ReduxDead)
            {
                // (cleaned up while still listed - read it again)
                IffFile dead;
                IffCache.TryRemove(file, out dead);
                result = IffCache.GetOrAdd(file, (k) => ReadEntry(r, k));
            }
            return result;
        }

        /// <summary>Its pictures: just this object's own entry in one of the objspf archives (cached for a while).</summary>
        public IffFile GetSprites(string file)
        {
            var spf = Path.GetFileNameWithoutExtension(file) + ".spf";
            Ref r;
            if (!Spfs.TryGetValue(spf, out r)) return null;
            var result = SpfCache.GetOrAdd(spf, (k) => ReadEntry(r, k));
            if (result != null && result.ReduxDead)
            {
                IffFile dead;
                SpfCache.TryRemove(spf, out dead);
                result = SpfCache.GetOrAdd(spf, (k) => ReadEntry(r, k));
            }
            return result;
        }

        public OTFFile GetTuning(string file)
        {
            var otf = Path.GetFileNameWithoutExtension(file) + ".otf";
            Ref r;
            if (!Otfs.TryGetValue(otf, out r)) return null;
            try
            {
                var data = r.Archive.GetEntry(r.Entry);
                var result = new OTFFile();
                using (var stream = new MemoryStream(data, false)) result.Read(stream);
                return result;
            }
            catch (Exception) { return null; } // (like the TSO game: a broken tuning file is ignored)
        }

        /// <summary>The resource for an object file ("~tso/x.iff"), running against TSO's own globals.</summary>
        public GameObjectResource MakeResource(string fileName)
        {
            var file = fileName.Substring(FilePrefix.Length);
            IffFile iff = null;
            try { iff = GetIff(file); }
            catch (Exception e) { Console.WriteLine("[SimsRedux] couldn't read the Sims Online object " + file + ": " + e.Message); }
            if (iff == null) return new GameObjectResource(null, null, null, fileName, ContentManager);
            iff.InitHash();
            iff.RuntimeInfo.Path = fileName;
            iff.RuntimeInfo.UseCase = IffUseCase.Object;
            var global = GetGlobal();
            if (global == null) return new GameObjectResource(null, null, null, fileName, ContentManager); // (never against the Sims 1 globals)
            IffFile sprites = null;
            try { sprites = GetSprites(file); }
            catch (Exception e) { Console.WriteLine("[SimsRedux] couldn't read the pictures for " + file + ": " + e.Message); }
            if (sprites != null) sprites.RuntimeInfo.UseCase = IffUseCase.ObjectSprites;
            return new GameObjectResource(iff, sprites, GetTuning(file), fileName, ContentManager, global, GetSemiGlobal);
        }

        /// <summary>TSO's global.iff (with its tuning).</summary>
        public GameGlobal GetGlobal()
        {
            lock (GlobalLock)
            {
                if (TSOGlobal == null) TSOGlobal = LoadGlobal("global");
                return TSOGlobal;
            }
        }

        /// <summary>One of TSO's semi-globals by name ("doorglobals"), null if it isn't there.</summary>
        public GameGlobal GetSemiGlobal(string name)
        {
            name = SemiName(name);
            if (name.Length == 0 || name == "global") return null;
            lock (GlobalLock)
            {
                GameGlobal result;
                if (!TSOSemiGlobals.TryGetValue(name, out result))
                {
                    result = LoadGlobal(name);
                    TSOSemiGlobals[name] = result;
                }
                return result;
            }
        }

        private GameGlobal LoadGlobal(string name)
        {
            try
            {
                var path = Content.TSOPath("objectdata/globals/" + name + ".iff");
                if (!FSOFile.Exists(path)) return null;
                var iff = new IffFile(path);
                // (named apart from the Sims 1 semi-global of the same name: objects are grouped by it)
                iff.SetFilename("tso_" + name + ".iff");
                iff.InitHash();
                OTFFile otf = null;
                try
                {
                    var otfPath = Content.TSOPath("objectdata/globals/" + name + ".otf");
                    if (FSOFile.Exists(otfPath)) otf = new OTFFile(otfPath);
                }
                catch (Exception) { }
                return new GameGlobal { Resource = new GameGlobalResource(iff, otf, true) };
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] couldn't load The Sims Online's " + name + ": " + e.Message);
                return null;
            }
        }
    }
}
