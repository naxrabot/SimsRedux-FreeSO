using System;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.2 (Part B #3): multitasking - Sims doing two things at once, Sims 3/4 style, the lot's side. Simitone's
    /// Utils/ReduxMultitask.cs is the game's side (the switch, the "+ Chatting" tag on the queue, a Happy reason).
    ///
    /// Sims 1 runs one action at a time per Sim (VMThread: Queue[0] is the ActiveAction, the rest wait), and an object's
    /// script owns the Sim while it runs (the seat they're held in, the motive changes it set). So nothing here touches the
    /// queue or the scripts: a second activity is a "layer" on top of the one the Sim is doing. Once a game minute (from the
    /// lot's ticks, VMAvatar.Tick), a Sim who is settled - sitting in a seat (a chair, a sofa, a bench, at the dining table),
    /// eating, watching TV or relaxing in bed - gets at most ONE extra:
    ///   Chatting     - another settled Sim is close by in the same room: a little Social, now and then a little closer
    ///                  (daily relationship, lifetime more slowly), and a talk balloon over their head;
    ///   Watching TV  - someone is watching a TV in the same room, close enough to see it: a little Fun;
    ///   Music        - someone is dancing to a stereo in the same room: a little Fun;
    ///   Reading      - relaxing in bed (awake), with a bookcase on the lot: a little Fun.
    /// The action they're doing goes on exactly as before - it keeps its own needs gains; the extra only adds the second one.
    ///
    /// Fair (no endless needs): the extra gains are slow (about a third of the real thing) and stop at a cap well short of full
    /// (Social 60, Fun 45-50), only while the main action runs; the relationship only up to RelCap and at most RelPerDay points
    /// a day per Sim. Visiting players' Sims chat and get Social but no relationship change (each player's game keeps its
    /// own Sims' relationships). Pets, service NPCs, sleeping Sims, Sims in a real conversation or WooHoo: nothing.
    ///
    /// Everything is worked out from the lot itself (positions, rooms, actions, seats, attributes, the lot's clock and tick),
    /// never the player's own disk, so on a visit every game does the same thing at the same moment. Kept on the Sims as
    /// person-object attributes 110-114 (saved with the lot, sent to everyone joining; no save format change):
    ///   110 StateAttr - bits 0-2 the extra now (Extra), bits 3-14 minutes they've been at it
    ///   111 WithAttr  - who they're chatting with / the TV / the stereo (object id, 0 none)
    ///   112 DayAttr   - bits 0-4 the day of the month, bits 5-10 relationship points from chatting that day
    ///   113 (spare, kept for this feature)
    ///   114 FlagsAttr - bit 0: off for this Sim (the lot's game turned multitasking off - VMNetReduxMultitaskCmd)
    /// </summary>
    public static class VMReduxMultitask
    {
        public const int StateAttr = 110, WithAttr = 111, DayAttr = 112, SpareAttr = 113, FlagsAttr = 114;
        private const int OffBit = 1;

        public enum Extra : byte { None = 0, Chatting = 1, WatchingTV = 2, Music = 3, Reading = 4 }

        /// <summary>How often each Sim's extra is looked at (ticks: a game minute).</summary>
        public const int Every = 30;
        /// <summary>How close (tiles, either way) for chatting, and to see a TV / hear a stereo.</summary>
        public const int ChatReach = 4, ViewReach = 8;
        /// <summary>The extras stop raising a need here (-100 to 100).</summary>
        public const int SocialCap = 60, TVFunCap = 50, MusicFunCap = 45, ReadingFunCap = 45;
        /// <summary>Game minutes per point: Social (chatting), Fun (TV, music, reading).</summary>
        public const int SocialEvery = 3, TVEvery = 4, MusicEvery = 5, ReadingEvery = 5;
        /// <summary>Relationship from chatting: daily +1 every RelEvery minutes (lifetime +1 every LifeEvery), only below RelCap,
        /// at most RelPerDay points a day per Sim.</summary>
        public const int RelEvery = 15, LifeEvery = 45, RelCap = 50, RelPerDay = 10;
        /// <summary>A talk balloon every BalloonEvery minutes of chatting, shown BalloonTicks ticks.</summary>
        public const int BalloonEvery = 8, BalloonTicks = 75;

        /// <summary>
        /// The conversation topics - the base game's own talk balloon pictures (Sprites.iff 0x190 + index: travel 0-2, weather
        /// 12-14, sports 15-17, music 18-20, the outdoors 21-23, toys 24-26, pets 30-32, school 33-35; never violence or
        /// politics) - for grown-ups...
        /// </summary>
        private static readonly sbyte[] Topics = { 0, 1, 2, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 30, 31, 32 };
        /// <summary>...and for a child (toys, pets, school, sports, music).</summary>
        private static readonly sbyte[] KidTopics = { 24, 25, 26, 30, 31, 32, 33, 34, 35, 15, 16, 18, 19 };
        /// <summary>The game's "conversation" balloon behind the picture.</summary>
        private const byte TalkBalloon = 2;

        private static int U(VMEntity a, int attr) { return a.GetAttribute(attr) & 0xFFFF; }
        private static void Set(VMEntity a, int attr, int v) { a.SetAttribute(attr, unchecked((short)(v & 0xFFFF))); }

        // ------------------------------------------------------------------ for the game (reading only)

        /// <summary>The extra the Sim has now.</summary>
        public static Extra Of(VMAvatar a) { return a == null ? Extra.None : (Extra)(U(a, StateAttr) & 7); }

        /// <summary>Who they're chatting with / the TV / the stereo (null: nothing).</summary>
        public static VMEntity With(VM vm, VMAvatar a)
        {
            if (vm == null || a == null) return null;
            short id = a.GetAttribute(WithAttr);
            return id == 0 ? null : vm.GetObjectById(id);
        }

        /// <summary>Minutes they've been at the extra.</summary>
        public static int Minutes(VMAvatar a) { return a == null ? 0 : (U(a, StateAttr) >> 3) & 0xFFF; }

        public static bool Off(VMAvatar a) { return a != null && (U(a, FlagsAttr) & OffBit) != 0; }

        /// <summary>Short words for the tag on the queue.</summary>
        public static string Label(Extra e)
        {
            switch (e)
            {
                case Extra.Chatting: return "Chatting";
                case Extra.WatchingTV: return "Watching TV";
                case Extra.Music: return "Listening to music";
                case Extra.Reading: return "Reading";
            }
            return "";
        }

        /// <summary>A Sim's extra started (the extra, who/what with) - on every game, from the lot's ticks.</summary>
        public static event Action<VM, VMAvatar, Extra, VMEntity> Started;

        // ------------------------------------------------------------------ the switch (VMNetReduxMultitaskCmd)

        private static bool Counts(VMAvatar a) { return a != null && !a.Dead && !a.IsPet; }

        /// <summary>Does every Sim on the lot already have this switch?</summary>
        public static bool SwitchIs(VM vm, bool on)
        {
            if (vm == null) return true;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var a = e as VMAvatar;
                if (!Counts(a)) continue;
                if (Off(a) == on) return false;
            }
            return true;
        }

        /// <summary>The lot's game turns multitasking on or off for every Sim on the lot (off: their extra stops now).</summary>
        public static void SetSwitch(VM vm, bool on)
        {
            if (vm == null) return;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var a = e as VMAvatar;
                if (!Counts(a)) continue;
                int f = U(a, FlagsAttr);
                int nf = on ? (f & ~OffBit) : (f | OffBit);
                if (nf != f) Set(a, FlagsAttr, nf);
                if (!on) Clear(a);
            }
        }

        private static void Clear(VMAvatar a)
        {
            if (a.GetAttribute(StateAttr) != 0) Set(a, StateAttr, 0);
            if (a.GetAttribute(WithAttr) != 0) Set(a, WithAttr, 0);
        }

        // ------------------------------------------------------------------ every tick (VMAvatar.Tick)

        public static void Tick(VMAvatar a)
        {
            if (a == null || a.IsPet || a.Dead) return;
            var vm = a.Thread?.Context?.VM;
            if (vm == null || !vm.TS1) return;
            if ((vm.Scheduler.CurrentTickID + (uint)a.ObjectID) % Every != 0) return;
            if (Off(a)) { Clear(a); return; }

            VMEntity with;
            var kind = Find(vm, a, out with);
            int state = U(a, StateAttr);
            var old = (Extra)(state & 7);
            if (kind == Extra.None) { Clear(a); return; }
            short withId = with == null ? (short)0 : with.ObjectID;
            bool same = kind == old && a.GetAttribute(WithAttr) == withId;
            int minutes = same ? ((state >> 3) & 0xFFF) + 1 : 1;
            if (minutes > 0xFFF) minutes = 0xF00 + (minutes % 0x60); // (hours on end: keeps counting, never back to "just started")
            Set(a, StateAttr, (int)kind | (minutes << 3));
            if (a.GetAttribute(WithAttr) != withId) Set(a, WithAttr, withId);
            if (kind != old)
            {
                try { Started?.Invoke(vm, a, kind, with); } catch (Exception e) { Console.WriteLine("[SimsRedux] multitask: " + e.Message); }
            }

            switch (kind)
            {
                case Extra.Chatting:
                    if (minutes % SocialEvery == 0) Up(a, VMMotive.Social, SocialCap);
                    if (minutes % RelEvery == 0) Closer(vm, a, with as VMAvatar, minutes % LifeEvery == 0);
                    if (minutes % BalloonEvery == (a.ObjectID & 0x7FFF) % BalloonEvery) Balloon(vm, a, minutes);
                    break;
                case Extra.WatchingTV:
                    if (minutes % TVEvery == 0) Up(a, VMMotive.Fun, TVFunCap);
                    break;
                case Extra.Music:
                    if (minutes % MusicEvery == 0) Up(a, VMMotive.Fun, MusicFunCap);
                    break;
                case Extra.Reading:
                    if (minutes % ReadingEvery == 0) Up(a, VMMotive.Fun, ReadingFunCap);
                    break;
            }
        }

        /// <summary>One point up, never past the cap (a need already above it is left alone).</summary>
        private static void Up(VMAvatar a, VMMotive m, int cap)
        {
            int v = a.GetMotiveData(m);
            if (v >= cap) return;
            a.SetMotiveData(m, (short)Math.Min(cap, v + 1));
        }

        /// <summary>A little closer to who they're chatting with (their side of it - the other Sim's own tick does theirs).</summary>
        private static void Closer(VM vm, VMAvatar a, VMAvatar b, bool life)
        {
            if (b == null || b.Dead || b.IsPet) return;
            // (another player's Sim: their relationships are kept by their own game - Social only)
            if (VMReduxRomance.Visitor(a) || VMReduxRomance.Visitor(b)) return;
            int day = Math.Max(1, Math.Min(31, vm.Context.Clock.DayOfMonth));
            int d = U(a, DayAttr);
            int given = (d & 0x1F) == day ? (d >> 5) & 63 : 0;
            if (given >= RelPerDay) return;
            // (counted before the relationship is looked at: the count is lot data and only comes from the lot, so it's the
            //  same on every game; the relationship itself lives in the neighbourhood, as the game's own chats keep it)
            Set(a, DayAttr, day | (Math.Min(63, given + 1) << 5));
            var r = ReduxWooHoo.Rel(a, b);
            int daily = (r != null && r.Count > 0) ? r[0] : 0;
            if (daily >= RelCap) return;
            VMReduxMatch.Closer(a, b, 1, life ? 1 : 0);
        }

        /// <summary>The Sims 1 talk balloon (a topic picture) over their head for a moment - only when nothing else is showing.</summary>
        private static void Balloon(VM vm, VMAvatar a, int minutes)
        {
            try
            {
                if (a.Headline != null) return;
                var list = a.IsChild ? KidTopics : Topics;
                int i = (minutes / BalloonEvery + (a.ObjectID & 0x7FFF)) % list.Length;
                var op = new VMSetBalloonHeadlineOperand
                {
                    Group = VMSetBalloonHeadlineOperandGroup.Conversation,
                    Index = list[i],
                    Duration = BalloonTicks,
                    Type = TalkBalloon,
                };
                a.Headline = new VMRuntimeHeadline(op, a, null, list[i]) { Duration = BalloonTicks };
                a.HeadlineRenderer = vm.Headline?.Get(a.Headline);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ what they're doing

        /// <summary>How a Sim is settled for a second activity (None: not settled - walking, cooking, dancing, asleep...).</summary>
        private enum Settled : byte { None, Seat, Eating, Watching, Bed }

        private static bool IsTV(VMEntity obj)
        {
            return obj != null && !(obj is VMAvatar) && ReduxTV.IsTV(obj.TreeTable, obj.TreeTableStrings);
        }

        private static bool IsStereo(VMEntity obj)
        {
            if (obj == null || obj is VMAvatar) return false;
            var label = ReduxWooHoo.Label(obj);
            return label.Contains("stereo") || label.Contains("boombox") || label.Contains("boom box") || label.Contains("jukebox") || label.Contains("radio");
        }

        /// <summary>A seat that isn't a bathroom one.</summary>
        private static bool GoodSeat(VMEntity seat)
        {
            if (seat == null || seat is VMAvatar) return false;
            var label = ReduxWooHoo.Label(seat);
            return !(label.Contains("toilet") || label.Contains("potty") || label.Contains("commode") || label.Contains("tub")
                || label.Contains("bath") || label.Contains("shower"));
        }

        private static string NameOf(VMQueuedAction act) { return (act?.Name ?? "").Trim(); }

        private static Settled SettledOf(VMAvatar a)
        {
            if (a == null || a.Dead || a.IsPet || a.Thread == null) return Settled.None;
            if (a.Position == LotTilePos.OUT_OF_WORLD) return Settled.None;
            if (a.GetMotiveData(VMMotive.SleepState) != 0) return Settled.None;
            if (a.GetPersonData(VMPersonDataVariable.PersonType) >= 2) return Settled.None; // (the maid, the police...)
            if (ReduxWooHoo.Busy(a)) return Settled.None;
            var act = a.Thread.ActiveAction;
            var callee = act?.Callee;
            if (callee is VMAvatar && callee != a) return Settled.None; // (in a real conversation, or being talked to)
            var name = NameOf(act);
            if (callee != null && (name.StartsWith("Watch TV", StringComparison.OrdinalIgnoreCase) || name.Equals("Join", StringComparison.OrdinalIgnoreCase)) && IsTV(callee))
                return Settled.Watching;
            if (name.StartsWith("Eat", StringComparison.OrdinalIgnoreCase)) return Settled.Eating;
            if (callee != null && name.Equals("Relax", StringComparison.OrdinalIgnoreCase) && ReduxBeds.IsBed(callee)) return Settled.Bed;
            if (a.GetPersonData(VMPersonDataVariable.Posture) == 1 && GoodSeat(a.Container)) return Settled.Seat;
            return Settled.None;
        }

        private static bool Near(VM vm, VMEntity a, VMEntity b, int reach, ushort room)
        {
            var pa = a.Position; var pb = b.Position;
            if (pb == LotTilePos.OUT_OF_WORLD || pa.Level != pb.Level) return false;
            if (Math.Abs(pa.TileX - pb.TileX) > reach || Math.Abs(pa.TileY - pb.TileY) > reach) return false;
            return vm.Context.GetObjectRoom(b) == room;
        }

        private static int Dist(VMEntity a, VMEntity b)
        {
            int dx = a.Position.x - b.Position.x, dy = a.Position.y - b.Position.y;
            return dx * dx + dy * dy;
        }

        /// <summary>The extra this Sim can have now (and who / what with). Only the lot's own state is read.</summary>
        private static Extra Find(VM vm, VMAvatar a, out VMEntity with)
        {
            with = null;
            var mine = SettledOf(a);
            if (mine == Settled.None) return Extra.None;
            ushort room = vm.Context.GetObjectRoom(a);

            // chatting: the nearest settled Sim close by in the same room (the same distance: the lower object id)
            VMAvatar buddy = null; int best = int.MaxValue;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var o = e as VMAvatar;
                if (o == null || o == a || o.IsPet || o.Dead || Off(o)) continue;
                if (!Near(vm, a, o, ChatReach, room)) continue;
                if (SettledOf(o) == Settled.None) continue;
                int d = Dist(a, o);
                if (d < best || (d == best && buddy != null && o.ObjectID < buddy.ObjectID)) { best = d; buddy = o; }
            }
            if (buddy != null) { with = buddy; return Extra.Chatting; }

            // a TV someone is watching / a stereo someone is dancing to, in the same room and close enough
            VMEntity tv = null, stereo = null; int bestTV = int.MaxValue, bestStereo = int.MaxValue;
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var o = e as VMAvatar;
                if (o == null || o == a || o.IsPet || o.Dead || o.Thread == null) continue;
                var act = o.Thread.ActiveAction;
                var obj = act?.Callee;
                if (obj == null || obj is VMAvatar || !Near(vm, a, obj, ViewReach, room)) continue;
                var name = NameOf(act);
                if (mine != Settled.Watching && (name.StartsWith("Watch TV", StringComparison.OrdinalIgnoreCase) || name.Equals("Join", StringComparison.OrdinalIgnoreCase)) && IsTV(obj))
                {
                    int d = Dist(a, obj);
                    if (d < bestTV || (d == bestTV && tv != null && obj.ObjectID < tv.ObjectID)) { bestTV = d; tv = obj; }
                }
                else if ((name.StartsWith("Dance", StringComparison.OrdinalIgnoreCase) || name.Equals("Join", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Listen", StringComparison.OrdinalIgnoreCase)) && IsStereo(obj))
                {
                    int d = Dist(a, obj);
                    if (d < bestStereo || (d == bestStereo && stereo != null && obj.ObjectID < stereo.ObjectID)) { bestStereo = d; stereo = obj; }
                }
            }
            if (tv != null) { with = tv; return Extra.WatchingTV; }
            if (stereo != null) { with = stereo; return Extra.Music; }

            // reading in bed: relaxing (awake), with a bookcase on the lot to take a book from
            if (mine == Settled.Bed && HasBookcase(vm)) return Extra.Reading;
            return Extra.None;
        }

        /// <summary>17.2: (the lot, its tick, the answer) - the bookcase check runs once per lot tick, not once per Sim in bed.</summary>
        private static Tuple<WeakReference, uint, bool> BookcaseCache; // (weak: a lot that was left isn't kept in memory)

        private static bool HasBookcase(VM vm)
        {
            // (per tick, not longer: a player joining a visit mid-minute works it out fresh, so every game agrees)
            uint tick = vm.Scheduler.CurrentTickID;
            var c = BookcaseCache;
            if (c != null && c.Item2 == tick && ReferenceEquals(c.Item1.Target, vm)) return c.Item3;
            bool has = false;
            foreach (var e in vm.Entities)
            {
                if (!(e is VMGameObject) || e.Dead || e.Position == LotTilePos.OUT_OF_WORLD) continue;
                var label = (e.Object?.OBJ?.ChunkLabel ?? "").ToLowerInvariant();
                if (label.Contains("bookcase") || label.Contains("bookshelf") || label.Contains("book shelf")) { has = true; break; }
            }
            BookcaseCache = Tuple.Create(new WeakReference(vm), tick, has);
            return has;
        }
    }
}
