using FSO.LotView.Model;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.6: more neighbourhood life - people out on the sidewalk in front of the lot (passers-by, joggers, dog
    /// walkers, children playing, neighbours stopping for a chat). The game running the lot decides who comes by
    /// (Simitone.Client.Utils.ReduxStreetPeople) and every change goes through VMNetReduxTownieCmd's street modes, so every
    /// game on a visit sees the same people. This is the shared part: where the sidewalk is (worked out from the lot's own
    /// floor, the same on every game), and the mark a street Sim carries.
    ///
    /// A Sim out on the street carries its kind in attribute 180 (StreetAttr) and the tile it's walking to in 181 (EndAttr) -
    /// both on the lot itself (saved and sent with it), so a game that joins a visit later knows them too. While marked,
    /// the Sim never chooses anything of its own (VMFindBestAction): it only walks along, stops for the social it's given,
    /// and leaves. Street Sims are never saved into the family's lot (FSOExcludeUserSave, as every townie).
    /// </summary>
    public static class ReduxSidewalk
    {
        /// <summary>Avatar attributes (after B5b's 140-169 and the brain's 170-174): the street kind, and the end tile.</summary>
        public const int StreetAttr = 180, EndAttr = 181;

        public enum Kind : short
        {
            None = 0,
            Passer = 1,
            Jogger = 2,
            DogWalker = 3,
            Dog = 4,
            Kid = 5,
            Neighbour = 6,
        }
        public const short MaxKind = 6;

        /// <summary>The "Go Here" object (the same one the pie menu's Go Here uses).</summary>
        public const uint GotoGUID = 0x000007C4;

        /// <summary>One sidewalk: the line of tiles (x = Line when not AlongX, y = Line when AlongX) from From to To along it.</summary>
        public class Walk
        {
            public bool AlongX;
            public int Line;
            public int From, To;

            public short X(int along) { return (short)(AlongX ? along : Line); }
            public short Y(int along) { return (short)(AlongX ? Line : along); }
        }

        private static readonly ConditionalWeakTable<VMArchitecture, List<Walk>> Cache = new ConditionalWeakTable<VMArchitecture, List<Walk>>();

        /// <summary>A Sim out on the street right now (marked by a street mode of VMNetReduxTownieCmd).</summary>
        public static bool IsStreet(VMEntity e)
        {
            return e is VMAvatar && !e.Dead && e.GetAttribute(StreetAttr) > 0;
        }

        /// <summary>
        /// VMFindBestAction: a Sim walking along the street chooses nothing of its own (never the family's things) - only
        /// while still a visitor passing by (a stray the family adopted on the way is theirs from then on).
        /// </summary>
        public static bool NoFreeWill(VMAvatar ava)
        {
            return ava != null && IsStreet(ava) && ava.GetPersonData(VMPersonDataVariable.PersonType) == 1;
        }

        public static Kind KindOf(VMEntity e)
        {
            if (!(e is VMAvatar) || e.Dead) return Kind.None;
            var k = e.GetAttribute(StreetAttr);
            return (k > 0 && k <= MaxKind) ? (Kind)k : Kind.None;
        }

        /// <summary>A tile packed into one attribute (lots are under 128 tiles across).</summary>
        public static short Pack(int x, int y)
        {
            return (short)((x & 0x7F) | ((y & 0x7F) << 8));
        }

        public static void Unpack(short v, out short x, out short y)
        {
            x = (short)(v & 0x7F);
            y = (short)((v >> 8) & 0x7F);
        }

        /// <summary>The tile a street Sim is walking to (false: not a street Sim).</summary>
        public static bool EndOf(VMEntity e, out short x, out short y)
        {
            x = 0; y = 0;
            if (!IsStreet(e)) return false;
            Unpack(e.GetAttribute(EndAttr), out x, out y);
            return true;
        }

        /// <summary>
        /// The sidewalks along the lot's street edge: for each street on the lot (5+ tiles of road side by side, the way
        /// ReduxTraffic finds them), the first line of tiles next to the road on the lot's side that isn't road or water,
        /// from near one end of the lot to near the other. Empty: no street (some community lots) - no street life there.
        /// Worked out once per lot from its floor (the street can't be built on).
        /// </summary>
        public static List<Walk> Sidewalks(VMContext ctx)
        {
            var arch = ctx?.Architecture;
            if (arch == null) return new List<Walk>();
            List<Walk> cached;
            if (Cache.TryGetValue(arch, out cached)) return cached;
            var result = new List<Walk>();
            try { Find(ctx, arch, result); }
            catch (Exception e) { Console.WriteLine("[SimsRedux] sidewalks: " + e.Message); }
            try { Cache.Add(arch, result); } catch (ArgumentException) { }
            return result;
        }

        private static void Find(VMContext ctx, VMArchitecture arch, List<Walk> result)
        {
            int w = arch.Width, ht = arch.Height;
            var floors = arch.Floors[0];
            Func<int, int, int> pattern = (x, y) => (x < 0 || y < 0 || x >= w || y >= ht) ? -1 : floors[y * w + x].Pattern;
            Func<int, int, bool> isRoad = (x, y) => { var p = pattern(x, y); return p == 9 || p == 10 || p == 11; };
            Func<int, int, bool> walkable = (x, y) =>
            {
                var p = pattern(x, y);
                return p >= 0 && p != 9 && p != 10 && p != 11 && p != 65534 && p != 65535; // (not road, water or pool)
            };
            for (int axis = 0; axis < 2; axis++)
            {
                bool alongX = axis == 0;
                int lines = alongX ? ht : w, len = alongX ? w : ht;
                Func<int, int, int> tx = (line, along) => alongX ? along : line;
                Func<int, int, int> ty = (line, along) => alongX ? line : along;
                int runStart = -1;
                for (int l = 0; l <= lines; l++)
                {
                    int count = 0;
                    if (l < lines) for (int i = 0; i < len; i++) if (isRoad(tx(l, i), ty(l, i))) count++;
                    bool road = count >= Math.Max(12, len / 3);
                    if (road && runStart < 0) runStart = l;
                    if (road || runStart < 0) continue;
                    int first = runStart, last = l - 1;
                    runStart = -1;
                    if (last - first + 1 < 5) continue;
                    int mid = (first + last) / 2;
                    int s = int.MaxValue, e = -1;
                    for (int i = 0; i < len; i++) if (isRoad(tx(mid, i), ty(mid, i))) { s = Math.Min(s, i); e = i; }
                    if (e - s <= 10) continue;
                    int from = Math.Max(s, 2), to = Math.Min(e, len - 3);
                    if (to - from < 10) continue;

                    // the lot's side of the road: the one with the buildable lot beyond it
                    Walk best = null;
                    int bestLot = -1;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        int edge = side < 0 ? first : last;
                        Walk found = null;
                        for (int d = 1; d <= 2 && found == null; d++)
                        {
                            int line = edge + side * d;
                            if (line < 1 || line > lines - 2) break;
                            int ok = 0;
                            for (int i = from; i <= to; i++) if (walkable(tx(line, i), ty(line, i))) ok++;
                            if (ok * 10 >= (to - from + 1) * 8) found = new Walk { AlongX = alongX, Line = line, From = from, To = to };
                        }
                        if (found == null) continue;
                        int lot = 0;
                        for (int d = 1; d <= 10; d++)
                        {
                            int line = found.Line + side * d;
                            if (line < 0 || line >= lines) break;
                            for (int i = from; i <= to; i += 2)
                                if (!ctx.IsUserOutOfBounds(LotTilePos.FromBigTile((short)tx(line, i), (short)ty(line, i), 1))) lot++;
                        }
                        if (lot > bestLot) { bestLot = lot; best = found; }
                    }
                    if (best != null) result.Add(best);
                }
            }
        }

        /// <summary>The street Sim's own nearest sidewalk (for one already out there whose end was lost) - null: none.</summary>
        public static Walk Nearest(VMContext ctx, LotTilePos pos)
        {
            Walk best = null;
            int bestD = int.MaxValue;
            foreach (var wk in Sidewalks(ctx))
            {
                int d = Math.Abs((wk.AlongX ? pos.TileY : pos.TileX) - wk.Line);
                if (d < bestD) { bestD = d; best = wk; }
            }
            return best;
        }
    }
}
