using System;
using System.Collections.Generic;
using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.7: Push Wall, the Sims 4 way. In Build mode the player grabs a straight stretch of wall (from one
    /// corner or crossing to the next) and drags it in or out, a tile at a time:
    ///  - the walls joined to its ends get longer or shorter to stay joined (or a gap is closed where the wall went on
    ///    in a straight line);
    ///  - the floor where the wall used to be is laid with the floor of the room that gets bigger (no floor if that
    ///    side has none), and the old floor there is taken up;
    ///  - doors, windows and anything else hanging on the wall move with it;
    ///  - it's refused, with a short reason, when something is in the way (objects, Sims, other walls, a pool, walls
    ///    at an angle, the edge of the lot, a floor upstairs that needs the wall), when a room would close up, or
    ///    when the family can't afford it;
    ///  - it costs only the difference: new wall (and its wallpaper) and floor are paid for, wall and floor taken
    ///    away are paid back at what they cost.
    ///
    /// The push itself is a VM command (VMNetReduxPushWallCmd, type 71) that works it out again on every game from
    /// the lot as it is when it runs, so all the games on a visit end up the same. The Build mode tool shows where the
    /// wall would go with a preview command (VMArchitectureCommandType.REDUX_PUSH_PREVIEW) that only ever changes the
    /// picture.
    ///
    /// Wall edges in "run" terms: u goes along the wall, v across it (the direction it's pushed). For a wall along x
    /// (each piece is a tile's TopRight side) u = x and v = y; for a wall along y (TopLeft sides) u = y and v = x.
    /// RunEdge(u, v) is the piece of wall between tiles (u, v-1) and (u, v); PerpEdge(u, v) is the piece across it,
    /// between tiles (u-1, v) and (u, v).
    /// </summary>
    public static class ReduxPushWall
    {
        /// <summary>How far one push can go (tiles).</summary>
        public const int MaxPush = 16;
        /// <summary>The longest stretch of wall pushed at once (tiles).</summary>
        public const int MaxRun = 64;

        /// <summary>
        /// A push was refused when it ran (on every game): the lot, who sent it and why, in plain words. The game shows
        /// the reason to the player who pushed.
        /// </summary>
        public static event Action<VM, uint, string> Refused;

        internal static void SignalRefused(VM vm, uint actor, string why)
        {
            try { Refused?.Invoke(vm, actor, why); } catch { }
        }

        /// <summary>What a push would do (or why it can't happen).</summary>
        public class Plan
        {
            /// <summary>Why it can't be done (null: it can).</summary>
            public string Reason;
            public bool Ok { get { return Reason == null; } }
            /// <summary>What the family pays (+) or gets back (-).</summary>
            public int Cost;
            public bool AlongX;
            public sbyte Level;
            /// <summary>The wall's line (v) and its stretch along it: u from Start up to (not including) End.</summary>
            public int Line, Start, End;
            public int Offset;
            public int Length { get { return End - Start; } }
            /// <summary>The wall tiles that change (offset in the level) and what they become.</summary>
            public List<int> WallOffsets = new List<int>();
            public List<WallTile> NewWalls = new List<WallTile>();
            public List<int> FloorOffsets = new List<int>();
            public List<FloorTile> NewFloors = new List<FloorTile>();
            /// <summary>Doors, windows and wall hangings that move with the wall (by ObjectID of their main part).</summary>
            public List<VMMultitileGroup> Movers = new List<VMMultitileGroup>();
        }

        // ------------------------------------------------------------------
        // Working copy of one level's walls and floors
        // ------------------------------------------------------------------

        private class Work
        {
            public VMArchitecture Arch;
            public int W, H;
            public sbyte Level;
            public bool AlongX;
            public WallTile[] Orig;
            public WallTile[] Walls;
            public FloorTile[] OrigFloors;
            public FloorTile[] Floors;
            public bool[] WallMark, FloorMark;
            public List<int> WallChanged = new List<int>();
            public List<int> FloorChanged = new List<int>();

            public Work(VMArchitecture arch, sbyte level, bool alongX)
            {
                Arch = arch;
                W = arch.Width;
                H = arch.Height;
                Level = level;
                AlongX = alongX;
                Orig = arch.Walls[level - 1];
                Walls = (WallTile[])Orig.Clone();
                OrigFloors = arch.Floors[level - 1];
                Floors = (FloorTile[])OrigFloors.Clone();
                WallMark = new bool[Walls.Length];
                FloorMark = new bool[Floors.Length];
            }

            public int TX(int u, int v) { return AlongX ? u : v; }
            public int TY(int u, int v) { return AlongX ? v : u; }
            public bool In(int x, int y) { return x >= 0 && y >= 0 && x < W && y < H; }
            public bool InUV(int u, int v) { return In(TX(u, v), TY(u, v)); }
            public int Off(int x, int y) { return y * W + x; }
            public int OffUV(int u, int v) { return Off(TX(u, v), TY(u, v)); }

            public void SetWall(int off, WallTile t)
            {
                Walls[off] = t;
                if (!WallMark[off]) { WallMark[off] = true; WallChanged.Add(off); }
            }

            public void SetFloor(int off, FloorTile f)
            {
                Floors[off] = f;
                if (!FloorMark[off]) { FloorMark[off] = true; FloorChanged.Add(off); }
            }
        }

        /// <summary>One piece of wall: whether it's there, its style, the wallpaper on each side, and a door/window's cut-out.</summary>
        private struct Edge
        {
            public bool Exists;
            public ushort Style;
            /// <summary>The wallpaper facing the edge's main tile (+u for a cross piece, +v for a run piece).</summary>
            public ushort PatMain;
            /// <summary>The wallpaper on the other side.</summary>
            public ushort PatSub;
            public ushort ObjStyle;
            public bool Door;
        }

        // h: the piece along x (a tile's TopRight side, the other side is the BottomLeft of the tile at y-1);
        // otherwise the piece along y (TopLeft, and the BottomRight of the tile at x-1).
        private static Edge GetEdge(Work w, WallTile[] walls, bool h, int x, int y)
        {
            var e = new Edge();
            if (!w.In(x, y)) return e;
            var t = walls[w.Off(x, y)];
            if (h)
            {
                e.Exists = (t.Segments & WallSegments.TopRight) != 0;
                e.Style = t.TopRightStyle;
                e.PatMain = t.TopRightPattern;
                e.ObjStyle = t.ObjSetTRStyle;
                e.Door = t.TopRightDoor;
                if (w.In(x, y - 1)) e.PatSub = walls[w.Off(x, y - 1)].BottomLeftPattern;
            }
            else
            {
                e.Exists = (t.Segments & WallSegments.TopLeft) != 0;
                e.Style = t.TopLeftStyle;
                e.PatMain = t.TopLeftPattern;
                e.ObjStyle = t.ObjSetTLStyle;
                e.Door = t.TopLeftDoor;
                if (w.In(x - 1, y)) e.PatSub = walls[w.Off(x - 1, y)].BottomRightPattern;
            }
            return e;
        }

        private static void SetEdge(Work w, bool h, int x, int y, Edge e)
        {
            if (!w.In(x, y)) return;
            int mo = w.Off(x, y);
            var t = w.Walls[mo];
            if (h)
            {
                if (e.Exists)
                {
                    t.Segments |= WallSegments.TopRight;
                    t.TopRightStyle = e.Style;
                    t.TopRightPattern = e.PatMain;
                }
                else t.Segments &= ~WallSegments.TopRight;
                t.ObjSetTRStyle = e.Exists ? e.ObjStyle : (ushort)0;
                t.TopRightDoor = e.Exists && e.Door;
            }
            else
            {
                if (e.Exists)
                {
                    t.Segments |= WallSegments.TopLeft;
                    t.TopLeftStyle = e.Style;
                    t.TopLeftPattern = e.PatMain;
                }
                else t.Segments &= ~WallSegments.TopLeft;
                t.ObjSetTLStyle = e.Exists ? e.ObjStyle : (ushort)0;
                t.TopLeftDoor = e.Exists && e.Door;
            }
            w.SetWall(mo, t);

            int sx = h ? x : x - 1, sy = h ? y - 1 : y;
            if (!w.In(sx, sy)) return;
            int so = w.Off(sx, sy);
            var s = w.Walls[so];
            if (h)
            {
                if (e.Exists) { s.Segments |= WallSegments.BottomLeft; s.BottomLeftPattern = e.PatSub; }
                else s.Segments &= ~WallSegments.BottomLeft;
            }
            else
            {
                if (e.Exists) { s.Segments |= WallSegments.BottomRight; s.BottomRightPattern = e.PatSub; }
                else s.Segments &= ~WallSegments.BottomRight;
            }
            w.SetWall(so, s);
        }

        private static Edge RunEdge(Work w, int u, int v) { return GetEdge(w, w.Walls, w.AlongX, w.TX(u, v), w.TY(u, v)); }
        private static Edge PerpEdge(Work w, int u, int v) { return GetEdge(w, w.Walls, !w.AlongX, w.TX(u, v), w.TY(u, v)); }
        private static void SetRunEdge(Work w, int u, int v, Edge e) { SetEdge(w, w.AlongX, w.TX(u, v), w.TY(u, v), e); }
        private static void SetPerpEdge(Work w, int u, int v, Edge e) { SetEdge(w, !w.AlongX, w.TX(u, v), w.TY(u, v), e); }

        private static bool Diag(Work w, int u, int v)
        {
            if (!w.InUV(u, v)) return false;
            return (w.Walls[w.OffUV(u, v)].Segments & WallSegments.AnyDiag) != 0;
        }

        /// <summary>Does something meet the wall at this corner point (u, line)? A wall across it, or one at an angle.</summary>
        private static bool Joint(Work w, int j, int v)
        {
            if (PerpEdge(w, j, v - 1).Exists || PerpEdge(w, j, v).Exists) return true;
            return Diag(w, j - 1, v - 1) || Diag(w, j, v - 1) || Diag(w, j - 1, v) || Diag(w, j, v);
        }

        /// <summary>Would a new piece of wall here (main tile and the one across) stand on something? (upstairs only)</summary>
        private static bool Held(Work w, bool h, int x, int y)
        {
            if (w.Level <= 1) return true;
            var sup = w.Arch.Supported[w.Level - 2];
            int sx = h ? x : x - 1, sy = h ? y - 1 : y;
            if (w.In(x, y) && sup[w.Off(x, y)]) return true;
            if (w.In(sx, sy) && sup[w.Off(sx, sy)]) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // Prices
        // ------------------------------------------------------------------

        private static int StylePrice(ushort style)
        {
            try { return FSO.Content.Content.Get().WorldWalls.GetWallStyle(style)?.Price ?? 0; } catch { return 0; }
        }

        private static int PatternPrice(ushort id)
        {
            if (id == 0) return 0;
            try
            {
                FSO.Content.WallReference r;
                if (FSO.Content.Content.Get().WorldWalls.Entries.TryGetValue(id, out r) && r != null) return r.Price;
            }
            catch { }
            return 0;
        }

        private static int FloorPrice(ushort id)
        {
            if (id == 0 || id == 1) return 0; // (as the floor tool counts it)
            try
            {
                FSO.Content.FloorReference r;
                if (FSO.Content.Content.Get().WorldFloors.Entries.TryGetValue(id, out r) && r != null) return r.Price;
            }
            catch { }
            return 0;
        }

        /// <summary>What a piece of wall is worth: the wall (or fence) and, on a wall, the wallpaper on both sides.</summary>
        private static int EdgePrice(Edge e)
        {
            if (!e.Exists) return 0;
            int p = StylePrice(e.Style);
            if (e.Style == 1) p += PatternPrice(e.PatMain) + PatternPrice(e.PatSub);
            return p;
        }

        private static string NameOf(VMEntity ent)
        {
            try
            {
                var b = ent?.MultitileGroup?.BaseObject ?? ent;
                var n = b?.ToString();
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            }
            catch { }
            return "something";
        }

        // ------------------------------------------------------------------
        // Working it out
        // ------------------------------------------------------------------

        /// <summary>
        /// Works out a push: the piece of wall at tile (x, y) - its TopRight side when alongX, its TopLeft side otherwise -
        /// and the whole straight stretch it's part of, pushed `offset` tiles (+: towards higher y / x). Changes nothing.
        /// </summary>
        public static Plan Make(VM vm, int x, int y, sbyte level, bool alongX, int offset)
        {
            var p = new Plan { AlongX = alongX, Level = level, Offset = offset };
            var arch = vm?.Context?.Architecture;
            if (arch == null) { p.Reason = "The lot isn't ready."; return p; }
            if (level < 1 || level > arch.Stories) { p.Reason = "There's no wall there."; return p; }
            if (!arch.BuildBuyEnabled) { p.Reason = "Building isn't allowed on this lot."; return p; }
            if (Math.Abs(offset) > MaxPush) { p.Reason = "That's too far to push in one go."; return p; }

            var w = new Work(arch, level, alongX);
            int u0 = alongX ? x : y;
            int V = alongX ? y : x;
            if (!w.InUV(u0, V)) { p.Reason = "There's no wall there."; return p; }
            if (Diag(w, u0, V)) { p.Reason = "Push Wall only works on straight walls."; return p; }
            var first = RunEdge(w, u0, V);
            if (!first.Exists) { p.Reason = "There's no wall there."; return p; }

            // the stretch: along the line until a corner, a crossing, a gap or a change of style (wall / fence)
            int S = u0, E = u0 + 1;
            while (E - S < MaxRun)
            {
                var prev = RunEdge(w, S - 1, V);
                if (!prev.Exists || prev.Style != first.Style || Joint(w, S, V)) break;
                S--;
            }
            while (E - S < MaxRun)
            {
                var next = RunEdge(w, E, V);
                if (!next.Exists || next.Style != first.Style || Joint(w, E, V)) break;
                E++;
            }
            p.Line = V; p.Start = S; p.End = E;
            if (offset == 0) return p;

            int D = offset, dir = D > 0 ? 1 : -1, n = Math.Abs(D);
            int vLo = Math.Min(V, V + D), vHi = Math.Max(V, V + D); // the tiles the wall passes over: v in [vLo, vHi)
            var ctx = vm.Context;

            // ---- the edge of the lot, walls at an angle, pools ----
            for (int u = S; u < E; u++)
            {
                for (int v = vLo - 1; v <= vHi; v++)
                {
                    int tx = w.TX(u, v), ty = w.TY(u, v);
                    if (!w.In(tx, ty) || arch.OutsideClip((short)tx, (short)ty, level)) { p.Reason = "That's the edge of the lot."; return p; }
                    if (Diag(w, u, v)) { p.Reason = "A wall at an angle is in the way."; return p; }
                    if (v >= vLo && v < vHi && w.Floors[w.Off(tx, ty)].Pattern >= 65534) { p.Reason = "A pool is in the way."; return p; }
                }
            }
            foreach (var j in new[] { S, E })
            {
                if (Diag(w, j - 1, V - 1) || Diag(w, j, V - 1) || Diag(w, j - 1, V) || Diag(w, j, V)) { p.Reason = "A wall at an angle is joined to this one."; return p; }
            }

            // ---- other walls: one on the way (or where it would end up), or one crossing its path ----
            for (int k = 1; k <= n; k++)
            {
                int line = V + dir * k;
                for (int u = S; u < E; u++)
                {
                    if (!RunEdge(w, u, line).Exists) continue;
                    p.Reason = (k == n) ? "There's already a wall there - the room would close up." : "Another wall is in the way.";
                    return p;
                }
                // walls across the path between the two lines
                int cv = dir > 0 ? V + k - 1 : V - k;
                for (int u = S + 1; u < E; u++)
                {
                    if (PerpEdge(w, u, cv).Exists) { p.Reason = "Another wall is in the way."; return p; }
                }
            }

            // ---- what hangs on the wall moves with it (doors, windows, pictures...) ----
            var mainSeg = alongX ? WallSegments.TopRight : WallSegments.TopLeft;
            var subSeg = alongX ? WallSegments.BottomLeft : WallSegments.BottomRight;
            var movers = new List<VMMultitileGroup>();
            for (int u = S; u < E; u++)
            {
                for (int row = 0; row < 2; row++)
                {
                    int v = (row == 0) ? V : V - 1;
                    if (!w.InUV(u, v)) continue;
                    int tx = w.TX(u, v), ty = w.TY(u, v);
                    var objs = ctx.ObjectQueries.GetObjectsAt(LotTilePos.FromBigTile((short)tx, (short)ty, level));
                    if (objs == null) continue;
                    var cur = w.Orig[w.Off(tx, ty)];
                    var without = cur;
                    without.Segments &= ~((row == 0) ? mainSeg : subSeg);
                    foreach (var obj in objs.ToArray())
                    {
                        if (obj == null || obj is VMAvatar || obj.MultitileGroup == null) continue;
                        if (movers.Contains(obj.MultitileGroup)) continue;
                        if (obj.WallChangeValid(cur, obj.Direction, false) != VMPlacementError.Success) continue;
                        if (obj.WallChangeValid(without, obj.Direction, false) == VMPlacementError.Success) continue;
                        movers.Add(obj.MultitileGroup);
                    }
                }
            }
            foreach (var g in movers)
            {
                var name = NameOf(g.BaseObject);
                foreach (var part in g.Objects)
                {
                    var pos = part.Position;
                    if (pos == LotTilePos.OUT_OF_WORLD) continue;
                    int pu = alongX ? pos.TileX : pos.TileY, pv = alongX ? pos.TileY : pos.TileX;
                    if (pos.Level != level || pu < S || pu >= E || (pv != V && pv != V - 1))
                    { p.Reason = "The " + name + " is fixed to another wall too, so it can't move with this one."; return p; }
                    int slots = part.TotalSlots();
                    for (int i = 0; i < slots; i++)
                        if (part.GetSlot(i) != null) { p.Reason = "Take the things off the " + name + " first."; return p; }
                }
                // (checked here rather than IsUserMovable, which also asks this game's own settings - every game
                //  on a visit has to answer the same)
                if (g.BaseObject == null) { p.Reason = "The " + name + " can't be moved."; return p; }
                foreach (var part in g.Objects)
                {
                    var flags = (VMMovementFlags)part.GetValue(VMStackObjectVariable.MovementFlags);
                    if (!ctx.Cheats.MoveObjects && (flags & VMMovementFlags.PlayersCanMove) == 0)
                    { p.Reason = "The " + name + " can't be moved, so this wall can't be pushed."; return p; }
                    if (part.IsInUse(ctx, true)) { p.Reason = "A Sim is using the " + name + " right now."; return p; }
                }
            }
            movers.Sort((a, b) => a.BaseObject.ObjectID.CompareTo(b.BaseObject.ObjectID));
            p.Movers = movers;

            int cost = 0;

            // ---- move the wall ----
            var moved = new Edge[E - S];
            for (int u = S; u < E; u++)
            {
                moved[u - S] = RunEdge(w, u, V);
                SetRunEdge(w, u, V, new Edge());
            }
            for (int u = S; u < E; u++)
            {
                int tx = w.TX(u, V + D), ty = w.TY(u, V + D);
                if (!Held(w, alongX, tx, ty)) { p.Reason = "There's nothing underneath to hold up the wall."; return p; }
                SetRunEdge(w, u, V + D, moved[u - S]);
            }

            // ---- the walls joined to its ends get longer or shorter ----
            for (int end = 0; end < 2; end++)
            {
                bool isStart = end == 0;
                int j = isStart ? S : E;
                var away = PerpEdge(w, j, dir > 0 ? V - 1 : V);
                var cont = isStart ? RunEdge(w, S - 1, V) : RunEdge(w, E, V); // the wall going on in a straight line
                bool connect = away.Exists || cont.Exists;
                bool touches = connect || PerpEdge(w, j, dir > 0 ? V : V - 1).Exists;
                if (!touches) continue; // (a free end just moves)

                // this end's cross pieces change: they have to be on the lot, and straight
                for (int k = 0; k < n; k++)
                {
                    int v = dir > 0 ? V + k : V - 1 - k;
                    for (int du = -1; du <= 0; du++)
                    {
                        int tx = w.TX(j + du, v), ty = w.TY(j + du, v);
                        if (!w.In(tx, ty) || arch.OutsideClip((short)tx, (short)ty, level)) { p.Reason = "That's the edge of the lot."; return p; }
                        if (Diag(w, j + du, v)) { p.Reason = "A wall at an angle is in the way."; return p; }
                    }
                }

                if (connect)
                {
                    Edge tmpl;
                    if (away.Exists)
                    {
                        tmpl = away;
                    }
                    else
                    {
                        // closing the gap to the wall that went on straight: the side towards the moved part gets
                        // the wallpaper of the room that grows, the other side the straight wall's
                        var run = moved[isStart ? 0 : (E - S - 1)];
                        ushort grow = dir > 0 ? run.PatSub : run.PatMain;
                        ushort other = dir > 0 ? cont.PatMain : cont.PatSub;
                        tmpl = new Edge { Exists = true, Style = cont.Style, PatMain = isStart ? grow : other, PatSub = isStart ? other : grow };
                    }
                    tmpl.Exists = true;
                    tmpl.ObjStyle = 0;
                    tmpl.Door = false;
                    for (int k = 0; k < n; k++)
                    {
                        int v = dir > 0 ? V + k : V - 1 - k;
                        if (PerpEdge(w, j, v).Exists) continue;
                        if (!Held(w, !alongX, w.TX(j, v), w.TY(j, v))) { p.Reason = "There's nothing underneath to hold up the wall."; return p; }
                        SetPerpEdge(w, j, v, tmpl);
                        cost += EdgePrice(tmpl);
                    }
                }
                else
                {
                    // the wall joined on the side it's pushed towards gets shorter
                    for (int k = 0; k < n; k++)
                    {
                        int v = dir > 0 ? V + k : V - 1 - k;
                        var e = PerpEdge(w, j, v);
                        if (!e.Exists) { p.Reason = "The wall joined to the end is too short to push this far."; return p; }
                        cost -= EdgePrice(e);
                        SetPerpEdge(w, j, v, new Edge());
                    }
                }
            }

            // ---- the floor: the room that grows lays its floor where the wall was ----
            var roomsHere = (level - 1 < arch.Rooms.Length) ? arch.Rooms[level - 1].Map : null;
            bool upstairs = level < arch.Stories;
            bool shrinksIndoors = false;
            for (int u = S; u < E; u++)
            {
                int srcV = dir > 0 ? V - 1 : V; // (the tile next to the old wall, on the side that grows)
                ushort srcPat = 0;
                bool srcOutside = true;
                if (w.InUV(u, srcV))
                {
                    int so = w.OffUV(u, srcV);
                    srcPat = w.OrigFloors[so].Pattern;
                    srcOutside = IsOutside(arch, roomsHere, so);
                }
                if (srcPat >= 65534) srcPat = 0;
                for (int v = vLo; v < vHi; v++)
                {
                    int off = w.OffUV(u, v);
                    var old = w.Floors[off];
                    if (!IsOutside(arch, roomsHere, off) && srcOutside) shrinksIndoors = true;
                    if (old.Pattern == srcPat) continue;
                    if (srcPat > 0 && level > 1 && !arch.Supported[level - 2][off]) { p.Reason = "There's nothing underneath to hold up the floor."; return p; }
                    cost += FloorPrice(srcPat) - FloorPrice(old.Pattern);
                    w.SetFloor(off, new FloorTile { Pattern = srcPat });
                }
            }

            // ---- upstairs: a floor or wall over the part that ends up outside needs this wall ----
            if (shrinksIndoors && upstairs)
            {
                var upW = arch.Walls[level];
                var upF = arch.Floors[level];
                for (int u = S; u < E; u++)
                    for (int v = vLo - 1; v <= vHi; v++)
                    {
                        if (!w.InUV(u, v)) continue;
                        int off = w.OffUV(u, v);
                        if (upW[off].Segments != 0 || upF[off].Pattern != 0)
                        { p.Reason = "Something upstairs is held up by this part of the house."; return p; }
                    }
            }

            // ---- Sims and objects in the way ----
            var held = vm.ReduxHeld;
            for (int u = S; u < E; u++)
                for (int v = vLo; v < vHi; v++)
                {
                    int tx = w.TX(u, v), ty = w.TY(u, v);
                    var pos = LotTilePos.FromBigTile((short)tx, (short)ty, level);
                    if (held != null && held.Any && held.HoldsTile(pos)) { p.Reason = "An object that couldn't be loaded is in the way."; return p; }
                    var objs = ctx.ObjectQueries.GetObjectsAt(pos);
                    if (objs == null) continue;
                    foreach (var obj in objs)
                    {
                        if (obj == null || (obj.MultitileGroup != null && movers.Contains(obj.MultitileGroup))) continue;
                        if (obj is VMAvatar) { p.Reason = "A Sim is in the way."; return p; }
                        p.Reason = "The " + NameOf(obj) + " is in the way.";
                        return p;
                    }
                }
            foreach (var off in w.WallChanged)
            {
                int tx = off % w.W, ty = off / w.W;
                var nt = w.Walls[off];
                var ot = w.Orig[off];
                if (nt.Segments == ot.Segments) continue;
                var pos = LotTilePos.FromBigTile((short)tx, (short)ty, level);
                if ((nt.Segments & ~ot.Segments) != 0 && arch.GetTerrainSloped((short)tx, (short)ty))
                { p.Reason = "The ground is too bumpy there for a wall."; return p; }
                if (held != null && held.Any && !held.WallChangeOk(pos, (int)nt.Segments))
                { p.Reason = "An object that couldn't be loaded is in the way."; return p; }
                var objs = ctx.ObjectQueries.GetObjectsAt(pos);
                if (objs == null) continue;
                foreach (var obj in objs)
                {
                    if (obj == null || obj is VMAvatar) continue;
                    if (obj.MultitileGroup != null && movers.Contains(obj.MultitileGroup)) continue;
                    if (obj.WallChangeValid(nt, obj.Direction, false) != VMPlacementError.Success)
                    { p.Reason = "The " + NameOf(obj) + " is in the way."; return p; }
                }
            }

            // ---- money ----
            var fam = vm.TS1State?.CurrentFamily;
            if (cost > 0 && fam != null && fam.Budget < cost)
            { p.Reason = "You can't afford that (§" + cost + ")."; return p; }
            p.Cost = cost;

            foreach (var off in w.WallChanged)
            {
                p.WallOffsets.Add(off);
                p.NewWalls.Add(w.Walls[off]);
            }
            foreach (var off in w.FloorChanged)
            {
                p.FloorOffsets.Add(off);
                p.NewFloors.Add(w.Floors[off]);
            }
            return p;
        }

        private static bool IsOutside(VMArchitecture arch, uint[] map, int off)
        {
            try
            {
                if (map == null || off < 0 || off >= map.Length) return true;
                var id = (int)(map[off] & 0xFFFF);
                if (id < 0 || id >= arch.RoomData.Count) return true;
                return arch.RoomData[id].IsOutside;
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------
        // Doing it
        // ------------------------------------------------------------------

        /// <summary>
        /// Pushes the wall (the VM command, on every game). Returns why it couldn't (null: done, and paid for).
        /// If anything doesn't fit at the end, everything is put back as it was.
        /// </summary>
        public static string Run(VM vm, int x, int y, sbyte level, bool alongX, int offset)
        {
            if (offset == 0) return null;
            var plan = Make(vm, x, y, level, alongX, offset);
            if (!plan.Ok) return plan.Reason;
            var ctx = vm.Context;
            var arch = ctx.Architecture;

            // take the doors and windows off the wall for a moment
            var movers = plan.Movers;
            var from = new List<LotTilePos>();
            var dirs = new List<Direction>();
            for (int i = 0; i < movers.Count; i++)
            {
                var b = movers[i].BaseObject;
                from.Add(b.Position);
                dirs.Add(b.Direction);
                var r = movers[i].ChangePosition(LotTilePos.OUT_OF_WORLD, b.Direction, ctx, VMPlaceRequestFlags.Default);
                if (r.Status != VMPlacementError.Success || b.Position != LotTilePos.OUT_OF_WORLD)
                {
                    PutBack(ctx, movers, from, dirs, i);
                    return "The " + NameOf(b) + " couldn't be moved.";
                }
            }

            // work it out again without them (their cut-outs are off the wall now, so the moved wall carries none)
            var plan2 = Make(vm, x, y, level, alongX, offset);
            if (!plan2.Ok || plan2.Movers.Count != 0)
            {
                PutBack(ctx, movers, from, dirs, movers.Count);
                return plan2.Ok ? "Something on the wall couldn't be moved." : plan2.Reason;
            }

            int W = arch.Width;
            var oldWalls = new WallTile[plan2.WallOffsets.Count];
            for (int i = 0; i < plan2.WallOffsets.Count; i++)
            {
                int off = plan2.WallOffsets[i];
                oldWalls[i] = arch.Walls[level - 1][off];
                arch.SetWall((short)(off % W), (short)(off / W), level, plan2.NewWalls[i]);
            }
            var oldFloors = new FloorTile[plan2.FloorOffsets.Count];
            for (int i = 0; i < plan2.FloorOffsets.Count; i++)
            {
                int off = plan2.FloorOffsets[i];
                oldFloors[i] = arch.Floors[level - 1][off];
                arch.SetFloor((short)(off % W), (short)(off / W), level, plan2.NewFloors[i], true);
            }

            // the doors and windows go back on, in the wall's new place
            short dx = (short)(alongX ? 0 : offset * 16), dy = (short)(alongX ? offset * 16 : 0);
            for (int i = 0; i < movers.Count; i++)
            {
                var target = new LotTilePos((short)(from[i].x + dx), (short)(from[i].y + dy), from[i].Level);
                var r = movers[i].ChangePosition(target, dirs[i], ctx, VMPlaceRequestFlags.Default);
                if (r.Status != VMPlacementError.Success || movers[i].BaseObject.Position == LotTilePos.OUT_OF_WORLD)
                {
                    // doesn't fit: everything back as it was
                    for (int k = 0; k < i; k++) movers[k].ChangePosition(LotTilePos.OUT_OF_WORLD, dirs[k], ctx, VMPlaceRequestFlags.Default);
                    for (int k = 0; k < plan2.WallOffsets.Count; k++)
                    {
                        int off = plan2.WallOffsets[k];
                        arch.SetWall((short)(off % W), (short)(off / W), level, oldWalls[k]);
                    }
                    for (int k = 0; k < plan2.FloorOffsets.Count; k++)
                    {
                        int off = plan2.FloorOffsets[k];
                        arch.SetFloor((short)(off % W), (short)(off / W), level, oldFloors[k], true);
                    }
                    PutBack(ctx, movers, from, dirs, movers.Count);
                    return "The " + NameOf(movers[i].BaseObject) + " wouldn't fit in the wall's new place.";
                }
            }
            for (int i = 0; i < movers.Count; i++) movers[i].ExecuteEntryPoint(11, ctx); // (user placement, as moving it does)

            var fam = vm.TS1State?.CurrentFamily;
            if (fam != null && plan2.Cost != 0) fam.Budget -= plan2.Cost;
            return null;
        }

        private static void PutBack(VMContext ctx, List<VMMultitileGroup> movers, List<LotTilePos> from, List<Direction> dirs, int count)
        {
            for (int i = 0; i < count && i < from.Count; i++)
            {
                try
                {
                    var g = movers[i];
                    if (g.BaseObject.Position == from[i]) continue;
                    var r = g.ChangePosition(from[i], dirs[i], ctx, VMPlaceRequestFlags.Default);
                    if (r.Status != VMPlacementError.Success)
                        g.ChangePosition(from[i], dirs[i], ctx, VMPlaceRequestFlags.AllowIntersection);
                }
                catch { }
            }
        }

        /// <summary>
        /// The Build mode tool's preview (VMArchitectureCommandType.REDUX_PUSH_PREVIEW): draws the pushed wall and floor
        /// on the picture only. x/y = the wall's tile, x2 = 0 along x / 1 along y, y2 = how far. Returns the cost.
        /// </summary>
        public static int Preview(VMArchitecture arch, VMArchitectureCommand com)
        {
            try
            {
                var vm = arch?.Context?.VM;
                if (vm == null) return 0;
                var plan = Make(vm, com.x, com.y, com.level, com.x2 == 0, com.y2);
                if (!plan.Ok || plan.Offset == 0) return 0;
                int W = arch.Width;
                for (int i = 0; i < plan.WallOffsets.Count; i++)
                {
                    int off = plan.WallOffsets[i];
                    arch.SetWall((short)(off % W), (short)(off / W), com.level, plan.NewWalls[i]);
                }
                for (int i = 0; i < plan.FloorOffsets.Count; i++)
                {
                    int off = plan.FloorOffsets[i];
                    arch.SetFloor((short)(off % W), (short)(off / W), com.level, plan.NewFloors[i], true);
                }
                return plan.Cost;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] push wall preview: " + e.Message);
                return 0;
            }
        }
    }
}
