using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: now and then a car drives down the street in front of the lot, just for the feel of a
    /// living town - fewer at night. They're the game's own carpool cars (the standard car, town car, SUV,
    /// the junker, the Bentley, now and then a limo), told not to do their carpool job and moved along the
    /// road a little every tick until they leave the lot.
    ///
    /// Everything here is worked out from what every player already has the same - the game clock and the
    /// cars' own positions - so online every player sees the same cars at the same time. A traffic car is
    /// marked by its attribute 0 (= Marker), so one that was saved with the lot simply drives on.
    ///
    /// 19.6 "Neighbourhood life" (on by default): the street follows the town's day - a morning rush (7-9) and
    /// an evening rush (4-7 PM) on weekdays with up to three cars at once, a little busier on weekend days,
    /// quiet late at night - with a wider mix of the game's own cars (the pickup truck, the staff sedan, now and
    /// then a limo or a jeep), and on school-day mornings and afternoons the school bus drives past when it
    /// isn't stopping for this family. Off (this device, single player): the street is as it was before 19.6.
    /// On a visit or an online lot it's always on, for every game there alike (a setting can't differ between
    /// two games playing one lot).
    /// </summary>
    public static class ReduxTraffic
    {
        public const short Marker = 99;

        /// <summary>Hot Date's cab service (the phone's cab) and Unleashed's Old Town trolley service (lot-wide objects).</summary>
        public const uint CabServiceGUID = 0xA6F31853, TrolleyServiceGUID = 0xEAA79D86;

        /// <summary>
        /// SimsRedux: the phone's "Call Cab..." (its script "Auto Call Cab") picks who comes by the lot and the
        /// expansion packs: on a community lot - and on a home lot whenever Hot Date's cab service didn't
        /// answer - that was Unleashed's Old Town trolley ("Get In"). A cab is a cab now: while the lot has
        /// Hot Date's cab service, that script finds it wherever it looked for the trolley service. (Vacation
        /// Island, Studio Town and Magic Town keep their own rides home.) Worked out from the script's name
        /// and the lot's own objects, so every player's game picks the same.
        /// </summary>
        public static uint CabService(Engine.VMStackFrame frame, uint guid)
        {
            if (guid != TrolleyServiceGUID) return guid;
            try
            {
                var vm = frame?.VM;
                if (vm == null || !vm.TS1) return guid;
                var label = frame.Routine?.Chunk?.ChunkLabel;
                if (label == null || label.IndexOf("call cab", StringComparison.OrdinalIgnoreCase) < 0) return guid;
                var cabs = vm.Context.ObjectQueries.GetObjectsByGUID(CabServiceGUID);
                return (cabs != null && cabs.Count > 0) ? CabServiceGUID : guid;
            }
            catch { return guid; }
        }
        /// <summary>Set while a traffic car is being moved (placement rules don't apply to it).</summary>
        [ThreadStatic] public static bool Moving;
        /// <summary>1/16ths of a tile a car moves each tick (about 3.75 tiles a second at normal speed).</summary>
        private const int Speed = 2;
        /// <summary>Set if the cars turn out to drive backwards (their front is the other way).</summary>
        public static bool FrontFlipped = false;

        /// <summary>The cars (their main object), with how often each turns up.</summary>
        private static readonly uint[] Cars = { 0xdb6dedb2, 0xdb6dedb2, 0xdb6dedb2, 0xbc3aec18, 0xbc3aec18, 0x1d3dec63, 0x1d3dec63, 0x4b71ecda, 0x17acec22, 0xc8ee6e7d };

        /// <summary>
        /// 19.6 Neighbourhood life: the wider mix - standard carpool car, town car, SUV, pickup truck (the expansion
        /// packs' shared objects - if it isn't there, nothing is made that time), junker, staff sedan, Bentley, limo,
        /// military jeep. (Not the squad car - the police come in one, and a passing one could be taken for theirs.) All use the game's shared car script, which just waits when attribute 0 isn't 1.
        /// </summary>
        private static readonly uint[] MoreCars =
        {
            0xdb6dedb2, 0xdb6dedb2, 0xdb6dedb2, 0xdb6dedb2, // standard carpool
            0xbc3aec18, 0xbc3aec18, 0xbc3aec18,             // town car
            0x1d3dec63, 0x1d3dec63, 0x1d3dec63,             // SUV
            0xcd0876e1, 0xcd0876e1,                         // pickup truck
            0x4b71ecda, 0x4b71ecda,                         // junker
            0xf72aec44,                                     // staff sedan
            0x17acec22,                                     // Bentley
            0xc8ee6e7d,                                     // limo (rarely)
            0xa6f5ed9a,                                     // military jeep (rarely)
        };
        /// <summary>The school bus (its own script also just waits when attribute 0 isn't 1).</summary>
        private const uint SchoolBusGUID = 0x50ac15bf;
        private static readonly HashSet<uint> AllCarGUIDs = new HashSet<uint>();

        /// <summary>
        /// 19.6: Options > Play > "Neighbourhood life" (this device). The game sets this from the setting; on a
        /// visit or online lot (vm.ReduxNoPause - set alike on every game there) it counts as on whatever it says.
        /// The setting itself is never read here.
        /// </summary>
        public static bool ExtraLife = true;
        /// <summary>The setting's key (ReduxSettings) - the same switch for passing cars, street sounds and people.</summary>
        public const string SettingKey = "nhoodlife";
        public static bool ExtraOn(VM vm) { return ExtraLife || (vm != null && vm.ReduxNoPause); }

        private class Road { public bool AlongX; public int Middle, Start, End; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VMArchitecture, List<Road>> RoadCache
            = new System.Runtime.CompilerServices.ConditionalWeakTable<VMArchitecture, List<Road>>();

        private class LotCars { public List<VMMultitileGroup> Traffic; public bool OtherCar; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VM, LotCars> Seen = new System.Runtime.CompilerServices.ConditionalWeakTable<VM, LotCars>();

        /// <summary>The lot was loaded again (a resync, an undo): the cars are looked for again straight away.</summary>
        public static void Forget(VM vm) { if (vm != null) Seen.Remove(vm); }

        public static void Tick(VM vm)
        {
            try
            {
                if (vm == null || !vm.TS1 || vm.Context?.Architecture == null || !vm.Context.Ready) return;
                var ctx = vm.Context;

                // drive the cars already out there (the lot's objects are looked through only once a game
                // minute - every tick was hundreds of objects, 30 times a second)
                var clock = ctx.Clock;
                var st = Seen.GetValue(vm, _ => new LotCars());
                if (st.Traffic == null || clock.Ticks % 30 == 0)
                {
                    st.Traffic = new List<VMMultitileGroup>();
                    st.OtherCar = false;
                    foreach (var e in vm.Entities)
                    {
                        if (!(e is VMGameObject) || e.MultitileGroup == null || e != e.MultitileGroup.BaseObject) continue;
                        if (!IsCar(e)) continue;
                        if (e.GetAttribute(0) == Marker)
                        {
                            st.Traffic.Add(e.MultitileGroup);
                            MarkMoving(e.MultitileGroup); // (one saved with the lot, or just made)
                        }
                        else st.OtherCar = true;
                    }
                }
                st.Traffic.RemoveAll(g => g.BaseObject == null || g.BaseObject.Dead);
                // (no copy each tick: driving a car only ever deletes lot objects, never touches this list)
                var traffic = st.Traffic;
                int carCount = traffic.Count;
                bool otherCar = st.OtherCar;
                for (int i = 0; i < traffic.Count; i++) Drive(vm, traffic[i]);

                // a new one now and then (decided from the game clock, the same for everyone)
                if (clock.Ticks % 30 != 0 || otherCar) return;
                int hour = clock.Hours;
                bool extra = ExtraOn(vm);
                int chance, maxCars;
                if (extra) Busy(hour, ReduxWeek.Day(clock) >= 5, out chance, out maxCars);
                else
                {
                    bool night = hour >= 22 || hour < 6;
                    bool evening = hour >= 19 || hour < 7;
                    chance = night ? 240 : evening ? 120 : 60; // one in this many game minutes or so
                    maxCars = 2;
                }
                bool bus = extra && SchoolBusDue(vm, clock); // (its one minute: it may make a rush-hour street one busier)
                if (carCount >= maxCars + (bus ? 1 : 0)) return;
                uint h = Hash((uint)(clock.Ticks / 30) * 2654435761u ^ (uint)vm.GetGlobalValue(10) * 40503u);
                if (!bus && h % (uint)chance != 0) return;

                var roads = Roads(ctx.Architecture);
                if (roads.Count == 0) return;
                var road = roads[(int)((h >> 8) % (uint)roads.Count)];
                bool forward = ((h >> 16) & 1) == 0;
                if (extra && !LaneFree(traffic, road, forward))
                {
                    forward = !forward; // (the other way, if that lane's clear)
                    if (!LaneFree(traffic, road, forward)) return;
                }
                uint guid = bus ? SchoolBusGUID
                    : extra ? MoreCars[(h >> 20) % (uint)MoreCars.Length]
                    : Cars[(h >> 20) % (uint)Cars.Length];
                Spawn(vm, road, forward, guid);
                st.Traffic = null; // (the new car is picked up on the next tick)
            }
            catch (Exception ex) { Console.WriteLine("[SimsRedux] traffic: " + ex.Message); }
        }

        private static bool IsCar(VMEntity e)
        {
            if (AllCarGUIDs.Count == 0) lock (AllCarGUIDs) if (AllCarGUIDs.Count == 0)
                    {
                        foreach (var g in Cars) AllCarGUIDs.Add(g);
                        foreach (var g in MoreCars) AllCarGUIDs.Add(g);
                        AllCarGUIDs.Add(SchoolBusGUID);
                    }
            var guid = e.Object?.OBJ?.GUID ?? 0;
            if (AllCarGUIDs.Contains(guid)) return true;
            // any other carpool car (the game's carpools and visitors' cars) - by its name
            var label = e.Object?.OBJ?.ChunkLabel ?? "";
            return label.StartsWith("Car - ", StringComparison.Ordinal);
        }

        /// <summary>
        /// 19.6 Neighbourhood life: how busy the street is at this hour - one new car in about this many game
        /// minutes, and the most cars on the street at once. Weekdays: a morning rush (7-9) and an evening rush
        /// (4-7 PM); weekends: no rush, a little busier all day; late at night: quiet.
        /// </summary>
        private static void Busy(int hour, bool weekend, out int chance, out int maxCars)
        {
            // (19.6 fix: the first numbers - one in 45 game minutes by day, 30 at weekends - were hardly more than
            //  the old street's one in 60, so the street looked the same. A car takes about 15 game minutes to drive
            //  past, so one in 10 keeps a car or two in sight most of the day, and the rush hours are clearly busy.)
            if (!weekend)
            {
                if (hour >= 7 && hour < 9) { chance = 4; maxCars = 3; return; }   // morning rush
                if (hour >= 16 && hour < 19) { chance = 4; maxCars = 3; return; } // evening rush
                if (hour >= 9 && hour < 16) { chance = 10; maxCars = 3; return; }
            }
            else if (hour >= 8 && hour < 20) { chance = 8; maxCars = 3; return; } // weekend days
            if (hour >= 19 && hour < 22) { chance = 16; maxCars = 2; return; }
            if (hour >= 5 && hour < 9) { chance = 25; maxCars = 2; return; }
            if (hour >= 22) { chance = 45; maxCars = 2; return; }
            chance = 90; maxCars = 1; // midnight to 5 AM
        }

        /// <summary>19.6: how many streets this lot has (for the game log's street-life line).</summary>
        public static int RoadCount(VMArchitecture arch)
        {
            try { return arch == null ? 0 : Roads(arch).Count; } catch { return 0; }
        }

        /// <summary>
        /// 19.6: the "streetlife" test cheat (through VMNetReduxTownieCmd StreetCar, so every game on the lot does the
        /// same): a car drives past right away - on the first street with a free lane. False: no street, or no room.
        /// </summary>
        public static bool SpawnNow(VM vm)
        {
            if (vm == null || !vm.TS1 || vm.Context?.Architecture == null) return false;
            var roads = Roads(vm.Context.Architecture);
            if (roads.Count == 0) return false;
            var st = Seen.GetValue(vm, _ => new LotCars());
            var traffic = new List<VMMultitileGroup>();
            foreach (var e in vm.Entities)
            {
                if (!(e is VMGameObject) || e.MultitileGroup == null || e != e.MultitileGroup.BaseObject || e.Dead) continue;
                if (IsCar(e) && e.GetAttribute(0) == Marker) traffic.Add(e.MultitileGroup);
            }
            uint h = Hash((uint)vm.Context.Clock.Ticks * 2654435761u ^ 0x51u);
            for (int i = 0; i < roads.Count * 2; i++)
            {
                var road = roads[(int)((h + (uint)(i / 2)) % (uint)roads.Count)];
                bool forward = (((h >> 16) & 1) == 0) ^ (i % 2 == 1);
                if (!LaneFree(traffic, road, forward)) continue;
                if (Spawn(vm, road, forward, MoreCars[(h >> 20) % (uint)MoreCars.Length]))
                {
                    st.Traffic = null; // (picked up on the next tick)
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 19.6: the school bus drives past once on a school-day morning (7:20-8:19) and once in the afternoon
        /// (3:00-3:49), at a minute picked from the date and the lot - unless it stops here for this family's
        /// children (then the game's own bus comes, as always). Monday to Friday, like school (ReduxWeek).
        /// </summary>
        private static bool SchoolBusDue(VM vm, VMClock clock)
        {
            if (ReduxWeek.Day(clock) >= 5) return false;
            if (clock.MinuteFractions >= 30) return false; // (one try at that minute, even when a minute is longer)
            int now = clock.Hours * 60 + clock.Minutes;
            bool morning = now >= 7 * 60 && now < 9 * 60;
            bool afternoon = now >= 15 * 60 && now < 16 * 60;
            if (!morning && !afternoon) return false;
            uint date = (uint)((clock.Year - 1997) * 360 + (clock.Month - 1) * 30 + clock.DayOfMonth);
            uint bh = Hash(date * 7919u ^ (uint)vm.GetGlobalValue(10) * 40503u ^ (morning ? 0x5bu : 0xa7u));
            int at = morning ? 7 * 60 + 20 + (int)(bh % 60) : 15 * 60 + (int)(bh % 50);
            if (now != at) return false;
            return !FamilyHasChildren(vm);
        }

        /// <summary>Does the family living here have school-age children (the game's own bus stops for them)?</summary>
        private static bool FamilyHasChildren(VM vm)
        {
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null) return false;
            foreach (var a in vm.Context.ObjectQueries.Avatars)
            {
                var ava = a as VMAvatar;
                if (ava == null || ava.IsPet) continue;
                if (ava.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) != fam.ChunkID) continue;
                if (ava.IsChild) return true;
            }
            return false;
        }

        /// <summary>
        /// 19.6: no new car in a lane while another car going the same way is still near where it would appear
        /// (they all drive at the same speed, so the gap stays).
        /// </summary>
        private static bool LaneFree(List<VMMultitileGroup> traffic, Road road, bool forward)
        {
            var dir = DirFor(road.AlongX, forward);
            int lane = road.AlongX ? (forward ? road.Middle + 2 : road.Middle - 2) : (forward ? road.Middle - 2 : road.Middle + 2);
            int along = forward ? road.Start + 7 : road.End - 7;
            for (int i = 0; i < traffic.Count; i++)
            {
                var b = traffic[i].BaseObject;
                if (b == null || b.Dead || b.Direction != dir) continue;
                var p = b.Position;
                if (p == LotTilePos.OUT_OF_WORLD) continue;
                int tAlong = road.AlongX ? p.x / 16 : p.y / 16;
                int tAcross = road.AlongX ? p.y / 16 : p.x / 16;
                if (Math.Abs(tAcross - lane) > 3) continue; // (another street)
                if (Math.Abs(tAlong - along) < 14) return false;
            }
            return true;
        }

        /// <summary>The streets on the lot: 5+ tiles of road side by side (the Sims 1 street is 7 tiles wide,
        /// a lane of 3 each side of the line down the middle).</summary>
        private static List<Road> Roads(VMArchitecture arch)
        {
            List<Road> cached;
            if (RoadCache.TryGetValue(arch, out cached)) return cached;
            var result = new List<Road>();
            int w = arch.Width, ht = arch.Height;
            var floors = arch.Floors[0];
            Func<int, int, bool> isRoad = (x, y) =>
            {
                if (x < 0 || y < 0 || x >= w || y >= ht) return false;
                var p = floors[y * w + x].Pattern;
                return p == 9 || p == 10 || p == 11;
            };
            for (int axis = 0; axis < 2; axis++)
            {
                bool alongX = axis == 0;
                int lines = alongX ? ht : w, len = alongX ? w : ht;
                int runStart = -1;
                for (int l = 0; l <= lines; l++)
                {
                    int count = 0, first = int.MaxValue, last = -1;
                    if (l < lines)
                        for (int i = 0; i < len; i++)
                            if (alongX ? isRoad(i, l) : isRoad(l, i)) { count++; first = Math.Min(first, i); last = i; }
                    bool road = count >= Math.Max(12, len / 3);
                    if (road && runStart < 0) runStart = l;
                    if (!road && runStart >= 0)
                    {
                        int width = l - runStart;
                        if (width >= 5)
                        {
                            int mid = runStart + width / 2;
                            // how far the road goes along its line
                            int s = int.MaxValue, e = -1;
                            for (int i = 0; i < len; i++) if (alongX ? isRoad(i, mid) : isRoad(mid, i)) { s = Math.Min(s, i); e = i; }
                            if (e - s > 10) result.Add(new Road { AlongX = alongX, Middle = mid, Start = s, End = e });
                        }
                        runStart = -1;
                    }
                }
            }
            RoadCache.Add(arch, result);
            return result;
        }

        private static Direction DirFor(bool alongX, bool forward)
        {
            Direction d;
            if (alongX) d = forward ? Direction.EAST : Direction.WEST;
            else d = forward ? Direction.SOUTH : Direction.NORTH;
            if (FrontFlipped) d = (Direction)(((int)d << 4 | (int)d >> 4) & 0xFF);
            return d;
        }

        private static bool Spawn(VM vm, Road road, bool forward, uint guid)
        {
            var ctx = vm.Context;
            // right-hand traffic: heading +x the lane is on the +y side, heading +y it's on the -x side
            int lane = road.AlongX ? (forward ? road.Middle + 2 : road.Middle - 2) : (forward ? road.Middle - 2 : road.Middle + 2);
            var dir = DirFor(road.AlongX, forward);
            int along = forward ? road.Start + 7 : road.End - 7;
            var group = ctx.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, dir);
            if (group == null) return false;
            foreach (var o in group.Objects) o.SetAttribute(0, Marker); // (not a carpool: its main just waits)
            MarkMoving(group);

            // put it there, then line its middle up with the lane
            var pos = road.AlongX ? LotTilePos.FromBigTile((short)along, (short)lane, 1) : LotTilePos.FromBigTile((short)lane, (short)along, 1);
            pos = new LotTilePos((short)(pos.x + 8), (short)(pos.y + 8), 1);
            if (Move(group, pos, dir, ctx, VMPlaceRequestFlags.AllowIntersection).Status != VMPlacementError.Success) { group.Delete(ctx); return false; }
            int cx = 0, cy = 0, n = 0;
            foreach (var o in group.Objects) { if (o.Position == LotTilePos.OUT_OF_WORLD) continue; cx += o.Position.x; cy += o.Position.y; n++; }
            if (n == 0) { group.Delete(ctx); return false; }
            cx /= n; cy /= n;
            int want = lane * 16 + 8;
            var fixedPos = road.AlongX ? new LotTilePos(pos.x, (short)(pos.y + (want - cy)), 1) : new LotTilePos((short)(pos.x + (want - cx)), pos.y, 1);
            if (Move(group, fixedPos, dir, ctx, VMPlaceRequestFlags.AllowIntersection).Status != VMPlacementError.Success) { group.Delete(ctx); return false; }
            return true;
        }

        private static void Drive(VM vm, VMMultitileGroup group)
        {
            var ctx = vm.Context;
            var b = group.BaseObject;
            var pos = b.Position;
            if (pos == LotTilePos.OUT_OF_WORLD) { group.Delete(ctx); return; }
            var dir = b.Direction;
            var travel = dir;
            if (FrontFlipped) travel = (Direction)(((int)dir << 4 | (int)dir >> 4) & 0xFF);
            int dx = 0, dy = 0;
            switch (travel)
            {
                case Direction.EAST: dx = Speed; break;
                case Direction.WEST: dx = -Speed; break;
                case Direction.SOUTH: dy = Speed; break;
                case Direction.NORTH: dy = -Speed; break;
                default: group.Delete(ctx); return;
            }
            var next = new LotTilePos((short)(pos.x + dx), (short)(pos.y + dy), pos.Level);
            var arch = ctx.Architecture;
            // gone when any part of it reaches the edge of the lot
            foreach (var o in group.Objects)
            {
                var p = o.Position;
                if (p == LotTilePos.OUT_OF_WORLD) continue;
                int tx = (p.x + dx) / 16, ty = (p.y + dy) / 16;
                if (tx < 1 || ty < 1 || tx >= arch.Width - 1 || ty >= arch.Height - 1) { group.Delete(ctx); return; }
            }
            if (Move(group, next, dir, ctx, VMPlaceRequestFlags.AllowIntersection).Status != VMPlacementError.Success)
                group.Delete(ctx);
        }

        /// <summary>
        /// SimsRedux: a traffic car moves every tick - marked as something that moves often (like a Sim's
        /// carried object), so it doesn't make the lot rebuild its outdoor lighting every tick. (Visual only:
        /// it just doesn't cast a light shadow.)
        /// </summary>
        private static void MarkMoving(VMMultitileGroup group)
        {
            foreach (var o in group.Objects) o.MovedSelf = true;
        }

        private static VMPlacementResult Move(VMMultitileGroup group, LotTilePos pos, Direction dir, VMContext ctx, VMPlaceRequestFlags flags)
        {
            Moving = true;
            try { return group.ChangePosition(pos, dir, ctx, flags); }
            finally { Moving = false; }
        }

        private static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
            return x;
        }
    }
}
