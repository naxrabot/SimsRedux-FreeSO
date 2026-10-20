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

        /// <summary>
        /// SimsRedux 13.8: each listed catalog object's shape (ReduxCatalogInfo.Shape - the size and place of its sprites),
        /// worked out from its pictures in the objspf archives by the same analysis. Buy and Build mode use it to show
        /// an object's colours together; reading the sprite archives for it would mean megabytes per object.
        /// </summary>
        private const string Stage3Shapes =
            "360CDFB2:6A10D19819467D8E;362A97B8:3A9D78E242BAD83B;29D3125F:229FE06CB3705AB5;29FB5A55:A5CD43300759270F;" +
            "369037E1:864AAFE1E0CACBB2;CFC9FD08:ECB754204F35847D;35A3DF98:15839CB12B06CF60;35BD9792:667A252EDBE7AD75;" +
            "4B78BF0E:B06B4D1FBD3DDF45;6F93A84A:76F33635C1E99893;A8AD8714:92C60F1975F70E84;A8C59F83:EC0093E629165BEE;" +
            "35971BB9:6BD7B1D0AD6B2476;35A34B24:9EA94A77B9286F51;35B5032E:F80B8BCD234C9D39;35C702C5:B56C7D25ED0650DE;" +
            "38D0DF9E:4335F52EC01BB86C;3B8937E4:EC075C504D4BFF6E;3A2737BA:CE565CB6B10348FE;3A99379B:AC740BCBC5AE2548;" +
            "218E9FCF:BB773491F73E1B0D;345E3754:EF0B594AA0A2D1F9;2913A1E9:3FB4FCEFD49F7E0D;259AACA5:689A1FACF80E0737;" +
            "266F98BA:5C80F6C9D199EB1E;27B7D0B0:0AF0E41BEA08CABC;3BAA97E2:B56C7D25ED0650DE;3BD4DFE8:9112C6D27E7400FB;" +
            "B123A8E1:9858B28013B3A0C1;60688651:572F4EE003D55330;2815D0E3:9C908264E6BA68C7;28CD98E9:3B7D86FE22144BEE;" +
            "D481CEE5:6356C1732687CCF2;34689D77:71A7B7AAB142921A;3468D57D:7FB745A2A402552F;04A88C61:0CBAC87624A487D5;" +
            "354A1785:72539DFD1230A774;36FE2161:7AD44158D24C8418;310197C6:34BF885BE2392F21;3115C75B:A5B78CD9897DBF23;" +
            "317DDFCC:89687795A7E4D273;3AE59DE0:DD73EF1CAE3BA0CD;3AE5D5EA:C7ACC5572AE3E4B6;16F2C4A6:60C32EB88779C0B8;" +
            "62B6F3C1:792D04286FFB535C;1F15AF7E:A1EB6875CCE32BA7;5880D445:97987A6224F319D0;5D0BBDB6:0C8B33FA698040C2;" +
            "855CDE99:6F23116F71063B1C;8D70F7B3:5286A6D85978D4E5;A11D2875:14A12B5D8C3E2247;A13F39CF:374EEB81051505FE;" +
            "A1594E42:AFF1C01FB1476324;A1BD4C80:599A85EE1B5AF881;A1D5048A:B8FA691FB680447D;A2666952:F05DB0813521FCD3;" +
            "DC2DD727:E91277C89A053555;2E7F37EA:C0CF518E67517E8B;34ACF253:9449B65C8ABA8A7E;417E691C:8350BBEDB5C35B94;" +
            "5EDB929D:5C4D697263894713;5FB93288:0136EE3B1E73EB84;89173B24:BC3665DAC35FC02E;9293B59E:7DD5B7A4F4E71966;" +
            "94FD43DC:C0F352AC27D541DA;1149EBCD:7C2605A06CD3E1BC;1154994A:074784F8CF7B8AF6;16AED8D8:A392B033EE66B247;" +
            "8276148D:1E3A18D7ED066FE3;829E5C87:6A140A844A030D51;82BC0C1A:4D99A5B326465BF7;2A49CCAB:00781DA32DE2464B;" +
            "342BA855:1AB01209D21C2689;3473E05F:B8832EAD224D0AE6;154522AF:C760AC5FA10EC34E;15460FCA:F122123B23315D94;" +
            "3A42A8A4:9E0A76895D12DCBE;3BA4E0AE:0D4FE04FF42AE733;029C9043:1F9A2944E6DE83FD;342A5E00:16F76FDE02B00C54;" +
            "375DD48B:C06245DE18A8FC84;4AE4F77C:4EF4757C6A1A08C4;877FECA1:C264EC32BDBF153A;95EFE015:12226FA547A8F8D7;" +
            "A478D407:5B8689797641A449;A805E814:88F0049949214CBA;B48A1144:516DB1A8373C8FB6;DAC39EA9:4C44D2732574956D;" +
            "EFF1FDB0:BF6B35CBE37485D7;01F02564:77BD500656225520;1E6D621B:51037C6B1FDF90AF;37BFA925:15862A68BAB0A36E;" +
            "B4B333E4:A8A57335415E10E0;E7D374D8:ED2B5920947A7CC9;EC3B09C0:27307F02E3F65B3A;C76DB5C1:593D71D7EB830D0A;" +
            "C7A3FDCB:7DF1E4FD079840B2;895E839E:196A48DF0832CF2C;895ECB94:B47F0DAEFDED67EA;8998815C:562E2E6958EFAA7E;" +
            "89CBB913:6ABAA6E869EB4EE3;89EAA2EF:D5AD97DA2000DC12;3A8D2542:5294D5699C3C8B22;3ABB3DD5:8E10D8410159AA73;" +
            "4BD3B0A4:D30CF485753423D8;0EB692FB:C796FFF6AB3074B0;215FBF2E:8FA8CEF49F0CB724;2163F6F7:4829AD223AA718A6;" +
            "263FAA5F:B4E51F34984F8C73;26939210:4E873881BF10F250;26B189EC:4174F558F67D9294;26DDDDD2:50BE1149EC2F1BEB;" +
            "26F5CC68:288B5E2C4AD2DD99;3DABFA03:68E478AD2E08E22C;3DC5B209:233625637D569030;3E1BF8C1:62B8CCDA3E0D7859;" +
            "3E49AA9E:FC23237CE1FD456C;3E65D5A4:19920181907DA77D;38EAE0DD:E4C828F070CD89BE;E482787C:4C6F7B3A486DA454;" +
            "E48328E1:EA1AEC93F3BCD516;E48C2A23:8C58697C9E797771;E48D0746:754B5D6B3176FB7A;E4FB60EB:27499F53F3464FAE;" +
            "315EA148:D20D494A166149B7;3698E942:ADE6EF0F362837AB;21F127AF:469FB4F6E0D81263;2FF38F53:A1043E687C48FBEF;" +
            "374B44F3:62D9B312796CFAD4;440087F3:30063B51D109962E;51474702:6D4E5EC0AE7EB953;56040A7B:4A40D72CAA4610C2;" +
            "563504AC:15850D1616ACF90D;56D56301:AE5BD4EBEE88F97C;5F421524:16EB2ABBBBA59FD8;65627AE4:C2D1F3BE93D4B44E;" +
            "70E7B9A5:00091A6D5CEB3518;7EB57E89:193C6819FEB6D782;8BA56284:6D4E5EC0AE7EB953;8C7F5607:5E795B83327FF233;" +
            "C99AC74E:3A65D9DE0260CE8E;D297087F:D7CFE0DA20F18E61;D8F649FF:8036E941A395FAB1;D9BD21CD:1107AC39554A6677;" +
            "E36B9622:BA9C4BF22498BC04;E87D6B95:3F6DBC4C50257895;F693720B:D89E04CE7E231C01;365ACF98:3B210B4BEA2B310D;" +
            "378AB380:23C6C0AB9D9D7D9B;37A2A8CA:452DDA691787B058;658E9089:86A4E16BFE139096;66BD805A:51AE8E1037CD7168;" +
            "6779ED37:39DE365044F32599;97FEE127:2FEE8A3316DD22A1;83684654:035D04DE9E4384AD;0C04CA02:CAE42C185035846E;" +
            "0D14B885:1777C6526749A448;0D4CE6FD:BF87B90DF6D319F0;0EDE95BB:98BB3C3242A57C53;7F0ABD26:88B6D11F5B8FBFC9;" +
            "862CCC31:606D3735A650332D;8C44BA58:2A0CDCEC3BE0ADA7;912CBED4:9748B7E302E1BE49;D158B046:D10BBBDFA3AB0CD2;" +
            "79E2BB28:A3CA96C5FDA5181D;15717E80:A37201E8BEFE0245;2A262D32:8DA2B77BEE5CD46F;B53903AA:175977A3457E5490;" +
            "CB790E58:7D7A86563B77FA8D;D0A244E4:2DC0253752A4B91C;D0B754E0:D60BC39AF5C3E6A6;F88E6AAC:B9217361574E7E9C;" +
            "323AF637:722998BBD8F5B3DB;EE72ED45:2496F39C21B9E23F;5E50C870:7719ECE17DCD387E;5EDDBAF4:C3D8680BBC5D8099;" +
            "79BBDF2F:8D4F744242BAB4E7;CA658DEB:029CDF4C3FF03408;";

        private static Dictionary<uint, ulong> _Shapes;
        /// <summary>A listed object's shape (0: not known).</summary>
        public static ulong ShapeOf(uint guid)
        {
            if (_Shapes == null)
            {
                var shapes = new Dictionary<uint, ulong>();
                foreach (var part in Stage3Shapes.Split(';'))
                {
                    var colon = part.IndexOf(':');
                    uint g; ulong h;
                    if (colon > 0 && uint.TryParse(part.Substring(0, colon), System.Globalization.NumberStyles.HexNumber, null, out g)
                        && ulong.TryParse(part.Substring(colon + 1), System.Globalization.NumberStyles.HexNumber, null, out h)) shapes[g] = h;
                }
                _Shapes = shapes;
            }
            ulong result;
            return _Shapes.TryGetValue(guid, out result) ? result : 0;
        }

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
            /// <summary>SimsRedux 13.8: its catalog description, recolour original and shape (see ObjectCatalogItem).</summary>
            public string Description = "";
            public uint Original;
            public ulong Shape;
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
                            // (name and description from the catalog text; the shape from the table above - the
                            //  pictures are in the sprite archives, which aren't read here)
                            var info = new ObjectCatalogItem();
                            ReduxCatalogInfo.Fill(ref info, iff, o, objds, 2);
                            if (info.CatalogName != null) found.Name = info.CatalogName;
                            found.Description = info.ReduxDescription ?? "";
                            found.Original = info.ReduxOriginal;
                            found.Shape = ShapeOf(o.GUID);
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
