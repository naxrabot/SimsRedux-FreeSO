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
    /// </summary>
    public static class ReduxTraffic
    {
        public const short Marker = 99;
        /// <summary>Set while a traffic car is being moved (placement rules don't apply to it).</summary>
        [ThreadStatic] public static bool Moving;
        /// <summary>1/16ths of a tile a car moves each tick (about 3.75 tiles a second at normal speed).</summary>
        private const int Speed = 2;
        /// <summary>Set if the cars turn out to drive backwards (their front is the other way).</summary>
        public static bool FrontFlipped = false;

        /// <summary>The cars (their main object), with how often each turns up.</summary>
        private static readonly uint[] Cars = { 0xdb6dedb2, 0xdb6dedb2, 0xdb6dedb2, 0xbc3aec18, 0xbc3aec18, 0x1d3dec63, 0x1d3dec63, 0x4b71ecda, 0x17acec22, 0xc8ee6e7d };
        private static readonly HashSet<uint> AllCarGUIDs = new HashSet<uint>();

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
                        if (e.GetAttribute(0) == Marker) st.Traffic.Add(e.MultitileGroup);
                        else st.OtherCar = true;
                    }
                }
                st.Traffic.RemoveAll(g => g.BaseObject == null || g.BaseObject.Dead);
                var traffic = st.Traffic.Count > 0 ? st.Traffic.ToList() : null;
                bool otherCar = st.OtherCar;
                if (traffic != null) foreach (var g in traffic) Drive(vm, g);

                // a new one now and then (decided from the game clock, the same for everyone)
                if (clock.Ticks % 30 != 0 || otherCar || (traffic != null && traffic.Count >= 2)) return;
                int hour = clock.Hours;
                bool night = hour >= 22 || hour < 6;
                bool evening = hour >= 19 || hour < 7;
                int chance = night ? 240 : evening ? 120 : 60; // one in this many game minutes or so
                uint h = Hash((uint)(clock.Ticks / 30) * 2654435761u ^ (uint)vm.GetGlobalValue(10) * 40503u);
                if (h % (uint)chance != 0) return;

                var roads = Roads(ctx.Architecture);
                if (roads.Count == 0) return;
                var road = roads[(int)((h >> 8) % (uint)roads.Count)];
                bool forward = ((h >> 16) & 1) == 0;
                uint guid = Cars[(h >> 20) % (uint)Cars.Length];
                Spawn(vm, road, forward, guid);
                st.Traffic = null; // (the new car is picked up on the next tick)
            }
            catch (Exception ex) { Console.WriteLine("[SimsRedux] traffic: " + ex.Message); }
        }

        private static bool IsCar(VMEntity e)
        {
            if (AllCarGUIDs.Count == 0) lock (AllCarGUIDs) if (AllCarGUIDs.Count == 0) foreach (var g in Cars) AllCarGUIDs.Add(g);
            var guid = e.Object?.OBJ?.GUID ?? 0;
            if (AllCarGUIDs.Contains(guid)) return true;
            // any other carpool car (the game's carpools and visitors' cars) - by its name
            var label = e.Object?.OBJ?.ChunkLabel ?? "";
            return label.StartsWith("Car - ", StringComparison.Ordinal);
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

        private static void Spawn(VM vm, Road road, bool forward, uint guid)
        {
            var ctx = vm.Context;
            // right-hand traffic: heading +x the lane is on the +y side, heading +y it's on the -x side
            int lane = road.AlongX ? (forward ? road.Middle + 2 : road.Middle - 2) : (forward ? road.Middle - 2 : road.Middle + 2);
            var dir = DirFor(road.AlongX, forward);
            int along = forward ? road.Start + 7 : road.End - 7;
            var group = ctx.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, dir);
            if (group == null) return;
            foreach (var o in group.Objects) o.SetAttribute(0, Marker); // (not a carpool: its main just waits)

            // put it there, then line its middle up with the lane
            var pos = road.AlongX ? LotTilePos.FromBigTile((short)along, (short)lane, 1) : LotTilePos.FromBigTile((short)lane, (short)along, 1);
            pos = new LotTilePos((short)(pos.x + 8), (short)(pos.y + 8), 1);
            if (Move(group, pos, dir, ctx, VMPlaceRequestFlags.AllowIntersection).Status != VMPlacementError.Success) { group.Delete(ctx); return; }
            int cx = 0, cy = 0, n = 0;
            foreach (var o in group.Objects) { if (o.Position == LotTilePos.OUT_OF_WORLD) continue; cx += o.Position.x; cy += o.Position.y; n++; }
            if (n == 0) { group.Delete(ctx); return; }
            cx /= n; cy /= n;
            int want = lane * 16 + 8;
            var fixedPos = road.AlongX ? new LotTilePos(pos.x, (short)(pos.y + (want - cy)), 1) : new LotTilePos((short)(pos.x + (want - cx)), pos.y, 1);
            if (Move(group, fixedPos, dir, ctx, VMPlaceRequestFlags.AllowIntersection).Status != VMPlacementError.Success) group.Delete(ctx);
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
