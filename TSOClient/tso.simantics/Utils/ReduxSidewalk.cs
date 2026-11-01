using FSO.LotView.Model;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.Routing;
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
            /// <summary>20.1: this end of the sidewalk runs off the edge of the lot (the street goes on off-screen there) - the only
            /// places street people come from and go to. An end that isn't open stops inside the lot: at a corner (where the
            /// sidewalk of the other street takes over) or a trimmed end - never a way on or off the lot.</summary>
            public bool OpenFrom, OpenTo;
            /// <summary>20.1: a line of Sims 1's sidewalk floor (the usual) - its ends and every stop on it are sidewalk tiles,
            /// never the grass strip by the kerb. False: a lot without one - the line right by the road.</summary>
            public bool Paved;

            public short X(int along) { return (short)(AlongX ? along : Line); }
            public short Y(int along) { return (short)(AlongX ? Line : along); }
            public int Min { get { return Math.Min(From, To); } }
            public int Max { get { return Math.Max(From, To); } }
            /// <summary>How far along this sidewalk a tile is, and how far off its line.</summary>
            public int Along(int x, int y) { return AlongX ? x : y; }
            public int Off(int x, int y) { return (AlongX ? y : x) - Line; }
            /// <summary>On the lot's side of the street (across the lines from the road towards the lot).</summary>
            public bool LotSide(int x, int y) { return ((AlongX ? y : x) - RoadEdge) * Side > 0; }
            public override string ToString() { return (AlongX ? "y=" : "x=") + Line + " " + From + (OpenFrom ? "(edge)" : "") + ".." + To + (OpenTo ? "(edge)" : "") + " (road " + RoadEdge + ", lot " + (Side > 0 ? "+" : "-") + ")"; }
        }

        /// <summary>20.1: one straight part of a way along the sidewalks: along sidewalk W from along-position From to To.</summary>
        public class Leg
        {
            public Walk W;
            public int From, To;
            public int Length { get { return Math.Abs(To - From); } }
        }

        /// <summary>
        /// 20.1: a way along the lot's sidewalks from one open end (the edge of the lot) to another: along one street, or on a
        /// corner lot along the first street to the corner, round it and along the other street (the near sidewalks of the
        /// streets are one path). Every street Sim walks one of these, start to end - they only ever appear and leave at the
        /// edge of the lot.
        /// </summary>
        public class Route
        {
            public List<Leg> Legs = new List<Leg>();
            public short StartX { get { return Legs[0].W.X(Legs[0].From); } }
            public short StartY { get { return Legs[0].W.Y(Legs[0].From); } }
            public short EndX { get { var l = Legs[Legs.Count - 1]; return l.W.X(l.To); } }
            public short EndY { get { var l = Legs[Legs.Count - 1]; return l.W.Y(l.To); } }
            public int Length { get { int n = 0; foreach (var l in Legs) n += l.Length; return n; } }
            public Route Reversed()
            {
                var r = new Route();
                for (int i = Legs.Count - 1; i >= 0; i--) r.Legs.Add(new Leg { W = Legs[i].W, From = Legs[i].To, To = Legs[i].From });
                return r;
            }
            public override string ToString()
            {
                var s = StartX + "," + StartY;
                foreach (var l in Legs) s += " -> " + l.W.X(l.To) + "," + l.W.Y(l.To);
                return s + " (" + Length + " tiles)";
            }
        }

        /// <summary>Sims 1's sidewalk floor (the grey strip along the street on every lot).</summary>
        public const int SidewalkPattern = 12;

        private class Band { public bool AlongX; public int First, Last, S, E; }

        /// <summary>
        /// 19.10: the sidewalks of a lot from its floor alone (the same on every game). For each street (5+ lines of road
        /// side by side, as ReduxTraffic finds them): the lot's own side of it - the side with the lot's buildable land beyond
        /// it (19.9 and before took the side with "more lot", counting only 10 lines and the land past the edge of a smaller
        /// lot); then on that side the line of Sims 1's sidewalk floor between the road and the lot (or, without one, the line
        /// right next to the road), never road (9-11), water or a pool. From and To are its two ends.
        /// 20.1: a sidewalk stops where it meets another street (the crossing is never part of it - a sidewalk cut by a
        /// crossing street is two sidewalks), the ends that run off the edge of the lot reach the lot's outermost row (the
        /// Sims 1 street portals' row), and routes (the ways from edge to edge along them, round the corners) come with them.
        /// </summary>
        public static void FindWalks(int w, int ht, Func<int, int, int> pattern, Func<int, int, bool> buildable, List<Walk> result, List<Route> routes = null)
        {
            Func<int, int, bool> isRoad = (x, y) => { var p = pattern(x, y); return p == 9 || p == 10 || p == 11; };
            Func<int, int, bool> walkable = (x, y) =>
            {
                var p = pattern(x, y);
                return p >= 0 && p != 9 && p != 10 && p != 11 && p != 65534 && p != 65535; // (not road, water or pool)
            };
            // the streets first (both ways), so a sidewalk knows where the other streets cross it
            var bands = new List<Band>();
            for (int axis = 0; axis < 2; axis++)
            {
                bool alongX = axis == 0;
                int lines = alongX ? ht : w, len = alongX ? w : ht;
                int runStart = -1;
                for (int l = 0; l <= lines; l++)
                {
                    int count = 0;
                    if (l < lines) for (int i = 0; i < len; i++) if (isRoad(alongX ? i : l, alongX ? l : i)) count++;
                    bool road = count >= Math.Max(12, len / 3);
                    if (road && runStart < 0) runStart = l;
                    if (road || runStart < 0) continue;
                    int first = runStart, last = l - 1;
                    runStart = -1;
                    // (20.1: a street at the edge of the lot can be cut down to 3 or 4 lines by it - it's still a street)
                    if (last - first + 1 < 5 && !(last - first + 1 >= 3 && (first <= 1 || last >= lines - 2))) continue;
                    int mid = (first + last) / 2;
                    int s = int.MaxValue, e = -1;
                    for (int i = 0; i < len; i++) if (isRoad(alongX ? i : mid, alongX ? mid : i)) { s = Math.Min(s, i); e = i; }
                    if (e - s <= 10) continue;
                    // (20.1: a street is road end to end - not a patch of road painted in the yard next to the street, which
                    //  can make the lines' counts look like one)
                    int onMid = 0;
                    for (int i = s; i <= e; i++) if (isRoad(alongX ? i : mid, alongX ? mid : i)) onMid++;
                    if (onMid * 10 < (e - s + 1) * 9) continue;
                    bands.Add(new Band { AlongX = alongX, First = first, Last = last, S = s, E = e });
                }
            }
            // a tile of a street's road (not a driveway, which a sidewalk crosses)
            Func<int, int, bool> street = (x, y) =>
            {
                if (!isRoad(x, y)) return false;
                foreach (var bd in bands)
                {
                    int ln = bd.AlongX ? y : x, al = bd.AlongX ? x : y;
                    if (ln >= bd.First && ln <= bd.Last && al >= bd.S && al <= bd.E) return true;
                }
                return false;
            };

            // (20.1: the lot's buildable land, as a box - a sidewalk is always between its street and the middle of the lot; a
            //  line along a patch of road painted inside the lot isn't one)
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            for (int y = 0; y < ht; y++)
                for (int x = 0; x < w; x++)
                    if (buildable(x, y)) { bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x); by1 = Math.Max(by1, y); }
            foreach (var band in bands)
            {
                bool alongX = band.AlongX;
                int lines = alongX ? ht : w, len = alongX ? w : ht;
                Func<int, int, int> tx = (line, along) => alongX ? along : line;
                Func<int, int, int> ty = (line, along) => alongX ? line : along;
                int first = band.First, last = band.Last, s = band.S, e = band.E;
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
                int lotMid = alongX ? (by0 + by1) / 2 : (bx0 + bx1) / 2;
                if (bx1 >= 0 && (lotMid - roadEdge) * lotSide <= 0) continue;

                // the sidewalk on that side: the line (up to 4 from the road, before the lot itself) with the most sidewalk
                // floor - more than half its length; otherwise the line right next to the road (20.1: where another street
                // crosses the line doesn't count either way)
                int bestLine = -1, bestWalk = -1;
                for (int d = 1; d <= 4; d++)
                {
                    int line = roadEdge + lotSide * d;
                    if (line < 1 || line > lines - 2) break;
                    int walk = 0, ok = 0, lot = 0, range = 0;
                    for (int i = from; i <= to; i++)
                    {
                        int x = tx(line, i), y = ty(line, i);
                        if (street(x, y)) continue;
                        range++;
                        if (walkable(x, y)) ok++;
                        if (pattern(x, y) == SidewalkPattern) walk++;
                        if (buildable(x, y)) lot++;
                    }
                    if (range < 11) continue;
                    if (d > 1 && lot * 2 > range) break; // (the lot itself from here)
                    if (walk * 2 > range && walk > bestWalk && ok * 10 >= range * 8) { bestWalk = walk; bestLine = line; }
                }
                if (bestLine < 0)
                {
                    int line = roadEdge + lotSide;
                    if (line < 1 || line > lines - 2) continue;
                    int ok = 0, range = 0;
                    for (int i = from; i <= to; i++)
                    {
                        if (street(tx(line, i), ty(line, i))) continue;
                        range++;
                        if (walkable(tx(line, i), ty(line, i))) ok++;
                    }
                    if (range < 11 || ok * 10 < range * 8) continue;
                    bestLine = line;
                }
                // 20.1: the parts of that line between the streets crossing it (each its own sidewalk); each part's ends: the
                // first and last tiles along it that can be walked on. An end at the edge of the lot is open, and reaches
                // the outermost row a Sim can stand on (1 / len - 2 - where Sims 1's own street portals are).
                int p0 = from;
                while (p0 <= to)
                {
                    while (p0 <= to && street(tx(bestLine, p0), ty(bestLine, p0))) p0++;
                    if (p0 > to) break;
                    int p1 = p0;
                    while (p1 + 1 <= to && !street(tx(bestLine, p1 + 1), ty(bestLine, p1 + 1))) p1++;
                    // (20.1: on a sidewalk-floor line, its ends are sidewalk tiles - never the grass by the kerb or the road's edge)
                    bool paved = bestWalk >= 0;
                    int ln = bestLine;
                    Func<int, bool> end = i => paved ? pattern(tx(ln, i), ty(ln, i)) == SidewalkPattern : walkable(tx(ln, i), ty(ln, i));
                    int a = p0, b = p1;
                    while (a < b && !end(a)) a++;
                    while (b > a && !end(b)) b--;
                    bool openA = p0 == from && a <= 3, openB = p1 == to && b >= len - 4;
                    if (openA) while (a > 1 && end(a - 1) && !street(tx(ln, a - 1), ty(ln, a - 1))) a--;
                    if (openB) while (b < len - 2 && end(b + 1) && !street(tx(ln, b + 1), ty(ln, b + 1))) b++;
                    if (b - a >= 10)
                        result.Add(new Walk { AlongX = alongX, Line = bestLine, From = a, To = b, RoadEdge = roadEdge, Side = lotSide, OpenFrom = openA, OpenTo = openB, Paved = paved });
                    p0 = p1 + 1;
                }
            }
            if (routes != null) FindRoutes(result, (x, y) => walkable(x, y) && !street(x, y), routes, (x, y) => pattern(x, y) == SidewalkPattern);
        }

        /// <summary>
        /// 20.1: the ways along the sidewalks from one open end to another (Route). The sidewalks of two streets meet at a corner
        /// - the tile on both their lines (within 2 of each one's ends, and walkable, not road): a way goes along one to the
        /// corner and on along the other. Each pair of open ends gets the shortest way between them (along one street, round
        /// one corner, or round two on a lot with streets on three sides). A sidewalk with no open end is only ever walked
        /// between two corners on the way; one whose only way out is a trimmed end isn't walked at all.
        /// </summary>
        public static void FindRoutes(List<Walk> walks, Func<int, int, bool> okTile, List<Route> routes, Func<int, int, bool> paved)
        {
            var nodeX = new List<int>();
            var nodeY = new List<int>();
            var exits = new List<int>();
            // per sidewalk: its stops (along-position, node)
            var stops = new List<List<KeyValuePair<int, int>>>();
            Func<int, int, int> node = (x, y) =>
            {
                for (int i = 0; i < nodeX.Count; i++) if (nodeX[i] == x && nodeY[i] == y) return i;
                nodeX.Add(x); nodeY.Add(y);
                return nodeX.Count - 1;
            };
            foreach (var wk in walks)
            {
                var st = new List<KeyValuePair<int, int>>();
                if (wk.OpenFrom) { int n = node(wk.X(wk.From), wk.Y(wk.From)); st.Add(new KeyValuePair<int, int>(wk.From, n)); if (!exits.Contains(n)) exits.Add(n); }
                if (wk.OpenTo) { int n = node(wk.X(wk.To), wk.Y(wk.To)); st.Add(new KeyValuePair<int, int>(wk.To, n)); if (!exits.Contains(n)) exits.Add(n); }
                stops.Add(st);
            }
            // the corners
            for (int i = 0; i < walks.Count; i++)
                for (int j = i + 1; j < walks.Count; j++)
                {
                    var a = walks[i]; var b = walks[j];
                    if (a.AlongX == b.AlongX) continue;
                    int cx = a.AlongX ? b.Line : a.Line, cy = a.AlongX ? a.Line : b.Line;
                    int aa = a.Along(cx, cy), ba = b.Along(cx, cy);
                    if (aa < a.Min - 2 || aa > a.Max + 2 || ba < b.Min - 2 || ba > b.Max + 2) continue;
                    if (!okTile(cx, cy)) continue;
                    if (a.Paved && b.Paved && !paved(cx, cy)) continue; // (two sidewalks meet on a sidewalk tile)
                    int n = node(cx, cy);
                    stops[i].Add(new KeyValuePair<int, int>(aa, n));
                    stops[j].Add(new KeyValuePair<int, int>(ba, n));
                }
            // the links between stops next to each other along a sidewalk
            int nodes = nodeX.Count;
            var linkTo = new List<int>[nodes]; var linkLen = new List<int>[nodes]; var linkLeg = new List<Leg>[nodes];
            for (int i = 0; i < nodes; i++) { linkTo[i] = new List<int>(); linkLen[i] = new List<int>(); linkLeg[i] = new List<Leg>(); }
            for (int i = 0; i < walks.Count; i++)
            {
                var st = stops[i];
                st.Sort((p, q) => p.Key != q.Key ? p.Key.CompareTo(q.Key) : p.Value.CompareTo(q.Value));
                for (int k = 0; k + 1 < st.Count; k++)
                {
                    int u = st[k].Value, v = st[k + 1].Value, d = st[k + 1].Key - st[k].Key;
                    if (u == v || d <= 0) continue;
                    linkTo[u].Add(v); linkLen[u].Add(d); linkLeg[u].Add(new Leg { W = walks[i], From = st[k].Key, To = st[k + 1].Key });
                    linkTo[v].Add(u); linkLen[v].Add(d); linkLeg[v].Add(new Leg { W = walks[i], From = st[k + 1].Key, To = st[k].Key });
                }
            }
            // the shortest way between each pair of open ends (the same on every game: lists, in order)
            exits.Sort();
            foreach (var start in exits)
            {
                var dist = new int[nodes]; var prevLeg = new Leg[nodes]; var prevNode = new int[nodes]; var done = new bool[nodes];
                for (int i = 0; i < nodes; i++) { dist[i] = int.MaxValue; prevNode[i] = -1; }
                dist[start] = 0;
                while (true)
                {
                    int u = -1;
                    for (int i = 0; i < nodes; i++) if (!done[i] && dist[i] != int.MaxValue && (u < 0 || dist[i] < dist[u])) u = i;
                    if (u < 0) break;
                    done[u] = true;
                    for (int k = 0; k < linkTo[u].Count; k++)
                    {
                        int v = linkTo[u][k], nd = dist[u] + linkLen[u][k];
                        if (nd < dist[v]) { dist[v] = nd; prevNode[v] = u; prevLeg[v] = linkLeg[u][k]; }
                    }
                }
                foreach (var end in exits)
                {
                    if (end <= start || dist[end] == int.MaxValue) continue;
                    var legs = new List<Leg>();
                    for (int v = end; v != start; v = prevNode[v]) legs.Insert(0, prevLeg[v]);
                    var r = new Route();
                    foreach (var l in legs)
                    {
                        var lastLeg = r.Legs.Count > 0 ? r.Legs[r.Legs.Count - 1] : null;
                        if (lastLeg != null && lastLeg.W == l.W && lastLeg.To == l.From && Math.Sign(lastLeg.To - lastLeg.From) == Math.Sign(l.To - l.From)) lastLeg.To = l.To;
                        else r.Legs.Add(new Leg { W = l.W, From = l.From, To = l.To });
                    }
                    if (r.Legs.Count > 0 && r.Length >= 10) routes.Add(r);
                }
            }
        }

        /// <summary>
        /// 20.1: the rest of the way for a street Sim at tile x,y walking to tile ex,ey (an open end): the route ending there that
        /// passes nearest them, from the point on it next to them on. anyExit (or no route ends at ex,ey): the shortest way
        /// to whichever open end is nearest along the sidewalks. toX/toY: where it ends. Null: no route at all.
        /// </summary>
        public static List<Leg> Plan(List<Route> routes, int x, int y, int ex, int ey, bool anyExit, out short toX, out short toY)
        {
            toX = 0; toY = 0;
            List<Leg> best = null;
            int bestOff = int.MaxValue, bestRest = int.MaxValue;
            for (int c = 0; c < routes.Count * 2; c++)
            {
                var r = (c & 1) == 0 ? routes[c / 2] : routes[c / 2].Reversed();
                if (!anyExit && (r.EndX != ex || r.EndY != ey)) continue;
                int off = int.MaxValue, k0 = -1, at = 0;
                for (int k = 0; k < r.Legs.Count; k++)
                {
                    var l = r.Legs[k];
                    int p = Math.Max(Math.Min(l.From, l.To), Math.Min(Math.Max(l.From, l.To), l.W.Along(x, y)));
                    int d = Math.Max(Math.Abs(l.W.X(p) - x), Math.Abs(l.W.Y(p) - y));
                    if (d <= off) { off = d; k0 = k; at = p; } // (at a corner: the leg after it)
                }
                if (k0 < 0) continue;
                int rest = Math.Abs(r.Legs[k0].To - at);
                for (int k = k0 + 1; k < r.Legs.Count; k++) rest += r.Legs[k].Length;
                if (off < bestOff || (off == bestOff && rest < bestRest))
                {
                    bestOff = off; bestRest = rest;
                    best = new List<Leg>();
                    if (at != r.Legs[k0].To) best.Add(new Leg { W = r.Legs[k0].W, From = at, To = r.Legs[k0].To });
                    for (int k = k0 + 1; k < r.Legs.Count; k++) best.Add(r.Legs[k]);
                    if (best.Count == 0) best.Add(new Leg { W = r.Legs[k0].W, From = at, To = at });
                    toX = r.EndX; toY = r.EndY;
                }
            }
            if (best == null && !anyExit) return Plan(routes, x, y, ex, ey, true, out toX, out toY);
            return best;
        }

        /// <summary>20.1: the route from tile fx,fy (on its first leg, give or take two) to the open end tx,ty - null: none.</summary>
        public static Route RouteFor(List<Route> routes, int fx, int fy, int tx, int ty)
        {
            for (int c = 0; c < routes.Count * 2; c++)
            {
                var r = (c & 1) == 0 ? routes[c / 2] : routes[c / 2].Reversed();
                if (r.EndX != tx || r.EndY != ty) continue;
                var l = r.Legs[0];
                if (l.W.Off(fx, fy) != 0) continue;
                int a = l.W.Along(fx, fy);
                if (a >= Math.Min(l.From, l.To) - 2 && a <= Math.Max(l.From, l.To) + 2) return r;
            }
            return null;
        }

        /// <summary>
        /// 19.10: the stops of a walk along a sidewalk, from along-position cur to end: a tile every few steps, so the Sim walks
        /// along the sidewalk in short straight goes (a Go Here each) - one long Go Here let the routing cut through the yard
        /// or behind the house. offLine: the Sim isn't on the sidewalk's line now - the first stop brings them back to it.
        /// 20.1: always HopTiles apart (the last one up to a tile more) - Sims 1's Go Here makes a Sim run to a spot 8 or more
        /// tiles away, so longer goes had passers-by sprinting.
        /// </summary>
        public static List<int> Hops(int cur, int end, bool offLine)
        {
            var res = new List<int>();
            if (offLine) res.Add(cur);
            int dist = Math.Abs(end - cur);
            if (dist == 0) return res;
            int dir = Math.Sign(end - cur);
            for (int a = cur + dir * HopTiles; dir > 0 ? a < end - 1 : a > end + 1; a += dir * HopTiles) res.Add(a); // (no stop a tile short of the end)
            res.Add(end);
            return res;
        }

        /// <summary>Tiles between the stops of a walk along the sidewalk (20.1: 5 - with a stop moved 2 for a taken tile, still
        /// under the 8 tiles at which Sims 1's Go Here makes a Sim run).</summary>
        public const int HopTiles = 5;
        /// <summary>Sims 1's Go Here (Destination.iff tree 0x1000): a spot this many tiles away or more and the Sim runs.</summary>
        public const int RunTiles = 8;
        // [swtest-end]

        /// <summary>A tile a street Sim may stand on: on the lot, not road, water or a pool (the same on every game).</summary>
        public static bool IsWalkTile(VMContext ctx, int x, int y)
        {
            var arch = ctx?.Architecture;
            if (arch == null || x < 0 || y < 0 || x >= arch.Width || y >= arch.Height) return false;
            var p = arch.Floors[0][y * arch.Width + x].Pattern;
            return p != 9 && p != 10 && p != 11 && p != 65534 && p != 65535;
        }

        /// <summary>20.1: a tile a street Sim may stand on along sidewalk wk: a walk tile, and on a sidewalk-floor line (Paved) a
        /// sidewalk tile - never the grass strip by the kerb (the same on every game).</summary>
        public static bool IsStopTile(VMContext ctx, Walk wk, int x, int y)
        {
            if (!IsWalkTile(ctx, x, y)) return false;
            if (wk == null || !wk.Paved) return true;
            var arch = ctx.Architecture;
            return arch.Floors[0][y * arch.Width + x].Pattern == SidewalkPattern;
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

        /// <summary>A lot's sidewalks and the routes along them (20.1: worked out together, kept together). 20.2: and the tiles
        /// street Sims may route on (worked out the first time a street Sim routes).</summary>
        private class Found { public List<Walk> Walks = new List<Walk>(); public List<Route> Routes = new List<Route>(); public bool[] Tiles; }

        // ------------------------------------------------------------------
        // 20.2: street Sims walk, and only on the sidewalk - decided in the router itself (VMRoutingFrame), from the lot's
        // floor and the Sim's street mark only (the same on every game)
        // ------------------------------------------------------------------

        /// <summary>
        /// 20.2: the walk style the router really uses for this Sim (VMRoutingFrame.WalkStyle, VMAnimateSim): a Sim out on the
        /// street never runs (1) - whatever Sims 1's Go Here (Destination.iff tree 0x1000 sets "run" for a far spot) or anything
        /// else put in its walk style - except joggers, and a dog (it trots to keep up with its walker).
        /// </summary>
        public static short WalkStyleOf(VMEntity ent, short style)
        {
            if (style != 1) return style;
            var ava = ent as VMAvatar;
            if (ava == null || ava.Dead || ava.IsPet) return style;
            var k = ava.GetAttribute(StreetAttr);
            return (k > 0 && k != (short)Kind.Jogger) ? (short)0 : style;
        }

        /// <summary>
        /// 20.2: the tiles of the lot a street Sim may route on (index y * width + x): every tile along the legs of the lot's
        /// ways (each sidewalk's own line, end to end - a driveway it crosses too; never water or a pool) and the tiles right
        /// round each corner where a way turns. Null: no ways on this lot (no limit). Worked out once per lot.
        /// </summary>
        public static bool[] RouteTiles(VMContext ctx)
        {
            var f = Get(ctx);
            if (f.Tiles != null || f.Routes.Count == 0) return f.Tiles;
            var arch = ctx.Architecture;
            int w = arch.Width, h = arch.Height;
            var t = new bool[w * h];
            var floors = arch.Floors[0];
            Func<int, int, bool> ok = (x, y) =>
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return false;
                var p = floors[y * w + x].Pattern;
                return p != 65534 && p != 65535; // (not water or a pool)
            };
            foreach (var r in f.Routes)
                for (int k = 0; k < r.Legs.Count; k++)
                {
                    var l = r.Legs[k];
                    for (int a = Math.Min(l.From, l.To); a <= Math.Max(l.From, l.To); a++)
                    {
                        int x = l.W.X(a), y = l.W.Y(a);
                        if (ok(x, y)) t[y * w + x] = true;
                    }
                    if (k == 0 || l.W == r.Legs[k - 1].W) continue;
                    // (a corner: a tile's give either way round it)
                    int cx = l.W.X(l.From), cy = l.W.Y(l.From);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (IsWalkTile(ctx, cx + dx, cy + dy)) t[(cy + dy) * w + cx + dx] = true;
                }
            f.Tiles = t;
            return t;
        }

        /// <summary>
        /// 20.2: VMRoutingFrame.AttemptWalk, for a street Sim on the ground floor: every tile it may not route on goes into the
        /// route's obstacles (as few rectangles as rows allow), so the route runs along the sidewalk and nowhere else - never
        /// the grass strip by the kerb or the yard. From a tile on the sidewalk: to a tile on it (a stop of its walk), or for
        /// anything else (a chat, a wave) to a spot right beside it - a Go Here to a spot off the sidewalk is refused (false:
        /// the router tries its next spot). A Sim not on the sidewalk now (moved off it, a chat) routes as usual - its walk
        /// takes it back onto the sidewalk.
        /// </summary>
        public static bool LimitRoute(VMContext ctx, LotTilePos start, LotTilePos end, bool goHere, VMObstacleSet obstacles)
        {
            if (start.Level != 1 || end.Level != 1) return true;
            var tiles = RouteTiles(ctx);
            if (tiles == null) return true;
            var arch = ctx.Architecture;
            int w = arch.Width, h = arch.Height;
            int si = TileIndex(start, w, h), ei = TileIndex(end, w, h);
            if (si < 0 || !tiles[si]) return true;
            if (ei < 0) return false;
            if (!tiles[ei] && goHere) return false;
            // the tiles not to be walked on, row by row; a row's runs joined with the same runs of the rows below
            var open = new Dictionary<long, int>(); // (x0 | x1 << 16 -> first row of a rectangle still growing)
            for (int y = 0; y <= h; y++)
            {
                var runs = new List<long>();
                if (y < h)
                {
                    int x = 0;
                    while (x < w)
                    {
                        int i = y * w + x;
                        if (tiles[i] || i == si || i == ei) { x++; continue; }
                        int x0 = x;
                        while (x < w && !tiles[y * w + x] && y * w + x != si && y * w + x != ei) x++;
                        runs.Add((long)x0 | ((long)x << 16));
                    }
                }
                // (rectangles whose run this row doesn't continue end here - in key order, the same on every game)
                var keys = new List<long>(open.Keys);
                keys.Sort();
                foreach (var key in keys)
                {
                    if (runs.Contains(key)) continue;
                    int x0 = (int)(key & 0xFFFF), x1 = (int)(key >> 16);
                    obstacles.Add(new VMObstacle(x0 << 4, open[key] << 4, x1 << 4, y << 4));
                    open.Remove(key);
                }
                foreach (var run in runs) if (!open.ContainsKey(run)) open[run] = y;
            }
            return true;
        }

        private static int TileIndex(LotTilePos p, int w, int h)
        {
            int x = p.TileX, y = p.TileY;
            return (x < 0 || y < 0 || x >= w || y >= h) ? -1 : y * w + x;
        }
        private static readonly ConditionalWeakTable<VMArchitecture, Found> Cache = new ConditionalWeakTable<VMArchitecture, Found>();

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
            return Get(ctx).Walks;
        }

        /// <summary>
        /// 20.1: the ways a street Sim may walk on this lot - from one edge of the lot to another along the sidewalks, round
        /// the corner on a corner lot (Route). Empty: no sidewalk runs off the edge of the lot (no street, or only sidewalks
        /// that stop inside it) - nobody walks past here.
        /// </summary>
        public static List<Route> Routes(VMContext ctx)
        {
            return Get(ctx).Routes;
        }

        private static Found Get(VMContext ctx)
        {
            var arch = ctx?.Architecture;
            if (arch == null) return new Found();
            Found cached;
            if (Cache.TryGetValue(arch, out cached)) return cached;
            var result = new Found();
            try { Find(ctx, arch, result.Walks, result.Routes); }
            catch (Exception e) { Console.WriteLine("[SimsRedux] sidewalks: " + e.Message); result = new Found(); }
            // (19.6 fix: an empty answer isn't kept - asked before the lot's floor was in, it stayed "no street" for good)
            if (result.Walks.Count > 0)
            {
                try { Cache.Add(arch, result); } catch (ArgumentException) { }
            }
            return result;
        }

        /// <summary>19.6: the game log's street-life line - how many sidewalks, and their tiles (20.1: and the ways along them).</summary>
        public static string Describe(VMContext ctx)
        {
            var walks = Sidewalks(ctx);
            if (walks.Count == 0) return "no sidewalk (no street on this lot)";
            int tiles = 0;
            foreach (var wk in walks) tiles += Math.Abs(wk.To - wk.From) + 1;
            var routes = Routes(ctx);
            string ways = routes.Count == 0 ? "no way along them runs off the edge of the lot (nobody walks past)" : routes.Count + " way" + (routes.Count == 1 ? "" : "s") + " along them: " + string.Join(", ", routes);
            return walks.Count + " sidewalk" + (walks.Count == 1 ? "" : "s") + ", " + tiles + " tiles; " + ways;
        }

        private static void Find(VMContext ctx, VMArchitecture arch, List<Walk> result, List<Route> routes)
        {
            int w = arch.Width, ht = arch.Height;
            var floors = arch.Floors[0];
            Func<int, int, int> pattern = (x, y) => (x < 0 || y < 0 || x >= w || y >= ht) ? -1 : floors[y * w + x].Pattern;
            // (the lot's buildable land - the property line - as build mode sees it)
            Func<int, int, bool> buildable = (x, y) => x >= 0 && y >= 0 && x < w && y < ht
                && !ctx.IsUserOutOfBounds(LotTilePos.FromBigTile((short)x, (short)y, 1));
            FindWalks(w, ht, pattern, buildable, result, routes);
        }

        /// <summary>20.1: the rest of the way for a street Sim at tile x,y walking to ex,ey (Plan) - null: no way here.</summary>
        public static List<Leg> PlanFor(VMContext ctx, int x, int y, int ex, int ey, bool anyExit, out short toX, out short toY)
        {
            return Plan(Routes(ctx), x, y, ex, ey, anyExit, out toX, out toY);
        }

        /// <summary>20.1: the open end of the lot's sidewalks nearest along them from tile x,y (false: no way here).</summary>
        public static bool NearestExit(VMContext ctx, int x, int y, out short toX, out short toY)
        {
            return Plan(Routes(ctx), x, y, -1, -1, true, out toX, out toY) != null;
        }

        /// <summary>20.1: tile x,y is an open end of the lot's sidewalks (where a street Sim comes on and goes off).</summary>
        public static bool IsExit(VMContext ctx, int x, int y)
        {
            foreach (var r in Routes(ctx))
                if ((r.StartX == x && r.StartY == y) || (r.EndX == x && r.EndY == y)) return true;
            return false;
        }

        /// <summary>20.1: the sidewalk a street Sim at x,y walking to ex,ey is on now (the one of the leg they're on) - null: none.</summary>
        public static Walk WalkNow(VMContext ctx, int x, int y, int ex, int ey)
        {
            short tx, ty;
            var legs = PlanFor(ctx, x, y, ex, ey, false, out tx, out ty);
            return (legs != null && legs.Count > 0) ? legs[0].W : null;
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
