using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.1 (Part B #7): the chore board - the lot's side. Simitone's Utils/ReduxChores.cs is the game's side (the
    /// side file, the board window, pocket money saved, the "Finish your chores" want); VMNetReduxChoresCmd (VM command 80)
    /// puts each Sim's chores on the lot, so on a visit every player's game does the same thing at the same moment.
    ///
    /// Each family Sim can have chores at two times of day (A and B). At that time, on their home lot, they do them one after
    /// another: the lot's own menu entries ("Clean Up" on a dirty plate, "Water" on a plant, "Make Bed", "Empty Trash",
    /// "Repair"...) are put in their queue as if the player had clicked them - so they happen even with free will off, and the
    /// object's own check says whether it needs doing. A chore is done when nothing of its kind is left; a small mood bump when
    /// the list is finished, and a child gets pocket money (from the family's money) for each chore they did.
    ///
    /// Kept on the Sims themselves, as person-object attributes 100-104 (the Sims 1 person scripts don't use attributes that
    /// high; WooHoo 70-77, attraction 78, traits 80-84, R1 85-89, R2 90-94, romance 95-99, the cellphone 105-109):
    ///   100 AAttr     - chores at time A: bits 0-9 which chores (Chore), bits 10-15 the time (half hour of the day + 1; 0 none)
    ///   101 BAttr     - the same for time B
    ///   102 TodoAttr  - bits 0-9 the chores still to do now, bits 10-15 how many times the current one was queued
    ///   103 StateAttr - bits 0-4 the day of the month time A last started, 5-9 time B, 10-13 chores done this time,
    ///                   14-15 pocket money per chore (PayLevels)
    ///   104 TargetAttr - the object the Sim was last sent to for a chore (0 none)
    /// Attributes are saved with the lot and sent to everyone joining; nothing new goes into the save format. Everything runs in
    /// the lot's ticks from what's on the lot (VMAvatar.Tick), so every game agrees.
    /// </summary>
    public static class VMReduxChores
    {
        public const int AAttr = 100, BAttr = 101, TodoAttr = 102, StateAttr = 103, TargetAttr = 104;

        [Flags]
        public enum Chore : ushort
        {
            None = 0, Dishes = 1, Clean = 2, Trash = 4, Plants = 8, Pets = 16, Beds = 32, Mail = 64, Repair = 128, Bills = 256,
        }

        /// <summary>The chores, in the order they're done and shown.</summary>
        public static readonly Chore[] All =
        {
            Chore.Dishes, Chore.Clean, Chore.Trash, Chore.Plants, Chore.Pets, Chore.Beds, Chore.Mail, Chore.Repair, Chore.Bills,
        };

        public const int ChoreMask = 0x3FF;
        /// <summary>What a child can be given (no repairs - the game won't let them - and no bills).</summary>
        public const int KidChores = (int)(Chore.Dishes | Chore.Clean | Chore.Trash | Chore.Plants | Chore.Pets | Chore.Beds | Chore.Mail);
        /// <summary>Pocket money per chore at each level (0 none).</summary>
        public static readonly int[] PayLevels = { 0, 5, 10, 20 };
        /// <summary>How many times one chore is queued in one go at most (a thing that can't be reached isn't tried forever).</summary>
        public const int MaxTries = 12;
        /// <summary>How long after its time a chore list can still start (half hours: loading the house late doesn't start it).</summary>
        public const int StartWindow = 6;
        /// <summary>How often each Sim looks at their chores (ticks).</summary>
        public const int Every = 30;

        public static string Label(Chore c)
        {
            switch (c)
            {
                case Chore.Dishes: return "Wash the dishes";
                case Chore.Clean: return "Clean up";
                case Chore.Trash: return "Take out the trash";
                case Chore.Plants: return "Water the plants";
                case Chore.Pets: return "Feed the pets";
                case Chore.Beds: return "Make the beds";
                case Chore.Mail: return "Get the mail";
                case Chore.Repair: return "Fix broken things";
                case Chore.Bills: return "Pay the bills";
            }
            return "";
        }

        /// <summary>Short words for a small chip ("Dishes", "Plants"...).</summary>
        public static string Short(Chore c)
        {
            switch (c)
            {
                case Chore.Dishes: return "Dishes";
                case Chore.Clean: return "Clean up";
                case Chore.Trash: return "Trash";
                case Chore.Plants: return "Plants";
                case Chore.Pets: return "Pets";
                case Chore.Beds: return "Beds";
                case Chore.Mail: return "Mail";
                case Chore.Repair: return "Repairs";
                case Chore.Bills: return "Bills";
            }
            return "";
        }

        public static bool KidOk(Chore c) { return ((int)c & KidChores) != 0; }

        // ------------------------------------------------------------------ the Sim's chores (attributes)

        private static int U(VMEntity a, int attr) { return a.GetAttribute(attr) & 0xFFFF; }
        private static void Set(VMEntity a, int attr, int v) { a.SetAttribute(attr, unchecked((short)(v & 0xFFFF))); }

        public static int MaskA(VMAvatar a) { return a == null ? 0 : U(a, AAttr) & ChoreMask; }
        public static int MaskB(VMAvatar a) { return a == null ? 0 : U(a, BAttr) & ChoreMask; }
        /// <summary>Time A / B as a half hour of the day (0-47), or -1 when not set.</summary>
        public static int TimeA(VMAvatar a) { return a == null ? -1 : ((U(a, AAttr) >> 10) & 63) - 1; }
        public static int TimeB(VMAvatar a) { return a == null ? -1 : ((U(a, BAttr) >> 10) & 63) - 1; }
        public static int Todo(VMAvatar a) { return a == null ? 0 : U(a, TodoAttr) & ChoreMask; }
        public static int Pay(VMAvatar a) { return a == null ? 0 : (U(a, StateAttr) >> 14) & 3; }
        public static int DoneNow(VMAvatar a) { return a == null ? 0 : (U(a, StateAttr) >> 10) & 15; }

        public static string TimeText(int half)
        {
            if (half < 0 || half > 47) return "-";
            int h = half / 2, m = (half % 2) * 30;
            int h12 = h % 12 == 0 ? 12 : h % 12;
            return h12 + ":" + m.ToString("00") + (h < 12 ? " AM" : " PM");
        }

        private static int Pack(int mask, int half) { return (mask & ChoreMask) | ((half < 0 || half > 47 ? 0 : half + 1) << 10); }

        private static int Day(VM vm) { return Math.Max(1, Math.Min(31, vm.Context.Clock.DayOfMonth)); }
        private static int HalfNow(VM vm) { var c = vm.Context.Clock; return Math.Max(0, Math.Min(47, (c.Hours % 24) * 2 + c.Minutes / 30)); }

        /// <summary>
        /// The lot's game puts a Sim's chores on the lot (VMNetReduxChoresCmd SetSchedule). A time that has already gone by
        /// today first comes round tomorrow (setting 8:00 at 8:30 doesn't start them now - "Do Chores Now" does).
        /// </summary>
        public static void SetSchedule(VM vm, VMAvatar a, int maskA, int timeA, int maskB, int timeB, int pay)
        {
            if (a == null || a.IsPet) return;
            if (a.IsChild) { maskA &= KidChores; maskB &= KidChores; }
            int oldA = U(a, AAttr), oldB = U(a, BAttr);
            int newA = Pack(maskA, timeA), newB = Pack(maskB, timeB);
            Set(a, AAttr, newA);
            Set(a, BAttr, newB);
            int st = U(a, StateAttr);
            int day = Day(vm), now = HalfNow(vm);
            // (a time that changed: started already today if it's gone by, so it isn't started right now)
            if ((oldA >> 10) != (newA >> 10) && timeA >= 0 && now >= timeA) st = (st & ~0x1F) | day;
            if ((oldB >> 10) != (newB >> 10) && timeB >= 0 && now >= timeB) st = (st & ~(0x1F << 5)) | (day << 5);
            st = (st & 0x3FFF) | ((Math.Max(0, Math.Min(3, pay))) << 14);
            Set(a, StateAttr, st);
            // (chores taken off the board stop now)
            int todo = U(a, TodoAttr);
            int keep = (todo & ChoreMask) & (maskA | maskB);
            if (keep != (todo & ChoreMask)) Set(a, TodoAttr, keep == 0 ? 0 : (todo & ~ChoreMask) | keep);
        }

        /// <summary>Starts a list of chores now (on top of any still to do). scheduled: from the Sim's own chore time (Tick), not
        /// "Do Chores Now" - Scheduled fires too.</summary>
        public static bool Start(VM vm, VMAvatar a, int mask, bool scheduled = false)
        {
            if (a == null || a.IsPet || !Here(vm, a)) return false;
            if (a.IsChild) mask &= KidChores;
            mask &= ChoreMask;
            if (mask == 0) return false;
            int todo = U(a, TodoAttr);
            bool fresh = (todo & ChoreMask) == 0;
            Set(a, TodoAttr, fresh ? mask : (todo | mask));
            if (fresh)
            {
                Set(a, StateAttr, U(a, StateAttr) & ~(15 << 10)); // (none done yet this time)
                Set(a, TargetAttr, 0);
            }
            Fire(Started, vm, a, mask);
            if (scheduled) Fire(Scheduled, vm, a, mask);
            return true;
        }

        /// <summary>Stops the chores still to do (Stop on the board).</summary>
        public static void Stop(VM vm, VMAvatar a)
        {
            if (a == null) return;
            Set(a, TodoAttr, 0);
            Set(a, TargetAttr, 0);
        }

        /// <summary>One of the family living on their own home lot, here and able (not a guest player's Sim, a pet or a ghost).</summary>
        public static bool Here(VM vm, VMAvatar a)
        {
            if (vm == null || !vm.TS1 || a == null || a.Dead || a.IsPet) return false;
            if (a.GetPersonData(VMPersonDataVariable.IsGhost) != 0) return false;
            if (!ReduxComputer.IsHouseholdSim(vm, a)) return false;
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null) return false;
            // (the home lot only: chores aren't done downtown or at a friend's house)
            try { if (fam.HouseNumber > 0 && vm.GetGlobalValue(10) != fam.HouseNumber) return false; } catch { return false; }
            return true;
        }

        // ------------------------------------------------------------------ events (on every game, from the lot's ticks)

        /// <summary>A Sim's chores started (the chores).</summary>
        public static event Action<VM, VMAvatar, int> Started;
        /// <summary>17.1 review: a Sim's chores started at their set time on the board (not "Do Chores Now") - the chores.</summary>
        public static event Action<VM, VMAvatar, int> Scheduled;
        /// <summary>A Sim finished one chore (the chore, the pocket money they got).</summary>
        public static event Action<VM, VMAvatar, Chore, int> ChoreDone;
        /// <summary>A Sim's chore list is over (how many they did; 0: nothing needed doing or they couldn't).</summary>
        public static event Action<VM, VMAvatar, int> AllDone;

        private static void Fire(Action<VM, VMAvatar, int> ev, VM vm, VMAvatar a, int n)
        {
            try { ev?.Invoke(vm, a, n); } catch (Exception e) { Console.WriteLine("[SimsRedux] chores: " + e.Message); }
        }

        // ------------------------------------------------------------------ every tick (VMAvatar.Tick)

        public static void Tick(VMAvatar a)
        {
            if (a == null || a.IsPet || a.Dead) return;
            // (cheap: most Sims have no chores at all)
            int ra = a.GetAttribute(AAttr), rb = a.GetAttribute(BAttr), rt = a.GetAttribute(TodoAttr);
            if (ra == 0 && rb == 0 && rt == 0) return;
            var vm = a.Thread?.Context?.VM;
            if (vm == null || !vm.TS1) return;
            if ((vm.Scheduler.CurrentTickID + (uint)a.ObjectID) % Every != 0) return;
            if (!Here(vm, a)) return;

            // their times: start the list once a day (within StartWindow half hours of the time)
            int day = Day(vm), now = HalfNow(vm);
            int st = U(a, StateAttr);
            for (int slot = 0; slot < 2; slot++)
            {
                int v = U(a, slot == 0 ? AAttr : BAttr);
                int half = ((v >> 10) & 63) - 1, mask = v & ChoreMask;
                if (half < 0 || mask == 0) continue;
                int shift = slot * 5;
                if (((st >> shift) & 0x1F) == day) continue;
                int since = now - half;
                if (since < 0 || since >= StartWindow) continue;
                st = (st & ~(0x1F << shift)) | (day << shift);
                Set(a, StateAttr, st);
                Start(vm, a, mask, true);
                st = U(a, StateAttr);
            }

            int todoRaw = U(a, TodoAttr);
            int todo = todoRaw & ChoreMask;
            if (todo == 0) return;
            var thread = a.Thread;
            // (asleep, away at work or school, carried, or busy with a lot of orders: later)
            if (a.GetMotiveData(VMMotive.SleepState) != 0) return;
            if (a.Position == LotTilePos.OUT_OF_WORLD || a.Container != null) return;
            if (thread.Queue.Count >= VMThread.MAX_USER_ACTIONS - 1) return;
            // (still at the last one)
            int last = a.GetAttribute(TargetAttr);
            if (last != 0 && Busy(a, (short)last)) return;

            Chore c = Chore.None;
            foreach (var x in All) if ((todo & (int)x) != 0) { c = x; break; }
            if (c == Chore.None) { Set(a, TodoAttr, 0); return; }
            int tries = (todoRaw >> 10) & 63;

            VMEntity piece = null; TTABInteraction ia = null;
            bool gaveUp = tries >= MaxTries;
            var target = gaveUp ? null : Find(vm, a, c, out piece, out ia);
            // (sent there last time and it's offered again: an object whose menu entry is always there (a plant's Water, the
            // mailbox...) or one they couldn't reach - leave this chore, and count it done since they went over once (17.1 review:
            // so pocket money and "done" work for always-available objects))
            bool again = false;
            if (target != null && last != 0 && piece.ObjectID == last) { target = null; gaveUp = true; again = true; }
            if (target == null)
            {
                // nothing more of it left: done if they did some (given up: if an earlier one went through)
                bool did = again ? tries >= 1 : gaveUp ? tries > 1 : tries > 0;
                Finish(vm, a, c, did, todo & ~(int)c);
                return;
            }
            if (!Push(vm, a, piece, ia)) { Finish(vm, a, c, false, todo & ~(int)c); return; }
            Set(a, TodoAttr, todo | (Math.Min(63, tries + 1) << 10));
            Set(a, TargetAttr, piece.ObjectID);
        }

        /// <summary>Does the Sim still have something queued on that object?</summary>
        private static bool Busy(VMAvatar a, short objectID)
        {
            var q = a.Thread.Queue;
            for (int i = 0; i < q.Count; i++) if (q[i]?.Callee != null && q[i].Callee.ObjectID == objectID) return true;
            return false;
        }

        private static void Finish(VM vm, VMAvatar a, Chore c, bool did, int rest)
        {
            Set(a, TargetAttr, 0);
            Set(a, TodoAttr, rest);
            int st = U(a, StateAttr);
            int done = (st >> 10) & 15;
            if (did)
            {
                done = Math.Min(15, done + 1);
                st = (st & ~(15 << 10)) | (done << 10);
                Set(a, StateAttr, st);
                int paid = 0;
                int amount = PayLevels[(st >> 14) & 3];
                if (a.IsChild && amount > 0)
                {
                    var fam = vm.TS1State?.CurrentFamily;
                    if (fam != null && fam.Budget >= amount)
                    {
                        fam.Budget -= amount;
                        paid = amount;
                        try { a.ShowMoneyHeadline(amount); } catch { }
                    }
                }
                try { ChoreDone?.Invoke(vm, a, c, paid); } catch (Exception e) { Console.WriteLine("[SimsRedux] chores: " + e.Message); }
            }
            if (rest == 0)
            {
                // the list is over: a small mood bump for doing their part
                if (done > 0)
                {
                    Motive(a, VMMotive.Fun, 6);
                    Motive(a, VMMotive.Comfort, 4);
                }
                Set(a, StateAttr, U(a, StateAttr) & ~(15 << 10));
                Fire(AllDone, vm, a, done);
            }
        }

        private static void Motive(VMAvatar a, VMMotive m, int add)
        {
            a.SetMotiveData(m, (short)Math.Max(-100, Math.Min(100, a.GetMotiveData(m) + add)));
        }

        /// <summary>Puts the object's own menu entry in the Sim's queue, as the player clicking it would (its own check ran in Find).</summary>
        private static bool Push(VM vm, VMAvatar a, VMEntity piece, TTABInteraction ia)
        {
            var action = piece.GetAction((int)ia.TTAIndex, a, vm.Context, false, new short[4]);
            if (action == null) return false;
            action.Priority = (short)VMQueuePriority.UserDriven;
            a.Thread.EnqueueAction(action);
            return true;
        }

        // ------------------------------------------------------------------ what needs doing

        /// <summary>Which chore a menu entry (by its name) could be - the object decides the rest (ChoreOf).</summary>
        private static Chore ByName(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0 || n.IndexOf("Eat", StringComparison.OrdinalIgnoreCase) >= 0) return Chore.None;
            if (n.StartsWith("Repair", StringComparison.OrdinalIgnoreCase)) return Chore.Repair;
            if (n.IndexOf("Pay Bills", StringComparison.OrdinalIgnoreCase) >= 0) return Chore.Bills;
            if (n.StartsWith("Make Bed", StringComparison.OrdinalIgnoreCase) || n.Equals("Make", StringComparison.OrdinalIgnoreCase)) return Chore.Beds;
            if (n.IndexOf("Empty Trash", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Take Out Trash", StringComparison.OrdinalIgnoreCase) >= 0
                || n.Equals("Empty", StringComparison.OrdinalIgnoreCase)) return Chore.Trash;
            if (n.Equals("Water", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Water ", StringComparison.OrdinalIgnoreCase)) return Chore.Plants;
            if (n.StartsWith("Feed", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Fill", StringComparison.OrdinalIgnoreCase)) return Chore.Pets;
            if (n.Equals("Get Mail", StringComparison.OrdinalIgnoreCase) || n.Equals("Check Mail", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Collect Mail", StringComparison.OrdinalIgnoreCase) || n.Equals("Pick Up Mail", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Newspaper", StringComparison.OrdinalIgnoreCase) >= 0 && (n.StartsWith("Get", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Pick Up", StringComparison.OrdinalIgnoreCase))
                || n.Equals("Get Paper", StringComparison.OrdinalIgnoreCase) || n.Equals("Pick Up Paper", StringComparison.OrdinalIgnoreCase)) return Chore.Mail;
            if (n.Equals("Wash Dishes", StringComparison.OrdinalIgnoreCase) || n.Equals("Do Dishes", StringComparison.OrdinalIgnoreCase)) return Chore.Dishes;
            if (n.Equals("Clean", StringComparison.OrdinalIgnoreCase) || n.Equals("Clean Up", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Clean ", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Mop", StringComparison.OrdinalIgnoreCase))
                return Chore.Clean; // (or the dishes - ChoreOf looks at the object)
            return Chore.None;
        }

        // (worked out once per object type's menu and kept, as VMFindBestAction does)
        private sealed class NameEntry { public string Name; public Chore Kind; }
        private static readonly ConditionalWeakTable<TTAs, Dictionary<uint, NameEntry>> Names = new ConditionalWeakTable<TTAs, Dictionary<uint, NameEntry>>();

        private static Chore ByName(TTAs ttas, uint index)
        {
            if (ttas == null) return Chore.None;
            var name = ttas.GetString((int)index) ?? "";
            var dict = Names.GetValue(ttas, _ => new Dictionary<uint, NameEntry>());
            lock (dict)
            {
                NameEntry e;
                if (!dict.TryGetValue(index, out e)) { e = new NameEntry(); dict[index] = e; }
                if (!ReferenceEquals(e.Name, name)) { e.Kind = ByName(name); e.Name = name; }
                return e.Kind;
            }
        }

        private static bool Has(string label, params string[] words)
        {
            foreach (var w in words) if (label.Contains(w)) return true;
            return false;
        }

        /// <summary>A plate, a meal, a snack or a pizza box - "the dishes" (not a pet's bowl).</summary>
        public static bool IsDish(VMEntity obj)
        {
            if (ReduxCooking.IsPlate(obj) || ReduxLeftovers.IsGroupMeal(obj)) return true;
            if (ReduxWooHoo.LabelWord(obj, "pet", "pets", "dog", "cat", "kitty", "puppy")) return false;
            var label = ReduxWooHoo.Label(obj);
            return label.StartsWith("food") || Has(label, " food", "plate", "dish", "meal", "snack", "pizza", "cake", "cereal", "burger", "leftover");
        }

        /// <summary>The chore this menu entry of this object is (None: not one).</summary>
        public static Chore ChoreOf(VMEntity obj, TTABInteraction ia)
        {
            var kind = ByName(obj.TreeTableStrings, ia.TTAIndex);
            if (kind == Chore.None) return kind;
            switch (kind)
            {
                case Chore.Clean:
                    if (IsDish(obj))
                    {
                        // (a meal with servings still on it is left alone - someone may still want some)
                        if (ReduxLeftovers.IsGroupMeal(obj) && obj.GetAttribute(2) > 0) return Chore.None;
                        return Chore.Dishes;
                    }
                    return Chore.Clean;
                case Chore.Pets:
                    {
                        var label = ReduxWooHoo.Label(obj);
                        var name = (obj.TreeTableStrings?.GetString((int)ia.TTAIndex) ?? "").ToLowerInvariant();
                        if (Has(label, "baby", "bassinet", "crib")) return Chore.None;
                        return Has(label, "bowl", "feeder", "pet", "aquarium", "fish", "tank", "dish") || Has(name, "fish", "pet", "bowl") ? Chore.Pets : Chore.None;
                    }
                case Chore.Repair:
                    foreach (var part in obj.MultitileGroup.Objects)
                        if (part.GetValue(VMStackObjectVariable.RepairState) > 0) return Chore.Repair;
                    return Chore.None;
            }
            return kind;
        }

        /// <summary>
        /// The nearest thing on the lot with this chore that needs doing now - its own check says the Sim can do it now -
        /// and that nobody else is already doing (the piece and its menu entry). Null: nothing left.
        /// </summary>
        public static VMEntity Find(VM vm, VMAvatar a, Chore c, out VMEntity piece, out TTABInteraction found)
        {
            piece = null; found = null;
            var cands = new List<Tuple<int, VMEntity, TTABInteraction>>();
            var seen = new HashSet<short>();
            int ax = a.Position.TileX, ay = a.Position.TileY, al = a.Position.Level;
            foreach (var e in vm.Entities)
            {
                if (!(e is VMGameObject) || e.Dead || e.Position == LotTilePos.OUT_OF_WORLD || e.MultitileGroup == null) continue;
                if (((VMGameObject)e).Disabled > 0) continue;
                var obj = e.MultitileGroup.GetInteractionGroupLeader(e);
                if (obj == null || !seen.Add(obj.ObjectID) || obj.TreeTable?.Interactions == null) continue;
                foreach (var ia in obj.TreeTable.Interactions)
                {
                    if (ChoreOf(obj, ia) != c) continue;
                    if (TakenByOther(vm, a, obj)) break;
                    int dx = obj.Position.TileX - ax, dy = obj.Position.TileY - ay;
                    int d = dx * dx + dy * dy + (obj.Position.Level != al ? 400 : 0);
                    cands.Add(Tuple.Create(d, obj, ia));
                    break;
                }
            }
            if (cands.Count == 0) return null;
            // (nearest first; the same distance: the lower object id - every game the same order)
            cands.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.Item2.ObjectID.CompareTo(y.Item2.ObjectID));

            var thread = a.Thread;
            // the objects' check trees share this Sim's temp registers: put them back exactly as they were afterwards
            var temps = (short[])thread.TempRegisters.Clone();
            var tempXL = (int[])thread.TempXL.Clone();
            var hide = a.ObjectData[(int)VMStackObjectVariable.HideInteraction];
            try
            {
                int checks = 0;
                foreach (var t in cands)
                {
                    if (++checks > 16) break; // (a lot full of them: the nearest are plenty)
                    VMQueuedAction action = null;
                    try
                    {
                        action = t.Item2.GetAction((int)t.Item3.TTAIndex, a, vm.Context, false, new short[4]);
                        if (action == null) continue;
                        a.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                        if (thread.CheckAction(action, false) == null) continue;
                    }
                    catch (Exception) { continue; } // (a broken object's check never stops the lot - the same on every machine)
                    piece = t.Item2; found = t.Item3;
                    return t.Item2;
                }
            }
            finally
            {
                Array.Copy(temps, thread.TempRegisters, Math.Min(temps.Length, thread.TempRegisters.Length));
                Array.Copy(tempXL, thread.TempXL, Math.Min(tempXL.Length, thread.TempXL.Length));
                a.ObjectData[(int)VMStackObjectVariable.HideInteraction] = hide;
            }
            return null;
        }

        /// <summary>Another Sim is already doing it (or has it queued).</summary>
        private static bool TakenByOther(VM vm, VMAvatar me, VMEntity obj)
        {
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var x = e as VMAvatar;
                if (x == null || x == me || x.Thread == null) continue;
                var q = x.Thread.Queue;
                for (int i = 0; i < q.Count; i++)
                {
                    var cal = q[i]?.Callee;
                    if (cal != null && (cal == obj || cal.MultitileGroup == obj.MultitileGroup) && q[i].Priority >= (short)VMQueuePriority.UserDriven) return true;
                }
            }
            return false;
        }

        /// <summary>For the board: is there anything for this chore on the lot right now (by name and object only)?</summary>
        public static bool AnyOnLot(VM vm, Chore c)
        {
            try
            {
                foreach (var e in vm.Entities)
                {
                    if (!(e is VMGameObject) || e.Dead || e.Position == LotTilePos.OUT_OF_WORLD || e.MultitileGroup == null) continue;
                    var obj = e.MultitileGroup.GetInteractionGroupLeader(e);
                    if (obj != e || obj.TreeTable?.Interactions == null) continue;
                    foreach (var ia in obj.TreeTable.Interactions) if (ChoreOf(obj, ia) == c) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
