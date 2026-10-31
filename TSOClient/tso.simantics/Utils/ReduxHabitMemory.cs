using System;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.5 (Part B #9, B5b): what a Sim's habits are made of - the memory behind VMReduxBrainHabits' factors and the
    /// "What (Sim) has learned" page. Learned from what the Sim actually does, a game minute at a time (VMAvatar.Tick):
    ///   - favourites: the seat they sit in most, the dish they eat most (cooking menus - ReduxCooking's dish on the plate),
    ///     the TV channel they switch to most, the kind of fun they spend most time on (their hobby: TV and screens, games
    ///     and play, chatting, skills, working out) and the person they spend most time with. Each is a "top two" count
    ///     (the favourite and the one catching up); every new day all counts drop by a quarter, so favourites can change;
    ///   - boredom (variety): each kind of fun gets more boring the more of it they've done lately (+3 on starting it, +2 each
    ///     half hour at it - chatting +1, since every chat is a short action of its own) and less every game hour (-1);
    ///   - sleep: when they usually go to bed (sleep that starts 19:00-03:59) and when they get up, on work or school days
    ///     and on days off apart (a running average: a quarter of the way to each new night).
    ///   - finish first: which of their own actions they're holding on to (VMReduxBrainHabits.FinishFirst) and since when.
    /// Kept in person attributes 140-169 (shorts; saved with the Sim, travels with a visiting Sim - no save format change).
    ///
    /// LOCKSTEP: only lot state, inside the lot's ticks - the Sim's queue, needs, container, the lot clock, objects' GUIDs and
    /// attributes and menu entries' English names (VMReduxBrain.LotName). Once per game minute per Sim (the minute is kept in
    /// an attribute, so a guest joining part way carries on exactly where the host is). Only the household's Sims and a
    /// visiting player's own Sim learn (their attributes are kept); everyone with an Adaptive brain is weighed by what's there.
    /// </summary>
    public static class VMReduxHabitMemory
    {
        // ---- the attributes (140-169: B5b's habits range) ----
        public const int LastUidAttr = 140;       // the queued action seen last (its UID) - a new one is a "start"
        public const int LastMinuteAttr = 141;    // the game minute (time of day + 1) already looked at
        public const int LastDayAttr = 142;       // the day (month * 32 + day) last decayed
        public const int SeatTopHi = 143, SeatTopLo = 144, SeatTopScore = 145, SeatAltHi = 146, SeatAltLo = 147, SeatAltScore = 148; // seat object GUIDs (hi/lo 16 bits)
        public const int DishIds = 149, DishTopScore = 150, DishAltScore = 151;           // ids: low byte the favourite, high byte the one catching up
        public const int ChannelIds = 152, ChannelTopScore = 153, ChannelAltScore = 154;
        public const int FriendTop = 155, FriendTopScore = 156, FriendAlt = 157, FriendAltScore = 158; // neighbour ids
        public const int HobbyFirst = 159;        // 159-161: hobby time, a byte (0-255) per kind: 159 screen|play<<8, 160 social|skill<<8, 161 exercise
        public const int BoredFirst = 162;        // 162-163: boredom, 4 bits (0-15) per kind: 162 screen, play, social, skill; 163 exercise
        public const int BedtimeAttr = 164;       // usual bedtime, minutes after noon + 1 (0 = not learned)
        public const int WakeWorkAttr = 165, WakeOffAttr = 166; // usual time up (time of day + 1) on work/school days, on days off
        public const int SleepAttr = 167;         // bit 0 asleep last minute; bits 1-4 nights learned; 5-8 mornings off; 9-12 work mornings
        public const int SleepStartAttr = 168;    // when this sleep began (time of day + 1)
        public const int HoldAttr = 169;          // finish first: the held action's UID (VMReduxBrainHabits.FinishFirst)
        // (and when that hold began - time of day + 1 - in bits 4-14 of attribute 163, above the exercise boredom: HoldStart)

        /// <summary>The kinds of fun a hobby can be (VMReduxBrain.Kind), in the order kept here.</summary>
        public static readonly VMReduxBrain.Kind[] HobbyKinds = { VMReduxBrain.Kind.Screen, VMReduxBrain.Kind.Play, VMReduxBrain.Kind.Social, VMReduxBrain.Kind.Skill, VMReduxBrain.Kind.Exercise };
        /// <summary>Plain English for each hobby kind (the page and "Why I'm doing this").</summary>
        public static readonly string[] HobbyNames = { "TV and screens", "games and play", "chatting", "learning skills", "working out" };

        /// <summary>The TV channels ("Switch to ..." on every TV - ReduxTV), 1-8 as kept here.</summary>
        public static readonly string[] Channels = { "", "Action", "Horror", "Romance", "Cartoons", "Animal Show", "News", "Sports", "Entertainment" };

        // ---- thresholds ----
        /// <summary>A favourite seat / dish / channel / friend needs at least this count, and this much more than the runner-up.</summary>
        public const int FavouriteMin = 12, FavouriteLead = 4;
        /// <summary>A hobby needs this much time (half hours, roughly) and a lead over the next kind.</summary>
        public const int HobbyMin = 16, HobbyLead = 6;
        /// <summary>Boredom: "a bit bored" from this, "really bored" from the next.</summary>
        public const int Bored = 9, VeryBored = 12;
        /// <summary>Nights (or mornings) before a bedtime (time up) counts as a habit.</summary>
        public const int HabitNights = 3;
        private const int Cap = 2000;

        public static int HobbyIndex(VMReduxBrain.Kind k)
        {
            switch (k)
            {
                case VMReduxBrain.Kind.Screen: return 0;
                case VMReduxBrain.Kind.Play: return 1;
                case VMReduxBrain.Kind.Social: return 2;
                case VMReduxBrain.Kind.Skill: return 3;
                case VMReduxBrain.Kind.Exercise: return 4;
            }
            return -1;
        }

        /// <summary>Does this Sim learn habits on this lot? The household's Sims and a visiting player's own Sim (their attributes are kept).</summary>
        public static bool Learns(VM vm, VMAvatar sim)
        {
            if (vm == null || !vm.TS1 || sim == null || sim.IsPet || sim.Dead) return false;
            if (sim.GetPersonData(VMPersonDataVariable.IsGhost) != 0) return false;
            return ReduxComputer.IsHouseholdSim(vm, sim) || VMReduxBrain.VisitingSim(vm, sim);
        }

        // ================================================================ reading (factors, the page)

        private static int Byte(VMAvatar a, int attr, bool high) { int v = a.GetAttribute(attr) & 0xFFFF; return high ? (v >> 8) & 0xFF : v & 0xFF; }

        public static int HobbyTime(VMAvatar a, int i)
        {
            if (a == null || i < 0 || i > 4) return 0;
            return Byte(a, HobbyFirst + i / 2, (i & 1) == 1);
        }

        public static int Boredom(VMAvatar a, int i)
        {
            if (a == null || i < 0 || i > 4) return 0;
            int attr = BoredFirst + i / 4, shift = (i % 4) * 4;
            return ((a.GetAttribute(attr) & 0xFFFF) >> shift) & 0xF;
        }

        /// <summary>The favourite seat's object GUID, or 0.</summary>
        public static uint FavouriteSeat(VMAvatar a)
        {
            if (a == null) return 0;
            int top = a.GetAttribute(SeatTopScore), alt = a.GetAttribute(SeatAltScore);
            if (top < FavouriteMin || top < alt + FavouriteLead) return 0;
            return SeatGuid(a, SeatTopHi, SeatTopLo);
        }

        /// <summary>The favourite dish's id (ReduxCooking.Dishes), or 0.</summary>
        public static int FavouriteDish(VMAvatar a)
        {
            if (a == null) return 0;
            int top = a.GetAttribute(DishTopScore), alt = a.GetAttribute(DishAltScore);
            if (top < FavouriteMin || top < alt + FavouriteLead) return 0;
            return Byte(a, DishIds, false);
        }

        /// <summary>The favourite TV channel (1-8, Channels), or 0.</summary>
        public static int FavouriteChannel(VMAvatar a)
        {
            if (a == null) return 0;
            int top = a.GetAttribute(ChannelTopScore), alt = a.GetAttribute(ChannelAltScore);
            if (top < FavouriteMin || top < alt + FavouriteLead) return 0;
            int c = Byte(a, ChannelIds, false);
            return c > 0 && c < Channels.Length ? c : 0;
        }

        /// <summary>The favourite person's neighbour id, or 0.</summary>
        public static short FavouriteFriend(VMAvatar a)
        {
            if (a == null) return 0;
            int top = a.GetAttribute(FriendTopScore), alt = a.GetAttribute(FriendAltScore);
            if (top < FavouriteMin || top < alt + FavouriteLead) return 0;
            return a.GetAttribute(FriendTop);
        }

        /// <summary>The favourite hobby (an index into HobbyKinds), or -1.</summary>
        public static int FavouriteHobby(VMAvatar a)
        {
            if (a == null) return -1;
            int best = -1, bestT = 0, second = 0;
            for (int i = 0; i < HobbyKinds.Length; i++)
            {
                int t = HobbyTime(a, i);
                if (t > bestT) { second = bestT; bestT = t; best = i; }
                else if (t > second) second = t;
            }
            if (best < 0 || bestT < HobbyMin || bestT < second + HobbyLead) return -1;
            return best;
        }

        /// <summary>The kind of fun they're most bored of (index into HobbyKinds) when it's at least Bored, else -1.</summary>
        public static int MostBored(VMAvatar a, out int level)
        {
            level = 0;
            int best = -1;
            for (int i = 0; i < HobbyKinds.Length; i++)
            {
                int b = Boredom(a, i);
                if (b >= Bored && b > level) { level = b; best = i; }
            }
            return best;
        }

        /// <summary>Usual bedtime (time of day, minutes) or -1 until it's a habit.</summary>
        public static int UsualBedtime(VMAvatar a)
        {
            if (a == null) return -1;
            int s = a.GetAttribute(SleepAttr), v = a.GetAttribute(BedtimeAttr);
            if (((s >> 1) & 0xF) < HabitNights || v <= 0) return -1;
            return (v - 1 + 720) % 1440;
        }

        /// <summary>Usual time up (time of day) on work / school days (off = false) or days off, or -1 until it's a habit.</summary>
        public static int UsualWake(VMAvatar a, bool off)
        {
            if (a == null) return -1;
            int s = a.GetAttribute(SleepAttr);
            int n = off ? (s >> 5) & 0xF : (s >> 9) & 0xF;
            int v = a.GetAttribute(off ? WakeOffAttr : WakeWorkAttr);
            if (n < HabitNights || v <= 0) return -1;
            return v - 1;
        }

        /// <summary>Finish first: when the hold on the current action began (time of day), or -1.</summary>
        public static int HoldStart(VMAvatar a)
        {
            if (a == null) return -1;
            return (((a.GetAttribute(BoredFirst + 1) & 0xFFFF) >> 4) & 0x7FF) - 1;
        }

        /// <summary>Finish first: the action being held (its UID) and since when (time of day; -1 = none). Lot code only.</summary>
        public static void SetHold(VMAvatar a, ushort uid, int tod)
        {
            if (a == null) return;
            a.SetAttribute(HoldAttr, unchecked((short)uid));
            int cur = a.GetAttribute(BoredFirst + 1) & 0xFFFF;
            cur = (cur & 0x800F) | (((tod + 1) & 0x7FF) << 4);
            a.SetAttribute(BoredFirst + 1, (short)cur);
        }

        /// <summary>Has this Sim learned anything at all yet (the page says "Still getting to know them" otherwise)?</summary>
        public static bool Started(VMAvatar a)
        {
            return a != null && a.GetAttribute(LastMinuteAttr) != 0;
        }

        private static uint SeatGuid(VMAvatar a, int hi, int lo)
        {
            return ((uint)(a.GetAttribute(hi) & 0xFFFF) << 16) | (uint)(a.GetAttribute(lo) & 0xFFFF);
        }

        /// <summary>
        /// A day off for this Sim (0 = Monday): a child at the weekend (no school bus - ReduxWeek), a grown-up on a day their
        /// career doesn't work (ReduxWeek.Works), a grown-up without a job at the weekend.
        /// </summary>
        public static bool DayOff(VMAvatar sim, int day)
        {
            if (sim == null) return false;
            if (sim.IsChild) return day >= 5;
            int job = sim.GetPersonData(VMPersonDataVariable.JobType);
            if (job <= 0) return day >= 5;
            return !ReduxWeek.Works(job, day);
        }

        // ================================================================ learning (VMAvatar.Tick)

        /// <summary>Every tick for every Sim (VMAvatar.Tick); does its work once per game minute. Lot state only.</summary>
        public static void Tick(VMAvatar ava)
        {
            var thread = ava?.Thread;
            var vm = thread?.Context?.VM;
            if (vm == null || !vm.TS1 || ava.IsPet) return;
            var clock = vm.Context.Clock;
            int tod = clock.Hours * 60 + clock.Minutes;
            if (ava.GetAttribute(LastMinuteAttr) == tod + 1) return; // (cheap way out: this minute's done)
            if (!Learns(vm, ava)) return;
            ava.SetAttribute(LastMinuteAttr, (short)(tod + 1));

            // a new day: every count drops by a quarter (favourites can change)
            int day = clock.Month * 32 + clock.DayOfMonth;
            int lastDay = ava.GetAttribute(LastDayAttr);
            if (lastDay != day)
            {
                if (lastDay != 0) Decay(ava);
                ava.SetAttribute(LastDayAttr, (short)day);
            }
            // boredom wears off: one a game hour
            if (clock.Minutes == 0) Unbore(ava);

            Sleep(vm, ava, tod, clock);
            if (ava.Position == LotTilePos.OUT_OF_WORLD || ava.GetMotiveData(VMMotive.SleepState) != 0) return;

            if (tod % 10 == 0) Seat(ava);
            Doing(ava, thread, tod);
        }

        private static void Decay(VMAvatar a)
        {
            Scale(a, SeatTopScore); Scale(a, SeatAltScore);
            Scale(a, DishTopScore); Scale(a, DishAltScore);
            Scale(a, ChannelTopScore); Scale(a, ChannelAltScore);
            Scale(a, FriendTopScore); Scale(a, FriendAltScore);
            for (int i = 0; i < HobbyKinds.Length; i++) SetHobbyTime(a, i, HobbyTime(a, i) * 3 / 4);
        }

        private static void Scale(VMAvatar a, int attr)
        {
            int v = a.GetAttribute(attr);
            if (v != 0) a.SetAttribute(attr, (short)(v * 3 / 4));
        }

        private static void SetHobbyTime(VMAvatar a, int i, int v)
        {
            v = Math.Max(0, Math.Min(255, v));
            int attr = HobbyFirst + i / 2;
            int cur = a.GetAttribute(attr) & 0xFFFF;
            cur = (i & 1) == 1 ? (cur & 0x00FF) | (v << 8) : (cur & 0xFF00) | v;
            if ((short)cur != a.GetAttribute(attr)) a.SetAttribute(attr, (short)cur);
        }

        private static void SetBoredom(VMAvatar a, int i, int v)
        {
            v = Math.Max(0, Math.Min(15, v));
            int attr = BoredFirst + i / 4, shift = (i % 4) * 4;
            int cur = a.GetAttribute(attr) & 0xFFFF;
            cur = (cur & ~(0xF << shift)) | (v << shift);
            if ((short)cur != a.GetAttribute(attr)) a.SetAttribute(attr, (short)cur);
        }

        private static void Unbore(VMAvatar a)
        {
            for (int i = 0; i < HobbyKinds.Length; i++)
            {
                int b = Boredom(a, i);
                if (b > 0) SetBoredom(a, i, b - 1);
            }
        }

        /// <summary>
        /// The favourite and the one catching up (a "top two" count): the same one again adds to its count; a new one takes the
        /// runner-up's place when that's no bigger than this time's weight, otherwise wears the runner-up down. The bigger
        /// count is always the favourite. Ids are never 0 (0 = none).
        /// </summary>
        private static void Bump(ref int topId, ref int topS, ref int altId, ref int altS, int id, int w)
        {
            if (id == 0) return;
            if (id == topId) topS += w;
            else if (id == altId) altS += w;
            else if (altS <= w) { altId = id; altS = w; }
            else altS -= w;
            if (altS > topS) { int t = topId; topId = altId; altId = t; t = topS; topS = altS; altS = t; }
            topS = Math.Min(Cap, topS); altS = Math.Min(Cap, altS);
        }

        private static void Seat(VMAvatar ava)
        {
            var c = ava.Container as VMGameObject;
            if (c == null) return;
            var leader = c.MultitileGroup?.GetInteractionGroupLeader(c) ?? c;
            var objd = leader.Object?.OBJ;
            if (objd == null || (objd.FunctionFlags & 1) == 0) return; // (Buy mode's Seating)
            if (ReduxBeds.IsBed(leader)) return;                     // (a bed is for sleeping, not a favourite chair)
            int topId = (int)SeatGuid(ava, SeatTopHi, SeatTopLo), altId = (int)SeatGuid(ava, SeatAltHi, SeatAltLo);
            int topS = ava.GetAttribute(SeatTopScore), altS = ava.GetAttribute(SeatAltScore);
            Bump(ref topId, ref topS, ref altId, ref altS, (int)objd.GUID, 1);
            ava.SetAttribute(SeatTopHi, (short)(((uint)topId >> 16) & 0xFFFF)); ava.SetAttribute(SeatTopLo, (short)((uint)topId & 0xFFFF));
            ava.SetAttribute(SeatAltHi, (short)(((uint)altId >> 16) & 0xFFFF)); ava.SetAttribute(SeatAltLo, (short)((uint)altId & 0xFFFF));
            ava.SetAttribute(SeatTopScore, (short)topS); ava.SetAttribute(SeatAltScore, (short)altS);
        }

        private static void BumpPacked(VMAvatar ava, int idsAttr, int topAttr, int altAttr, int id, int w)
        {
            if (id <= 0 || id > 255) return;
            int ids = ava.GetAttribute(idsAttr) & 0xFFFF;
            int topId = ids & 0xFF, altId = (ids >> 8) & 0xFF;
            int topS = ava.GetAttribute(topAttr), altS = ava.GetAttribute(altAttr);
            Bump(ref topId, ref topS, ref altId, ref altS, id, w);
            ava.SetAttribute(idsAttr, (short)((topId & 0xFF) | ((altId & 0xFF) << 8)));
            ava.SetAttribute(topAttr, (short)topS); ava.SetAttribute(altAttr, (short)altS);
        }

        private static void Friend(VMAvatar ava, short id, int w)
        {
            if (id <= 0) return;
            int topId = ava.GetAttribute(FriendTop), altId = ava.GetAttribute(FriendAlt);
            int topS = ava.GetAttribute(FriendTopScore), altS = ava.GetAttribute(FriendAltScore);
            Bump(ref topId, ref topS, ref altId, ref altS, id, w);
            ava.SetAttribute(FriendTop, (short)topId); ava.SetAttribute(FriendAlt, (short)altId);
            ava.SetAttribute(FriendTopScore, (short)topS); ava.SetAttribute(FriendAltScore, (short)altS);
        }

        /// <summary>The TV channel (1-8) of a "Switch to ..." menu entry's English name, or 0.</summary>
        public static int ChannelOf(string english)
        {
            if (string.IsNullOrEmpty(english)) return 0;
            var n = english.Trim();
            if (!n.StartsWith("Switch to", StringComparison.OrdinalIgnoreCase)) return 0;
            for (int i = 1; i < Channels.Length; i++)
                if (n.IndexOf(Channels[i], StringComparison.OrdinalIgnoreCase) >= 0) return i;
            if (n.IndexOf("Cartoon", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            if (n.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0) return 5;
            return 0;
        }

        /// <summary>The menu entry a queued action runs (its object's TTAB), or null (a global interaction).</summary>
        public static TTABInteraction EntryOf(VMQueuedAction qa)
        {
            var callee = qa?.Callee;
            if (callee?.TreeTable?.InteractionByIndex == null || qa.InteractionNumber < 0) return null;
            TTABInteraction entry;
            return callee.TreeTable.InteractionByIndex.TryGetValue((uint)qa.InteractionNumber, out entry) ? entry : null;
        }

        /// <summary>What they're doing: a new action (a "start") counts double; then every half hour at it counts once.</summary>
        private static void Doing(VMAvatar ava, VMThread thread, int tod)
        {
            var qa = thread.ActiveAction;
            if (qa == null || qa.Callee == null || qa.Mode != VMQueueMode.Normal || qa.Priority <= (short)VMQueuePriority.Idle) return;
            bool start = ava.GetAttribute(LastUidAttr) != unchecked((short)qa.UID);
            if (start) ava.SetAttribute(LastUidAttr, unchecked((short)qa.UID));
            bool half = (tod % 30) == 0;
            if (!start && !half) return;
            int w = start ? 2 : 1;
            var callee = qa.Callee;

            // who with (a person - not a pet, not themselves): their favourite person
            var other = callee as VMAvatar;
            if (other != null && other != ava && !other.IsPet) Friend(ava, other.GetPersonData(VMPersonDataVariable.NeighborId), w);

            var entry = other != null ? null : EntryOf(qa);
            var kind = (other != null) ? (other.IsPet ? VMReduxBrain.Kind.Play : VMReduxBrain.Kind.Social)
                : (entry != null ? VMReduxBrain.KindOf(callee, entry, null) : VMReduxBrain.Kind.Other);
            if (other == ava) kind = VMReduxBrain.Kind.Other;

            // their hobby, and boredom
            int h = HobbyIndex(kind);
            if (h >= 0)
            {
                bool chat = kind == VMReduxBrain.Kind.Social;
                SetHobbyTime(ava, h, HobbyTime(ava, h) + (start ? (chat ? 1 : 2) : 1));
                SetBoredom(ava, h, Boredom(ava, h) + (start ? (chat ? 1 : 3) : (chat ? 1 : 2)));
            }

            if (!start || other != null || entry == null) return;
            // a dish: eating a plate (or serving from a group meal) of it, or cooking it (the fridge's dish choice)
            if (ReduxCooking.IsPlate(callee))
            {
                int dish = callee.GetAttribute(ReduxCooking.DishAttr);
                if (ReduxCooking.Find(dish) != null) BumpPacked(ava, DishIds, DishTopScore, DishAltScore, dish, 3);
            }
            else
            {
                var d = ReduxCooking.DishOf(qa);
                if (d != null && ReduxLeftovers.IsFridge(callee)) BumpPacked(ava, DishIds, DishTopScore, DishAltScore, d.Id, 2);
                // a TV channel they switched to
                int ch = ChannelOf(VMReduxBrain.LotName(callee, entry));
                if (ch > 0) BumpPacked(ava, ChannelIds, ChannelTopScore, ChannelAltScore, ch, 4);
            }
        }

        /// <summary>Bedtime and time up, learned from when they really fall asleep and wake.</summary>
        private static void Sleep(VM vm, VMAvatar ava, int tod, VMClock clock)
        {
            bool asleep = ava.GetMotiveData(VMMotive.SleepState) != 0;
            int s = ava.GetAttribute(SleepAttr) & 0xFFFF;
            bool was = (s & 1) != 0;
            if (asleep == was) return;
            if (asleep)
            {
                s |= 1;
                ava.SetAttribute(SleepStartAttr, (short)(tod + 1));
                if (NightStart(tod))
                {
                    int rel = (tod - 720 + 1440) % 1440; // (minutes after noon: no wrap at midnight)
                    int nights = (s >> 1) & 0xF;
                    int old = ava.GetAttribute(BedtimeAttr) - 1;
                    int now = (nights == 0 || old < 0) ? rel : old + (rel - old) / 4;
                    ava.SetAttribute(BedtimeAttr, (short)(now + 1));
                    s = (s & ~(0xF << 1)) | (Math.Min(15, nights + 1) << 1);
                }
            }
            else
            {
                s &= ~1;
                int began = ava.GetAttribute(SleepStartAttr) - 1;
                ava.SetAttribute(SleepStartAttr, 0);
                if (began >= 0 && NightStart(began) && tod >= 4 * 60 && tod <= 13 * 60)
                {
                    bool off = DayOff(ava, ReduxWeek.Day(clock));
                    int shift = off ? 5 : 9;
                    int n = (s >> shift) & 0xF;
                    int attr = off ? WakeOffAttr : WakeWorkAttr;
                    int old = ava.GetAttribute(attr) - 1;
                    int now = (n == 0 || old < 0) ? tod : old + (tod - old) / 4;
                    ava.SetAttribute(attr, (short)(now + 1));
                    s = (s & ~(0xF << shift)) | (Math.Min(15, n + 1) << shift);
                }
            }
            ava.SetAttribute(SleepAttr, (short)s);
        }

        /// <summary>Sleep that starts 19:00-03:59 is a night's sleep (a bedtime); any other is a nap.</summary>
        public static bool NightStart(int tod) { return tod >= 19 * 60 || tod < 4 * 60; }
    }
}
