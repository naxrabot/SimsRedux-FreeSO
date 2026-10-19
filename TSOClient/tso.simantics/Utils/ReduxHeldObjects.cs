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
    /// (it was being carried around, not placed). Their object IDs stay reserved (no new object gets one) and
    /// their tiles can't be built on or have other objects put on them, so they fit back in when they return.
    ///
    /// Every game loading a lot holds back the same objects (the content stamp makes sure everyone on a lot
    /// has the same files), so object IDs and the lot stay the same for everyone.
    /// </summary>
    public class ReduxHeldObjects
    {
        public readonly List<VMEntityMarshal> Entities = new List<VMEntityMarshal>();
        public readonly List<VMThreadMarshal> Threads = new List<VMThreadMarshal>();
        public readonly List<VMMultitileGroupMarshal> Groups = new List<VMMultitileGroupMarshal>();
        public readonly HashSet<short> IDs = new HashSet<short>();
        /// <summary>The tiles they stand on (TileID - x, y and level).</summary>
        public readonly HashSet<int> Tiles = new HashSet<int>();

        public bool Any { get { return IDs.Count > 0; } }

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
                    continue;
                }
                foreach (var id in group) result.IDs.Add(id);
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
                if (pos.x >= 0 && pos.y >= 0 && pos != LotTilePos.OUT_OF_WORLD) result.Tiles.Add(pos.TileID);
            }
            if (input.MultitileGroups != null)
                foreach (var g in input.MultitileGroups)
                    if (g?.Objects != null && g.Objects.Length > 0 && g.Objects.All(o => result.IDs.Contains(o))) result.Groups.Add(g);

            Console.WriteLine("[SimsRedux] " + result.IDs.Count + " objects held back (" + missing.Count(x => result.IDs.Contains(x)) + " of them missing from this game) - they're kept, and saved with the lot");
            return result;
        }

        /// <summary>Is this one of the held objects?</summary>
        public bool Holds(short id) { return IDs.Contains(id); }

        /// <summary>Does a held object stand on this tile?</summary>
        public bool HoldsTile(LotTilePos pos)
        {
            return Tiles.Count > 0 && pos.x >= 0 && pos.y >= 0 && Tiles.Contains(pos.TileID);
        }

        /// <summary>Does an object footprint (on this level) cover a held object's tile?</summary>
        public bool Blocks(VMObstacle footprint, sbyte level)
        {
            if (Tiles.Count == 0 || footprint == null) return false;
            int x1 = Math.Max(0, footprint.x1 >> 4), x2 = Math.Max(0, (footprint.x2 - 1) >> 4);
            int y1 = Math.Max(0, footprint.y1 >> 4), y2 = Math.Max(0, (footprint.y2 - 1) >> 4);
            if (x2 - x1 > 64 || y2 - y1 > 64) return false; // (not a real footprint)
            for (int x = x1; x <= x2; x++)
                for (int y = y1; y <= y2; y++)
                {
                    var tile = new VMObstacle(x << 4, y << 4, (x + 1) << 4, (y + 1) << 4);
                    if (!tile.Intersects(footprint)) continue;
                    if (Tiles.Contains(new LotTilePos((short)((x << 4) + 8), (short)((y << 4) + 8), level).TileID)) return true;
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
