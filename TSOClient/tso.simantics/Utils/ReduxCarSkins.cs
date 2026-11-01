using FSO.Common.IO;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.Engine.Scopes;
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
        public const int NormalPaints = 6, CustomPaints = 5;

        /// <summary>A skin: 0 = none; else 1 + (variant * 64 + car) * 16 + paint, variant 0 stock / 1 customised.</summary>
        public static short Code(int car, bool custom, int paint) { return (short)(1 + ((custom ? 64 : 0) + car) * 16 + paint); }
        public static bool Decode(short code, out int car, out bool custom, out int paint)
        {
            car = 0; custom = false; paint = 0;
            if (code <= 0) return false;
            int v = code - 1;
            paint = v % 16; v /= 16;
            car = v % 64; custom = v / 64 == 1;
            if (v / 64 > 1 || car >= CarFolders.Length) return false;
            if (custom && car >= EverydayCars) return false;
            return paint < (custom ? CustomPaints : NormalPaintsOf(car));
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
        };
        public static bool IsSkinnable(uint baseGuid) { return Skinnable.Contains(baseGuid); }

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
                && !traffic.Any(g => g.BaseObject != null && Decode(g.BaseObject.GetAttribute(SkinAttribute), out _, out var c, out _) && c);
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

        private class PackFile { public string Path; public Dictionary<short, ushort> DgrpBySub; public byte[] Bytes; }
        private static Dictionary<string, PackFile> Files; // "COROLLA" / "COROLLA_custom" -> file
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
            if (Files != null) return;
            Files = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(PackFolder)) return;
            try
            {
                var manifest = Path.Combine(PackFolder, "carpack.json");
                if (!FSOFile.Exists(manifest)) return;
                // the manifest's "file" entries (cars/COROLLA.iff, cars/COROLLA_custom.iff) - only plain names inside the pack
                foreach (Match m in Regex.Matches(FSOFile.ReadAllText(manifest), "\"file\"\\s*:\\s*\"(cars/[A-Za-z0-9_\\-]+\\.iff)\""))
                {
                    var rel = m.Groups[1].Value;
                    var key = Path.GetFileNameWithoutExtension(rel);
                    Files[key] = new PackFile { Path = Path.Combine(PackFolder, rel) };
                }
                Console.WriteLine("[SimsRedux] car pack: " + Files.Count + " car files from " + PackFolder);
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] car pack: " + e.Message); Files.Clear(); }
        }

        /// <summary>The IFF for a skin with its paint copied over PALT #1 (a few kept; the oldest dropped).</summary>
        private static IffFile Load(short code, out PackFile file)
        {
            file = null;
            if (!Decode(code, out int car, out bool custom, out int paint)) return null;
            EnsureIndex();
            if (!Files.TryGetValue(CarFolders[car] + (custom ? "_custom" : ""), out file)) return null;
            IffFile iff;
            if (Loaded.TryGetValue(code, out iff)) return iff;
            try
            {
                if (file.Bytes == null) file.Bytes = FSOFile.ReadAllBytes(file.Path);
                iff = new IffFile();
                using (var ms = new MemoryStream(file.Bytes)) iff.Read(ms);
                var main = iff.Get<PALT>(1);
                var want = iff.Get<PALT>((ushort)(101 + paint));
                if (main == null || want == null) return null;
                Array.Copy(want.Colors, main.Colors, Math.Min(want.Colors.Length, main.Colors.Length));
                if (file.DgrpBySub == null)
                {
                    file.DgrpBySub = new Dictionary<short, ushort>();
                    foreach (var o in iff.List<OBJD>()) if (o.SubIndex != -1) file.DgrpBySub[o.SubIndex] = o.BaseGraphicID;
                }
                Loaded[code] = iff;
                LoadOrder.Enqueue(code);
                while (LoadOrder.Count > MaxLoaded) Loaded.Remove(LoadOrder.Dequeue());
                return iff;
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] car pack: " + file.Path + ": " + e.Message); return null; }
        }

        /// <summary>The skin a car is drawn with, or 0. Read only.</summary>
        public static short SkinOf(VMEntity baseObj)
        {
            if (baseObj == null) return 0;
            short code = baseObj.GetAttribute(SkinAttribute);
            if (code > 0) return code;
            var guid = baseObj.Object?.OBJ?.GUID ?? 0;
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
            try
            {
                if (!Enabled || string.IsNullOrEmpty(PackFolder) || obj?.MultitileGroup == null) return null;
                if (obj.ObjectData[(int)VMStackObjectVariable.Graphic] != 0) return null;
                var b = obj.MultitileGroup.BaseObject;
                if (b == null || !IsSkinnable(b.Object?.OBJ?.GUID ?? 0)) return null;
                short code = SkinOf(b);
                if (code <= 0) return null;
                lock (Lock)
                {
                    PackFile file;
                    var iff = Load(code, out file);
                    ushort id;
                    if (iff == null || file.DgrpBySub == null || !file.DgrpBySub.TryGetValue(obj.Object.OBJ.SubIndex, out id)) return null;
                    return iff.Get<DGRP>(id);
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] car pack: " + e.Message); return null; }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VMEntity, object> Drawn
            = new System.Runtime.CompilerServices.ConditionalWeakTable<VMEntity, object>();

        /// <summary>
        /// A carpool car seen by ReduxTraffic's once-a-minute look round: its pictures are worked out again once its job
        /// can be known (it may have been drawn while the lot was still loading). Drawing only.
        /// </summary>
        public static void EnsureDrawn(VMEntity baseObj)
        {
            try
            {
                if (!Enabled || string.IsNullOrEmpty(PackFolder) || baseObj == null || !IsSkinnable(baseObj.Object?.OBJ?.GUID ?? 0)) return;
                object x;
                if (Drawn.TryGetValue(baseObj, out x)) return;
                SkinOf(baseObj); // (settles the car's job once the lot's Sims are in)
                if (!CarpoolSkins.TryGetValue(baseObj, out x)) return;
                Drawn.Add(baseObj, true);
                Refresh(baseObj.MultitileGroup);
            }
            catch { }
        }

        /// <summary>After a car's skin is set: its pictures again (drawing only).</summary>
        public static void Refresh(VMMultitileGroup group)
        {
            if (group == null) return;
            foreach (var o in group.Objects) (o as VMGameObject)?.RefreshGraphic();
        }
    }
}
