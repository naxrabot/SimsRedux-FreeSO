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

        /// <summary>The Go Here object's own "Go Here" tree (Destination.iff BHAV 0x1000).</summary>
        private const ushort GotoActionTree = 0x1000;

        /// <summary>
        /// 19.7 fix: the TTA index of the Go Here object's "Go Here" interaction - what the pie menu sends (its interactions are
        /// numbered 2 to 7 in Sims 1's Destination.iff: 2 "Go Here", 3 "Move", 4 "Go Here Frantically"...; there is no 0, so
        /// asking for interaction 0 got nothing and every street Sim was taken off again the moment it was made). Found by the
        /// English name (tagged strings only) or the action tree - the object's own data, the same on every game. -1: none.
        /// </summary>
        public static int GotoInteraction(VMEntity go)
        {
            var ttab = go?.TreeTable;
            if (ttab?.Interactions == null) return -1;
            var ttas = go.TreeTableStrings;
            foreach (var ia in ttab.Interactions)
            {
                if (ia == null || ttas == null) continue;
                var n = ttas.TaggedEnglish((int)ia.TTAIndex);
                if (n != null && n.Trim().Equals("Go Here", StringComparison.OrdinalIgnoreCase)) return (int)ia.TTAIndex;
            }
            foreach (var ia in ttab.Interactions)
                if (ia != null && ia.ActionFunction == GotoActionTree) return (int)ia.TTAIndex;
            return -1;
        }

        // [swtest-begin] (19.10: this part is copied as-is into the scratchpad harness that checks it on Sims 1's house files)
        /// <summary>One sidewalk: the line of tiles (x = Line when not AlongX, y = Line when AlongX) from From to To along it.</summary>
        public class Walk
        {
            public bool AlongX;
            public int Line;
            public int From, To;
            /// <summary>19.10: the street's lane next to this sidewalk (a line of road tiles), and the way from it to the lot
            /// (+1 / -1 across the lines): the sidewalk is on the lot's own side of the street, never across it.</summary>
            public int RoadEdge, Side;

            public short X(int along) { return (short)(AlongX ? along : Line); }
            public short Y(int along) { return (short)(AlongX ? Line : along); }
            public int Min { get { return Math.Min(From, To); } }
            public int Max { get { return Math.Max(From, To); } }
            /// <summary>How far along this sidewalk a tile is, and how far off its line.</summary>
            public int Along(int x, int y) { return AlongX ? x : y; }
            public int Off(int x, int y) { return (AlongX ? y : x) - Line; }
            /// <summary>On the lot's side of the street (across the lines from the road towards the lot).</summary>
            public bool LotSide(int x, int y) { return ((AlongX ? y : x) - RoadEdge) * Side > 0; }
            public override string ToString() { return (AlongX ? "y=" : "x=") + Line + " " + From + ".." + To + " (road " + RoadEdge + ", lot " + (Side > 0 ? "+" : "-") + ")"; }
        }

        /// <summary>Sims 1's sidewalk floor (the grey strip along the street on every lot).</summary>
        public const int SidewalkPattern = 12;

        /// <summary>
        /// 19.10: the sidewalks of a lot from its floor alone (the same on every game). For each street (5+ lines of road
        /// side by side, as ReduxTraffic finds them): the lot's own side of it - the side with the lot's buildable land beyond
        /// it (19.9 and before took the side with "more lot", counting only 10 lines and the land past the edge of a smaller
        /// lot); then on that side the line of Sims 1's sidewalk floor between the road and the lot (or, without one, the line
        /// right next to the road), never road (9-11), water or a pool. From and To are its two ends, at the lot's edges.
        /// </summary>
        public static void FindWalks(int w, int ht, Func<int, int, int> pattern, Func<int, int, bool> buildable, List<Walk> result)
        {
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

                    // the lot's side of the street: the one with the lot's buildable land beyond it (all of it, to the lot's edge)
                    int[] land = new int[2];
                    for (int k = 0; k < 2; k++)
                    {
                        int side = k == 0 ? -1 : 1, edge = side < 0 ? first : last;
                        for (int line = edge + side; line >= 0 && line < lines; line += side)
                            for (int i = from; i <= to; i++)
                                if (buildable(tx(line, i), ty(line, i))) land[k]++;
                    }
                    if (land[0] == 0 && land[1] == 0) continue; // (no lot along this street)
                    int lotSide = land[1] > land[0] ? 1 : -1;
                    int roadEdge = lotSide < 0 ? first : last;

                    // the sidewalk on that side: the line (up to 4 from the road, before the lot itself) with the most sidewalk
                    // floor - more than half its length; otherwise the line right next to the road
                    int range = to - from + 1, bestLine = -1, bestWalk = -1;
                    for (int d = 1; d <= 4; d++)
                    {
                        int line = roadEdge + lotSide * d;
                        if (line < 1 || line > lines - 2) break;
                        int walk = 0, ok = 0, lot = 0;
                        for (int i = from; i <= to; i++)
                        {
                            int x = tx(line, i), y = ty(line, i);
                            if (walkable(x, y)) ok++;
                            if (pattern(x, y) == SidewalkPattern) walk++;
                            if (buildable(x, y)) lot++;
                        }
                        if (d > 1 && lot * 2 > range) break; // (the lot itself from here)
                        if (walk * 2 > range && walk > bestWalk && ok * 10 >= range * 8) { bestWalk = walk; bestLine = line; }
                    }
                    if (bestLine < 0)
                    {
                        int line = roadEdge + lotSide;
                        if (line < 1 || line > lines - 2) continue;
                        int ok = 0;
                        for (int i = from; i <= to; i++) if (walkable(tx(line, i), ty(line, i))) ok++;
                        if (ok * 10 < range * 8) continue;
                        bestLine = line;
                    }
                    // its ends: the first and last tiles along it that can be walked on
                    int a = from, b = to;
                    while (a < b && !walkable(tx(bestLine, a), ty(bestLine, a))) a++;
                    while (b > a && !walkable(tx(bestLine, b), ty(bestLine, b))) b--;
                    if (b - a < 10) continue;
                    result.Add(new Walk { AlongX = alongX, Line = bestLine, From = a, To = b, RoadEdge = roadEdge, Side = lotSide });
                }
            }
        }

        /// <summary>
        /// 19.10: the stops of a walk along a sidewalk, from along-position cur to end: a tile every few steps, so the Sim walks
        /// along the sidewalk in short straight goes (a Go Here each) - one long Go Here let the routing cut through the yard
        /// or behind the house. offLine: the Sim isn't on the sidewalk's line now - the first stop brings them back to it.
        /// </summary>
        public static List<int> Hops(int cur, int end, bool offLine)
        {
            var res = new List<int>();
            if (offLine) res.Add(cur);
            int dist = Math.Abs(end - cur);
            if (dist == 0) return res;
            int hop = Math.Max(HopTiles, (dist + MaxHops - 1) / MaxHops);
            int dir = Math.Sign(end - cur);
            for (int a = cur + dir * hop; dir > 0 ? a < end - 1 : a > end + 1; a += dir * hop) res.Add(a); // (no stop a tile short of the end)
            res.Add(end);
            return res;
        }

        /// <summary>Tiles between the stops of a walk along the sidewalk, and the most stops (longer sidewalks: longer goes).</summary>
        public const int HopTiles = 6, MaxHops = 10;
        // [swtest-end]

        /// <summary>A tile a street Sim may stand on: on the lot, not road, water or a pool (the same on every game).</summary>
        public static bool IsWalkTile(VMContext ctx, int x, int y)
        {
            var arch = ctx?.Architecture;
            if (arch == null || x < 0 || y < 0 || x >= arch.Width || y >= arch.Height) return false;
            var p = arch.Floors[0][y * arch.Width + x].Pattern;
            return p != 9 && p != 10 && p != 11 && p != 65534 && p != 65535;
        }

        /// <summary>19.10: the sidewalk a tile is on (its line, between its ends give or take two) - null: none.</summary>
        public static Walk WalkAt(VMContext ctx, int x, int y)
        {
            foreach (var wk in Sidewalks(ctx))
            {
                if (wk.Off(x, y) != 0) continue;
                int a = wk.Along(x, y);
                if (a >= wk.Min - 2 && a <= wk.Max + 2) return wk;
            }
            return null;
        }

        /// <summary>
        /// 19.10: a tile next to a sidewalk, on the lot's side of the street and not on the road (or water) - where a street Sim
        /// may be put when the sidewalk itself has no room (by the street portal): within two lines of the sidewalk.
        /// </summary>
        public static bool NearSidewalk(VMContext ctx, Walk wk, int x, int y)
        {
            if (wk == null || !IsWalkTile(ctx, x, y) || !wk.LotSide(x, y) || Math.Abs(wk.Off(x, y)) > 2) return false;
            int a = wk.Along(x, y);
            return a >= wk.Min - 2 && a <= wk.Max + 2;
        }

        /// <summary>19.10: the end of the sidewalk nearer this tile (along-position).</summary>
        public static int NearerEnd(Walk wk, int x, int y)
        {
            int a = wk.Along(x, y);
            return Math.Abs(a - wk.From) <= Math.Abs(a - wk.To) ? wk.From : wk.To;
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
            // (19.6 fix: an empty answer isn't kept - asked before the lot's floor was in, it stayed "no street" for good)
            if (result.Count > 0)
            {
                try { Cache.Add(arch, result); } catch (ArgumentException) { }
            }
            return result;
        }

        /// <summary>19.6: the game log's street-life line - how many sidewalks, and their tiles.</summary>
        public static string Describe(VMContext ctx)
        {
            var walks = Sidewalks(ctx);
            if (walks.Count == 0) return "no sidewalk (no street on this lot)";
            int tiles = 0;
            foreach (var wk in walks) tiles += Math.Abs(wk.To - wk.From) + 1;
            return walks.Count + " sidewalk" + (walks.Count == 1 ? "" : "s") + ", " + tiles + " tiles";
        }

        private static void Find(VMContext ctx, VMArchitecture arch, List<Walk> result)
        {
            int w = arch.Width, ht = arch.Height;
            var floors = arch.Floors[0];
            Func<int, int, int> pattern = (x, y) => (x < 0 || y < 0 || x >= w || y >= ht) ? -1 : floors[y * w + x].Pattern;
            // (the lot's buildable land - the property line - as build mode sees it)
            Func<int, int, bool> buildable = (x, y) => x >= 0 && y >= 0 && x < w && y < ht
                && !ctx.IsUserOutOfBounds(LotTilePos.FromBigTile((short)x, (short)y, 1));
            FindWalks(w, ht, pattern, buildable, result);
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
