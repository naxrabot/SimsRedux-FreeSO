using System;
using System.Collections.Generic;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Threads;
using FSO.SimAntics.Model.Routing;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: objects on a lot whose files this game doesn't have - e.g. The Sims Online's objects, for a
    /// player without The Sims Online's files (or with older ones). They used to be dropped when the lot loaded,
    /// so the next save deleted them for good. Now they're "held back": not in the game, but kept exactly as
    /// saved and written back with every save (VM.Save), so they're there again for anyone who has the files.
    ///
    /// What is held back together, so nothing is left half there: every object of a multi-tile object, and
    /// everything in or on an object (and what it's in or on) - a Sims 1 table with a missing lamp on it is
    /// held back with the lamp. Sims are never held back. A missing object in a Sim's hands is dropped as before
    /// (it was being carried around, not placed). Their object IDs stay reserved (no new object gets one).
    ///
    /// Their space is kept only where something would really collide with them when they come back (same
    /// level; see BlocksPlacement, WallChangeOk, FloorChangeOk): a held floor object keeps its tile's floor
    /// space, a held wall object (painting, window, wall lamp) keeps only its side of the wall - a sofa can go
    /// under a held window, a lamp in front of a held painting. Objects in or on a held object keep nothing of
    /// their own (the object they're on does).
    ///
    /// Every game loading a lot holds back the same objects (the content stamp makes sure everyone on a lot
    /// has the same files), so object IDs and the lot stay the same for everyone.
    /// </summary>
    public class ReduxHeldObjects
    {
        /// <summary>The short notice a player gets when a lot they load has held-back objects.</summary>
        public const string Notice = "Some objects on this lot need files this game doesn't have; they're kept and will come back when the files are there.";

        /// <summary>
        /// Called once when a lot with held-back objects has loaded (VM.Load), so the game can show Notice.
        /// May be called off the UI thread.
        /// </summary>
        public static Action<VM, ReduxHeldObjects> OnLotLoaded;

        public readonly List<VMEntityMarshal> Entities = new List<VMEntityMarshal>();
        public readonly List<VMThreadMarshal> Threads = new List<VMThreadMarshal>();
        public readonly List<VMMultitileGroupMarshal> Groups = new List<VMMultitileGroupMarshal>();
        public readonly HashSet<short> IDs = new HashSet<short>();
        /// <summary>Missing objects that were in a Sim's hands: dropped (as always), not held.</summary>
        public readonly HashSet<short> Dropped = new HashSet<short>();
        /// <summary>The missing objects, for the log ("0xGUID name (id)").</summary>
        public readonly List<string> Missing = new List<string>();
        /// <summary>The tiles they stand on (TileID - x, y and level).</summary>
        public readonly HashSet<int> Tiles = new HashSet<int>();
        /// <summary>The tiles whose floor space a held object takes (TileID).</summary>
        public readonly HashSet<int> FloorTiles = new HashSet<int>();
        private readonly Dictionary<int, List<Spot>> ByTile = new Dictionary<int, List<Spot>>();

        /// <summary>How a held object sits on its tile (from its saved object data).</summary>
        public class Spot
        {
            public short ID;
            public LotTilePos Pos;
            public Direction Dir;
            /// <summary>In or on another object (a slot): takes no space of its own.</summary>
            public bool Contained;
            /// <summary>Takes floor space on its tile.</summary>
            public bool Floor;
            public int WallPlace;
            public int PlaceFlags;
            /// <summary>The sides of its tile's walls it uses (WallSegments bits), when it uses them alone.</summary>
            public int ExclusiveSides;
        }

        public bool Any { get { return IDs.Count > 0; } }
        /// <summary>Anything held back or dropped for missing files (threads then check what they point at).</summary>
        public bool AnyMissing { get { return IDs.Count > 0 || Dropped.Count > 0; } }

        /// <summary>
        /// Works out what to hold back from a lot being loaded. `exists` says whether this game has an object
        /// (by GUID). Never null.
        /// </summary>
        public static ReduxHeldObjects Find(VMMarshal input, Func<uint, bool> exists)
        {
            var result = new ReduxHeldObjects();
            if (input?.Entities == null) return result;
            var ents = input.Entities;
            var byId = new Dictionary<short, int>();
            for (int i = 0; i < ents.Length; i++)
                if (ents[i] != null && !byId.ContainsKey(ents[i].ObjectID)) byId[ents[i].ObjectID] = i;

            Func<short, bool> isAvatar = (id) => { int k; return byId.TryGetValue(id, out k) && ents[k] is VMAvatarMarshal; };
            Func<short, bool> isObject = (id) => { int k; return byId.TryGetValue(id, out k) && !(ents[k] is VMAvatarMarshal); };

            var missing = new List<short>();
            foreach (var e in ents)
            {
                if (e == null || e is VMAvatarMarshal) continue;
                bool has;
                try { has = exists(e.GUID); } catch { has = false; }
                if (!has) missing.Add(e.ObjectID);
            }
            if (missing.Count == 0) return result;

            // what belongs together: multi-tile objects, and objects in or on each other (not Sims)
            var parent = new Dictionary<short, short>();
            Func<short, short> find = null;
            find = (x) =>
            {
                short p;
                if (!parent.TryGetValue(x, out p) || p == x) return x;
                var r = find(p);
                parent[x] = r;
                return r;
            };
            Action<short, short> union = (a, b) =>
            {
                if (!isObject(a) || !isObject(b)) return;
                var ra = find(a); var rb = find(b);
                if (ra != rb) parent[ra] = rb;
            };
            if (input.MultitileGroups != null)
                foreach (var g in input.MultitileGroups)
                    if (g?.Objects != null)
                        for (int i = 1; i < g.Objects.Length; i++) union(g.Objects[0], g.Objects[i]);
            foreach (var e in ents)
            {
                if (e == null || e is VMAvatarMarshal) continue;
                if (e.Container != 0) union(e.ObjectID, e.Container);
                if (e.Contained != null) foreach (var c in e.Contained) if (c != 0) union(e.ObjectID, c);
            }

            var heldRoots = new HashSet<short>(missing.Select(x => find(x)));
            var members = new Dictionary<short, List<short>>();
            foreach (var e in ents)
            {
                if (e == null || e is VMAvatarMarshal) continue;
                var r = find(e.ObjectID);
                if (!heldRoots.Contains(r)) continue;
                List<short> list;
                if (!members.TryGetValue(r, out list)) members[r] = list = new List<short>();
                list.Add(e.ObjectID);
            }

            foreach (var group in members.Values)
            {
                // carried by a Sim: not held back (the missing ones are dropped, as they always were)
                if (group.Any(id => isAvatar(ents[byId[id]].Container)))
                {
                    Console.WriteLine("[SimsRedux] a missing object was in a Sim's hands - dropped");
                    foreach (var id in group) result.Dropped.Add(id);
                    continue;
                }
                foreach (var id in group) result.IDs.Add(id);
            }

            // the missing objects themselves go in the log (they're no longer a load error: nothing is lost)
            foreach (var id in missing)
            {
                var e = ents[byId[id]];
                string name = null;
                if (input.MultitileGroups != null)
                    foreach (var g in input.MultitileGroups)
                        if (g?.Objects != null && g.Objects.Contains(id)) { name = g.Name; break; }
                var desc = "0x" + e.GUID.ToString("X8") + " " + (string.IsNullOrEmpty(name) ? "(unknown name)" : name) + " (" + id + ")";
                result.Missing.Add(desc);
                Console.WriteLine("[SimsRedux] missing object " + desc + (result.IDs.Contains(id) ? " - held back" : " - dropped"));
            }
            if (!result.Any) return result;

            for (int i = 0; i < ents.Length; i++)
            {
                var e = ents[i];
                if (e == null || e is VMAvatarMarshal || !result.IDs.Contains(e.ObjectID)) continue;
                // a Sim sitting on one: the Sim stays (and gets up); the seat doesn't keep them
                if (e.Contained != null)
                    for (int c = 0; c < e.Contained.Length; c++)
                        if (e.Contained[c] != 0 && isAvatar(e.Contained[c])) e.Contained[c] = 0;
                result.Entities.Add(e);
                result.Threads.Add((input.Threads != null && i < input.Threads.Length) ? input.Threads[i] : null);
                var pos = e.Position;
                if (pos.x >= 0 && pos.y >= 0 && pos != LotTilePos.OUT_OF_WORLD) result.AddSpot(e);
            }
            // ...and the Sim is standing again: still "sitting" with no seat, the game's idle script looked at the
            // seat it was sitting on (object 0) and stopped with a SimAntics error
            foreach (var e in ents)
            {
                var ava = e as VMAvatarMarshal;
                if (ava == null || ava.Container == 0 || !result.IDs.Contains(ava.Container)) continue;
                ava.Container = 0;
                ava.ContainerSlot = 0;
                if (ava.PersonData != null && ava.PersonData.Length > 0) ava.PersonData[0] = 0; // (Posture: standing)
            }
            if (input.MultitileGroups != null)
                foreach (var g in input.MultitileGroups)
                    if (g?.Objects != null && g.Objects.Length > 0 && g.Objects.All(o => result.IDs.Contains(o))) result.Groups.Add(g);

            Console.WriteLine("[SimsRedux] " + result.IDs.Count + " objects held back (" + missing.Count(x => result.IDs.Contains(x)) + " of them missing from this game) - they're kept, and saved with the lot");
            return result;
        }

        private static short Data(VMEntityMarshal e, int index)
        {
            return (e.ObjectData != null && index < e.ObjectData.Length) ? e.ObjectData[index] : (short)0;
        }

        private void AddSpot(VMEntityMarshal e)
        {
            var pos = e.Position;
            var dir = (e is VMGameObjectMarshal) ? ((VMGameObjectMarshal)e).Direction : Direction.NORTH;
            short flags = Data(e, 8), flags2 = Data(e, 40); // Flags, FlagField2
            int wallPlace = Data(e, 13), placeFlags = Data(e, 42), exclusive = Data(e, 63); // WallPlacementFlags, PlacementFlags, ExclusivePlacementFlags
            bool contained = e.Container != 0;
            var spot = new Spot
            {
                ID = e.ObjectID,
                Pos = pos,
                Dir = dir,
                Contained = contained,
                Floor = !contained && HasFloorFootprint(flags, flags2, wallPlace),
                WallPlace = wallPlace,
                PlaceFlags = placeFlags,
                ExclusiveSides = (!contained && (exclusive & 2) != 0) ? WallSides(wallPlace, dir) : 0,
            };
            Tiles.Add(pos.TileID);
            if (spot.Floor) FloorTiles.Add(pos.TileID);
            List<Spot> list;
            if (!ByTile.TryGetValue(pos.TileID, out list)) ByTile[pos.TileID] = list = new List<Spot>();
            list.Add(spot);
        }

        /// <summary>The held objects on this tile (and level), or null.</summary>
        public List<Spot> At(LotTilePos pos)
        {
            List<Spot> list;
            if (ByTile.Count == 0 || pos.x < 0 || pos.y < 0 || !ByTile.TryGetValue(pos.TileID, out list)) return null;
            return list;
        }

        // --- what takes space, worked out from an object's flags (the same for held objects and ones being placed) ---

        /// <summary>
        /// Does an object with these flags (Flags, FlagField2, WallPlacementFlags) take floor space on its tile?
        /// Not when it has no extent, not a window, and not something hanging on a wall that Sims can pass under
        /// (a painting, a wall lamp); a door does.
        /// </summary>
        public static bool HasFloorFootprint(short flags, short flags2, int wallPlace)
        {
            if ((flags & 4) != 0) return false; // HasZeroExtent
            int f2 = (ushort)flags2;
            if ((f2 & (1 << 14)) != 0) return false; // ArchitectualWindow
            if ((f2 & (1 << 15)) != 0) return true; // ArchitectualDoor
            bool passable = (flags & 2) == 0 && (flags & 16) != 0; // !DisallowPersonIntersection && AllowPersonIntersection
            if ((wallPlace & 15) != 0 && passable) return false; // hangs on a wall
            return true;
        }

        private static bool Split(Direction dir, out Direction a, out Direction b)
        {
            switch (dir)
            {
                case Direction.NORTHEAST: a = Direction.NORTH; b = Direction.EAST; return true;
                case Direction.SOUTHEAST: a = Direction.EAST; b = Direction.SOUTH; return true;
                case Direction.SOUTHWEST: a = Direction.SOUTH; b = Direction.WEST; return true;
                case Direction.NORTHWEST: a = Direction.WEST; b = Direction.NORTH; return true;
                default: a = dir; b = dir; return false;
            }
        }

        private static int WallOff(Direction dir)
        {
            switch (dir)
            {
                case Direction.EAST: return 1;
                case Direction.SOUTH: return 2;
                case Direction.WEST: return 3;
                default: return 0;
            }
        }

        private static int Rotate(int ws, int rotate)
        {
            return ((ws << (4 - rotate)) & 15) | ((ws & 15) >> rotate);
        }

        /// <summary>The wall sides of its tile (WallSegments bits) an object with these flags facing `dir` hangs on (as VMEntity.SetWallUse).</summary>
        public static int WallSides(int wallPlace, Direction dir)
        {
            Direction a, b;
            if (Split(dir, out a, out b)) return 0; // (diagonal objects never hang on walls)
            int rotate = (8 - (WallOff(dir) + 1)) % 4;
            return Rotate(wallPlace & 15, rotate);
        }

        /// <summary>Would this wall (WallSegments bits) suit an object with these flags facing `dir`? (as VMEntity.WallChangeValid)</summary>
        public static bool WallOk(int wallPlace, Direction dir, int segs)
        {
            Direction a, b;
            if (Split(dir, out a, out b)) return WallOk(wallPlace & ~15, a, segs) && WallOk(wallPlace & ~15, b, segs);
            bool diag = (segs & (16 | 32)) != 0;
            if (diag && (wallPlace & 128) == 0) return false; // no diagonal allowed
            if (!diag && (wallPlace & 64) != 0) return false; // diagonal required
            int rotPart = Rotate(segs, (WallOff(dir) + 1) % 4);
            if ((wallPlace & rotPart) != (wallPlace & 15)) return false; // a wall it needs isn't there
            if ((wallPlace & (rotPart << 8)) != 0) return false; // a wall it can't have is there
            return true;
        }

        /// <summary>
        /// Would this floor suit an object with these PlacementFlags? (as VMEntity.FloorChangeValid; its
        /// level offset isn't known, so it's taken as none - the floor under a held object upstairs stays)
        /// </summary>
        public static bool FloorOk(int placeFlags, ushort floor, sbyte level)
        {
            if (floor == 65535) return (placeFlags & ((1 << 11) | (1 << 12))) != 0; // pool: AllowOnPool | RequirePool
            if ((placeFlags & (1 << 12)) != 0) return false; // RequirePool
            if (floor == 65534) return (placeFlags & ((1 << 2) | (1 << 13))) != 0; // water: OnWater | RequireWater
            if ((placeFlags & (1 << 13)) != 0) return false; // RequireWater
            if (floor == 0)
            {
                if (level == 1) return (placeFlags & (1 << 1)) != 0; // OnTerrain
                return (placeFlags & (1 << 9)) != 0; // InAir
            }
            return (placeFlags & 1) != 0; // OnFloor
        }

        // --- the checks the game makes (VMContext) ---

        /// <summary>
        /// Would an object placed at `pos` collide with a held object? `footprint` is its footprint there (null:
        /// none), `floor` whether it takes floor space (HasFloorFootprint), `exclusiveSides` the wall sides it
        /// would use alone (0: none). 0 = no, 1 = floor space taken, 2 = wall side taken.
        /// </summary>
        public int BlocksPlacement(VMObstacle footprint, LotTilePos pos, bool floor, int exclusiveSides)
        {
            if (!Any || pos.Level < 1 || pos.x < 0 || pos.y < 0) return 0;
            if (floor && footprint != null && FloorTiles.Count > 0 && Overlaps(FloorTiles, footprint, pos.Level)) return 1;
            if (exclusiveSides != 0)
            {
                var spots = At(pos);
                if (spots != null)
                    foreach (var s in spots)
                        if ((s.ExclusiveSides & exclusiveSides) != 0) return 2;
            }
            return 0;
        }

        /// <summary>Can the walls of this tile become `segs` (WallSegments bits) with the held objects there?</summary>
        public bool WallChangeOk(LotTilePos pos, int segs)
        {
            var spots = At(pos);
            if (spots == null) return true;
            foreach (var s in spots)
                if (!WallOk(s.WallPlace, s.Dir, segs)) return false;
            return true;
        }

        /// <summary>Can the floor of this tile become `floor` with the held objects there?</summary>
        public bool FloorChangeOk(LotTilePos pos, ushort floor)
        {
            var spots = At(pos);
            if (spots == null) return true;
            foreach (var s in spots)
                if (!FloorOk(s.PlaceFlags, floor, pos.Level)) return false;
            return true;
        }

        /// <summary>Is this one of the held objects?</summary>
        public bool Holds(short id) { return IDs.Contains(id); }

        /// <summary>Does a held object stand on this tile?</summary>
        public bool HoldsTile(LotTilePos pos)
        {
            return Tiles.Count > 0 && pos.x >= 0 && pos.y >= 0 && Tiles.Contains(pos.TileID);
        }

        /// <summary>Does an object footprint (on this level) cover the floor space of a held object?</summary>
        public bool Blocks(VMObstacle footprint, sbyte level)
        {
            return Overlaps(FloorTiles, footprint, level);
        }

        private static bool Overlaps(HashSet<int> tiles, VMObstacle footprint, sbyte level)
        {
            if (tiles.Count == 0 || footprint == null) return false;
            int x1 = Math.Max(0, footprint.x1 >> 4), x2 = Math.Max(0, (footprint.x2 - 1) >> 4);
            int y1 = Math.Max(0, footprint.y1 >> 4), y2 = Math.Max(0, (footprint.y2 - 1) >> 4);
            if (x2 - x1 > 64 || y2 - y1 > 64) return false; // (not a real footprint)
            for (int x = x1; x <= x2; x++)
                for (int y = y1; y <= y2; y++)
                {
                    var tile = new VMObstacle(x << 4, y << 4, (x + 1) << 4, (y + 1) << 4);
                    if (!tile.Intersects(footprint)) continue;
                    if (tiles.Contains(new LotTilePos((short)((x << 4) + 8), (short)((y << 4) + 8), level).TileID)) return true;
                }
            return false;
        }

        /// <summary>Adds the held objects back into a save, in object ID order (as the game keeps its objects).</summary>
        public void AddTo(VMMarshal save)
        {
            if (!Any || save == null) return;
            var ents = new List<VMEntityMarshal>(save.Entities ?? new VMEntityMarshal[0]);
            var threads = new List<VMThreadMarshal>(save.Threads ?? new VMThreadMarshal[0]);
            var pairs = new List<KeyValuePair<VMEntityMarshal, VMThreadMarshal>>();
            for (int i = 0; i < ents.Count; i++) pairs.Add(new KeyValuePair<VMEntityMarshal, VMThreadMarshal>(ents[i], i < threads.Count ? threads[i] : null));
            var have = new HashSet<short>(ents.Select(x => x.ObjectID));
            for (int i = 0; i < Entities.Count; i++)
            {
                if (have.Contains(Entities[i].ObjectID)) continue; // (never twice)
                pairs.Add(new KeyValuePair<VMEntityMarshal, VMThreadMarshal>(Entities[i], Threads[i] ?? new VMThreadMarshal { Stack = new VMStackFrameMarshal[0], Queue = new VMQueuedActionMarshal[0], ActiveQueueBlock = -1 }));
            }
            pairs.Sort((a, b) => a.Key.ObjectID.CompareTo(b.Key.ObjectID));
            save.Entities = pairs.Select(x => x.Key).ToArray();
            save.Threads = pairs.Select(x => x.Value).ToArray();
            var groups = new List<VMMultitileGroupMarshal>(save.MultitileGroups ?? new VMMultitileGroupMarshal[0]);
            groups.AddRange(Groups);
            save.MultitileGroups = groups.ToArray();
        }
    }
}
