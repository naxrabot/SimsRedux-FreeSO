using FSO.Common.IO;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.Engine.Scopes;
using FSO.LotView.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: NFSU2 cars on the street (the "car pack" - made on Joey's PC from his own Need for Speed Underground 2
    /// files by Client/Tools/SimsRedux.CarPack, and sent like the other restricted game files: Admin and Tester accounts).
    ///
    /// HOW IT STAYS THE SAME ON EVERY GAME OF A VISIT
    /// The cars are still the game's own cars: the same objects, GUIDs, scripts and positions - a game with the pack and
    /// one without play exactly the same lot. Only the picture changes, and only on a game that has the pack:
    ///   * which NFSU2 car a passing car is ("skin") is decided when ReduxTraffic makes it, from the game clock and the lot
    ///     (Pick: the same numbers on every game, pack or not) and kept on the car (attribute SkinAttribute) so it's
    ///     saved with the lot and a game that joins later sees the same car. A car from before this (no skin) gets one from
    ///     its object id. The customised version turns up at most once a game week per lot: in one 15-minute slot, picked
    ///     from the week and the lot, and only while no other customised car is on the street;
    ///   * a carpool / visitor car (the game's own car scripts) gets the car of the job it's for (CarpoolSkin: one NFSU2
    ///     car and paint per career track, picked once from the track's number, so the Medicine carpool is always the
    ///     same car whatever the level) - worked out from the lot as it is, never written anywhere;
    ///   * drawing: VMGameObject.RefreshGraphic asks Swap for the pack's picture of that tile part (the pack cars copy
    ///     the standard car's 3 x 6 tile layout; every Maxis car the street or a carpool uses has the same layout).
    /// Without the pack (or a car the pack doesn't have) the standard picture stays. Nothing here is read inside the
    /// VM's decisions except Pick, which never looks at the pack.
    ///
    /// ROUND 2 (drawing only, like the rest):
    ///   * turning wheels: a pack file made with wheel layers has, per tile, a DGRP per spin step (its DGRP number + step).
    ///     SpinWheels (ReduxTraffic's tick, every car on the lot) follows how far each car has moved since the last tick on
    ///     this game - forward or back - and draws the step for that distance: the wheels turn while it drives, faster
    ///     the faster it goes, and stay still while it waits (a carpool at the kerb). Kept on this game only, never on
    ///     the lot;
    ///   * the paints are NFSU2's ten basic gloss colours (NormalPaints = 10; a car saved with 0-5 keeps a valid paint);
    ///   * the school bus is drawn as NFSU2's bus (its own 3 x 10 layout in the pack), and a Sims cab (a "Car - " object
    ///     named as a taxi or cab, 3 x 6) as the NFSU2 taxi; a car is only drawn from a pack file that has every one of
    ///     its tile parts (never half a car);
    ///   * the Lite pack (phones and Lite mode) has fewer cars: a car it doesn't have is drawn as one it does (the same
    ///     kind, picked from the car's number - the same on every game with that pack), a customised car as its stock one.
    ///
    /// ROUND 3: a family's own car (ReduxCarShop - bought in The Showroom, parked on their lot) keeps its skin on itself the
    /// same way. A skin's version is now a number (0 stock, 1 customised, 2..MaxVariant the manifest's other premade versions
    /// - each with its own "id" and file, round 4's Car Customizer), so more versions slot in without changing a car's code.
    /// </summary>
    public static class ReduxCarSkins
    {
        /// <summary>Where a traffic car keeps its skin (an attribute the car scripts never use; the list grows as needed).</summary>
        public const int SkinAttribute = 20;

        // ---- THE CATALOG: must match Client/Tools/SimsRedux.CarPack/Pack/CarCatalog.cs (numbers never change) ----
        public static readonly string[] CarFolders =
        {
            "240SX", "A3", "CELICA", "CIVIC", "COROLLA", "ECLIPSE", "ESCALADE", "FOCUS", "GOLF", "IS300", "LANCEREVO8", "MIATA",
            "PEUGOT", "RSX", "SENTRA", "SKYLINE", "TIBURON",           // 0-16: everyday cars (stock + customised)
            "PICKUP", "MINIVAN", "PANELVAN", "SUV",                    // 17-20: traffic vehicles (stock)
            "TAXI", "BUS", "FIRETRUCK", "AMBULANCE",                   // 21-24: service vehicles (one paint)
        };
        public const int EverydayCars = 17, FirstTraffic = 17, Taxi = 21, FirstService = 21;
        /// <summary>How many paints a car's stock file has (PALT #101..): six, one for a service vehicle; customised files have five.</summary>
        public static int NormalPaintsOf(int car) { return car >= FirstService ? 1 : NormalPaints; }
        public const int NormalPaints = 10, CustomPaints = 5;
        public const int Bus = 22;

        /// <summary>A skin: 0 = none; else 1 + (variant * 64 + car) * 16 + paint, variant 0 stock / 1 customised.</summary>
        public static short Code(int car, bool custom, int paint) { return Code(car, custom ? 1 : 0, paint); }
        /// <summary>
        /// Round 3: any version - 0 stock, 1 customised, 2..MaxVariant the pack's other premade versions (round 4: the panel's
        /// Car Customizer adds them to the manifest with their own "id"). The numbers fit an attribute up to version 31.
        /// </summary>
        public static short Code(int car, int variant, int paint) { return (short)(1 + (variant * 64 + car) * 16 + paint); }
        public const int MaxVariant = 31;
        public static bool Decode(short code, out int car, out bool custom, out int paint)
        {
            int variant;
            bool ok = Decode(code, out car, out variant, out paint);
            custom = variant != 0;
            return ok;
        }
        /// <summary>Round 3: a skin's car, version and paint (a version past 1 is checked against the pack when it's drawn).</summary>
        public static bool Decode(short code, out int car, out int variant, out int paint)
        {
            car = 0; variant = 0; paint = 0;
            if (code <= 0) return false;
            int v = code - 1;
            paint = v % 16; v /= 16;
            car = v % 64; variant = v / 64;
            if (variant > MaxVariant || car >= CarFolders.Length) return false;
            if (variant != 0 && car >= EverydayCars) return false;
            if (variant > 1) return true; // (its paints are the manifest's - up to 16)
            return paint < (variant == 1 ? CustomPaints : NormalPaintsOf(car));
        }

        /// <summary>Passing traffic's mix: each everyday car x3, each traffic vehicle x2, the taxi x1.</summary>
        private static readonly int[] TrafficMix = BuildMix();
        private static int[] BuildMix()
        {
            var l = new List<int>();
            for (int c = 0; c < EverydayCars; c++) { l.Add(c); l.Add(c); l.Add(c); }
            for (int c = FirstTraffic; c < Taxi; c++) { l.Add(c); l.Add(c); }
            l.Add(Taxi);
            return l.ToArray();
        }

        // ---- the Maxis cars a pack car can be drawn over (all 3 x 6 tiles) ----
        private static readonly HashSet<uint> Skinnable = new HashSet<uint>
        {
            0xdb6dedb2, // standard carpool
            0xbc3aec18, // town car
            0x1d3dec63, // SUV
            0xcd0876e1, // pickup truck
            0x4b71ecda, // junker
            0xf72aec44, // staff sedan
            0x17acec22, // Bentley
            0xc8ee6e7d, // limo
            0xa6f5ed9a, // military jeep
            SchoolBusGUID, // round 2: the school bus (3 x 10) - always the NFSU2 bus
        };
        /// <summary>The school bus (ReduxTraffic's passing one and the game's own).</summary>
        public const uint SchoolBusGUID = 0x50ac15bf;
        public static bool IsSkinnable(uint baseGuid) { return Skinnable.Contains(baseGuid); }

        /// <summary>
        /// Round 2: the skin a vehicle always has whatever it's picked as: the school bus is the NFSU2 bus, and a cab (an
        /// expansion pack's "Car - " object named as a taxi or cab - Hot Date's) the NFSU2 taxi. 0 = none. Read only.
        /// </summary>
        public static short ServiceSkin(VMEntity e)
        {
            var def = e == null ? null : (e.MasterDefinition ?? e.Object?.OBJ);
            if (def == null) return 0;
            if (def.GUID == SchoolBusGUID) return Code(Bus, false, 0);
            var label = def.ChunkLabel ?? "";
            if (label.StartsWith("Car - ", StringComparison.Ordinal)
                && (label.IndexOf("taxi", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(label, @"\bcab\b", RegexOptions.IgnoreCase)))
                return Code(Taxi, false, 0);
            return 0;
        }

        /// <summary>
        /// 20.3: the car an object is part of (its GUID as the catalog knows it). A Maxis car is a multi-tile object: the
        /// lot only gets its 18 tile parts (never the lead object), each with its own GUID ("Car - Limo - Row 0 Tile 0"),
        /// and the group's BaseObject is the tile at (0,0) - so the list above (the cars' own GUIDs) must be matched
        /// against the tile's master. (20.0-20.2 matched the tile's GUID: no car was ever skinnable.)
        /// </summary>
        public static uint CarGuid(VMEntity e)
        {
            if (e == null) return 0;
            return (e.MasterDefinition ?? e.Object?.OBJ)?.GUID ?? 0;
        }
        public static bool IsSkinnableCar(VMEntity e) { return IsSkinnable(CarGuid(e)) || ServiceSkin(e) != 0; }

        private static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
            return x;
        }

        // ================================================================ deciding (deterministic, no pack)

        /// <summary>
        /// The skin for a passing car ReduxTraffic is making now (h: its spawn hash). The same on every game of the lot.
        /// </summary>
        public static short PickTraffic(VM vm, uint h, List<VMMultitileGroup> traffic)
        {
            uint k = Hash(h ^ 0x5ca1ab1e);
            int car = TrafficMix[k % (uint)TrafficMix.Length];
            bool custom = car < EverydayCars && CustomSlot(vm)
                && !traffic.Any(g => g.BaseObject != null && Decode(g.BaseObject.GetAttribute(SkinAttribute), out int _, out bool c, out int _) && c);
            int paint = (int)((k >> 8) % (uint)(custom ? CustomPaints : NormalPaintsOf(car)));
            return Code(car, custom, paint);
        }

        /// <summary>The game week's one 15-minute slot for a customised car on this lot (a day, between 9 AM and 7 PM).</summary>
        public static bool CustomSlot(VM vm)
        {
            var clock = vm.Context.Clock;
            long abs = (long)(clock.Year - 1997) * 360 + (clock.Month - 1) * 30 + (clock.DayOfMonth - 1);
            uint week = (uint)((abs - 150) / 7), day = (uint)ReduxWeek.Day(clock);
            uint s = Hash(week * 2654435761u ^ (uint)vm.GetGlobalValue(10) * 40503u ^ 0xc057);
            int slotDay = (int)(s % 7), slotMin = 9 * 60 + (int)((s >> 8) % 40) * 15;
            int now = clock.Hours * 60 + clock.Minutes;
            return day == slotDay && now >= slotMin && now < slotMin + 15;
        }

        /// <summary>A car with no skin of its own (saved before 20.x, or a carpool): one from its object id.</summary>
        public static short FromId(short objectId, uint guid)
        {
            uint k = Hash((uint)(ushort)objectId * 2246822519u ^ guid);
            return Code((int)(k % (uint)EverydayCars), false, (int)((k >> 8) % NormalPaints));
        }

        /// <summary>The carpool car for a career track: one car and paint per track, the same forever.</summary>
        public static short ForJob(int jobType)
        {
            uint k = Hash((uint)jobType * 0x9E3779B1u ^ 0x6a0b);
            return Code((int)(k % (uint)EverydayCars), false, (int)((k >> 8) % NormalPaints));
        }

        /// <summary>
        /// Which job a carpool car on the lot is for: the family member whose ride time (an hour before work starts, or
        /// when it ends) is nearest now, within 3 hours; ties go to the lower object id. -1 = none (a visitor's car).
        /// Read only - and the same on every game, since it looks at the same lot.
        /// </summary>
        public static int CarpoolJob(VM vm)
        {
            try
            {
                var jobs = FSO.Content.Content.Get()?.Jobs;
                if (vm?.Context == null || jobs == null) return -1;
                int now = vm.Context.Clock.Hours * 60 + vm.Context.Clock.Minutes, best = int.MaxValue, bestJob = -1; short bestId = short.MaxValue;
                foreach (var e in vm.Context.ObjectQueries.Avatars)
                {
                    var ava = e as VMAvatar;
                    if (ava == null || ava.IsPet) continue;
                    int type = ava.GetPersonData(VMPersonDataVariable.JobType);
                    if (type <= 0) continue;
                    var lvl = jobs.GetJobLevel((short)type, ava.GetPersonData(VMPersonDataVariable.JobPromotionLevel));
                    if (lvl == null) continue;
                    int d = Math.Min(Dist(now, (lvl.StartTime - 1) * 60), Dist(now, lvl.EndTime * 60));
                    if (d > 180) continue;
                    if (d < best || (d == best && ava.ObjectID < bestId)) { best = d; bestJob = type; bestId = ava.ObjectID; }
                }
                return bestJob;
            }
            catch { return -1; }
        }

        private static int Dist(int a, int b) { int d = Math.Abs(a - b) % 1440; return Math.Min(d, 1440 - d); }

        // ================================================================ drawing (only with the pack)

        /// <summary>The pack's folder ("" = none). Set before a lot loads (the apps: GameFiles/cars; the website: /cars/).</summary>
        public static string PackFolder = "";
        /// <summary>Options switch (this device): NFSU2 cars on, when the pack is there.</summary>
        public static bool Enabled = true;
        /// <summary>
        /// 20.2: where the pack came from on this game, or why there's none - in plain words, set by whoever sets PackFolder
        /// (the apps' NativeGameFlow, the Windows build, the website's WebStartup). Shown by the "carpack" cheat.
        /// </summary>
        public static string Source = "not set up by this program";
        /// <summary>20.2: how many car parts have been drawn from the pack so far (the "carpack" cheat).</summary>
        public static int Swapped;

        private class PackFile
        {
            public string Path; public Dictionary<short, ushort> DgrpBySub; public byte[] Bytes;
            /// <summary>Round 2: spin steps (1 = still wheels; worked out from the file's DGRPs when it's read).</summary>
            public int Spins = 1;
            /// <summary>Round 2: whether this file has every tile part of a vehicle (by its GUID).</summary>
            public Dictionary<uint, bool> Fits = new Dictionary<uint, bool>();
        }
        private static Dictionary<string, PackFile> Files; // "COROLLA" / "COROLLA_custom" -> file
        /// <summary>Round 3: the file of each premade version past "custom" (car * 64 + version -> its Files key), from the manifest's ids.</summary>
        private static Dictionary<int, string> VariantKeys = new Dictionary<int, string>();
        /// <summary>20.2: the folder Files was read from (PackFolder changed since - e.g. set after something asked: read again).</summary>
        private static string IndexedFolder;
        /// <summary>20.2: what reading the manifest found ("" = fine) - the "carpack" cheat.</summary>
        private static string IndexProblem = "";
        private static bool ManifestSeen;
        /// <summary>20.3: the last car file that couldn't be read, and why ("" = none) - the "carpack" cheat.</summary>
        private static string LoadProblem = "";
        /// <summary>The IFFs read from the pack (to tell a car drawn from the pack from one drawn as the Sims car).</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IffFile, object> PackIffs
            = new System.Runtime.CompilerServices.ConditionalWeakTable<IffFile, object>();
        private static readonly object Lock = new object();
        private static readonly Dictionary<short, IffFile> Loaded = new Dictionary<short, IffFile>(); // skin code -> its IFF (with its paint)
        private static readonly Queue<short> LoadOrder = new Queue<short>();
        private const int MaxLoaded = 12;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VMEntity, object> CarpoolSkins
            = new System.Runtime.CompilerServices.ConditionalWeakTable<VMEntity, object>();

        public static bool HasPack
        {
            get { lock (Lock) { EnsureIndex(); return Files.Count > 0; } }
        }

        /// <summary>Forget what was read (the pack was updated).</summary>
        public static void Reset() { lock (Lock) { Files = null; Loaded.Clear(); LoadOrder.Clear(); } }

        private static void EnsureIndex()
        {
            var folder = PackFolder ?? "";
            if (Files != null && IndexedFolder == folder) return;
            // (20.2: read again when the folder changed - it was once cached empty for good when asked before it was set)
            if (Files != null) { Loaded.Clear(); LoadOrder.Clear(); }
            Files = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
            VariantKeys = new Dictionary<int, string>();
            IndexedFolder = folder;
            ManifestSeen = false;
            IndexProblem = "";
            if (string.IsNullOrEmpty(folder)) { IndexProblem = "no pack folder"; return; }
            try
            {
                var manifest = Path.Combine(folder, "carpack.json");
                if (!FSOFile.Exists(manifest))
                {
                    IndexProblem = "no carpack.json in " + folder;
                    Console.WriteLine("[SimsRedux] car pack: " + IndexProblem + " - the Sims cars stay");
                    return;
                }
                ManifestSeen = true;
                // the manifest's "file" entries (cars/COROLLA.iff, cars/COROLLA_custom.iff) - only plain names inside the pack
                foreach (Match m in Regex.Matches(FSOFile.ReadAllText(manifest), "\"file\"\\s*:\\s*\"(cars/[A-Za-z0-9_\\-]+\\.iff)\""))
                {
                    var rel = m.Groups[1].Value;
                    var key = Path.GetFileNameWithoutExtension(rel);
                    Files[key] = new PackFile { Path = Path.Combine(folder, rel) };
                }
                if (Files.Count == 0) IndexProblem = "carpack.json lists no car files";
                IndexVariants(FSOFile.ReadAllText(manifest));
                Console.WriteLine("[SimsRedux] car pack: " + Files.Count + " car files from " + folder + " (" + Source + ")");
            }
            catch (Exception e) { IndexProblem = "couldn't read carpack.json: " + e.Message; Console.WriteLine("[SimsRedux] car pack: " + e.Message); Files.Clear(); }
        }

        /// <summary>
        /// Round 3: the manifest's premade versions past "custom" (round 4) - each listed with its own "id" (2..MaxVariant) and
        /// its file. Read only; a manifest without them (round 1 and 2 packs) has none.
        /// </summary>
        private static void IndexVariants(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    System.Text.Json.JsonElement cars;
                    if (!doc.RootElement.TryGetProperty("cars", out cars) || cars.ValueKind != System.Text.Json.JsonValueKind.Array) return;
                    foreach (var c in cars.EnumerateArray())
                    {
                        System.Text.Json.JsonElement idEl, vars;
                        if (!c.TryGetProperty("id", out idEl) || !idEl.TryGetInt32(out int car) || car < 0 || car >= EverydayCars) continue;
                        if (!c.TryGetProperty("variants", out vars) || vars.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                        foreach (var v in vars.EnumerateArray())
                        {
                            System.Text.Json.JsonElement vid, file;
                            if (!v.TryGetProperty("id", out vid) || vid.ValueKind != System.Text.Json.JsonValueKind.Number || !vid.TryGetInt32(out int id)) continue;
                            if (id < 2 || id > MaxVariant || !v.TryGetProperty("file", out file) || file.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                            var rel = file.GetString() ?? "";
                            if (!Regex.IsMatch(rel, "^cars/[A-Za-z0-9_\\-]+\\.iff$")) continue;
                            VariantKeys[car * 64 + id] = Path.GetFileNameWithoutExtension(rel);
                        }
                    }
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] car pack: couldn't read the manifest's versions: " + e.Message); }
        }

        /// <summary>The IFF for a skin with its paint copied over PALT #1 (a few kept; the oldest dropped).</summary>
        private static IffFile Load(short code, out PackFile file)
        {
            file = null;
            if (!Decode(code, out int car, out int variant, out int paint)) return null;
            EnsureIndex();
            file = FileFor(car, variant);
            if (file == null) return null;
            IffFile iff;
            if (Loaded.TryGetValue(code, out iff)) return iff;
            try
            {
                if (file.Bytes == null) file.Bytes = FSOFile.ReadAllBytes(file.Path);
                iff = new IffFile();
                using (var ms = new MemoryStream(file.Bytes)) iff.Read(ms);
                var main = iff.Get<PALT>(1);
                var want = iff.Get<PALT>((ushort)(101 + paint));
                if (main == null || want == null) { LoadProblem = Path.GetFileName(file.Path) + " has no paint " + (101 + paint); return null; }
                Array.Copy(want.Colors, main.Colors, Math.Min(want.Colors.Length, main.Colors.Length));
                if (file.DgrpBySub == null)
                {
                    file.DgrpBySub = new Dictionary<short, ushort>();
                    foreach (var o in iff.List<OBJD>()) if (o.SubIndex != -1) file.DgrpBySub[o.SubIndex] = o.BaseGraphicID;
                    // round 2: how many spin steps (DGRP number + 1, + 2 ... are there for every tile)
                    int spins = 1;
                    while (spins < 8 && file.DgrpBySub.Count > 0 && file.DgrpBySub.Values.All(id => iff.Get<DGRP>((ushort)(id + spins)) != null)) spins++;
                    file.Spins = spins;
                }
                Loaded[code] = iff;
                try { PackIffs.Add(iff, null); } catch { }
                LoadOrder.Enqueue(code);
                while (LoadOrder.Count > MaxLoaded) Loaded.Remove(LoadOrder.Dequeue());
                return iff;
            }
            catch (Exception e)
            {
                // (the browser can say a file is "still loading": the next look - a game minute later - tries again)
                LoadProblem = Path.GetFileName(file.Path) + ": " + e.Message;
                Console.WriteLine("[SimsRedux] car pack: " + file.Path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Round 2: the pack file a car is drawn from: its own, else (the Lite pack) its stock one, else one the pack has
        /// of the same kind (everyday car / traffic vehicle; then any everyday car), picked from the car's number. Service
        /// vehicles only ever use their own. Null = none. Call inside Lock.
        /// </summary>
        private static PackFile FileFor(int car, bool custom) { return FileFor(car, custom ? 1 : 0); }
        /// <summary>Round 3: a premade version's own file (VariantKeys), else as before (the customised file, the stock one...).</summary>
        private static PackFile FileFor(int car, int variant)
        {
            PackFile f;
            string key;
            if (variant > 1 && VariantKeys.TryGetValue(car * 64 + variant, out key) && Files.TryGetValue(key, out f)) return f;
            bool custom = variant != 0;
            if (custom && Files.TryGetValue(CarFolders[car] + "_custom", out f)) return f;
            if (Files.TryGetValue(CarFolders[car], out f)) return f;
            if (car >= FirstService) return null;
            for (int pass = 0; pass < 2; pass++)
            {
                int from = pass == 0 && car >= FirstTraffic ? FirstTraffic : 0, to = pass == 0 && car >= FirstTraffic ? FirstService : EverydayCars;
                var have = new List<int>();
                for (int c = from; c < to; c++) if (Files.ContainsKey(CarFolders[c])) have.Add(c);
                if (have.Count > 0) return Files[CarFolders[have[car % have.Count]]];
            }
            return null;
        }

        /// <summary>Does this pack file have every tile part of this car (never draw half a car)? Call inside Lock.</summary>
        private static bool Fits(PackFile file, VMEntity baseObj)
        {
            uint guid = CarGuid(baseObj);
            bool ok;
            if (file.Fits.TryGetValue(guid, out ok)) return ok;
            ok = file.DgrpBySub != null && baseObj.MultitileGroup.Objects.All(o => o.Object?.OBJ != null && file.DgrpBySub.ContainsKey(o.Object.OBJ.SubIndex));
            file.Fits[guid] = ok;
            return ok;
        }

        /// <summary>The skin a car is drawn with, or 0. Read only.</summary>
        public static short SkinOf(VMEntity baseObj)
        {
            if (baseObj == null) return 0;
            short service = ServiceSkin(baseObj);
            if (service != 0) return service;
            short code = baseObj.GetAttribute(SkinAttribute);
            if (code > 0) return code;
            var guid = CarGuid(baseObj);
            if (baseObj.GetAttribute(0) == ReduxTraffic.Marker) return FromId(baseObj.ObjectID, guid);
            // a carpool (or a visitor's car): the job's car, worked out once for this car
            object cached;
            if (CarpoolSkins.TryGetValue(baseObj, out cached)) return (short)cached;
            var vm = baseObj.Thread?.Context?.VM;
            int job = CarpoolJob(vm);
            code = job > 0 ? ForJob(job) : FromId(baseObj.ObjectID, guid);
            // (kept once the lot's Sims are there - while a lot is still loading, asked again next time)
            if (job > 0 || (vm?.Context?.ObjectQueries?.Avatars?.Count ?? 0) > 0) CarpoolSkins.Add(baseObj, code);
            return code;
        }

        /// <summary>
        /// VMGameObject.RefreshGraphic: the pack's picture for this tile part of a car, or null (keep the game's own).
        /// Drawing only.
        /// </summary>
        public static DGRP Swap(VMGameObject obj)
        {
            string why;
            var d = Want(obj, out why);
            if (d != null && Swapped++ == 0) Console.WriteLine("[SimsRedux] car pack: the first car part is drawn from the pack");
            return d;
        }

        /// <summary>20.3: the pack's picture for this tile part, or null and why not (plain words). Drawing only.</summary>
        private static DGRP Want(VMGameObject obj, out string why)
        {
            why = "";
            try
            {
                if (!Enabled) { why = "switched off on this device"; return null; }
                if (string.IsNullOrEmpty(PackFolder)) { why = "no pack here"; return null; }
                if (obj?.MultitileGroup == null) { why = "still loading"; return null; }
                var b = obj.MultitileGroup.BaseObject;
                if (b == null || !IsSkinnableCar(b)) { why = "not a car the pack covers"; return null; }
                if (obj.ObjectData[(int)VMStackObjectVariable.Graphic] != 0) { why = "car picture " + obj.ObjectData[(int)VMStackObjectVariable.Graphic] + " (not 0)"; return null; }
                short code = SkinOf(b);
                int car; int variant; int paint;
                if (code <= 0 || !Decode(code, out car, out variant, out paint)) { why = "no NFSU2 car picked (skin " + code + ")"; return null; }
                bool custom = variant != 0;
                lock (Lock)
                {
                    PackFile file;
                    var iff = Load(code, out file);
                    if (file == null) { why = "the pack has no " + CarFolders[car] + (custom ? " (customised)" : ""); return null; }
                    if (iff == null) { why = "couldn't read " + Path.GetFileName(file.Path) + (LoadProblem.Length > 0 ? " (" + LoadProblem + ")" : ""); return null; }
                    ushort id;
                    if (file.DgrpBySub == null || !file.DgrpBySub.TryGetValue(obj.Object.OBJ.SubIndex, out id)) { why = "no tile part " + obj.Object.OBJ.SubIndex + " in " + Path.GetFileName(file.Path); return null; }
                    if (!Fits(file, b)) { why = Path.GetFileName(file.Path) + " doesn't have every part of this vehicle"; return null; }
                    // round 2: the wheels' spin step now (SpinWheels), when the file has turning wheels
                    int step = StepOf(b);
                    if (step > 0 && step < file.Spins) id = (ushort)(id + step);
                    var d = iff.Get<DGRP>(id);
                    if (d == null) why = "no picture " + id + " in " + Path.GetFileName(file.Path);
                    return d;
                }
            }
            catch (Exception e) { why = e.Message; Console.WriteLine("[SimsRedux] car pack: " + e.Message); return null; }
        }

        /// <summary>
        /// Round 3 (The Showroom's picture): the pack's car for this skin, painted - each tile part's SubIndex and its picture
        /// (DGRP, still wheels). Null without the pack, or a car it can't draw (the Lite pack draws a missing car as one it has,
        /// as on the lot). Drawing only.
        /// </summary>
        public static List<KeyValuePair<short, DGRP>> PreviewParts(short code)
        {
            try
            {
                if (!Enabled || string.IsNullOrEmpty(PackFolder)) return null;
                lock (Lock)
                {
                    PackFile file;
                    var iff = Load(code, out file);
                    if (iff == null || file?.DgrpBySub == null) return null;
                    var l = new List<KeyValuePair<short, DGRP>>();
                    foreach (var kv in file.DgrpBySub)
                    {
                        var d = iff.Get<DGRP>(kv.Value);
                        if (d != null) l.Add(new KeyValuePair<short, DGRP>(kv.Key, d));
                    }
                    return l.Count > 0 ? l : null;
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] car pack: preview: " + e.Message); return null; }
        }

        // ================================================================ round 2: turning wheels (drawing only)

        private class SpinState { public LotTilePos Last; public bool Seen; public float Distance; public int Step; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VMEntity, SpinState> SpinStates
            = new System.Runtime.CompilerServices.ConditionalWeakTable<VMEntity, SpinState>();
        /// <summary>How far (tiles) a car moves per spin step: a wheel ~0.34 tiles across turns 18 degrees in ~0.1 tile.</summary>
        public const float SpinStepTiles = 0.1f;

        private static int StepOf(VMEntity baseObj)
        {
            SpinState st;
            return baseObj != null && SpinStates.TryGetValue(baseObj, out st) ? st.Step : 0;
        }

        /// <summary>
        /// ReduxTraffic's tick, for every car on the lot: how far it moved since the last tick (on this game - forward or
        /// back, by its facing) turns its wheels; when that's a new spin step, its tile parts are drawn again with that
        /// step's picture. Nothing happens on a game that doesn't draw (the server), without the pack, or for a car whose
        /// file has still wheels. Drawing only - it never changes the lot.
        /// </summary>
        public static void SpinWheels(VMEntity baseObj)
        {
            try
            {
                if (!Enabled || string.IsNullOrEmpty(PackFolder) || baseObj?.MultitileGroup == null || baseObj.WorldUI == null) return;
                var pos = baseObj.Position;
                if (pos == LotTilePos.OUT_OF_WORLD) return;
                var st = SpinStates.GetValue(baseObj, _ => new SpinState());
                if (!st.Seen) { st.Seen = true; st.Last = pos; return; }
                int dx = pos.x - st.Last.x, dy = pos.y - st.Last.y;
                bool jumped = pos.Level != st.Last.Level || Math.Abs(dx) + Math.Abs(dy) > 32; // (placed somewhere else: no spin)
                st.Last = pos;
                if ((dx == 0 && dy == 0) || jumped || !IsSkinnableCar(baseObj)) return;
                int fx = 0, fy = 0;
                switch (baseObj.Direction)
                {
                    case Direction.NORTH: fy = -1; break;
                    case Direction.SOUTH: fy = 1; break;
                    case Direction.EAST: fx = 1; break;
                    case Direction.WEST: fx = -1; break;
                }
                bool back = dx * fx + dy * fy < 0;
                if (ReduxTraffic.FrontFlipped) back = !back;
                st.Distance += (back ? -1 : 1) * (Math.Abs(dx) + Math.Abs(dy)) / 16f;
                int spins;
                lock (Lock)
                {
                    PackFile file;
                    var code = SkinOf(baseObj);
                    if (Load(code, out file) == null || file == null) return;
                    spins = file.Spins;
                }
                if (spins <= 1) return;
                int step = (int)Math.Floor(st.Distance / SpinStepTiles) % spins;
                if (step < 0) step += spins;
                if (step == st.Step) return;
                st.Step = step;
                Refresh(baseObj.MultitileGroup);
            }
            catch { }
        }

        private static bool FromPack(DGRP d)
        {
            object x;
            return d?.ChunkParent != null && PackIffs.TryGetValue(d.ChunkParent, out x);
        }

        /// <summary>
        /// 20.3: ReduxTraffic's once-a-game-minute look round, for every car on the lot (passing traffic and carpools):
        /// each tile part is drawn with the picture it should have now - the pack's, once it can be (a car loaded with
        /// the lot or a game joined part way is drawn before its group is there; a file the browser was still fetching;
        /// a carpool whose job is known once the lot's Sims are in), or the Sims car's again (the pack switched off).
        /// Drawing only - it never changes the lot.
        /// </summary>
        public static void EnsureDrawn(VMEntity baseObj)
        {
            try
            {
                if (baseObj?.MultitileGroup == null || !IsSkinnableCar(baseObj)) return;
                foreach (var e in baseObj.MultitileGroup.Objects)
                {
                    var o = e as VMGameObject;
                    var oc = o?.WorldUI as FSO.LotView.Components.ObjectComponent;
                    if (oc == null) continue;
                    string why;
                    var want = Want(o, out why);
                    if (want != null ? oc.DGRP != want : FromPack(oc.DGRP)) o.RefreshGraphic();
                }
            }
            catch { }
        }

        // ================================================================ 20.2: what's going on (the "carpack" cheat)

        /// <summary>Forget what was read and draw every car on the lot again (drawing only - the cheat's "try again").</summary>
        public static void Redraw(VM vm)
        {
            Reset();
            if (vm?.Entities == null) return;
            foreach (var g in vm.Entities.Where(e => e != null && e.MultitileGroup != null && e.MultitileGroup.BaseObject == e
                && IsSkinnableCar(e)).Select(e => e.MultitileGroup).ToList())
                Refresh(g);
        }

        /// <summary>
        /// The pack's side of the chain, for the "carpack" cheat: the folder, its manifest, the car files listed, and the
        /// cars on this lot - how many could be NFSU2 cars, how many the pack has a car for, how many are drawn from it now.
        /// Read only (drawing state).
        /// </summary>
        public static string Report(VM vm, out bool working)
        {
            working = false;
            int indexed, cars = 0, haveSkin = 0, drawn = 0;
            bool manifest; string problem, loadProblem;
            var lines = new List<string>();
            lock (Lock)
            {
                EnsureIndex();
                indexed = Files.Count; manifest = ManifestSeen; problem = IndexProblem;
            }
            if (vm?.Entities != null)
            {
                // 20.3: every car on the lot (anything car-like: the game's "Car - " objects too, so one the pack doesn't
                // cover still shows up), one line each: which car, skinnable?, its skin, its picture number, parts drawn
                foreach (var e in vm.Entities.ToList())
                {
                    if (e == null || e.MultitileGroup == null || e.MultitileGroup.BaseObject != e || !(e is VMGameObject)) continue;
                    uint guid = CarGuid(e);
                    var label = (e.MasterDefinition ?? e.Object?.OBJ)?.ChunkLabel ?? "";
                    bool skinnable = IsSkinnable(guid) || ServiceSkin(e) != 0;
                    if (!skinnable && !label.StartsWith("Car - ", StringComparison.Ordinal)) continue;
                    cars++;
                    try
                    {
                        short attr = e.GetAttribute(SkinAttribute);
                        short code = skinnable ? SkinOf(e) : (short)0;
                        int car; int variant; int paint;
                        bool ok = Decode(code, out car, out variant, out paint);
                        if (ok) lock (Lock) { if (FileFor(car, variant) != null) haveSkin++; } // (round 2: the Lite pack draws a missing car as one it has)
                        int parts = 0, fromPack = 0;
                        foreach (var o in e.MultitileGroup.Objects)
                        {
                            parts++;
                            if (FromPack(((o as VMGameObject)?.WorldUI as FSO.LotView.Components.ObjectComponent)?.DGRP)) fromPack++;
                        }
                        if (fromPack > 0) drawn++;
                        string why;
                        Want(e as VMGameObject, out why);
                        var name = label.StartsWith("Car - ", StringComparison.Ordinal) ? label.Substring(6) : label;
                        lines.Add(name + " " + guid.ToString("X8") + (skinnable ? "" : " (not covered)") +
                            ": skin " + (ok ? CarFolders[car] + (variant == 1 ? " custom" : variant > 1 ? " version " + variant : "") + " paint " + paint : "none") +
                            (ReduxCarShop.IsOwnedCar(e) ? " (a family's own car)" : ReduxCarShop.IsVisitCar(e) ? " (parked for a trip)" : "") +
                            (attr > 0 ? "" : skinnable ? (e.GetAttribute(0) == ReduxTraffic.Marker ? " (from id)" : " (carpool)") : "") +
                            ", picture " + e.ObjectData[(int)VMStackObjectVariable.Graphic] +
                            ", " + fromPack + "/" + parts + " NFSU2" + (fromPack == 0 && why.Length > 0 ? " - " + why : "") + ", wheels step " + StepOf(e));
                    }
                    catch (Exception ex) { lines.Add(label + ": " + ex.Message); }
                }
            }
            lock (Lock) loadProblem = LoadProblem;
            working = Enabled && indexed > 0 && (cars == 0 || drawn > 0);
            var folder = string.IsNullOrEmpty(PackFolder) ? "(none)" : PackFolder;
            // (a notice holds 8 lines: 4 here, then up to 4 cars - the rest in the game log)
            var head = "Pack: " + Source + ", folder " + folder + "\n" +
                "carpack.json " + (manifest ? "found" : "NOT found") + ", " + indexed + " car files" + (problem.Length > 0 ? " (" + problem + ")" : "") + "\n" +
                "This lot: " + cars + " cars, " + haveSkin + " in the pack, " + drawn + " drawn as NFSU2 cars (" + Swapped + " parts so far)" + (Enabled ? "" : " - switched off on this device") + "\n" +
                (loadProblem.Length > 0 ? "Last file problem: " + loadProblem : "No file problems");
            foreach (var l in lines) Console.WriteLine("[SimsRedux] car pack: " + l);
            int show = lines.Count > 4 ? 3 : lines.Count;
            for (int i = 0; i < show; i++) head += "\n" + lines[i];
            if (lines.Count > show) head += "\n+" + (lines.Count - show) + " more cars (in the game log)";
            return head;
        }

        /// <summary>After a car's skin is set: its pictures again (drawing only).</summary>
        public static void Refresh(VMMultitileGroup group)
        {
            if (group == null) return;
            foreach (var o in group.Objects) (o as VMGameObject)?.RefreshGraphic();
        }
    }
}
