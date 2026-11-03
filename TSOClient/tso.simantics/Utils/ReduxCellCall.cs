using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 20.4: the cellphone works as the house phone, for calls to and from the game's own Sims (Joey: "add
    /// ability to call npc sims from your cell phone too. give it the same settings as the home phone, if you don't have a
    /// home phone, then any calls that you would normally get from npc ... will come through the cellphone").
    ///
    /// How the Sims 1 phone works (the game's own phones.iff, PhoneGlobals.iff and PhoneLine.iff):
    ///  - Every lot has a hidden "Phone Line" (a global sim object, VMTS1Activator.EnsureGlobalSimObjects). Its attribute 0 is
    ///    the line's state (0 quiet, 1 ringing, 2 answered/in use), 2 the calls waiting to be made to the family. Calls are
    ///    put on the line by the game's callers - the job's "Phonecall Scheduler", the Visit Generator (a friend calling
    ///    because the relationship fell, the adoption agency...) - through the global "schedule phone call"; with none
    ///    waiting the line also rings now and then by chance (prank calls, "you won a prize" calls). It rings every phone on
    ///    the lot assigned to it ("is one of my phones") for a while; picked up, the phone's "Answer" runs the line's
    ///    "incoming - answer" (the caller's own "phone call dialog with response"), or a chance call when nobody's waiting.
    ///    Not picked up in time, the line says the call was missed (the caller's dialog with "missed") - with no phone on the
    ///    lot at all it just rings into nothing and is missed the same way.
    ///  - Calling out is the phone object's own menu (TTAB): Call Neighbor, Call Friend, Throw Party, Services.../Maid, ...
    ///    each with its test (who may, when) and its script (the phone book, the talk, invite over, hire...), which walks the
    ///    Sim to the phone and plays the phone's animations on the way.
    ///
    /// The cellphone, the same on every game (lockstep - only lot state and the lot's own commands, VMNetReduxPhoneCmd):
    ///  - Calls out: the house phone's own menu (the phone's own tree table, tests and scripts), with the Phone Line as the
    ///    phone (it's always there, never on the floor; its attributes 3-13 are free - the phone's scripts keep the talk's
    ///    state there, the line only uses 0-2, and the tests' "the phone is free" is the line's own state). The scripts run
    ///    from copies made when the lot loads, in memory only (never written to any file), where the walk to the phone, the
    ///    phone's animations, its stool and the phone's picture are left out ("anim - ..." becomes a short wait; the Sim
    ///    stands where they are with the cellphone out, VMReduxPhone). Everything else - the phone book, the dialogs, the
    ///    money, the relationships, who comes over - is the game's own script, so the rules, costs and limits are the house
    ///    phone's.
    ///  - Calls in: when the line rings and no house phone is on it (HasHousePhone), the player's game shows the ringing
    ///    cellphone of a family Sim who owns one (UIReduxCellRing; who owns one is that game's business - ReduxCellphone).
    ///    Answering (the command, Answer) marks the line answered and the Sim runs the line's own "incoming - answer", or the
    ///    house phone's chance call (a copy, as above) when nobody's waiting. Not answered: the line misses it as always.
    ///    A house phone on the lot always rings instead (the cellphone stays quiet).
    /// One cellphone call on the lot at a time (they share the line's attributes).
    /// </summary>
    public static class VMReduxCellCall
    {
        /// <summary>The game's house phones (phones.iff - both run the same scripts): their menu and scripts are the cellphone's.</summary>
        public const uint PhoneTableGUID = 0x86E0BBB5, PhoneWallGUID = 0x6B264238;
        /// <summary>The hidden Phone Line every lot has (PhoneLine.iff).</summary>
        public const uint LineGUID = 0xAED879C5;
        /// <summary>The line's attributes: its state (0 quiet, 1 ringing, 2 answered) and the calls waiting.</summary>
        public const int LineStateAttr = 0, LineWaitingAttr = 2;
        public const string AnswerName = "Answer Cellphone";

        // where the copies go in the phone's file (in memory only): the phone's semi-global scripts (0x20xx) at 0x1Cxx,
        // its own (0x10xx) at 0x1Dxx - the same numbers on every game, whatever else is loaded - and the answer at 0x1BFE
        private const ushort SemiCopyBase = 0x1C00, OwnCopyBase = 0x1D00, AnswerTreeID = 0x1BFE;
        private const string CopyPrefix = "SimsRedux Cell - ";
        private const ushort IdleGlobal = 0x118; // the global "idle" (Param 0 = ticks)

        private static readonly ReduxWeakMap<GameObjectResource, object> Built = new ReduxWeakMap<GameObjectResource, object>();
        private static readonly object Lock = new object();
        /// <summary>Kept so the phone's files (with the copies in them) stay loaded while the game runs.</summary>
        private static GameObject Phone;

        // ------------------------------------------------------------------ the lot

        /// <summary>The lot's Phone Line (null: none).</summary>
        public static VMEntity Line(VM vm)
        {
            try
            {
                var list = vm?.Context?.ObjectQueries?.GetObjectsByGUID(LineGUID);
                if (list == null) return null;
                foreach (var e in list) if (e != null && !e.Dead) return e;
            }
            catch { }
            return null;
        }

        /// <summary>A house phone the line rings ("is one of my phones": the line's relationship 0 to it is 1), on the lot.</summary>
        public static bool HasHousePhone(VM vm)
        {
            var line = Line(vm);
            if (line?.MeToObject == null) return false;
            try
            {
                foreach (var kv in line.MeToObject)
                {
                    if (kv.Value == null || kv.Value.Count == 0 || kv.Value[0] != 1) continue;
                    var e = vm.GetObjectById((short)kv.Key);
                    if (e == null || e.Dead || e is VMAvatar || e.Position == LotTilePos.OUT_OF_WORLD) continue;
                    return true;
                }
            }
            catch { } // (read from a player's screen while the lot changes it: asked again next frame)
            return false;
        }

        /// <summary>The line is ringing and there's no house phone to ring - it's the cellphone's call to answer (lot state only).</summary>
        public static bool Ringing(VM vm)
        {
            if (vm == null || !vm.TS1) return false;
            var line = Line(vm);
            return line != null && line.GetAttribute(LineStateAttr) == 1 && !HasHousePhone(vm);
        }

        /// <summary>One of the cellphone's calls (in a Sim's queue): the Phone Line acted on with the house phone's scripts.</summary>
        public static bool IsCellAction(VMQueuedAction a)
        {
            if (a?.Callee?.Object?.OBJ == null || a.CodeOwner?.OBJ == null) return false;
            if (a.Callee.Object.OBJ.GUID != LineGUID) return false;
            var g = a.CodeOwner.OBJ.GUID;
            return g == PhoneTableGUID || g == PhoneWallGUID;
        }

        /// <summary>A Sim on the lot is on a cellphone call now (or has one queued) - one at a time.</summary>
        public static VMAvatar Busy(VM vm)
        {
            try
            {
                foreach (var e in vm.Context.ObjectQueries.Avatars)
                {
                    var a = e as VMAvatar;
                    if (a?.Thread?.Queue == null || a.Dead) continue;
                    foreach (var q in a.Thread.Queue) if (IsCellAction(q)) return a;
                }
            }
            catch { }
            return null;
        }

        /// <summary>A family Sim (a grown-up on the lot) - the only ones whose cellphones make and take the family's calls.</summary>
        public static bool FamilySim(VM vm, VMAvatar sim)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || sim?.Object?.OBJ == null || !ReduxWooHoo.Grownup(sim)) return false;
            return (fam.FamilyGUIDs ?? new uint[0]).Contains(sim.Object.OBJ.GUID);
        }

        /// <summary>Why this Sim can't answer the ringing line on their cellphone (null: they can). Lot state only.</summary>
        public static string CanAnswer(VM vm, VMAvatar sim)
        {
            if (!Ringing(vm)) return "";
            if (!FamilySim(vm, sim)) return "";
            if (Build(vm) == null) return "";
            var busy = Busy(vm);
            if (busy != null) return busy == sim ? "" : "Someone's already on the cellphone.";
            if ((sim.Thread?.Queue?.Count ?? 0) >= VMThread.MAX_USER_ACTIONS) return "";
            return null;
        }

        // ------------------------------------------------------------------ calling out

        /// <summary>
        /// The house phone's menu for this Sim on the cellphone, as the house phone would offer it now (its own tests - who,
        /// when, the family's money...). For the cellphone's screen (the player's own game) - choosing one sends Call.
        /// "Answer" is left out (the cellphone answers its own way).
        /// </summary>
        public static List<VMPieMenuInteraction> Choices(VM vm, VMAvatar sim)
        {
            var result = new List<VMPieMenuInteraction>();
            try
            {
                if (vm == null || !vm.TS1 || sim?.Thread == null) return result;
                var phone = Build(vm);
                var line = Line(vm);
                if (phone == null || line == null) return result;
                TTAB ttab; TTAs ttas;
                if (!Table(phone, out ttab, out ttas)) return result;
                foreach (var ia in ttab.Interactions)
                {
                    if (IsAnswer(phone, ia, ttas)) continue;
                    var action = MakeAction(vm, phone, line, sim, ia, ttas, null);
                    if (action == null) continue;
                    sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                    var strings = sim.Thread.CheckAction(action);
                    if (strings == null || sim.ObjectData[(int)VMStackObjectVariable.Hidden] == 1
                        || sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] == 1) continue;
                    var id = ia.TTAIndex;
                    if (strings.Count > 0)
                    {
                        foreach (var s in strings)
                        {
                            if (s.Name == null) s.Name = ttas?.GetString((int)id) ?? "";
                            s.ID = (byte)id;
                            s.Entry = ia;
                            s.Global = false;
                            result.Add(s);
                        }
                    }
                    else result.Add(new VMPieMenuInteraction { Name = ttas?.GetString((int)id) ?? "", ID = (byte)id, Entry = ia, Global = false });
                }
                sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] cellphone calls: " + e.Message); }
            return result;
        }

        /// <summary>
        /// The command (Call): the Sim makes the house phone's call number "interaction" (its tree table's index) on their
        /// cellphone. Its test runs again when it comes up in their queue (as the house phone's do). null: queued; else why not.
        /// </summary>
        public static string PushCall(VM vm, VMAvatar sim, int interaction, short param0)
        {
            if (!FamilySim(vm, sim)) return "";
            var phone = Build(vm);
            var line = Line(vm);
            if (phone == null || line == null) return "";
            if (Busy(vm) != null) return "Someone's already on the cellphone - one call at a time.";
            if ((sim.Thread?.Queue?.Count ?? 0) >= VMThread.MAX_USER_ACTIONS) return "";
            TTAB ttab; TTAs ttas;
            if (!Table(phone, out ttab, out ttas)) return "";
            TTABInteraction ia;
            if (interaction < 0 || !ttab.InteractionByIndex.TryGetValue((uint)interaction, out ia) || IsAnswer(phone, ia, ttas)) return "";
            var action = MakeAction(vm, phone, line, sim, ia, ttas, new short[] { param0, 0, 0, 0 });
            if (action == null) return "";
            sim.Thread.EnqueueAction(action); // (the phone comes out by their head once the call starts - Tick)
            return null;
        }

        /// <summary>The command (Answer): the line is answered, and the Sim takes the call on their cellphone (next, before their other orders).</summary>
        public static string PushAnswer(VM vm, VMAvatar sim)
        {
            var why = CanAnswer(vm, sim);
            if (why != null) return why;
            var phone = Build(vm);
            var line = Line(vm);
            var routine = phone?.Resource?.GetRoutine(AnswerTreeID) as VMRoutine;
            if (routine == null || line == null) return "";
            // (answered: the line stops ringing. Its "ring" script only looks again when its 25 minutes are up - then, not
            //  ringing any more, it isn't a missed call - and it can't ring again before that; the phone's calls are free again)
            line.SetAttribute(LineStateAttr, 0);
            sim.Thread.EnqueueAction(new VMQueuedAction
            {
                Callee = line,
                StackObject = line,
                IconOwner = sim,
                CodeOwner = phone,
                ActionRoutine = routine,
                Name = AnswerName,
                Args = new short[] { 0, 0, 0, 0 },
                Priority = (short)VMQueuePriority.UserDriven,
                Flags = TTABFlags.FSOSkipPermissions | TTABFlags.FSOPushHead,
            });
            return null;
        }

        /// <summary>
        /// Every tick, for every Sim (ReduxComputer.Tick): on a cellphone call, the phone stays out by their head; 21.6: when
        /// the call ends (answered and done, hung up, cancelled) it's put away at once, and any phone that's been out its
        /// minute is put away for good (it used to come back each time the tick count wrapped). Lot state only - every game.
        /// </summary>
        public static void Tick(VMAvatar ava)
        {
            var vm = ava?.Thread?.Context?.VM;
            if (vm == null) return;
            int v = ava.GetAttribute(VMReduxPhone.OutAttr);
            var a = ava.Thread.ActiveAction;
            if (a != null && IsCellAction(a))
            {
                // (the call's stamp is kept negative - "out for a call" - so the end of the call can put it away)
                int since = VMReduxPhone.Since(vm, v);
                if (since >= VMReduxPhone.ShowTicks - 300) ava.SetAttribute(VMReduxPhone.OutAttr, (short)-(vm.Scheduler.CurrentTickID % VMReduxPhone.TickWrap + 1));
                else if (v > 0) ava.SetAttribute(VMReduxPhone.OutAttr, (short)-v);
                return;
            }
            if (v < 0) { ava.SetAttribute(VMReduxPhone.OutAttr, 0); return; } // (the call's over: the phone goes away)
            if (v > 0) VMReduxPhone.Expire(vm, ava);
        }

        private static bool IsAnswer(GameObject phone, TTABInteraction ia, TTAs ttas)
        {
            var label = (Bhav(phone, ia.ActionFunction)?.ChunkLabel ?? "").Trim();
            if (label.Equals("Interaction - Answer phone", StringComparison.OrdinalIgnoreCase)) return true;
            return (ttas?.GetString((int)ia.TTAIndex) ?? "").Trim().Equals("Answer", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Table(GameObject phone, out TTAB ttab, out TTAs ttas)
        {
            ttab = phone.Resource.Get<TTAB>(phone.OBJ.TreeTableID);
            ttas = ttab != null ? phone.Resource.Get<TTAs>(phone.OBJ.TreeTableID) : null;
            if (ttab == null && phone.Resource.SemiGlobal != null)
            {
                ttab = phone.Resource.SemiGlobal.Get<TTAB>(phone.OBJ.TreeTableID);
                ttas = phone.Resource.SemiGlobal.Get<TTAs>(phone.OBJ.TreeTableID);
            }
            return ttab != null;
        }

        /// <summary>The house phone's interaction as the cellphone's: on the line, the copy of its script, its own test and rules.</summary>
        private static VMQueuedAction MakeAction(VM vm, GameObject phone, VMEntity line, VMAvatar sim, TTABInteraction ia, TTAs ttas, short[] args)
        {
            var action = Routine(vm, phone, CopyOf(phone, ia.ActionFunction));
            if (action == null) return null;
            VMRoutine check = null;
            if (ia.TestFunction != 0)
            {
                check = Routine(vm, phone, ia.TestFunction);
                if (check == null) return null; // (as the game: an interaction whose test is missing isn't offered)
            }
            return new VMQueuedAction
            {
                Callee = line,
                StackObject = line,
                IconOwner = sim,
                CodeOwner = phone,
                ActionRoutine = action,
                CheckRoutine = check,
                Name = QueueName(ttas?.GetString((int)ia.TTAIndex)),
                Args = args,
                InteractionNumber = (int)ia.TTAIndex,
                Priority = (short)VMQueuePriority.UserDriven,
                Flags = ia.Flags & ~VMThread.ReduxContinuation & ~TTABFlags.RunImmediately,
                Flags2 = ia.Flags2,
            };
        }

        /// <summary>The call's name in the Sim's queue: "Services.../Maid" is "Call Maid"; "Call Neighbor" stays as it is.</summary>
        private static string QueueName(string name)
        {
            var parts = (name ?? "").Split('/');
            var last = parts[parts.Length - 1];
            var semi = last.IndexOf(';');
            if (semi > -1) last = last.Substring(0, semi);
            last = last.Trim();
            if (last.Length == 0) return "Cellphone Call";
            if (parts.Length > 1 && parts[0].Trim().StartsWith("Services", StringComparison.OrdinalIgnoreCase)) return "Call " + last;
            return last;
        }

        private static VMRoutine Routine(VM vm, GameObject phone, ushort id)
        {
            if (id >= 8192) return phone.Resource.SemiGlobal?.GetRoutine(id) as VMRoutine;
            if (id >= 4096) return phone.Resource.GetRoutine(id) as VMRoutine;
            return vm.Context.Globals?.Resource?.GetRoutine(id) as VMRoutine;
        }

        /// <summary>The cellphone copy of a phone script, or the script itself when it needs no change.</summary>
        private static ushort CopyOf(GameObject phone, ushort id)
        {
            var c = CopyID(id);
            return (c != 0 && phone.Resource.GetRoutine(c) != null) ? c : id;
        }

        private static ushort CopyID(ushort id)
        {
            if (id >= 0x2000 && id <= 0x20FF) return (ushort)(SemiCopyBase | (id & 0xFF));
            if (id >= 0x1000 && id <= 0x10FF) return (ushort)(OwnCopyBase | (id & 0xFF));
            return 0;
        }

        // ------------------------------------------------------------------ the copies

        /// <summary>
        /// Called as each object is made (VMEntity): when the lot's Phone Line is (every lot has one, made before anyone's
        /// queue is read back from a save), the cellphone's copies of the phone's scripts are made, once per loaded phone file.
        /// </summary>
        public static void Patch(VMEntity ent)
        {
            try
            {
                if (ent?.Object?.OBJ == null) return;
                var g = ent.Object.OBJ.GUID;
                if (g != LineGUID && g != PhoneTableGUID && g != PhoneWallGUID) return;
                if (FSO.Content.Content.Get()?.TS1 != true) return;
                Build(null);
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] cellphone calls: " + e.Message); }
        }

        /// <summary>The house phone (its files, with the cellphone's copies in them) - null if this game has no phone files.</summary>
        public static GameObject Build(VM vm)
        {
            if (vm != null && !vm.TS1) return null;
            GameObject phone = null;
            try
            {
                var objs = FSO.Content.Content.Get()?.WorldObjects;
                if (objs == null) return null;
                phone = objs.Get(PhoneTableGUID);
                if (phone?.Resource?.Iff == null || phone.OBJ == null) phone = objs.Get(PhoneWallGUID);
            }
            catch { phone = null; }
            if (phone?.Resource?.Iff == null || phone.OBJ == null || phone.Resource.RoutineCache == null || phone.Resource.SemiGlobal == null) return null;
            var res = phone.Resource;
            lock (Lock)
            {
                Phone = phone;
                object ok;
                if (Built.TryGetValue(res, out ok)) return ok != null ? phone : null;
                bool done = false;
                try { done = MakeCopies(res); }
                catch (Exception e) { Console.WriteLine("[SimsRedux] cellphone calls: couldn't set up the phone's scripts: " + e.Message); }
                Built[res] = done ? (object)true : null;
                if (!done) Console.WriteLine("[SimsRedux] cellphone calls: the house phone's scripts aren't as expected - calling from the cellphone is off.");
                return done ? phone : null;
            }
        }

        private enum Swap { None, Wait, Skip, SkipFalse, Copy }

        private class Maker
        {
            public GameObjectResource Res;
            public GameIffResource Semi, Global;
            /// <summary>The phone's scripts (its own and semi-global) that need a cellphone copy.</summary>
            public readonly HashSet<ushort> Dirty = new HashSet<ushort>();
            public readonly HashSet<ushort> Made = new HashSet<ushort>();
        }

        private static BHAV Bhav(GameObject phone, ushort id)
        {
            try
            {
                if (id >= 8192) return phone.Resource.SemiGlobal?.Get<BHAV>(id);
                if (id >= 4096) return phone.Resource.Iff.Get<BHAV>(id);
                return FSO.Content.Content.Get().WorldObjectGlobals.Get("global")?.Resource?.Get<BHAV>(id);
            }
            catch { return null; }
        }

        private static BHAV Get(Maker m, ushort id)
        {
            if (id >= 8192) return m.Semi?.Get<BHAV>(id);
            if (id >= 4096) return m.Res.Iff.Get<BHAV>(id);
            return m.Global?.Get<BHAV>(id);
        }

        private static string Label(Maker m, ushort id) { return (Get(m, id)?.ChunkLabel ?? "").Trim(); }

        /// <summary>A call to this phone script is swapped by its name: a wait, or left out (on to its true or false path).</summary>
        private static Swap NamedSwap(Maker m, ushort id)
        {
            if (id < 4096) return Swap.None; // (the game's globals: the same for every object - never changed)
            var label = Label(m, id);
            if (label.StartsWith("anim - ", StringComparison.OrdinalIgnoreCase)) return Swap.Wait;
            if (label.Equals("Common entry", StringComparison.OrdinalIgnoreCase) || label.Equals("Common exit", StringComparison.OrdinalIgnoreCase)
                || label.Equals("set graphic by state", StringComparison.OrdinalIgnoreCase) || label.Equals("notify line", StringComparison.OrdinalIgnoreCase))
                return Swap.Skip;
            if (label.Equals("is wall phone", StringComparison.OrdinalIgnoreCase)) return Swap.SkipFalse;
            return Swap.None;
        }

        /// <summary>What becomes of a call to this script in a copy.</summary>
        private static Swap CallSwap(Maker m, ushort id)
        {
            if (id < 256) return Swap.None;
            var named = NamedSwap(m, id);
            if (named != Swap.None) return named;
            return m.Dirty.Contains(id) ? Swap.Copy : Swap.None;
        }

        /// <summary>Primitives a copy leaves out (they'd walk to, animate at or dress for the phone object).</summary>
        private static bool LeftOut(ushort opcode)
        {
            switch (opcode)
            {
                case 4: case 5: case 6: case 22: case 27: case 44: case 45: case 46: case 47: return true; // grab, drop, change suit, look towards, go to relative position, animate, go to routing slot, snap, reach
            }
            return false;
        }

        /// <summary>
        /// Works out which of the phone's scripts reachable from these need a copy: one that leaves something out itself
        /// (a primitive or a named call above), or calls one that does - worked out again until nothing changes, so a loop
        /// of calls is decided by everything in it.
        /// </summary>
        private static void FindDirty(Maker m, IEnumerable<ushort> roots)
        {
            var reach = new HashSet<ushort>();
            var todo = new Stack<ushort>(roots.Where(r => r >= 4096));
            while (todo.Count > 0)
            {
                var id = todo.Pop();
                if (!reach.Add(id)) continue;
                var b = Get(m, id);
                if (b?.Instructions == null) continue;
                foreach (var ins in b.Instructions)
                    if (ins != null && ins.Opcode >= 4096 && NamedSwap(m, ins.Opcode) == Swap.None && !reach.Contains(ins.Opcode)) todo.Push(ins.Opcode);
            }
            foreach (var id in reach)
            {
                var b = Get(m, id);
                if (b?.Instructions == null) continue;
                foreach (var ins in b.Instructions)
                    if (ins != null && ((ins.Opcode < 256 && LeftOut(ins.Opcode)) || NamedSwap(m, ins.Opcode) != Swap.None)) { m.Dirty.Add(id); break; }
            }
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var id in reach)
                {
                    if (m.Dirty.Contains(id)) continue;
                    var b = Get(m, id);
                    if (b?.Instructions == null) continue;
                    foreach (var ins in b.Instructions)
                        if (ins != null && ins.Opcode >= 4096 && m.Dirty.Contains(ins.Opcode)) { m.Dirty.Add(id); changed = true; break; }
                }
            }
        }

        private static int WaitTicks(string label)
        {
            label = (label ?? "").ToLowerInvariant();
            if (label.Contains("loop") || label.Contains("volley")) return 75; // (a bit of talk or listening)
            if (label.Contains("talk") || label.Contains("earsplit")) return 40;
            return 15; // (picking up, dialling, hanging up)
        }

        private static bool MakeCopies(GameObjectResource res)
        {
            var global = FSO.Content.Content.Get().WorldObjectGlobals.Get("global")?.Resource;
            var m = new Maker { Res = res, Semi = res.SemiGlobal, Global = global };
            if (m.Semi == null || global == null) return false;
            var idle = Label(m, IdleGlobal);
            if (!idle.Equals("idle", StringComparison.OrdinalIgnoreCase)) return false;

            // every script the phone's menu (actions and tests) can reach that needs a change
            var ttab = res.Iff.List<TTAB>()?.FirstOrDefault();
            if (ttab == null) return false;
            var roots = new List<ushort>();
            foreach (var ia in ttab.Interactions) { roots.Add(ia.ActionFunction); if (ia.TestFunction != 0) roots.Add(ia.TestFunction); }
            // the chance call (prank calls, prizes), for answering when nobody's waiting
            ushort chance = Find(m.Semi, "RandomCallDialog");
            if (chance != 0) roots.Add(chance);
            FindDirty(m, roots);
            foreach (var r in roots) if (m.Dirty.Contains(r)) MakeCopy(m, r);
            // (every script that needs a copy has one - else the phone's script would try to walk to a phone that isn't there)
            foreach (var d in m.Dirty) if (CopyID(d) == 0 || !m.Made.Contains(CopyID(d))) return false;

            // the answer: the line's own "incoming - answer" (a waiting caller's call), else the chance call
            var strs = global.Get<STR>(303);
            int answerString = -1;
            if (strs != null)
                for (int i = 0; i < strs.Length; i++)
                    if ((strs.GetString(i) ?? "").Trim().Equals("incoming - answer", StringComparison.OrdinalIgnoreCase)) { answerString = i + 1; break; }
            if (answerString < 1 || answerString > 255 || chance == 0) return false;
            ushort chanceRun = (m.Dirty.Contains(chance) && m.Made.Contains(CopyID(chance))) ? CopyID(chance) : chance;
            var answer = new BHAV
            {
                ChunkID = AnswerTreeID,
                ChunkLabel = CopyPrefix + AnswerName,
                ChunkType = "BHAV",
                ChunkProcessed = true,
                AddedByPatch = true,
                Version = 0,
                Type = 0,
                Args = 4,
                Locals = 0,
                Instructions = new BHAVInstruction[]
                {
                    // 0: run the stack object's (the line's) "incoming - answer" here, in the Sim's own script (Call Named Tree:
                    //    global string table 303, push onto my stack) - true: a waiting caller's call was taken
                    new BHAVInstruction { Opcode = 28, TruePointer = 1, FalsePointer = 2,
                        Operand = new byte[] { 0x2F, 0x01, 1, 0, (byte)answerString, 2, 0, 0 } },
                    // 1: a moment more on the phone, then done
                    Call(IdleGlobal, 45, 254, 254),
                    // 2: nobody waiting: the house phone's chance call (args from the temps, as the house phone calls it)
                    new BHAVInstruction { Opcode = chanceRun, TruePointer = 254, FalsePointer = 254,
                        Operand = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF } },
                }
            };
            if (!Add(res, answer)) return false;
            Console.WriteLine("[SimsRedux] cellphone calls: the house phone's calls are ready for the cellphone (" + m.Made.Count + " scripts).");
            return true;
        }

        private static ushort Find(GameIffResource res, string label)
        {
            foreach (var b in res.List<BHAV>() ?? new List<BHAV>())
                if (b != null && (b.ChunkLabel ?? "").Trim().Equals(label, StringComparison.OrdinalIgnoreCase)) return b.ChunkID;
            return 0;
        }

        /// <summary>The cellphone copy of a script (and of the scripts it calls that need one).</summary>
        private static void MakeCopy(Maker m, ushort id)
        {
            var to = CopyID(id);
            if (to == 0 || m.Made.Contains(to)) return;
            var src = Get(m, id);
            if (src?.Instructions == null) return;
            m.Made.Add(to); // (before the calls in it: a loop of calls is copied once)
            var ins = new BHAVInstruction[src.Instructions.Length];
            for (int i = 0; i < ins.Length; i++)
            {
                var s = src.Instructions[i];
                var op = (byte[])(s.Operand ?? new byte[8]).Clone();
                if (op.Length < 8) Array.Resize(ref op, 8);
                ushort opcode = s.Opcode;
                byte t = s.TruePointer, f = s.FalsePointer;
                if (opcode < 256)
                {
                    if (LeftOut(opcode)) { ins[i] = Nothing(t); continue; }
                }
                else
                {
                    switch (CallSwap(m, opcode))
                    {
                        case Swap.Wait: ins[i] = Call(IdleGlobal, WaitTicks(Label(m, opcode)), t, f); continue;
                        case Swap.Skip: ins[i] = Nothing(t); continue;
                        case Swap.SkipFalse: ins[i] = Nothing(f); continue;
                        case Swap.Copy:
                            {
                                var c = CopyID(opcode);
                                if (c != 0) { MakeCopy(m, opcode); if (m.Made.Contains(c)) opcode = c; }
                                break;
                            }
                    }
                }
                ins[i] = new BHAVInstruction { Opcode = opcode, TruePointer = t, FalsePointer = f, Operand = op };
            }
            var bhav = new BHAV
            {
                ChunkID = to,
                ChunkLabel = CopyPrefix + (src.ChunkLabel ?? "").Trim(),
                ChunkType = "BHAV",
                ChunkProcessed = true,
                AddedByPatch = true,
                Version = src.Version,
                Type = src.Type,
                Args = src.Args,
                Locals = src.Locals,
                Instructions = ins
            };
            if (!Add(m.Res, bhav)) m.Made.Remove(to);
        }

        private static bool Add(GameObjectResource res, BHAV bhav)
        {
            var had = res.Iff.Get<BHAV>(bhav.ChunkID);
            if (had != null && !(had.ChunkLabel ?? "").StartsWith(CopyPrefix, StringComparison.Ordinal)) return false; // (taken by the file itself)
            var routine = VMTranslator.ReduxAssemble(bhav, res);
            if (routine == null) return false;
            if (had == null) res.Iff.AddChunk(bhav);
            res.RoutineCache[bhav.ChunkID] = routine;
            return true;
        }

        /// <summary>Does nothing, then goes to "next" (literal 0 == literal 0).</summary>
        private static BHAVInstruction Nothing(byte next)
        {
            return new BHAVInstruction { Opcode = 2, TruePointer = next, FalsePointer = next, Operand = new byte[] { 0, 0, 0, 0, 0, 2, 7, 7 } };
        }

        private static BHAVInstruction Call(ushort id, int arg0, byte t, byte f)
        {
            return new BHAVInstruction { Opcode = id, TruePointer = t, FalsePointer = f,
                Operand = new byte[] { (byte)(arg0 & 0xFF), (byte)((arg0 >> 8) & 0xFF), 0, 0, 0, 0, 0, 0 } };
        }
    }
}
