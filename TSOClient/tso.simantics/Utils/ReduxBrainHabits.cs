using System;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.5 (Part B #9, B5b): habits and personality - favourites, variety, finishing what they start, natural sleep.
    /// Brain factors (see VMReduxBrainFactors and VMReduxBrain's FACTOR API), each a multiplier per mille (1000 = no change)
    /// on one choice of one Sim with an Adaptive brain, kept mild (x0.55-x1.4) so a need in the red still wins. What they read
    /// is learned by VMReduxHabitMemory (person attributes 140-169) from what the Sim actually does - so two Sims of the same
    /// family end up behaving differently.
    ///
    /// Favourites:
    ///   habit-seat      their favourite seat (the kind of chair or sofa they sit in most): sitting there x1.3.
    ///   habit-dish      their favourite dish (cooking menus): a plate or group meal of it x1.35.
    ///   habit-channel   their favourite TV channel: switching to it x1.4, to another channel x0.85.
    ///   habit-hobby     their favourite kind of fun (TV, games, chatting, skills, working out): x1.2.
    ///   habit-friend    their favourite person: a friendly social with them x1.25 (never romance, never mean ones).
    /// Variety:
    ///   habit-variety   a kind of fun they've done a lot of lately: x0.7 (bored), x0.55 (really bored) - unless a need it
    ///                   helps is low; while bored of one, other kinds of fun x1.1 ("Bored of TV - something else").
    /// Natural sleep:
    ///   habit-bedtime   their usual bedtime (learned from when they really go to sleep; an hour later on a night before a
    ///                   day off): from 30 minutes before it bed x1.25, for 4 hours after it x1.4 (energy under 75), and long
    ///                   fun after it x0.85. Not while ahead-bedtime's "early start tomorrow" window is on (that one decides).
    ///                   FindBed (VMFindBestAction) also counts a Sim with energy under 50 at their usual bedtime as tired.
    ///   habit-nap       a tiring day (been to work today, or school on a weekday), 14:00-19:59, energy -55..20, bedtime
    ///                   still 2+ hours off: a nap x1.4.
    ///   habit-sleepin   a day off, 6:00-10:59, energy under 60: back to bed for a lie-in x1.3.
    /// Finish what they start (FinishFirst, called by VMFindBestAction - not a factor: a factor can only reweigh the choices,
    /// not keep the Sim at what they're doing):
    ///   an Adaptive Sim's own action that's nearly done (every need it's for at 65 or more, one still under 95) isn't dropped
    ///   for something minor - free will finds "nothing better" for up to 45 game minutes - unless a need is in the red, there's
    ///   a fire, or the baby needs someone. The player's orders are never touched: they're never interrupted by free will (their
    ///   priority, VMThread.EvaluateQueuePriorities) and free will waits while one is queued (VMFindBestAction).
    ///
    /// The player's style (hands-on / hands-off) is already learned per Sim by 17.4's brain (ReduxSimBrain's night review:
    /// orders a day vs own choices a day -> VMReduxBrain.Style, attribute 120) and used by VMReduxBrain.IdleWait - not doubled.
    ///
    /// LOCKSTEP: only lot state - the Sim's attributes (VMReduxHabitMemory), needs, person data and queue, the lot clock,
    /// objects' GUIDs and attributes, choices' English names. No allocations in the factors.
    /// </summary>
    public static class VMReduxBrainHabits
    {
        public static void Register()
        {
            VMReduxBrain.AddFactorPerMille("habit-seat", Seat);
            VMReduxBrain.AddFactorPerMille("habit-dish", Dish);
            VMReduxBrain.AddFactorPerMille("habit-channel", Channel);
            VMReduxBrain.AddFactorPerMille("habit-hobby", Hobby);
            VMReduxBrain.AddFactorPerMille("habit-friend", Friend);
            VMReduxBrain.AddFactorPerMille("habit-variety", Variety);
            VMReduxBrain.AddFactorPerMille("habit-bedtime", Bedtime);
            VMReduxBrain.AddFactorPerMille("habit-nap", Nap);
            VMReduxBrain.AddFactorPerMille("habit-sleepin", SleepIn);
        }

        // ------------------------------------------------------------------ what a choice is

        /// <summary>For hunger, washing, the bathroom or sleep (a basic need).</summary>
        private static bool ForBasics(VMReduxBrainContext ctx)
        {
            return ctx.Ad(VMMotive.Hunger) > 0 || ctx.Ad(VMMotive.Hygiene) > 0 || ctx.Ad(VMMotive.Bladder) > 0 || ctx.Ad(VMMotive.Energy) > 0;
        }

        /// <summary>Sleep or a nap (rests energy, and it's a rest kind of choice - not a coffee).</summary>
        private static bool ForSleep(VMReduxBrainContext ctx)
        {
            return ctx.Kind == VMReduxBrain.Kind.Rest && ctx.Ad(VMMotive.Energy) > 0;
        }

        /// <summary>TV, the computer, games, toys - fun that takes a while and does nothing for the basics.</summary>
        private static bool LongFun(VMReduxBrainContext ctx)
        {
            return (ctx.Kind == VMReduxBrain.Kind.Screen || ctx.Kind == VMReduxBrain.Kind.Play) && !(ctx.Target is VMAvatar) && !ForBasics(ctx);
        }

        /// <summary>Does the choice help a need that's already low (under VMReduxBrain.LowBelow)?</summary>
        private static bool ForLowNeed(VMReduxBrainContext ctx)
        {
            var needs = VMReduxBrain.Needs;
            for (int i = 0; i < needs.Length; i++)
                if (ctx.Ad(needs[i]) > 0 && ctx.Need(needs[i]) < VMReduxBrain.LowBelow) return true;
            return false;
        }

        // (by English name: never boost these towards a favourite person - children never in romance, and no favourite enemies)
        private static readonly string[] RomanceWords = { "kiss", "flirt", "woohoo", "romanc", "cuddle", "propose", "make out", "serenade", "slow dance", "back rub", "massage", "love", "rose", "hold hands", "date" };
        private static readonly string[] MeanWords = { "insult", "fight", "slap", "attack", "tease", "taunt", "poke", "annoy", "shove", "argue", "yell", "nag", "scare" };

        private static bool HasWord(string n, string[] words)
        {
            for (int i = 0; i < words.Length; i++) if (n.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ------------------------------------------------------------------ favourites

        /// <summary>habit-seat: their favourite seat (by its kind - the object's GUID, so it's "that armchair" wherever they are): x1.3.</summary>
        private static int Seat(VMReduxBrainContext ctx)
        {
            if (!(ctx.Target is VMGameObject) || (ctx.Kind != VMReduxBrain.Kind.Rest && ctx.Ad(VMMotive.Comfort) <= 0)) return 1000;
            uint fav = VMReduxHabitMemory.FavouriteSeat(ctx.Sim);
            if (fav == 0 || ctx.Target.Object?.OBJ?.GUID != fav) return 1000;
            ctx.Note("My favourite chair");
            return 1300;
        }

        /// <summary>habit-dish: a plate (or group meal) of their favourite dish: x1.35.</summary>
        private static int Dish(VMReduxBrainContext ctx)
        {
            if (!ReduxCooking.IsPlate(ctx.Target) || ctx.Ad(VMMotive.Hunger) <= 0) return 1000;
            int fav = VMReduxHabitMemory.FavouriteDish(ctx.Sim);
            if (fav == 0 || ctx.Target.GetAttribute(ReduxCooking.DishAttr) != fav) return 1000;
            ctx.Note("My favourite dish");
            return 1350;
        }

        /// <summary>habit-channel: switching the TV to their favourite channel x1.4, to another one x0.85.</summary>
        private static int Channel(VMReduxBrainContext ctx)
        {
            int ch = VMReduxHabitMemory.ChannelOf(ctx.Name);
            if (ch == 0) return 1000;
            int fav = VMReduxHabitMemory.FavouriteChannel(ctx.Sim);
            if (fav == 0) return 1000;
            if (ch != fav) return 850;
            ctx.Note("My favourite channel");
            return 1400;
        }

        /// <summary>habit-hobby: their favourite kind of fun (not for a basic need): x1.2.</summary>
        private static int Hobby(VMReduxBrainContext ctx)
        {
            int h = VMReduxHabitMemory.HobbyIndex(ctx.Kind);
            if (h < 0 || ForBasics(ctx)) return 1000;
            if (h != VMReduxHabitMemory.FavouriteHobby(ctx.Sim)) return 1000;
            if (ctx.Target is VMAvatar && (ctx.Name == null || HasWord(ctx.Name, RomanceWords) || HasWord(ctx.Name, MeanWords))) return 1000;
            ctx.Note("One of my favourite things to do");
            return 1200;
        }

        /// <summary>habit-friend: a friendly social with their favourite person: x1.25 (never romance, never a mean one).</summary>
        private static int Friend(VMReduxBrainContext ctx)
        {
            var other = ctx.Target as VMAvatar;
            if (other == null || other == ctx.Sim || other.IsPet || ctx.Name == null) return 1000;
            short fav = VMReduxHabitMemory.FavouriteFriend(ctx.Sim);
            if (fav <= 0 || other.GetPersonData(VMPersonDataVariable.NeighborId) != fav) return 1000;
            if (HasWord(ctx.Name, RomanceWords) || HasWord(ctx.Name, MeanWords)) return 1000;
            ctx.Note("My favourite person to spend time with");
            return 1250;
        }

        // ------------------------------------------------------------------ variety

        private static readonly string[] BoredNotes =
        {
            "Bored of TV - something else", "Bored of games - something else", "Bored of chatting - something else",
            "Bored of studying - something else", "Bored of working out - something else"
        };

        /// <summary>
        /// habit-variety: a kind of fun they've done a lot of lately is less appealing (bored x0.7, really bored x0.55) - not
        /// when it helps a need that's low. While they're bored of one kind, the other kinds of fun x1.1.
        /// </summary>
        private static int Variety(VMReduxBrainContext ctx)
        {
            int h = VMReduxHabitMemory.HobbyIndex(ctx.Kind);
            if (h < 0 || ForLowNeed(ctx)) return 1000;
            int b = VMReduxHabitMemory.Boredom(ctx.Sim, h);
            if (b >= VMReduxHabitMemory.VeryBored) return 550;
            if (b >= VMReduxHabitMemory.Bored) return 700;
            int level;
            int m = VMReduxHabitMemory.MostBored(ctx.Sim, out level);
            if (m < 0 || m == h) return 1000;
            ctx.Note(BoredNotes[m]);
            return 1100;
        }

        // ------------------------------------------------------------------ natural sleep

        /// <summary>Is the morning after tonight (or this morning, before noon) a day off for this Sim?</summary>
        private static bool NextMorningOff(VM vm, VMAvatar sim, int tod)
        {
            int today = ReduxWeek.Day(vm.Context.Clock);
            return VMReduxHabitMemory.DayOff(sim, tod >= 720 ? (today + 1) % 7 : today);
        }

        /// <summary>Their usual bedtime tonight (an hour later before a day off), or -1.</summary>
        private static int BedtimeTonight(VM vm, VMAvatar sim, int tod)
        {
            int bt = VMReduxHabitMemory.UsualBedtime(sim);
            if (bt < 0) return -1;
            if (NextMorningOff(vm, sim, tod)) bt = (bt + 60) % 1440;
            return bt;
        }

        /// <summary>Minutes since tonight's usual bedtime, -30..-1 just before it; int.MinValue outside -30..1409.</summary>
        private static int SinceBedtime(int tod, int bt)
        {
            int since = (tod - bt + 1440) % 1440;
            if (since >= 1440 - 30) since -= 1440;
            return since;
        }

        /// <summary>
        /// habit-bedtime: around their usual bedtime - from 30 minutes before it bed x1.25, for 4 hours after it x1.4 (energy
        /// under 75), long fun after it x0.85. Leaves it to ahead-bedtime while an early start tomorrow has its own bedtime.
        /// </summary>
        private static int Bedtime(VMReduxBrainContext ctx)
        {
            bool sleep = ForSleep(ctx), fun = !sleep && LongFun(ctx);
            if (!sleep && !fun) return 1000;
            int tod = ctx.TimeOfDay;
            int bt = BedtimeTonight(ctx.VM, ctx.Sim, tod);
            if (bt < 0) return 1000;
            int since = SinceBedtime(tod, bt);
            if (since < -30 || since >= 240) return 1000;
            if (VMReduxBrainAhead.EarlyBedtimeWindow(ctx)) return 1000;
            if (sleep)
            {
                if (ctx.Need(VMMotive.Energy) >= 75) return 1000;
                ctx.Note("Usual bedtime");
                return since < 0 ? 1250 : 1400;
            }
            return since < 0 ? 1000 : 850;
        }

        /// <summary>
        /// For FindBed (VMFindBestAction - Adaptive only): their usual bedtime (an hour later before a day off) is here, up to 4
        /// hours ago, and energy is under 50 - they count as tired, so it's off to bed instead of one more round of fun.
        /// </summary>
        public static bool PastUsualBedtime(VM vm, VMAvatar sim, int energy)
        {
            if (vm == null || sim == null || sim.IsPet || energy >= 50) return false;
            var clock = vm.Context.Clock;
            int tod = clock.Hours * 60 + clock.Minutes;
            int bt = BedtimeTonight(vm, sim, tod);
            if (bt < 0) return false;
            int since = SinceBedtime(tod, bt);
            return since >= 0 && since < 240;
        }

        /// <summary>
        /// habit-nap: after a tiring day - been to work today (grown-ups), or school (children, weekdays after 15:00) - in the
        /// afternoon or early evening (14:00-19:59), energy -55..20 and bedtime (usual, else 22:00) 2+ hours off: a nap x1.4.
        /// (Under -55 FindBed sends them to bed anyway.)
        /// </summary>
        private static int Nap(VMReduxBrainContext ctx)
        {
            if (!ForSleep(ctx)) return 1000;
            int tod = ctx.TimeOfDay;
            if (tod < 14 * 60 || tod >= 20 * 60) return 1000;
            int energy = ctx.Need(VMMotive.Energy);
            if (energy > 20 || energy < -55) return 1000;
            var sim = ctx.Sim; var clock = ctx.VM.Context.Clock;
            bool tiring;
            if (sim.IsChild) tiring = tod >= 15 * 60 && ReduxWeek.Day(clock) < 5;
            else tiring = sim.GetPersonData(VMPersonDataVariable.JobType) > 0 && sim.GetPersonData(VMPersonDataVariable.TS1LastDayAtWork) == clock.DayOfMonth;
            if (!tiring) return 1000;
            int bt = BedtimeTonight(ctx.VM, sim, tod);
            if (bt < 0) bt = 22 * 60;
            if (((bt - tod + 1440) % 1440) < 120) return 1000;
            ctx.Note("Tiring day - a quick nap");
            return 1400;
        }

        /// <summary>habit-sleepin: a day off, 6:00-10:59, energy under 60: back to bed for a lie-in x1.3.</summary>
        private static int SleepIn(VMReduxBrainContext ctx)
        {
            if (!ForSleep(ctx)) return 1000;
            int tod = ctx.TimeOfDay;
            if (tod < 6 * 60 || tod >= 11 * 60 || ctx.Need(VMMotive.Energy) >= 60) return 1000;
            if (!VMReduxHabitMemory.DayOff(ctx.Sim, ReduxWeek.Day(ctx.VM.Context.Clock))) return 1000;
            ctx.Note("Day off - sleeping in");
            return 1300;
        }

        // ------------------------------------------------------------------ finish what they start (VMFindBestAction)

        /// <summary>Every need the action is for at this or more...</summary>
        public const int NearlyDoneAt = 65;
        /// <summary>...and at least one still under this (all of them there: it's done, free will as usual).</summary>
        public const int DoneAt = 95;
        /// <summary>The longest one action is held on to (game minutes) - an object that can't fill a need any further never traps a Sim.</summary>
        public const int HoldMinutes = 45;

        /// <summary>The baby (Baby.iff) is hungry or wants attention (as family-baby reads it).</summary>
        private static bool BabyNeedsSomeone(VM vm)
        {
            var babies = vm.Context.ObjectQueries.GetObjectsByGUID(ReduxWooHoo.BabyGUID);
            if (babies == null) return false;
            for (int i = 0; i < babies.Count; i++)
            {
                var b = babies[i];
                if (b == null || b.Dead || b.GetAttribute(37) == 1) continue; // (37: being taken away)
                if (b.GetAttribute(4) <= 1 || b.GetAttribute(3) <= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Finish what they start: called by VMFindBestAction (free will looking for something better) - true = "nothing
        /// better, keep at it". Only an Adaptive Sim of the household or a visiting player's own Sim, only their OWN action
        /// (never the player's: free will can't interrupt those anyway), only when it's nearly done (every need it's for at 65+,
        /// one still under 95), at most 45 game minutes per action, and never with a need in the red, a fire on the lot (the
        /// caller passes it) or the baby needing someone. Remembers the hold in the Sim's attributes (VMReduxHabitMemory) - lot
        /// state, so every game on a visit holds the same action for the same minutes.
        /// </summary>
        public static bool FinishFirst(VM vm, VMAvatar sim, int level, bool fireOnLot)
        {
            if (level < 3 || fireOnLot || vm == null || !vm.TS1 || sim == null || sim.IsPet) return false;
            if (!ReduxComputer.IsHouseholdSim(vm, sim) && !VMReduxBrain.VisitingSim(vm, sim)) return false;
            var qa = sim.Thread?.ActiveAction;
            if (qa == null || qa.Callee == null || qa.Mode != VMQueueMode.Normal) return false;
            if (qa.Priority >= (short)VMQueuePriority.UserDriven || qa.Priority <= (short)VMQueuePriority.Idle) return false;
            if (VMReduxBrain.InRed(sim)) return false;
            if (!sim.IsChild && BabyNeedsSomeone(vm)) return false;
            var entry = VMReduxHabitMemory.EntryOf(qa);
            if (entry == null) return false;
            // nearly done?
            int n = 0; bool stillGoing = false;
            var needs = VMReduxBrain.Needs;
            for (int i = 0; i < needs.Length; i++)
            {
                if (VMReduxBrain.AdOf(entry, null, needs[i]) <= 0) continue;
                int v = sim.GetMotiveData(needs[i]);
                if (v < NearlyDoneAt) return false;
                if (v < DoneAt) stillGoing = true;
                n++;
            }
            if (n == 0 || !stillGoing) return false;
            // how long it's been held
            var clock = vm.Context.Clock;
            int tod = clock.Hours * 60 + clock.Minutes;
            if (sim.GetAttribute(VMReduxHabitMemory.HoldAttr) != unchecked((short)qa.UID) || VMReduxHabitMemory.HoldStart(sim) < 0)
            {
                VMReduxHabitMemory.SetHold(sim, qa.UID, tod);
                VMReduxBrain.SetWhy(sim, qa.Callee, qa.InteractionNumber, "Nearly done - finishing this first"); // (display only)
                return true;
            }
            int held = (tod - VMReduxHabitMemory.HoldStart(sim) + 1440) % 1440;
            return held < HoldMinutes;
        }
    }
}
