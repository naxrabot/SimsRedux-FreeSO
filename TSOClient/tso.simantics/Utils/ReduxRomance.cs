using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.0 (Part B #1b "Romance and closeness"): the lot's side. Family-friendly: nothing is ever shown - romance
    /// is hearts, a privacy cloud and plain words. Simitone's Utils/ReduxRomance.cs is the game's side (the side file, the
    /// words, what's offered); VMNetReduxRomanceCmd (VM command 79) makes every change, so every game on a visit agrees.
    ///
    /// Kept on the Sims themselves, as person-object attributes 95-99 (the Sims 1 person scripts don't use attributes that
    /// high; WooHoo has 70-77, attraction 78, traits 80-84, R1 85-89, R2 90-94):
    ///   95 SkillAttr - the Romance skill, 0-1000 (100 a level, like the game's own skills). Put on the lot by the game that
    ///                  keeps the Sim (SetSkill, from its side file); raised on the lot by flirting, romantic choices,
    ///                  Practice Romance and chatting - the keeping game then saves the higher number.
    ///   96 ActAttr   - when the Sim last did a romantic choice (the lot's clock in game minutes, wrapped, + 1; 0 never).
    ///   97 PerkAttr  - their account's romance perks from the Perk Tree (Irresistible 1, Smooth Talker 2, Faithful Heart 4).
    ///   98 DateAttr  - when the Sim last had a "great date" (as ActAttr).
    ///   99 (kept free for later).
    /// Attributes are saved with the lot and sent to everyone joining; nothing new goes into the save format.
    /// </summary>
    public static class VMReduxRomance
    {
        public const int SkillAttr = 95, ActAttr = 96, PerkAttr = 97, DateAttr = 98;
        public const int MaxSkill = 1000;
        public const short PerkIrresistible = 1, PerkSmoothTalker = 2, PerkFaithfulHeart = 4;
        /// <summary>Game minutes between romantic choices (Practice Romance: PracticeCooldown), and between great dates.</summary>
        public const int ActCooldown = 30, PracticeCooldown = 60, DateCooldown = 360, Wrap = 30000;
        /// <summary>17.0: game minutes before a child can have the birds and the bees talk again (a week - under Wrap).</summary>
        public const int BirdsBeesCooldown = 7 * 24 * 60;

        // ------------------------------------------------------------------ the switch (lot-wide, from the lot's game)

        /// <summary>Until the lot's game has said (SetSwitch): off - every game on a visit does the same.</summary>
        public const bool UntoldOn = false;
        private static readonly ConditionalWeakTable<VM, StrongBox<bool>> Switch = new ConditionalWeakTable<VM, StrongBox<bool>>();

        public static bool On(VM vm)
        {
            if (vm == null || !vm.TS1) return false;
            StrongBox<bool> b;
            lock (Switch) { if (Switch.TryGetValue(vm, out b)) return b.Value; }
            return UntoldOn;
        }

        public static bool Told(VM vm, out bool on)
        {
            on = UntoldOn;
            if (vm == null) return false;
            StrongBox<bool> b;
            lock (Switch) { if (!Switch.TryGetValue(vm, out b)) return false; }
            on = b.Value;
            return true;
        }

        public static void SetOn(VM vm, bool on)
        {
            if (vm == null) return;
            lock (Switch) { Switch.GetOrCreateValue(vm).Value = on; }
        }

        private static VM VMOf(VMAvatar a) { return a?.Thread?.Context?.VM; }
        private static bool Person(VMAvatar a) { return a != null && !a.IsPet && !a.Dead; }

        // ------------------------------------------------------------------ skill and perks

        public static int Skill(VMAvatar a) { return Person(a) ? Math.Max(0, Math.Min(MaxSkill, (int)a.GetAttribute(SkillAttr))) : 0; }
        public static int Level(VMAvatar a) { return Skill(a) / 100; }
        public static bool Perk(VMAvatar a, short perk) { return Person(a) && (a.GetAttribute(PerkAttr) & perk) != 0; }

        /// <summary>
        /// 17.0: a visiting player's Sim (their perks come from their own game - the lot can't check them) with one of the
        /// lot's own Sims: perks only give a small bonus (GuestPerkCap) there.
        /// </summary>
        public static bool GuestOnHost(VMAvatar visitor, VMAvatar lotSim) { return Visitor(visitor) && lotSim != null && !Visitor(lotSim); }
        public const int GuestPerkCap = 8;

        /// <summary>Raises the Romance skill (Smooth Talker: half as much again). Only from the lot's own scripts and commands.</summary>
        public static void Gain(VMAvatar a, int points)
        {
            if (!Person(a) || points <= 0 || a.IsChild) return;
            if (Perk(a, PerkSmoothTalker)) points += (points + 1) / 2;
            a.SetAttribute(SkillAttr, (short)Math.Min(MaxSkill, Skill(a) + points));
        }

        /// <summary>The lot's clock in game minutes, wrapped to fit an attribute.</summary>
        public static int Clock(VM vm)
        {
            var c = vm.Context.Clock;
            long m = ((((long)c.Year * 12 + c.Month) * 31 + c.DayOfMonth) * 24 + c.Hours) * 60 + c.Minutes;
            return (int)(((m % Wrap) + Wrap) % Wrap);
        }

        /// <summary>Game minutes since the clock stamp in this attribute (int.MaxValue: never).</summary>
        public static int Since(VM vm, VMAvatar a, int attr)
        {
            if (vm == null || a == null) return int.MaxValue;
            int last = a.GetAttribute(attr);
            if (last <= 0 || last > Wrap) return int.MaxValue;
            return ((Clock(vm) - (last - 1)) % Wrap + Wrap) % Wrap;
        }

        public static void Stamp(VM vm, VMAvatar a, int attr) { a?.SetAttribute(attr, (short)(Clock(vm) + 1)); }

        /// <summary>Game minutes until this Sim can do another romantic choice (0: now).</summary>
        public static int ActWait(VM vm, VMAvatar a, Choice c)
        {
            int cd = c == Choice.PracticeRomance ? PracticeCooldown : ActCooldown;
            int since = Since(vm, a, ActAttr);
            return since >= cd ? 0 : cd - since;
        }

        // ------------------------------------------------------------------ "not in the mood"

        public enum Mood : byte { Fine = 0, Tired = 1, Hungry = 2, Upset = 3, Sad = 4, Awkward = 5 }

        /// <summary>
        /// Would "who" turn down romance from "asker" right now (tired, hungry or needing the bathroom, upset with them, or
        /// sad)? Traits and the asker's perks make it more or less likely. Reads the lot only - every game agrees.
        /// </summary>
        public static Mood NotInTheMood(VMAvatar who, VMAvatar asker, bool woohoo)
        {
            if (!Person(who) || !Person(asker) || !On(VMOf(who))) return Mood.Fine;
            int slack = 0;
            if (VMReduxTraits.Has(who, VMReduxTraits.T.Romantic) || VMReduxTraits.Has(who, VMReduxTraits.T.Flirty)) slack += 15;
            if (VMReduxTraits.Has(who, VMReduxTraits.T.Shy) || VMReduxTraits.Has(who, VMReduxTraits.T.Loner)) slack -= 12;
            if (VMReduxTraits.Has(who, VMReduxTraits.T.Gloomy)) slack -= 5;
            int perks = 0;
            if (Perk(asker, PerkIrresistible)) perks += 20;
            if (Perk(asker, PerkFaithfulHeart) && ReduxWooHoo.InLove(who, asker)) perks += 25;
            slack += GuestOnHost(asker, who) ? Math.Min(perks, GuestPerkCap) : perks;
            if (woohoo) slack -= 5;
            if (who.GetMotiveData(VMMotive.Energy) < -55 - slack) return Mood.Tired;
            if (who.GetMotiveData(VMMotive.Hunger) < -65 - slack || who.GetMotiveData(VMMotive.Bladder) < -65 - slack) return Mood.Hungry;
            var r = ReduxWooHoo.Rel(who, asker);
            if (r != null && r.Count > 0 && r[0] < -10 - slack / 2) return Mood.Upset;
            if (who.GetMotiveData(VMMotive.Mood) < -45 - slack) return Mood.Sad;
            return Mood.Fine;
        }

        /// <summary>Someone said no (who, to asker, why) - from a social's answer, a romantic choice or WooHoo. On every game.</summary>
        public static event Action<VM, VMAvatar, VMAvatar, Mood> Refused;

        public static void FireRefused(VM vm, VMAvatar who, VMAvatar asker, Mood why)
        {
            try { Refused?.Invoke(vm, who, asker, why); } catch (Exception e) { Console.WriteLine("[SimsRedux] romance: " + e.Message); }
        }

        /// <summary>
        /// A romantic social's "will they say yes?" (ReduxAttraction.Hook, after the attraction test passed): flirting
        /// raises the asker's Romance skill; a Sim not in the mood says no; and a beginner's flirt can come out awkward
        /// (less often as the skill grows - never from level 3, or with Irresistible). True: the answer is no.
        /// </summary>
        public static bool Decide(VMStackFrame frame, VMAvatar me, VMAvatar other, bool propose)
        {
            try
            {
                var vm = frame?.VM;
                if (vm == null || !On(vm) || !Person(me) || !Person(other) || me == other) return false;
                if (me.IsChild || other.IsChild) return false;
                if (!propose) Gain(me, 6);
                var mood = NotInTheMood(other, me, false);
                if (mood != Mood.Fine)
                {
                    FireRefused(vm, other, me, mood);
                    return true;
                }
                if (propose || (Perk(me, PerkIrresistible) && !GuestOnHost(me, other))) return false;
                int awkward = Math.Max(0, 12 - Level(me) * 4);
                if (awkward > 0 && (int)vm.Context.NextRandom(100) < awkward)
                {
                    FireRefused(vm, other, me, Mood.Awkward);
                    return true;
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] romance decide: " + e.Message); }
            return false;
        }

        // ------------------------------------------------------------------ romantic choices

        public enum Choice : byte
        {
            None = 0, CuddleSofa = 1, SlowDance = 2, Stargaze = 3, HoldHands = 4, FeedEachOther = 5, LovePoem = 6, Serenade = 7,
            GiveRose = 8, PracticeRomance = 9, BirdsBees = 10,
        }

        /// <summary>The romantic choices between two Sims, in menu order.</summary>
        public static readonly Choice[] Couple =
        {
            Choice.HoldHands, Choice.GiveRose, Choice.LovePoem, Choice.SlowDance, Choice.Stargaze, Choice.Serenade,
            Choice.CuddleSofa, Choice.FeedEachOther,
        };

        public static string Label(Choice c)
        {
            switch (c)
            {
                case Choice.CuddleSofa: return "Cuddle on the Sofa";
                case Choice.SlowDance: return "Slow Dance";
                case Choice.Stargaze: return "Stargaze Together";
                case Choice.HoldHands: return "Hold Hands";
                case Choice.FeedEachOther: return "Feed Each Other";
                case Choice.LovePoem: return "Read a Love Poem";
                case Choice.Serenade: return "Serenade";
                case Choice.GiveRose: return "Give a Rose";
                case Choice.PracticeRomance: return "Practice Romance";
                case Choice.BirdsBees: return "The Birds and the Bees";
            }
            return "";
        }

        /// <summary>Is this the name of one of these choices' actions (for the hearts drawn over them)?</summary>
        public static bool IsRomanceAction(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var c in Couple) if (name == Label(c)) return true;
            return false;
        }

        /// <summary>How close (the asker's daily relationship) they have to be first.</summary>
        public static int MinDaily(Choice c)
        {
            switch (c)
            {
                case Choice.HoldHands: case Choice.GiveRose: case Choice.LovePoem: return 15;
                case Choice.SlowDance: case Choice.Stargaze: case Choice.Serenade: return 35;
                case Choice.CuddleSofa: case Choice.FeedEachOther: return 50;
            }
            return 0;
        }

        /// <summary>How long they're at it (game minutes).</summary>
        public static int Minutes(Choice c)
        {
            switch (c)
            {
                case Choice.HoldHands: case Choice.GiveRose: return 10;
                case Choice.LovePoem: return 15;
                case Choice.SlowDance: case Choice.Serenade: case Choice.FeedEachOther: return 25;
                case Choice.Stargaze: case Choice.CuddleSofa: return 40;
                case Choice.PracticeRomance: return 30;
                case Choice.BirdsBees: return 20;
            }
            return 10;
        }

        private static bool LabelHas(VMEntity o, params string[] words)
        {
            var b = o?.MultitileGroup?.BaseObject ?? o;
            var label = ((b?.Object?.OBJ?.ChunkLabel ?? "") + " " + (o?.Object?.OBJ?.ChunkLabel ?? "")).ToLowerInvariant();
            foreach (var w in words) if (label.Contains(w)) return true;
            return false;
        }

        private static bool HasInteractionStarting(VMEntity obj, string start, out VMEntity piece, out TTABInteraction found)
        {
            piece = null; found = null;
            var group = obj?.MultitileGroup?.Objects ?? (obj == null ? new List<VMEntity>() : new List<VMEntity> { obj });
            foreach (var o in group)
            {
                if (o?.TreeTable == null || o.TreeTableStrings == null) continue;
                foreach (var ia in o.TreeTable.Interactions)
                    if (ReduxWooHoo.InteractionName(o, ia).StartsWith(start, StringComparison.OrdinalIgnoreCase)) { piece = o; found = ia; return true; }
            }
            return false;
        }

        /// <summary>Does this object work for the choice (a sofa to cuddle on, a telescope, a piano or guitar, a mirror)?</summary>
        public static bool Suits(VMEntity obj, Choice c)
        {
            if (obj == null || obj is VMAvatar || obj.Dead || obj.Position == LotTilePos.OUT_OF_WORLD) return false;
            VMEntity p; TTABInteraction ia;
            switch (c)
            {
                case Choice.CuddleSofa: return ReduxWooHoo.IsCouch(obj);
                case Choice.Stargaze:
                    return ReduxWooHoo.FindInteraction(obj, "Stargaze", out ia) != null || (LabelHas(obj, "telescope") && HasInteractionStarting(obj, "", out p, out ia));
                case Choice.Serenade: return LabelHas(obj, "piano", "guitar") && HasInteractionStarting(obj, "Play", out p, out ia);
                case Choice.PracticeRomance: return ReduxWooHoo.FindInteraction(obj, "Practice Speech", out ia) != null;
            }
            return false;
        }

        /// <summary>Does the choice need an object (Suits), or can it happen anywhere?</summary>
        public static bool NeedsObject(Choice c)
        {
            return c == Choice.CuddleSofa || c == Choice.Serenade || c == Choice.PracticeRomance;
        }

        /// <summary>Stargazing without a telescope: only in the evening or at night, outside.</summary>
        public static bool StarsOut(VM vm)
        {
            int h = vm?.Context?.Clock?.Hours ?? 12;
            return h >= 20 || h < 5;
        }

        /// <summary>The nearest object on the lot that suits the choice (base pieces), or null.</summary>
        public static VMEntity Nearest(VM vm, VMAvatar near, Choice c)
        {
            VMEntity best = null;
            float bestD = float.MaxValue;
            if (vm == null || near == null) return null;
            foreach (var e in vm.Entities)
            {
                if (e is VMAvatar || e.Dead || e.MultitileGroup == null || e.MultitileGroup.BaseObject != e) continue;
                if (e.Position == LotTilePos.OUT_OF_WORLD || !Suits(e, c)) continue;
                float dx = (e.Position.x - near.Position.x) / 16f, dy = (e.Position.y - near.Position.y) / 16f;
                float d = dx * dx + dy * dy + (e.Position.Level != near.Position.Level ? 400 : 0);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>Somewhere outside to stand together (the nearest outdoor object on the ground floor), or null.</summary>
        public static VMEntity Outside(VM vm, VMAvatar near)
        {
            VMEntity best = null;
            float bestD = float.MaxValue;
            if (vm?.Context?.RoomInfo == null || near == null) return null;
            foreach (var e in vm.Entities)
            {
                if (e is VMAvatar || e.Dead || e.MultitileGroup == null || e.MultitileGroup.BaseObject != e) continue;
                if (e.Position == LotTilePos.OUT_OF_WORLD || e.Position.Level != 1) continue;
                var room = vm.Context.GetObjectRoom(e);
                if (room >= vm.Context.RoomInfo.Length || !vm.Context.RoomInfo[room].Room.IsOutside) continue;
                float dx = (e.Position.x - near.Position.x) / 16f, dy = (e.Position.y - near.Position.y) / 16f;
                float d = dx * dx + dy * dy;
                if (d < 4) continue; // (not right on top of them)
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>Puts one of the object's own menu entries in the Sim's queue under another name (its test skipped).</summary>
        public static bool PushOwnNamed(VM vm, VMAvatar sim, VMEntity piece, TTABInteraction ia, string name)
        {
            if (sim?.Thread == null || piece == null || ia == null) return false;
            var action = piece.GetAction((int)ia.TTAIndex, sim, vm.Context, false, new short[4]);
            if (action == null) return false;
            action.CheckRoutine = null;
            action.Name = name;
            action.Priority = (short)VMQueuePriority.UserDriven;
            action.Flags |= TTABFlags.FSOSkipPermissions;
            sim.Thread.EnqueueAction(action);
            return true;
        }

        private static void Motive(VMAvatar a, VMMotive m, int add)
        {
            if (a == null) return;
            a.SetMotiveData(m, (short)Math.Max(-100, Math.Min(100, a.GetMotiveData(m) + add)));
        }

        /// <summary>Daily and lifetime relationship from one to the other (the game's own numbers, -100 to 100).</summary>
        public static void Closer(VMAvatar from, VMAvatar to, int daily, int life)
        {
            var r = ReduxWooHoo.Rel(from, to);
            if (r == null) return;
            while (r.Count < 3) r.Add(0);
            r[0] = (short)Math.Max(-100, Math.Min(100, r[0] + daily));
            r[2] = (short)Math.Max(-100, Math.Min(100, r[2] + life));
        }

        public static int Daily(VMAvatar from, VMAvatar to)
        {
            var r = ReduxWooHoo.Rel(from, to);
            return (r != null && r.Count > 0) ? r[0] : 0;
        }

        /// <summary>Can "a" offer this choice to "b" at all (who, how close, attraction)? null: yes; else why not ("" = say nothing).</summary>
        public static string CanDo(VM vm, VMAvatar a, VMAvatar b, Choice c)
        {
            if (vm == null || !On(vm) || !Person(a) || a.Thread == null) return "";
            if (c == Choice.PracticeRomance) return ReduxWooHoo.Grownup(a) ? null : "";
            if (!Person(b) || a == b || b.Position == LotTilePos.OUT_OF_WORLD) return "";
            if (c == Choice.BirdsBees)
            {
                if (!ReduxWooHoo.Grownup(a) || !b.IsChild) return "";
                if (a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) != b.GetPersonData(VMPersonDataVariable.TS1FamilyNumber)) return "";
                return null;
            }
            if (!ReduxWooHoo.Grownup(a) || !ReduxWooHoo.Grownup(b)) return "";
            if (ReduxWooHoo.Busy(a) || ReduxWooHoo.Busy(b)) return "";
            if (!ReduxAttraction.Mutual(a, b)) return "";
            if (Daily(a, b) < MinDaily(c)) return "";
            return null;
        }

        /// <summary>
        /// Does a romantic choice (only VMNetReduxRomanceCmd calls this): both Sims go and do it, and the effects follow.
        /// agreed: b's player already said yes (b is another player's Sim). Returns null when it went ahead (ok: it went
        /// well), else why not.
        /// </summary>
        public static string Act(VM vm, VMAvatar a, VMAvatar b, VMEntity obj, Choice c, bool agreed, out bool ok, out Mood mood)
        {
            ok = false; mood = Mood.Fine;
            var why = CanDo(vm, a, b, c);
            if (why != null) return why.Length > 0 ? why : "That can't be done right now.";
            if (a.Thread.Queue.Count >= VMThread.MAX_USER_ACTIONS) return "";
            int wait = ActWait(vm, a, c);
            if (wait > 0 && c != Choice.BirdsBees) return (a.Name ?? "They") + " did something romantic not long ago - try again in about " + Math.Max(1, wait) + " game minutes.";
            if (NeedsObject(c) && !Suits(obj, c)) return "There's nothing here to do that with.";
            if (c == Choice.Stargaze && !Suits(obj, c))
            {
                if (!StarsOut(vm)) return "The stars aren't out yet - try in the evening, or with a telescope.";
                obj = Outside(vm, a);
            }
            int perMinute = Math.Max(1, vm.Context.Clock.TicksPerMinute);
            int ticks = perMinute * Minutes(c);
            var name = Label(c);

            if (c == Choice.PracticeRomance)
            {
                TTABInteraction ia;
                var piece = ReduxWooHoo.FindInteraction(obj, "Practice Speech", out ia);
                if (piece == null || !PushOwnNamed(vm, a, piece, ia, name)) ReduxWooHoo.PushWait(vm, a, obj, ticks, name);
                Stamp(vm, a, ActAttr);
                Gain(a, Level(a) < 3 ? 25 : 15);
                Motive(a, VMMotive.Fun, 5);
                ok = true;
                return null;
            }

            if (c == Choice.BirdsBees)
            {
                // (17.0: once is plenty - the child remembers it for BirdsBeesCooldown game minutes, on the child's ActAttr,
                //  which children don't otherwise use)
                if (Since(vm, b, ActAttr) < BirdsBeesCooldown) return (b.Name ?? "They") + " has already had that talk - once is plenty!";
                Stamp(vm, b, ActAttr);
                ReduxWooHoo.PushWait(vm, a, b, ticks, name);
                ReduxWooHoo.PushWait(vm, b, a, ticks, name);
                Motive(a, VMMotive.Social, 10);
                Motive(b, VMMotive.Social, 10);
                Motive(b, VMMotive.Fun, 5);
                Closer(a, b, 5, 2);
                Closer(b, a, 5, 2);
                ok = true;
                return null;
            }

            if (!agreed)
            {
                mood = NotInTheMood(b, a, false);
                if (mood != Mood.Fine)
                {
                    ReduxWooHoo.PushWait(vm, a, b, perMinute * 2, name);
                    Stamp(vm, a, ActAttr);
                    Closer(a, b, -2, 0);
                    FireRefused(vm, b, a, mood);
                    return null; // (it happened - they said no)
                }
            }

            // how it goes: the Romance skill, traits, Irresistible
            int chance = 65 + Level(a) * 4;
            if (VMReduxTraits.Has(a, VMReduxTraits.T.Romantic) || VMReduxTraits.Has(a, VMReduxTraits.T.Flirty)) chance += 10;
            if (VMReduxTraits.Has(a, VMReduxTraits.T.Shy)) chance -= 5;
            if (Perk(a, PerkIrresistible)) chance += GuestOnHost(a, b) ? 5 : 15;
            if (c == Choice.HoldHands || c == Choice.GiveRose) chance += 10;
            if (agreed) chance += 20;
            chance = Math.Max(30, Math.Min(98, chance));
            ok = (int)vm.Context.NextRandom(100) < chance;

            // they go and do it
            bool pushed = false;
            if (c == Choice.CuddleSofa)
            {
                TTABInteraction ia;
                var seat = ReduxWooHoo.FindInteraction(obj, "Sit", out ia);
                if (seat != null) pushed = PushOwnNamed(vm, a, seat, ia, name) && PushOwnNamed(vm, b, seat, ia, name);
                if (!pushed) { ReduxWooHoo.CancelNamed(a, name); ReduxWooHoo.CancelNamed(b, name); }
            }
            else if (c == Choice.Serenade)
            {
                VMEntity piece; TTABInteraction ia;
                if (HasInteractionStarting(obj, "Play", out piece, out ia)) pushed = PushOwnNamed(vm, a, piece, ia, name);
                if (pushed) ReduxWooHoo.PushWait(vm, b, obj, ticks, name);
            }
            if (!pushed)
            {
                if (obj != null)
                {
                    ReduxWooHoo.PushWait(vm, a, obj, ticks, name);
                    ReduxWooHoo.PushWait(vm, b, obj, ticks, name);
                }
                else
                {
                    ReduxWooHoo.PushWait(vm, a, b, ticks, name);
                    ReduxWooHoo.PushWait(vm, b, a, ticks, name);
                }
            }
            Stamp(vm, a, ActAttr);

            if (!ok)
            {
                // awkward - it didn't quite land
                Closer(a, b, -4, 0);
                Closer(b, a, -3, 0);
                Gain(a, 8);
                return null;
            }

            int daily, life;
            switch (c)
            {
                case Choice.CuddleSofa: daily = 10; life = 3; Both(a, b, VMMotive.Comfort, 15); Both(a, b, VMMotive.Social, 15); Both(a, b, VMMotive.Fun, 8); break;
                case Choice.SlowDance: daily = 9; life = 3; Both(a, b, VMMotive.Fun, 15); Both(a, b, VMMotive.Social, 12); break;
                case Choice.Stargaze: daily = 8; life = 3; Both(a, b, VMMotive.Fun, 12); Both(a, b, VMMotive.Social, 10); break;
                case Choice.HoldHands: daily = 6; life = 2; Both(a, b, VMMotive.Social, 8); break;
                case Choice.FeedEachOther: daily = 8; life = 2; Both(a, b, VMMotive.Hunger, 10); Both(a, b, VMMotive.Fun, 8); Both(a, b, VMMotive.Social, 8); break;
                case Choice.LovePoem: daily = VMReduxTraits.Has(a, VMReduxTraits.T.Romantic) ? 12 : 8; life = 3; Motive(b, VMMotive.Social, 10); Motive(a, VMMotive.Fun, 6); break;
                case Choice.Serenade: daily = 9; life = 3; Motive(b, VMMotive.Fun, 12); Both(a, b, VMMotive.Social, 10); break;
                case Choice.GiveRose: daily = 7; life = 2; Motive(b, VMMotive.Social, 8); Motive(b, VMMotive.Room, 5); break;
                default: daily = 5; life = 1; break;
            }
            int bonus = 0, lifeBonus = 0;
            if (Perk(a, PerkSmoothTalker)) bonus += daily / 2;
            if (Perk(a, PerkFaithfulHeart) && ReduxWooHoo.InLove(a, b)) { bonus += daily / 2; lifeBonus = 1; }
            if (GuestOnHost(a, b)) { bonus = Math.Min(bonus, 2); lifeBonus = 0; } // (a visitor's perks: a small bonus at most)
            daily += bonus;
            life += lifeBonus;
            Closer(a, b, daily, life);
            Closer(b, a, daily, life);
            Gain(a, 15);
            Gain(b, 5);
            return null;
        }

        private static void Both(VMAvatar a, VMAvatar b, VMMotive m, int add) { Motive(a, m, add); Motive(b, m, add); }

        // ------------------------------------------------------------------ moments (VMNetReduxRomanceCmd)

        /// <summary>Their first kiss: a little closer, a moment to remember.</summary>
        public static void FirstKiss(VMAvatar a, VMAvatar b)
        {
            Closer(a, b, 8, 4);
            Closer(b, a, 8, 4);
            Motive(a, VMMotive.Fun, 10);
            Motive(b, VMMotive.Fun, 10);
        }

        /// <summary>A great date at a date spot (once every DateCooldown game minutes for each). False: too soon.</summary>
        public static bool GreatDate(VM vm, VMAvatar a, VMAvatar b)
        {
            if (!Person(a) || !Person(b) || a == b) return false;
            if (Since(vm, a, DateAttr) < DateCooldown || Since(vm, b, DateAttr) < DateCooldown) return false;
            Stamp(vm, a, DateAttr);
            Stamp(vm, b, DateAttr);
            Closer(a, b, 10, 4);
            Closer(b, a, 10, 4);
            Both(a, b, VMMotive.Fun, 10);
            Both(a, b, VMMotive.Social, 10);
            Gain(a, 10);
            Gain(b, 10);
            return true;
        }

        /// <summary>
        /// Someone came in on a WooHoo (or, Yes = nearly - close by): Shameless Sims don't mind, Daring Sims get a thrill
        /// (more Fun), everyone else is embarrassed. Once per WooHoo (ReduxWooHoo.FlagsAttr bit CaughtFlag).
        /// </summary>
        public static bool Caught(VMAvatar a, bool near)
        {
            if (!ReduxWooHoo.Busy(a)) return false;
            var vm = VMOf(a);
            var b = vm?.GetObjectById(a.GetAttribute(ReduxWooHoo.PartnerAttr)) as VMAvatar;
            foreach (var s in new[] { a, b })
            {
                if (s == null || !ReduxWooHoo.Busy(s)) continue;
                int flags = s.GetAttribute(ReduxWooHoo.FlagsAttr);
                int bit = near ? ReduxWooHoo.NearFlag : ReduxWooHoo.CaughtFlag;
                if ((flags & bit) != 0) continue;
                s.SetAttribute(ReduxWooHoo.FlagsAttr, (short)(flags | bit));
                if (VMReduxTraits.Has(s, VMReduxTraits.T.Shameless)) continue;
                if (VMReduxTraits.Has(s, VMReduxTraits.T.Daring)) { Motive(s, VMMotive.Fun, near ? 15 : 10); continue; }
                if (!near) { Motive(s, VMMotive.Comfort, -10); Motive(s, VMMotive.Social, -5); }
            }
            return true;
        }

        // ------------------------------------------------------------------ privacy (VMNetReduxWooHooCmd Start)

        /// <summary>Is a visiting player's Sim (never moved by the lot)?</summary>
        public static bool Visitor(VMAvatar a) { return a != null && a.PersistID >= VMReduxPerks.FirstGuestId; }

        /// <summary>
        /// Before a WooHoo indoors: everyone else in that room (the lot's family and townies, not a visiting player's Sim or a
        /// pet) is asked to leave - they go and stand by something in another room for a while. Shameless Sims don't bother.
        /// Returns how many were asked.
        /// </summary>
        public static int AskForPrivacy(VM vm, VMAvatar a, VMAvatar b, VMEntity spot)
        {
            if (!On(vm) || spot == null || vm.Context?.RoomInfo == null) return 0;
            if (VMReduxTraits.Has(a, VMReduxTraits.T.Shameless) || VMReduxTraits.Has(b, VMReduxTraits.T.Shameless)) return 0;
            var room = vm.Context.GetObjectRoom(spot);
            if (room == 0 || room >= vm.Context.RoomInfo.Length || vm.Context.RoomInfo[room].Room.IsOutside) return 0;
            int level = spot.Position.Level, asked = 0;
            int perMinute = Math.Max(1, vm.Context.Clock.TicksPerMinute);
            foreach (var e in vm.Context.ObjectQueries.Avatars.ToList())
            {
                var x = e as VMAvatar;
                if (x == null || x == a || x == b || x.Dead || x.IsPet || Visitor(x) || ReduxWooHoo.Busy(x)) continue;
                if (x.Position == LotTilePos.OUT_OF_WORLD || x.Position.Level != level || vm.Context.GetRoomAt(x.Position) != room) continue;
                if (x.Thread == null || x.Thread.Queue.Count >= VMThread.MAX_USER_ACTIONS) continue;
                var to = ElsewhereFrom(vm, x, room, level);
                if (to != null && ReduxWooHoo.PushWait(vm, x, to, perMinute * 30, "Give Them Some Privacy")) asked++;
            }
            return asked;
        }

        /// <summary>The nearest object on this floor that isn't in that room.</summary>
        private static VMEntity ElsewhereFrom(VM vm, VMAvatar x, ushort room, int level)
        {
            VMEntity best = null;
            float bestD = float.MaxValue;
            foreach (var e in vm.Entities)
            {
                if (e is VMAvatar || e.Dead || e.MultitileGroup == null || e.MultitileGroup.BaseObject != e) continue;
                if (e.Position == LotTilePos.OUT_OF_WORLD || e.Position.Level != level || vm.Context.GetObjectRoom(e) == room) continue;
                float dx = (e.Position.x - x.Position.x) / 16f, dy = (e.Position.y - x.Position.y) / 16f;
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>Is this spot risky (outdoors, or a room with someone else in it) - what a Daring Sim likes?</summary>
        public static bool Risky(VM vm, VMEntity spot, VMAvatar a, VMAvatar b)
        {
            if (vm?.Context?.RoomInfo == null || spot == null) return false;
            var room = vm.Context.GetObjectRoom(spot);
            if (room >= vm.Context.RoomInfo.Length) return false;
            if (room == 0 || vm.Context.RoomInfo[room].Room.IsOutside) return true;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var x = e as VMAvatar;
                if (x == null || x == a || x == b || x.Dead || x.IsPet || x.IsChild || x.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (x.Position.Level == spot.Position.Level && vm.Context.GetRoomAt(x.Position) == room) return true;
            }
            return false;
        }

        /// <summary>
        /// 17.0 (family-friendly): is a child in the spot's room (indoors) or close by (outdoors, within 5 tiles)? Such a
        /// spot isn't offered for WooHoo (Simitone's WooHoo menu).
        /// </summary>
        public static bool ChildNearby(VM vm, VMEntity spot)
        {
            if (vm?.Context?.RoomInfo == null || spot == null || spot.Position == LotTilePos.OUT_OF_WORLD) return false;
            var room = vm.Context.GetObjectRoom(spot);
            bool outdoor = room == 0 || room >= vm.Context.RoomInfo.Length || vm.Context.RoomInfo[room].Room.IsOutside;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var x = e as VMAvatar;
                if (x == null || !x.IsChild || x.Dead || x.IsPet || x.Position == LotTilePos.OUT_OF_WORLD || x.Position.Level != spot.Position.Level) continue;
                if (outdoor)
                {
                    float dx = (x.Position.x - spot.Position.x) / 16f, dy = (x.Position.y - spot.Position.y) / 16f;
                    if (dx * dx + dy * dy <= 25) return true;
                }
                else if (vm.Context.GetRoomAt(x.Position) == room) return true;
            }
            return false;
        }
    }
}
