using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.3 (visiting part 2): "Better together!" Two PLAYERS' Sims learning the same skill side by side - a
    /// visitor and the host's Sim studying at the bookshelves, playing chess, cooking, working out, chatting - learn it
    /// BonusPercent (25%) faster.
    ///
    /// Worked out only from the lot's own state, so every game in the visit gets exactly the same answer:
    ///   - whose Sim it is: a visiting player's Sim has a player id (PersistID 100001 and up - the game's
    ///     ReduxOnlineInfo.FirstGuestID); the lot's own family (ReduxComputer.IsHouseholdSim) is the host's. Townies,
    ///     NPCs and pets belong to no player. Only Sims of two DIFFERENT players count - so it only ever happens while
    ///     another player is on the lot (a visit or a house party), never in plain single player;
    ///   - together: the other player's Sim is within Reach tiles on the same floor, and its current interaction is
    ///     with the same object (or the same kind of object, or one that teaches this skill - Buy mode's skill
    ///     ratings), with this Sim itself (a chat), or practises the same skill the Sims 2 way (ReduxSkillByDoing).
    /// Applied where a skill rises: a script raising it (ReduxPerkRules.SkillWrite - studying, chess, the exercise
    /// machine...) and ReduxSkillByDoing's minute of practice. Nothing is saved; no new lot data.
    /// Together fires (on every game alike) when the bonus is given - the game shows a "Better together!" note once a
    /// visit for its own player's Sim.
    /// </summary>
    public static class ReduxBetterTogether
    {
        public const int BonusPercent = 25;
        /// <summary>How close (tiles, either way) the other player's Sim has to be.</summary>
        public const int Reach = 6;
        /// <summary>A visiting player's Sim's id starts here (the game's ReduxOnlineInfo.FirstGuestID).</summary>
        public const uint FirstPlayerID = 100001;

        /// <summary>The bonus was given to this Sim (raised during the lot's tick - UI notes only, never the lot).</summary>
        public static Action<VMAvatar> Together;

        /// <summary>Which player a Sim belongs to: their id, 1 for the lot's own family (the host), 0 for nobody.</summary>
        private static uint PlayerOf(VM vm, VMAvatar ava)
        {
            if (ava == null || ava.Dead || ava.IsPet) return 0;
            if (ava.PersistID >= FirstPlayerID) return ava.PersistID;
            return ReduxComputer.IsHouseholdSim(vm, ava) ? 1u : 0u;
        }

        /// <summary>Buy mode's skill rating bits (OBJD RatingSkillFlags) for a skill, or -1.</summary>
        private static int SkillBit(VMPersonDataVariable v)
        {
            switch (v)
            {
                case VMPersonDataVariable.CookingSkill: return 0;
                case VMPersonDataVariable.MechanicalSkill: return 1;
                case VMPersonDataVariable.LogicSkill: return 2;
                case VMPersonDataVariable.BodySkill: return 3;
                case VMPersonDataVariable.CreativitySkill: return 4;
                case VMPersonDataVariable.CharismaSkill: return 5;
                default: return -1;
            }
        }

        private static bool Teaches(VMEntity obj, int bit)
        {
            if (obj == null || bit < 0) return false;
            var objd = obj.Object?.OBJ;
            return objd != null && (objd.RatingSkillFlags & (1 << bit)) != 0;
        }

        private static bool SameThing(VMEntity a, VMEntity b)
        {
            if (a == null || b == null) return false;
            if (a == b) return true;
            if (a.MultitileGroup != null && a.MultitileGroup == b.MultitileGroup) return true;
            // (the same kind of object: two exercise machines, two bookshelves side by side)
            var ga = a.Object?.OBJ?.GUID ?? 0; var gb = b.Object?.OBJ?.GUID ?? 0;
            return ga != 0 && ga == gb && !(a is VMAvatar);
        }

        /// <summary>Is another player's Sim learning this skill with this Sim right now?</summary>
        public static bool IsTogether(VM vm, VMAvatar ava, VMPersonDataVariable skill)
        {
            try
            {
                if (vm == null || !vm.TS1 || ava?.Thread == null) return false;
                uint me = PlayerOf(vm, ava);
                if (me == 0) return false;
                var mine = ava.Thread.ActiveAction;
                var myObj = mine?.Callee;
                if (myObj == null) return false;
                int bit = SkillBit(skill);
                var here = ava.Position;
                foreach (var e in vm.Context.ObjectQueries.Avatars)
                {
                    var o = e as VMAvatar;
                    if (o == null || o == ava || o.Thread == null) continue;
                    uint them = PlayerOf(vm, o);
                    if (them == 0 || them == me) continue;
                    var there = o.Position;
                    if (there.Level != here.Level) continue;
                    if (Math.Abs(there.TileX - here.TileX) > Reach || Math.Abs(there.TileY - here.TileY) > Reach) continue;
                    var act = o.Thread.ActiveAction;
                    var theirObj = act?.Callee;
                    if (theirObj == null) continue;
                    if (theirObj == ava || myObj == o) return true; // (talking together)
                    if (SameThing(myObj, theirObj)) return true;
                    if (!(theirObj is VMAvatar) && Teaches(theirObj, bit)) return true;
                    if (ReduxSkillByDoing.SkillOf(act) == (int)skill) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static void Tell(VMAvatar ava)
        {
            try { Together?.Invoke(ava); } catch (Exception) { }
        }

        /// <summary>
        /// ReduxPerkRules.SkillWrite: extra percent on a script raising a skill (value above the Sim's skill now) -
        /// BonusPercent when another player's Sim is learning it with them, else 0.
        /// </summary>
        public static int SkillBonus(VM vm, VMAvatar ava, VMPersonDataVariable skill, short value)
        {
            if (ava == null || vm == null || !vm.TS1) return 0;
            if (value <= ava.GetPersonData(skill)) return 0;
            if (!IsTogether(vm, ava, skill)) return 0;
            Tell(ava);
            return BonusPercent;
        }

        /// <summary>ReduxSkillByDoing: a minute's gain (hundredths of a point), BonusPercent more when together.</summary>
        public static int Gain(VMAvatar ava, VMPersonDataVariable skill, int gain)
        {
            if (gain <= 0 || ava?.Thread == null) return gain;
            var vm = ava.Thread.Context?.VM;
            if (!IsTogether(vm, ava, skill)) return gain;
            Tell(ava);
            return (int)((long)gain * (100 + BonusPercent) / 100);
        }
    }
}
