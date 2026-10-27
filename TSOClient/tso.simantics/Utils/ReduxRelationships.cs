using System;
using System.Collections.Generic;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.0 (Part B #1b extras, R1): relationship stages, romantic boundaries, breakups and divorce, weddings and
    /// commitment ceremonies, anniversaries - the lot's side. Simitone's Utils/ReduxRelationships.cs is the game's side (the
    /// side file, the stages shown on the Relationships page, the pie menu, the wedding planner, the honeymoon).
    ///
    /// The Sims 1 keeps a relationship as numbers each Sim has for the other (the neighbourhood's relationships):
    ///   [0] daily (-100..100), [1] in love today (0/1 - "do I love temp 0?"), [2] lifetime (-100..100), [3] the lasting
    ///   crush (0/1, set by the game when the lifetime relationship passes its romance mark - Propose asks for it).
    /// It has no stages, no engagement and no "married" flag: its own Propose is a wedding on the spot (and moving in).
    /// So the stages after Dating live on the Sims themselves while they're on a lot, as person-object attributes 85-89
    /// (the Sims 1 person scripts don't use attributes that high; ReduxWooHoo 70-77, ReduxAttraction 78, traits 80-84):
    ///   85 PartnerAttr   - the neighbour id of the Sim's partner (going steady, engaged, married, committed), 0 none
    ///   86 StateAttr     - bits 0-3 the stage with that partner (Stage), 4-10 romantic boundaries (B*), 11 on honeymoon,
    ///                      12 has Committed Partners too, 13 "on": the game that keeps the Sim's side file has put them here
    ///   87 HeartAttr     - heartbroken until this day (Today), 0 none
    ///   88 CelebrateAttr - the day of their wedding / ceremony / anniversary (a happy day), 0 none
    ///   89 HurtAttr      - the day they found their partner cheating on them, 0 none
    /// 85-87 are put there by the game that keeps the Sim (VMNetReduxRelationshipsCmd SetState: the lot's game for its own
    /// family and townies, a visiting player's game for their own Sim); everything else changes only in that command, on
    /// every game at the same moment, with the lot's own random numbers. A Sim without the "on" bit is plain Sims 1 (the
    /// Feature Switch "relationships" off on the game that keeps them).
    /// </summary>
    public static class VMReduxRelationships
    {
        public const int PartnerAttr = 85, StateAttr = 86, HeartAttr = 87, CelebrateAttr = 88, HurtAttr = 89;

        /// <summary>The stage with the partner in PartnerAttr. (Crush, Dating, Ex, friends and enemies are worked out, not kept.)</summary>
        public enum Stage : byte { None = 0, Steady = 1, Engaged = 2, Married = 3, Committed = 4, LifePartners = 5 }

        public const int StageMask = 0xF;
        /// <summary>Romantic boundaries: Custom (the player or a talk set them - otherwise they come from the relationship type),
        /// then what counts as cheating (flirting, kissing, WooHoo with someone else, romance with someone else at home),
        /// OK with their partner having other partners, will WooHoo with someone who has a partner.</summary>
        public const int BCustom = 1 << 4, BFlirt = 1 << 5, BKiss = 1 << 6, BWooHoo = 1 << 7, BHome = 1 << 8, BOkOthers = 1 << 9, BTaken = 1 << 10;
        public const int BoundaryBits = BFlirt | BKiss | BWooHoo | BHome | BOkOthers | BTaken;
        public const int HoneymoonBit = 1 << 11, CommittedBit = 1 << 12, OnBit = 1 << 13;
        /// <summary>The bits the game that keeps the Sim sets (the rest are the lot's).</summary>
        public const int OwnerBits = StageMask | BCustom | BoundaryBits | HoneymoonBit | CommittedBit | OnBit;

        /// <summary>What counts as cheating.</summary>
        public enum Cheat : byte { None = 0, Flirt = 1, Kiss = 2, WooHoo = 3 }

        /// <summary>Sims 1's "Food - Fruit Cake" (Food.iff, the base game): the cake at a wedding or ceremony.</summary>
        public const uint CakeGUID = 0x96ECF350;

        // ------------------------------------------------------------------ who

        private static bool Person(VMAvatar a) { return a != null && !a.IsPet && !a.Dead; }

        /// <summary>Is this Sim's relationship data on the lot (their game has the feature on)?</summary>
        public static bool On(VMAvatar a) { return Person(a) && (a.GetAttribute(StateAttr) & OnBit) != 0; }

        public static short Nid(VMAvatar a) { return a == null ? (short)0 : a.GetPersonData(VMPersonDataVariable.NeighborId); }

        public static Stage StageOf(VMAvatar a)
        {
            if (!On(a)) return Stage.None;
            var s = a.GetAttribute(StateAttr) & StageMask;
            return s <= (int)Stage.LifePartners ? (Stage)s : Stage.None;
        }

        /// <summary>The partner's neighbour id (0: none).</summary>
        public static short PartnerOf(VMAvatar a) { return On(a) ? a.GetAttribute(PartnerAttr) : (short)0; }

        /// <summary>Are a and b each other's partner (any stage)? The stage (None: they aren't).</summary>
        public static Stage Together(VMAvatar a, VMAvatar b)
        {
            if (!On(a) || !On(b) || a == b) return Stage.None;
            var nb = Nid(b);
            if (nb <= 0 || PartnerOf(a) != nb || PartnerOf(b) != Nid(a)) return Stage.None;
            return StageOf(a);
        }

        /// <summary>The partner, if they're on this lot.</summary>
        public static VMAvatar PartnerOnLot(VM vm, VMAvatar a)
        {
            var p = PartnerOf(a);
            if (vm == null || p <= 0) return null;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var o = e as VMAvatar;
                if (Person(o) && o != a && Nid(o) == p) return o;
            }
            return null;
        }

        // ------------------------------------------------------------------ boundaries

        /// <summary>The boundaries a relationship type starts with: Monogamous - all romance with someone else counts as
        /// cheating; Open - only bringing someone home; Polyamorous - nothing (and both are fine with other partners).</summary>
        public static int Defaults(ReduxAttraction.RelType t)
        {
            switch (t)
            {
                case ReduxAttraction.RelType.Open: return BHome | BOkOthers | BTaken;
                case ReduxAttraction.RelType.Polyamorous: return BOkOthers | BTaken;
                default: return BFlirt | BKiss | BWooHoo | BHome;
            }
        }

        /// <summary>The Sim's boundaries now (their own if set, otherwise their relationship type's).</summary>
        public static int Boundaries(VMAvatar a)
        {
            if (!Person(a)) return 0;
            int s = a.GetAttribute(StateAttr);
            if ((s & BCustom) != 0) return s & BoundaryBits;
            return Defaults(ReduxAttraction.TypeOf(a));
        }

        /// <summary>Does this kind of romance with someone else count as cheating to "partner"? (atHome: on the partner's home lot)</summary>
        public static bool Counts(VMAvatar partner, Cheat kind, bool atHome)
        {
            if (!On(partner) || kind == Cheat.None) return false;
            int b = Boundaries(partner);
            if (atHome && (b & BHome) != 0) return true;
            switch (kind)
            {
                case Cheat.Flirt: return (b & BFlirt) != 0;
                case Cheat.Kiss: return (b & BKiss) != 0;
                case Cheat.WooHoo: return (b & BWooHoo) != 0;
            }
            return false;
        }

        /// <summary>Does a have a partner who isn't b (going steady, engaged, married, or Committed Partners)?</summary>
        public static bool Taken(VMAvatar a, VMAvatar b)
        {
            if (!On(a)) return false;
            var p = PartnerOf(a);
            if (p > 0 && (b == null || p != Nid(b))) return true;
            // (Committed Partners are only in the side file: someone a isn't in love with isn't one of them)
            return (a.GetAttribute(StateAttr) & CommittedBit) != 0 && (b == null || !ReduxWooHoo.InLove(a, b));
        }

        /// <summary>
        /// WooHoo (VMNetReduxWooHooCmd Start): b turns a down because a has a partner and b won't WooHoo with someone who
        /// has one (b's boundaries). Only when both Sims' games have the feature on.
        /// </summary>
        public static bool RefusesTaken(VMAvatar b, VMAvatar a)
        {
            try
            {
                if (!On(a) || !On(b)) return false;
                if ((Boundaries(b) & BTaken) != 0) return false;
                return Taken(a, b);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ the day

        /// <summary>The lot's day as a number that only goes up (1 = 1 January 1997 on Sims 1's 30-day months).</summary>
        public static short Today(VM vm)
        {
            var c = vm?.Context?.Clock;
            if (c == null) return 1;
            int abs = (c.Year - 1997) * 360 + (c.Month - 1) * 30 + (c.DayOfMonth - 1);
            return (short)Math.Max(1, Math.Min(32000, abs + 1));
        }

        public static bool Heartbroken(VM vm, VMAvatar a) { return Person(a) && a.GetAttribute(HeartAttr) > Today(vm); }
        public static bool Celebrating(VM vm, VMAvatar a) { return Person(a) && a.GetAttribute(CelebrateAttr) == Today(vm); }
        public static bool HurtToday(VM vm, VMAvatar a) { return Person(a) && a.GetAttribute(HurtAttr) == Today(vm); }
        public static bool OnHoneymoon(VMAvatar a) { return On(a) && (a.GetAttribute(StateAttr) & HoneymoonBit) != 0; }

        // ------------------------------------------------------------------ changing the lot (only from the command)

        /// <summary>a's numbers for b: daily and lifetime up or down (-100..100).</summary>
        public static void Closer(VMAvatar from, VMAvatar to, int daily, int life)
        {
            var r = ReduxWooHoo.Rel(from, to);
            if (r == null) return;
            while (r.Count < 4) r.Add(0);
            r[0] = (short)Math.Max(-100, Math.Min(100, r[0] + daily));
            r[2] = (short)Math.Max(-100, Math.Min(100, r[2] + life));
        }

        /// <summary>Sims 1's own "in love" numbers, both ways (the heart on the original's relationships, and what it asks for kisses and proposals).</summary>
        public static void SetLove(VMAvatar a, VMAvatar b, bool love)
        {
            foreach (var pair in new[] { new[] { a, b }, new[] { b, a } })
            {
                var r = ReduxWooHoo.Rel(pair[0], pair[1]);
                if (r == null) continue;
                while (r.Count < 4) r.Add(0);
                r[1] = (short)(love ? 1 : 0);
                r[3] = (short)(love ? 1 : 0);
            }
        }

        public static void Motive(VMAvatar a, VMMotive m, int by)
        {
            if (!Person(a)) return;
            a.SetMotiveData(m, (short)Math.Max(-100, Math.Min(100, a.GetMotiveData(m) + by)));
        }

        /// <summary>a and b become partners at this stage (the lot holds it until their games' side files say the same).</summary>
        public static void Link(VMAvatar a, VMAvatar b, Stage stage)
        {
            foreach (var pair in new[] { new[] { a, b }, new[] { b, a } })
            {
                var s = pair[0];
                int st = s.GetAttribute(StateAttr) & ~StageMask & ~HoneymoonBit;
                s.SetAttribute(PartnerAttr, Nid(pair[1]));
                s.SetAttribute(StateAttr, (short)(st | (int)stage | OnBit));
            }
        }

        /// <summary>a and b aren't partners any more (each only if the other was their partner).</summary>
        public static void Unlink(VMAvatar a, VMAvatar b)
        {
            foreach (var pair in new[] { new[] { a, b }, new[] { b, a } })
            {
                var s = pair[0];
                if (s.GetAttribute(PartnerAttr) != Nid(pair[1])) continue;
                s.SetAttribute(PartnerAttr, 0);
                s.SetAttribute(StateAttr, (short)(s.GetAttribute(StateAttr) & ~StageMask & ~HoneymoonBit));
            }
        }

        /// <summary>Puts the cake down for a ceremony: on a free table or counter near "near" (nearest first), else on the floor nearby.</summary>
        public static bool PlaceCake(VM vm, VMAvatar near)
        {
            if (vm == null || near == null || near.Position == LotTilePos.OUT_OF_WORLD) return false;
            VMMultitileGroup group = null;
            try { group = vm.Context.CreateObjectInstance(CakeGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH); } catch { group = null; }
            if (group?.BaseObject == null) return false;
            var cake = group.BaseObject;
            try
            {
                // (in a fixed order - distance, then object id - so every game picks the same surface)
                var surfaces = vm.Entities
                    .Where(e => e is VMGameObject && !e.Dead && e != cake && e.Position != LotTilePos.OUT_OF_WORLD && e.Position.Level == near.Position.Level
                        && e.TotalSlots() > 0 && e.GetSlot(0) == null && e.Container == null && IsSurface(e))
                    .Select(e => new { E = e, D = Math.Abs(e.Position.TileX - near.Position.TileX) + Math.Abs(e.Position.TileY - near.Position.TileY) })
                    .Where(x => x.D <= 14)
                    .OrderBy(x => x.D).ThenBy(x => x.E.ObjectID)
                    .ToList();
                foreach (var s in surfaces)
                    if (s.E.PlaceInSlot(cake, 0, true, vm.Context)) return true;
                var arch = vm.Context.Architecture;
                for (int r = 1; r <= 4; r++)
                    for (int dx = -r; dx <= r; dx++)
                        for (int dy = -r; dy <= r; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                            int x = near.Position.TileX + dx, y = near.Position.TileY + dy;
                            if (x < 1 || y < 1 || x >= arch.Width - 1 || y >= arch.Height - 1) continue;
                            var pos = LotTilePos.FromBigTile((short)x, (short)y, near.Position.Level);
                            if (group.ChangePosition(pos, Direction.NORTH, vm.Context, VMPlaceRequestFlags.Default).Status == VMPlacementError.Success)
                            {
                                group.ExecuteEntryPoint(11, vm.Context);
                                return true;
                            }
                        }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] relationships: the cake: " + e.Message); }
            group.Delete(vm.Context);
            return false;
        }

        private static bool IsSurface(VMEntity e)
        {
            var l = (e.Object?.OBJ?.ChunkLabel ?? "").ToLowerInvariant();
            return l.Contains("table") || l.Contains("counter") || l.Contains("bar - ");
        }

        // ------------------------------------------------------------------ moving out (only from the command; moving in is
        //                                                                    R2's VMReduxMatch.MoveIn - VMNetReduxMatchCmd MoveIn)

        /// <summary>The family playing here (null: none).</summary>
        public static Files.Formats.IFF.Chunks.FAMI Family(VM vm) { return vm?.TS1State?.CurrentFamily; }

        public static bool InFamily(VM vm, VMAvatar a)
        {
            var fam = Family(vm);
            return fam != null && a != null && (fam.FamilyGUIDs ?? new uint[0]).Contains(a.Object.OBJ.GUID);
        }

        /// <summary>The grown-ups in the family (on the lot or not).</summary>
        public static int GrownUps(VM vm)
        {
            var fam = Family(vm);
            if (fam?.FamilyGUIDs == null) return 0;
            var neigh = Content.Content.Get().Neighborhood;
            int n = 0;
            foreach (var g in fam.FamilyGUIDs)
            {
                var nb = neigh?.Neighbors?.Entries?.FirstOrDefault(x => x != null && x.GUID == g);
                if (nb?.PersonData == null || nb.PersonData.Length <= (int)VMPersonDataVariable.Gender) continue;
                if ((nb.PersonData[(int)VMPersonDataVariable.Gender] & (8 | 16)) != 0) continue; // (pets)
                if (nb.PersonData.Length > (int)VMPersonDataVariable.PersonsAge && nb.PersonData[(int)VMPersonDataVariable.PersonsAge] < 18) continue;
                n++;
            }
            return n;
        }

        /// <summary>Why a can't move out (null: they can).</summary>
        public static string CantMoveOut(VM vm, VMAvatar a)
        {
            if (!Person(a) || !ReduxWooHoo.Grownup(a)) return "";
            if (!InFamily(vm, a)) return "";
            if (GrownUps(vm) <= 1) return (a.Name ?? "They") + " is the only grown-up in the family - someone has to stay.";
            return null;
        }

        /// <summary>a leaves the family: a townie from now on (in the neighbourhood, out and about), and walks off the lot.</summary>
        public static bool MoveOut(VM vm, VMAvatar a)
        {
            var fam = Family(vm);
            if (fam == null || CantMoveOut(vm, a) != null) return false;
            var guid = a.Object.OBJ.GUID;
            var neigh = Content.Content.Get().Neighborhood;
            var n = neigh.GetNeighborByID(Nid(a));
            if (n?.PersonData != null && n.PersonData.Length > (int)VMPersonDataVariable.TS1FamilyNumber)
                n.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber] = 0;
            fam.FamilyGUIDs = (fam.FamilyGUIDs ?? new uint[0]).Where(x => x != guid).ToArray();
            fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Where(x => x != guid).ToArray();
            a.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, 0);
            a.SetPersonData(VMPersonDataVariable.PersonType, 1); // (a visitor now)
            a.SetPersonData(VMPersonDataVariable.GreetStatus, 2); // (on their way)
            a.SetValue(VMStackObjectVariable.MovementFlags, (short)(a.GetValue(VMStackObjectVariable.MovementFlags) | (short)VMMovementFlags.FSOExcludeUserSave));
            // (the game hands control of them to someone else first - Simitone's ReduxRelationships.AskMoveOut)
            if (a.Thread?.Queue != null)
                foreach (var q in a.Thread.Queue.ToList()) a.Thread.CancelAction(q.UID);
            NetPlay.Model.Commands.VMNetReduxTownieCmd.PushLeave(vm, a);
            return true;
        }

        // ------------------------------------------------------------------ the game's own wedding (VMRoutine's hook)

        /// <summary>Sims 1's own Propose ended in a wedding (the two Sims, on every game).</summary>
        public static event Action<VM, VMAvatar, VMAvatar> Married;

        /// <summary>The script Sims 1's own wedding runs once the couple has said yes ("Wedding Picture" - the photo of the two).</summary>
        public static bool IsHookLabel(string label)
        {
            return (label ?? "").Trim().Equals("Wedding Picture", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The game's own Propose ended in a wedding: the two are Married on the lot (their games' side files keep it - the
        /// Married event). Runs the same on every game; never changes what the script does.
        /// </summary>
        public static void Hook(VMStackFrame frame)
        {
            try
            {
                var vm = frame?.VM;
                if (vm == null || !vm.TS1) return;
                // (the social's two people: attributes 6 (the one proposing) and 0 (the one saying yes))
                VMAvatar a = null, b = null;
                var social = frame.Callee;
                if (social != null && !(social is VMAvatar))
                {
                    a = vm.GetObjectById(social.GetAttribute(6)) as VMAvatar;
                    b = vm.GetObjectById(social.GetAttribute(0)) as VMAvatar;
                }
                if (!Person(a)) a = frame.Caller as VMAvatar;
                if (!Person(b) || b == a) b = frame.StackObject as VMAvatar;
                if (!Person(a) || !Person(b) || a == b) return;
                if (On(a) || On(b))
                {
                    Link(a, b, Stage.Married);
                    var today = Today(vm);
                    a.SetAttribute(CelebrateAttr, today);
                    b.SetAttribute(CelebrateAttr, today);
                }
                try { Married?.Invoke(vm, a, b); } catch (Exception e) { Console.WriteLine("[SimsRedux] relationships: " + e.Message); }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] relationships hook: " + e.Message); }
        }
    }
}
