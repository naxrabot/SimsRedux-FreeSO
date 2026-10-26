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
    /// SimsRedux 17.0 (Part A #10): WooHoo, Sims 3 style - the lot's side of it (VMNetReduxWooHooCmd does the changes).
    ///
    /// Where it happens, kept family-friendly:
    ///   Bed      - any double bed: both Sims get in under the covers, using the bed's own "Sleep" (its get in / lie
    ///              down / get out). While they're in, the bed's sleep scripts are told to leave them be (no dreams, no
    ///              snoring, no waking up because they aren't tired) - see Hook.
    ///   Hot tub  - both Sims use the hot tub's own "Get In".
    ///   Shower   - one Sim takes the shower's own "Take a Shower" and the other waits right by it; the screen covers
    ///              the shower in steam (Simitone's UIReduxWooHoo, which also draws the hearts).
    ///
    /// What's going on is kept on the two Sims themselves, as person-object attributes 70-77 (the Sims 1 person scripts
    /// don't use attributes that high). Attributes are already saved with the lot and sent to every player joining, a
    /// game from before this loads and saves them untouched, and nothing new goes into the save format.
    ///
    /// Also here: the original "Should we have a baby?" question is switched off while WooHoo is on (Hook, the
    /// game's "should have baby?" script says no). The lot's game says whether WooHoo is on with the command's
    /// SetSwitch, so every player on a visit does the same.
    /// </summary>
    public static class ReduxWooHoo
    {
        // ------------------------------------------------------------------ the Sims' attributes
        public const int StateAttr = 70, PartnerAttr = 71, PlaceAttr = 72, KindAttr = 73, StartAttr = 74, InAttr = 75, ChanceAttr = 76, FlagsAttr = 77;
        public const short StateNone = 0, StateOn = 1;

        public enum Place : byte { None = 0, Bed = 1, HotTub = 2, Shower = 3 }

        /// <summary>What the Sims' pushed actions are called in their queue (and how they're found again).</summary>
        public const string ActionName = "WooHoo";
        /// <summary>The longest a WooHoo can stay on before the lot ends it by itself (game minutes since it started).</summary>
        public const int GiveUpMinutes = 180;

        /// <summary>The game's own baby (the bassinet, Baby.iff) and its left part, which keeps the baby's DNA.</summary>
        public const uint BabyGUID = 0x76F256C5, BabyPartGUID = 0xC5F45AA3;
        /// <summary>The most Sims and pets a family can have.</summary>
        public const int MaxFamily = 8;

        // ------------------------------------------------------------------ the switch (lot-wide, from the lot's game)

        /// <summary>What a game uses before its lot's game has said (the Feature Switch "woohoo" - Simitone sets it).</summary>
        public static bool DefaultOn = true;
        private static readonly ConditionalWeakTable<VM, StrongBox<bool>> Switch = new ConditionalWeakTable<VM, StrongBox<bool>>();

        public static bool On(VM vm)
        {
            if (vm == null) return DefaultOn;
            StrongBox<bool> b;
            lock (Switch) { if (Switch.TryGetValue(vm, out b)) return b.Value; }
            return DefaultOn;
        }

        /// <summary>Has this lot been told yet (VMNetReduxWooHooCmd SetSwitch)?</summary>
        public static bool Told(VM vm, out bool on)
        {
            on = DefaultOn;
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

        // ------------------------------------------------------------------ who and where

        public static short MinuteOfDay(VM vm)
        {
            var c = vm?.Context?.Clock;
            if (c == null) return 0;
            return (short)(((c.Hours % 24) * 60 + c.Minutes) % 1440);
        }

        /// <summary>Game minutes from "from" (a minute of the day) to now - right across midnight.</summary>
        public static int MinutesSince(VM vm, int from)
        {
            return ((MinuteOfDay(vm) - from) % 1440 + 1440) % 1440;
        }

        /// <summary>A grown-up person on the lot who can WooHoo: not a child, a pet, a ghost or a service NPC.</summary>
        public static bool Grownup(VMAvatar a)
        {
            if (a == null || a.Dead || a.IsPet || a.IsChild) return false;
            if (a.Position == LotTilePos.OUT_OF_WORLD) return false;
            if (a.GetPersonData(VMPersonDataVariable.IsGhost) != 0) return false;
            if (a.GetPersonData(VMPersonDataVariable.PersonType) >= 2) return false; // (maids, the burglar, the police...)
            return true;
        }

        public static bool Female(VMAvatar a)
        {
            return a != null && (a.GetPersonData(VMPersonDataVariable.Gender) & 1) != 0;
        }

        /// <summary>What a's neighbour feels for b's: daily, in love (the game's own "are both people in love" reads this), lifetime.</summary>
        public static List<short> Rel(VMAvatar a, VMAvatar b)
        {
            try
            {
                var neigh = Content.Content.Get().Neighborhood;
                var na = neigh?.GetNeighborByID(a.GetPersonData(VMPersonDataVariable.NeighborId));
                var nb = b.GetPersonData(VMPersonDataVariable.NeighborId);
                if (na?.Relationships == null || nb <= 0) return null;
                List<short> r;
                return na.Relationships.TryGetValue(nb, out r) ? r : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The relationship allows it - the same test as the game's "are both people in love with each other?" (before
        /// it asks "Should we have a baby?"): each is in love with the other.
        /// </summary>
        public static bool InLove(VMAvatar a, VMAvatar b)
        {
            var ab = Rel(a, b);
            var ba = Rel(b, a);
            return ab != null && ba != null && ab.Count > 1 && ba.Count > 1 && (ab[1] & 1) != 0 && (ba[1] & 1) != 0;
        }

        /// <summary>Is this Sim in the middle of a WooHoo (or on their way to one)?</summary>
        public static bool Busy(VMAvatar a)
        {
            return a != null && a.GetAttribute(StateAttr) == StateOn;
        }

        public static string InteractionName(VMEntity obj, TTABInteraction ia)
        {
            return (obj?.TreeTableStrings?.GetString((int)ia.TTAIndex) ?? "").Trim();
        }

        /// <summary>The first piece of the object's group with this menu entry (by its name), and the entry.</summary>
        public static VMEntity FindInteraction(VMEntity obj, string name, out TTABInteraction found)
        {
            found = null;
            var group = obj?.MultitileGroup?.Objects ?? (obj == null ? new List<VMEntity>() : new List<VMEntity> { obj });
            foreach (var o in group)
            {
                if (o?.TreeTable == null || o.TreeTableStrings == null) continue;
                foreach (var ia in o.TreeTable.Interactions)
                    if (string.Equals(InteractionName(o, ia), name, StringComparison.OrdinalIgnoreCase)) { found = ia; return o; }
            }
            return null;
        }

        /// <summary>What kind of WooHoo spot this is (None: not one).</summary>
        public static Place KindOf(VMEntity obj)
        {
            try
            {
                if (obj == null || obj is VMAvatar || obj.Dead) return Place.None;
                if (ReduxBeds.IsBed(obj)) return ReduxBeds.IsDouble(obj) ? Place.Bed : Place.None;
                TTABInteraction ia;
                if (FindInteraction(obj, "Join Me in Hot Tub", out ia) != null || FindInteraction(obj, "Join", out ia) != null)
                {
                    if (FindInteraction(obj, "Get In", out ia) != null) return Place.HotTub;
                }
                if (FindInteraction(obj, "Take a Shower", out ia) != null || FindInteraction(obj, "Take Shower", out ia) != null) return Place.Shower;
            }
            catch { }
            return Place.None;
        }

        /// <summary>Is the Sim at their spot (on it, or right next to it) doing their WooHoo action?</summary>
        public static bool InPlace(VM vm, VMAvatar a)
        {
            if (!Busy(a)) return false;
            var kind = (Place)a.GetAttribute(KindAttr);
            if (kind == Place.Bed) return a.GetAttribute(InAttr) > 0;
            var place = vm.GetObjectById(a.GetAttribute(PlaceAttr));
            var act = a.Thread?.ActiveAction;
            if (place?.MultitileGroup == null || act == null || act.Name != ActionName) return false;
            if (act.Callee == null || !place.MultitileGroup.Objects.Contains(act.Callee)) return false;
            foreach (var o in place.MultitileGroup.Objects)
            {
                if (o.Position == LotTilePos.OUT_OF_WORLD || o.Position.Level != a.Position.Level) continue;
                var dx = (o.Position.x - a.Position.x) / 16f;
                var dy = (o.Position.y - a.Position.y) / 16f;
                if (dx * dx + dy * dy <= 1.7f * 1.7f) return true;
            }
            return false;
        }

        /// <summary>Is a WooHoo action still in the Sim's queue?</summary>
        public static bool HasAction(VMAvatar a)
        {
            return a?.Thread?.Queue != null && a.Thread.Queue.Any(q => q.Name == ActionName);
        }

        public static void Clear(VMAvatar a)
        {
            if (a == null) return;
            for (int i = StateAttr; i <= FlagsAttr; i++) if (a.GetAttribute(i) != 0) a.SetAttribute(i, 0);
        }

        // ------------------------------------------------------------------ the scripts (VMRoutine's hook)

        private static readonly string[] HookLabels =
        {
            "should have baby?", "tweak sleep begin", "tweak sleep inc", "tweak sleep finished", "Try Dream", "set motive deltas for sleep",
        };

        /// <summary>Is a script with this name one of the hooks here (worked out once per script - VMRoutine.ReduxKind)?</summary>
        public static bool IsHookLabel(string label)
        {
            label = (label ?? "").Trim();
            foreach (var l in HookLabels) if (label.Equals(l, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// A script is about to call "target" (one of HookLabels). 0: let it run; 1: skip it, as if it said true; -1: skip
        /// it, as if it said false. Only reads and changes the lot's own state, so every player's game does the same.
        /// </summary>
        public static int Hook(VMStackFrame frame, VMRoutine target)
        {
            try
            {
                var vm = frame?.VM;
                if (vm == null || !vm.TS1) return 0;
                var label = (target?.Chunk?.ChunkLabel ?? "").Trim();
                if (label.Equals("should have baby?", StringComparison.OrdinalIgnoreCase))
                    return On(vm) ? -1 : 0; // (WooHoo replaces the question)

                var sim = frame.Caller as VMAvatar;
                if (sim == null || !Busy(sim) || (Place)sim.GetAttribute(KindAttr) != Place.Bed) return 0;
                // (the bed this is about must be the WooHoo bed)
                var place = vm.GetObjectById(sim.GetAttribute(PlaceAttr));
                var callee = frame.Callee;
                if (place?.MultitileGroup == null || callee == null || callee.MultitileGroup != place.MultitileGroup) return 0;

                if (label.Equals("tweak sleep finished", StringComparison.OrdinalIgnoreCase))
                {
                    // no snoring: the sleep loop counts to 7 in its local 0 before each snore
                    var running = (frame.Routine?.Chunk?.ChunkLabel ?? "").Trim();
                    if (running.Equals("_sleep", StringComparison.OrdinalIgnoreCase) && frame.Locals != null && frame.Locals.Length > 0) frame.Locals[0] = 0;
                    var start = sim.GetAttribute(StartAttr) - 1;
                    if (start >= 0 && MinutesSince(vm, start) > GiveUpMinutes)
                    {
                        // (nobody ended it - the lot was saved mid-way and the game that runs it went away): they get up
                        Clear(sim);
                        return 1;
                    }
                    return -1; // not finished: they stay in until the WooHoo ends (VMNetReduxWooHooCmd Finish)
                }
                if (label.Equals("tweak sleep begin", StringComparison.OrdinalIgnoreCase))
                {
                    // they're in, under the covers (no sleep: they aren't tired, and no Zzz)
                    if (sim.GetAttribute(InAttr) == 0) sim.SetAttribute(InAttr, (short)(MinuteOfDay(vm) + 1));
                    return 1;
                }
                // no dreams, no rest, no sleep needs while they're in
                return 1;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] woohoo hook: " + e.Message);
                return 0;
            }
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Puts one of the spot's own menu entries in the Sim's queue as "WooHoo" (its own test is skipped).</summary>
        public static bool PushOwn(VM vm, VMAvatar sim, VMEntity piece, TTABInteraction ia)
        {
            if (sim == null || piece == null || ia == null) return false;
            var action = piece.GetAction((int)ia.TTAIndex, sim, vm.Context, false, new short[4]);
            if (action == null) return false;
            action.CheckRoutine = null; // (the bed's "Sleep test" says no to a Sim who isn't tired)
            action.Name = ActionName;
            action.Priority = (short)VMQueuePriority.UserDriven;
            action.Flags |= TTABFlags.FSOSkipPermissions;
            sim.Thread.EnqueueAction(action);
            return true;
        }

        /// <summary>The waiting script: walk up to the object, then stand by it for a while (Args[0] ticks) - or until told to stop.</summary>
        public const ushort WaitTreeID = 0x1F60;
        private static readonly object Lock = new object();

        private static VMRoutine WaitRoutine(VMEntity obj)
        {
            var res = obj?.Object?.Resource;
            if (res?.Iff == null || res.RoutineCache == null) return null;
            lock (Lock)
            {
                object cached;
                if (res.RoutineCache.TryGetValue(WaitTreeID, out cached))
                {
                    var r = cached as VMRoutine;
                    return (r?.Chunk != null && r.Chunk.ChunkLabel == WaitLabel) ? r : null; // (the id is the object's own)
                }
                if (res.Iff.Get<BHAV>(WaitTreeID) != null) return null; // (taken by something else)
                var bhav = new BHAV
                {
                    ChunkID = WaitTreeID,
                    ChunkLabel = WaitLabel,
                    ChunkType = "BHAV",
                    ChunkProcessed = true,
                    AddedByPatch = true,
                    Version = 0,
                    Type = 0,
                    Args = 4,
                    Locals = 0,
                    Instructions = new BHAVInstruction[]
                    {
                        // 0: go up to it (anywhere near, facing it; no "can't get there" balloon) - either way, wait there
                        I(27, 1, 1, 0, 0, 0xFF, 0xFE, 0, 0, 2, 0),
                        // 1: stand by for Args[0] ticks, or until the action is cancelled
                        I(17, 254, 254, 0, 0, 0, 0),
                    }
                };
                var routine = VMTranslator.ReduxAssemble(bhav, res);
                if (routine == null) return null;
                res.Iff.AddChunk(bhav);
                res.RoutineCache[WaitTreeID] = routine;
                return routine;
            }
        }
        private const string WaitLabel = "SimsRedux - WooHoo Wait";

        /// <summary>The Sim walks up to obj and stands by it for "ticks" (shows as name in their queue).</summary>
        public static bool PushWait(VM vm, VMAvatar sim, VMEntity obj, int ticks, string name)
        {
            var routine = WaitRoutine(obj);
            if (sim == null || routine == null) return false;
            sim.Thread.EnqueueAction(new VMQueuedAction
            {
                Callee = obj,
                StackObject = obj,
                IconOwner = obj,
                CodeOwner = obj.Object,
                ActionRoutine = routine,
                Name = name ?? ActionName,
                Args = new short[] { (short)Math.Max(1, Math.Min(30000, ticks)), 0, 0, 0 },
                Priority = (short)VMQueuePriority.UserDriven,
                Flags = TTABFlags.FSOSkipPermissions,
            });
            return true;
        }

        public static void CancelActions(VMAvatar sim)
        {
            if (sim?.Thread?.Queue == null) return;
            foreach (var q in sim.Thread.Queue.Where(x => x.Name == ActionName).ToList())
                sim.Thread.CancelAction(q.UID);
        }

        private static BHAVInstruction I(ushort opcode, byte t, byte f, params byte[] operand)
        {
            var op = new byte[8];
            Array.Copy(operand, op, Math.Min(operand.Length, op.Length));
            return new BHAVInstruction { Opcode = opcode, TruePointer = t, FalsePointer = f, Operand = op };
        }

        // ------------------------------------------------------------------ family room

        /// <summary>Babies waiting in a bassinet on the lot (they join the family when they grow up).</summary>
        public static int BabiesOnLot(VM vm)
        {
            try { return vm.Context.ObjectQueries.GetObjectsByGUID(BabyGUID)?.Count ?? 0; }
            catch { return 0; }
        }
    }
}
