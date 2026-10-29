using System;
using System.Collections.Generic;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Entities;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 15.7: Sims pick their own bed. "Assign Bed" on a single bed, "Assign Left Side" / "Assign Right
    /// Side" on a double bed, and "Unassign". A Sim going to bed on their own goes to their own bed (or side);
    /// the others leave an assigned bed or side alone while another free bed is there. "Sleep" ordered from the
    /// menu still works on any bed - and on a double bed that's partly the Sim's, they take their own side.
    ///
    /// Where it's kept: on the bed itself, as object attributes of the bed's base piece (the Sims 1 bed scripts
    /// only use attributes 0-3; these are 40-43). Attributes are already saved with the lot and sent to every
    /// player, a game from before this (15.5) loads and saves them untouched, and its beds simply ignore them.
    /// Nothing new is written to the save. Each side keeps its owner's Sim GUID in two attributes (low, high).
    /// Changes only come through VMNetReduxBedCmd, so on an online lot every game has the same beds.
    /// </summary>
    public static class ReduxBeds
    {
        /// <summary>The first attribute used: side 0 (left, or the whole single bed) at 40-41, side 1 (right) at 42-43.</summary>
        public const int OwnerAttr = 40;

        // ------------------------------------------------------------------
        // What is a bed, and its sides
        // ------------------------------------------------------------------

        /// <summary>A bed: something to "Sleep" in that's called a bed (the game's own, an expansion's or a download).</summary>
        public static bool IsBed(VMEntity obj)
        {
            if (!(obj is VMGameObject) || obj.MultitileGroup == null) return false;
            var ttas = obj.TreeTableStrings;
            var ttab = obj.TreeTable;
            if (ttas == null || ttab == null) return false;
            bool sleep = false;
            foreach (var ia in ttab.Interactions)
                if (IsSleepName(VMReduxBrain.LotName(ttas, (int)ia.TTAIndex))) { sleep = true; break; } // (18.5: English - the same on every game)
            if (!sleep) return false;
            var b = obj.MultitileGroup.BaseObject ?? obj;
            var def = b.MasterDefinition ?? b.Object?.OBJ;
            var label = (def?.ChunkLabel ?? "") + " " + (obj.Object?.OBJ?.ChunkLabel ?? "");
            if (label.IndexOf("bed", StringComparison.OrdinalIgnoreCase) < 0) return false;
            // (not a pet bed - those are the pets' own)
            foreach (var pet in PetWords) if (label.IndexOf(pet, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static readonly string[] PetWords = { "pet", "dog", "cat ", "cat-", "kitty", "puppy", "doggie" };

        public static bool IsSleepName(string name)
        {
            return name != null && name.Trim().Equals("Sleep", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Is this interaction (by its number) the bed's "Sleep"? (18.5: by its English name - VMReduxBrain.LotName - so a bed
        /// is a bed on every game of a visit, whatever language each is in. IsSleepName takes that English name.)
        /// </summary>
        public static bool IsSleep(VMEntity obj, int ttaIndex)
        {
            return obj?.TreeTableStrings != null && IsSleepName(VMReduxBrain.LotName(obj.TreeTableStrings, ttaIndex));
        }

        /// <summary>
        /// The bed's sides: the interaction group leaders of its pieces (a Sims 1 double bed has two - its left and
        /// right halves; a single bed one), in order: index 0 = left, 1 = right. Sides are told apart by the bed's
        /// own "side" attribute (2: 0 left, 1 right) when it has one, otherwise by their group number. Anything
        /// that isn't clearly two sides is a single bed (one side, index 0).
        /// </summary>
        public static List<VMEntity> Sides(VMEntity bed)
        {
            var result = new List<VMEntity>();
            var group = bed?.MultitileGroup;
            if (group == null) return result;
            var leaders = group.Objects.Where(x => x?.Object?.OBJ != null && x.Object.OBJ.InteractionGroupID < 0)
                .OrderBy(x => -x.Object.OBJ.InteractionGroupID).ThenBy(x => x.ObjectID).ToList();
            if (leaders.Count == 2)
            {
                short a = leaders[0].GetAttribute(2), b = leaders[1].GetAttribute(2);
                if (a == 1 && b == 0) leaders.Reverse();
                return leaders;
            }
            result.Add(group.BaseObject ?? bed);
            return result;
        }

        /// <summary>Which side of its bed this piece is on (0 or 1).</summary>
        public static int SideOf(VMEntity piece)
        {
            var sides = Sides(piece);
            if (sides.Count < 2) return 0;
            var leader = piece.MultitileGroup.GetInteractionGroupLeader(piece);
            int i = sides.IndexOf(leader);
            return i < 0 ? 0 : i;
        }

        public static bool IsDouble(VMEntity bed) { return Sides(bed).Count == 2; }

        // ------------------------------------------------------------------
        // Owners
        // ------------------------------------------------------------------

        private static VMEntity Store(VMEntity bed) { return bed?.MultitileGroup?.BaseObject ?? bed; }

        /// <summary>The GUID of the Sim this side was assigned to (0 = nobody), as stored - see Owner for a check that they still live here.</summary>
        public static uint StoredOwner(VMEntity bed, int side)
        {
            var s = Store(bed);
            if (s == null || side < 0 || side > 1) return 0;
            return (uint)(ushort)s.GetAttribute((ushort)(OwnerAttr + side * 2)) | ((uint)(ushort)s.GetAttribute((ushort)(OwnerAttr + side * 2 + 1)) << 16);
        }

        public static void SetOwner(VMEntity bed, int side, uint guid)
        {
            var s = Store(bed);
            if (s == null || side < 0 || side > 1) return;
            s.SetAttribute((ushort)(OwnerAttr + side * 2), (short)(ushort)(guid & 0xFFFF));
            s.SetAttribute((ushort)(OwnerAttr + side * 2 + 1), (short)(ushort)(guid >> 16));
        }

        /// <summary>A Sim's GUID (what a bed keeps).</summary>
        public static uint GuidOf(VMAvatar ava) { return ava?.Object?.OBJ?.GUID ?? 0; }

        /// <summary>
        /// Whose this side is - only someone who's still around: a Sim on the lot, or one of the family who lives
        /// here (out at work or school). A Sim who moved out or died doesn't hold on to a bed (0).
        /// </summary>
        public static uint Owner(VM vm, VMEntity bed, int side)
        {
            var guid = StoredOwner(bed, side);
            if (guid == 0) return 0;
            return StillHere(vm, guid) ? guid : 0;
        }

        public static bool StillHere(VM vm, uint guid)
        {
            if (vm == null || guid == 0) return false;
            if (FindSim(vm, guid) != null) return true;
            var fam = vm.TS1State?.CurrentFamily;
            return fam?.FamilyGUIDs != null && fam.FamilyGUIDs.Contains(guid);
        }

        /// <summary>The Sim with this GUID on the lot, or null.</summary>
        public static VMAvatar FindSim(VM vm, uint guid)
        {
            if (vm?.Context?.ObjectQueries?.Avatars == null) return null;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var a = e as VMAvatar;
                if (a != null && !a.Dead && GuidOf(a) == guid) return a;
            }
            return null;
        }

        /// <summary>Every bed on the lot (one entry per bed).</summary>
        public static List<VMEntity> AllBeds(VM vm)
        {
            var result = new List<VMEntity>();
            if (vm?.Entities == null) return result;
            var seen = new HashSet<VMMultitileGroup>();
            foreach (var e in vm.Entities)
            {
                if (!(e is VMGameObject) || e.MultitileGroup == null || e.Dead) continue;
                if (!seen.Add(e.MultitileGroup)) continue;
                var b = Store(e);
                if (b != null && IsBed(b)) result.Add(b);
            }
            return result;
        }

        /// <summary>The bed and side this Sim has (bed null if none).</summary>
        public static VMEntity BedOf(VM vm, VMAvatar sim, out int side)
        {
            side = -1;
            var guid = GuidOf(sim);
            if (guid == 0) return null;
            foreach (var bed in AllBeds(vm))
                for (int s = 0; s < 2; s++)
                    if (StoredOwner(bed, s) == guid && (s == 0 || IsDouble(bed))) { side = s; return bed; }
            return null;
        }

        /// <summary>
        /// Assign (the VM command does this, on every game alike): the Sim gives up any other bed or side they had,
        /// then takes this one (whoever had it before).
        /// </summary>
        public static void Assign(VM vm, VMAvatar sim, VMEntity bed, int side)
        {
            var guid = GuidOf(sim);
            if (guid == 0 || bed == null) return;
            Unassign(vm, sim, null);
            SetOwner(bed, side, guid);
        }

        /// <summary>The Sim gives up their bed (on this bed only, or any bed when bed is null).</summary>
        public static void Unassign(VM vm, VMAvatar sim, VMEntity bed)
        {
            var guid = GuidOf(sim);
            if (guid == 0) return;
            var beds = (bed != null) ? new List<VMEntity> { Store(bed) } : AllBeds(vm);
            foreach (var b in beds)
                for (int s = 0; s < 2; s++)
                    if (StoredOwner(b, s) == guid) SetOwner(b, s, 0);
        }

        // ------------------------------------------------------------------
        // Sleeping
        // ------------------------------------------------------------------

        /// <summary>Is someone using (or on their way to) this side of the bed? Worked out from the Sims' own queues - the same on every game.</summary>
        public static bool SideBusy(VM vm, VMEntity bed, int side, VMAvatar except)
        {
            if (vm?.Context?.ObjectQueries?.Avatars == null) return false;
            var group = bed?.MultitileGroup;
            if (group == null) return false;
            bool dbl = IsDouble(bed);
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var a = e as VMAvatar;
                if (a == null || a == except || a.Thread?.Queue == null || a.Thread.Queue.Count == 0) continue;
                var q = a.Thread.Queue[0];
                var c = q?.Callee;
                if (c == null || c.MultitileGroup != group) continue;
                if (!dbl || SideOf(c) == side) return true;
            }
            return false;
        }

        /// <summary>
        /// "Sleep" ordered from the menu: on a double bed where the Sim has a side, they take their own side (when
        /// nobody's already in it). Otherwise the bed piece that was clicked. Called by VMNetInteractionCmd on
        /// every game, from the lot as it is - so every player sends the Sim to the same side.
        /// </summary>
        public static VMEntity UserSleepTarget(VM vm, VMAvatar caller, VMEntity callee, int interaction, bool global)
        {
            try
            {
                if (vm == null || !vm.TS1 || caller == null || callee == null || global || caller.IsPet) return callee;
                if (!IsSleep(callee, interaction) || !IsBed(callee)) return callee;
                var sides = Sides(callee);
                if (sides.Count < 2) return callee;
                var guid = GuidOf(caller);
                int mine = -1;
                for (int s = 0; s < 2; s++) if (StoredOwner(callee, s) == guid) mine = s;
                if (mine < 0 || SideOf(callee) == mine) return callee;
                if (SideBusy(vm, callee, mine, caller)) return callee;
                var target = sides[mine];
                if (target == null || target.Dead || !IsSleep(target, interaction)) return callee;
                return target;
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] beds: " + e.Message); return callee; }
        }

        /// <summary>
        /// For the Sim's own choices (VMFindBestAction): how this bed side suits them. 0 = their own bed or side,
        /// 1 = nobody's (or the Sim's own bed is taken), 2 = someone else's. Not a bed's Sleep: -1.
        /// </summary>
        public static int Preference(VM vm, VMAvatar sim, VMEntity sideLeader, int ttaIndex)
        {
            try
            {
                if (vm == null || !vm.TS1 || sim == null || sim.IsPet || sideLeader == null) return -1;
                if (!IsSleep(sideLeader, ttaIndex) || !IsBed(sideLeader)) return -1;
                int side = SideOf(sideLeader);
                var owner = Owner(vm, sideLeader, side);
                var guid = GuidOf(sim);
                if (owner == 0) return 1;
                if (owner == guid) return 0;
                return 2;
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] beds: " + e.Message); return -1; }
        }

        /// <summary>
        /// A weight for the Sim's own choices: their own bed or side counts double, a side that's someone else's
        /// counts very little while another bed side that isn't someone else's is free (so it's only taken when
        /// there's nowhere else to sleep).
        /// </summary>
        public static float AutonomyWeight(VM vm, VMAvatar sim, VMEntity sideLeader, int ttaIndex)
        {
            var pref = Preference(vm, sim, sideLeader, ttaIndex);
            if (pref == 0) return 2f;
            if (pref == 2) return OtherFreeSide(vm, sim, sideLeader) ? 0.05f : 1f;
            if (pref == 1)
            {
                // (someone with a bed of their own that's free leaves the others' spare beds alone)
                int side;
                var own = BedOf(vm, sim, out side);
                if (own != null && own.MultitileGroup != sideLeader.MultitileGroup && !SideBusy(vm, own, side, sim)) return 0.3f;
            }
            return 1f;
        }

        /// <summary>Is there a bed side, other than this one, that's this Sim's own or nobody's, with nobody in it?</summary>
        public static bool OtherFreeSide(VM vm, VMAvatar sim, VMEntity except)
        {
            var guid = GuidOf(sim);
            bool child = sim.IsChild;
            foreach (var bed in AllBeds(vm))
            {
                if (bed.Position == LotTilePos.OUT_OF_WORLD) continue;
                var sides = Sides(bed);
                for (int s = 0; s < sides.Count; s++)
                {
                    if (except != null && sides[s] == except) continue;
                    var owner = Owner(vm, bed, s);
                    if (owner != 0 && owner != guid) continue;
                    if (SideBusy(vm, bed, s, sim)) continue;
                    // (a grown-up doesn't count a child's bed as somewhere to sleep)
                    if (!child && IsChildBed(bed)) continue;
                    return true;
                }
            }
            return false;
        }

        private static bool IsChildBed(VMEntity bed)
        {
            var b = Store(bed);
            var def = b?.MasterDefinition ?? b?.Object?.OBJ;
            var label = def?.ChunkLabel ?? "";
            return label.IndexOf("child", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("kid", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
