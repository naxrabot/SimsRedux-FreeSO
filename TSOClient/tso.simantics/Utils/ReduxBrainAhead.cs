using System;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.Files.Formats.IFF.Chunks;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 18.5 (Part B #9, B5a): the Sim brain's "thinking ahead" and "a family that works together" factors - see
    /// VMReduxBrainFactors and VMReduxBrain's FACTOR API. Each is a multiplier (per mille, 1000 = no change) on one choice of
    /// one Sim with an Adaptive brain, kept moderate (about x0.35-x1.8) so a need in the red still wins.
    ///
    /// Thinking ahead:
    ///   ahead-dayplan   the couple of hours before the carpool (2-3 hours before it - the last hour is the Smartest brain's own
    ///                   "before work" rule in VMFindBestAction, not doubled here) or the school bus (8:00, Monday to Friday):
    ///                   eat, wash and use the bathroom; no naps; less TV and games.
    ///   ahead-bedtime   an early start tomorrow: from an hour before "bedtime" (9 hours before the carpool, 10 before the school
    ///                   bus) bed is more appealing and late fun less. (Which bed is still ReduxBeds; when to go is still FindBed.)
    ///   ahead-predict   a need that isn't low yet but soon will be (hunger or bladder under 40, hygiene, fun, social or comfort
    ///                   under 30): what takes care of it now gets a mild boost.
    ///   ahead-twoneeds  a choice that helps two or more needs that are under 40 at once.
    /// A family that works together:
    ///   family-busy     no rushing to the same shower: an object another Sim is using or heading to (the front of their
    ///                   queue) is less appealing, unless others can join in (a hot tub, a sofa, the TV...) or it's food.
    ///   family-onecook  one cook: if someone is already making a meal, the others don't start one too; with two or more of
    ///                   the family hungry, serving a meal for everyone is more appealing.
    ///   family-broken   something broken (its repair state, as the repairman reads it) is avoided for normal use - the
    ///                   nearest working one wins; repairing it isn't affected.
    ///   family-chores   housework goes to the least busy one of the family (fewest things queued, needs in the best shape).
    ///   family-baby     a baby who's hungry or wants attention: the least busy grown-up of the family goes to them.
    ///
    /// LOCKSTEP: only lot state - the Sims' needs, queues and person data, the lot clock, objects' attributes and GUIDs, and
    /// choices' English names (VMReduxBrain.EnglishName) and motive ads. Nothing is cached between calls and nothing is changed,
    /// so every game on a visit gets the same numbers. No allocations: plain loops over the lot's avatar list.
    /// </summary>
    public static class VMReduxBrainAhead
    {
        public static void Register()
        {
            VMReduxBrain.AddFactorPerMille("ahead-dayplan", DayPlan);
            VMReduxBrain.AddFactorPerMille("ahead-bedtime", Bedtime);
            VMReduxBrain.AddFactorPerMille("ahead-predict", Predict);
            VMReduxBrain.AddFactorPerMille("ahead-twoneeds", TwoNeeds);
            VMReduxBrain.AddFactorPerMille("family-busy", NoRushing);
            VMReduxBrain.AddFactorPerMille("family-onecook", OneCook);
            VMReduxBrain.AddFactorPerMille("family-broken", Broken);
            VMReduxBrain.AddFactorPerMille("family-chores", Chores);
            VMReduxBrain.AddFactorPerMille("family-baby", Baby);
        }

        // ------------------------------------------------------------------ the day's plan (carpool, school bus, bedtime)

        /// <summary>
        /// When the school bus comes (game minutes, 8:00). Sims 1 keeps it in the bus's own script, not in any data a factor
        /// can read, so it's the game's usual time; Monday to Friday (ReduxWeek: no bus at the weekend).
        /// </summary>
        public const int SchoolBusMinute = 8 * 60;

        /// <summary>
        /// Minutes until this Sim's next carpool (an hour before work starts, as ReduxClockJump has it) or school bus - only
        /// one that really comes (a work day for their career, a school day, not been to work today) - or -1. Only the family
        /// living here: the carpool never comes for a guest, and a community lot (Downtown, Vacation...) has no family.
        /// </summary>
        private static int MinutesToLeave(VMReduxBrainContext ctx, out bool school)
        {
            school = false;
            var vm = ctx.VM; var sim = ctx.Sim;
            if (vm == null || !vm.TS1 || sim == null || sim.IsPet || !ReduxComputer.IsHouseholdSim(vm, sim)) return -1;
            int tod = ctx.TimeOfDay;
            int today = ReduxWeek.Day(vm.Context.Clock);
            if (sim.IsChild)
            {
                // (the next bus: today's if it's still to come, else tomorrow's)
                int busDay = (tod < SchoolBusMinute) ? today : (today + 1) % 7;
                if (busDay >= 5) return -1;
                school = true;
                return (SchoolBusMinute - tod + 1440) % 1440;
            }
            int job = sim.GetPersonData(VMPersonDataVariable.JobType);
            if (job <= 0) return -1;
            var lvl = Content.Content.Get().Jobs?.GetJobLevel((short)job, sim.GetPersonData(VMPersonDataVariable.JobPromotionLevel));
            if (lvl == null) return -1;
            int carPool = Math.Max(0, (lvl.StartTime % 24) * 60 - 60);
            int day = (tod < carPool) ? today : (today + 1) % 7;
            if (!ReduxWeek.Works(job, day)) return -1;
            // (today's carpool is still to come but they've already been to work today - a clock jump: none today)
            if (day == today && sim.GetPersonData(VMPersonDataVariable.TS1LastDayAtWork) == vm.Context.Clock.DayOfMonth) return -1;
            return (carPool - tod + 1440) % 1440;
        }

        /// <summary>For hunger, washing or the bathroom (what to do before leaving).</summary>
        private static bool ForBasics(VMReduxBrainContext ctx)
        {
            return ctx.Ad(VMMotive.Hunger) > 0 || ctx.Ad(VMMotive.Hygiene) > 0 || ctx.Ad(VMMotive.Bladder) > 0;
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

        /// <summary>
        /// ahead-dayplan: in the hours before the carpool or school bus, get ready - eat, wash, the bathroom (x1.4, a child
        /// x1.5) - and don't start a nap (x0.6) or a long round of TV or games (x0.75, a child x0.7). Grown-ups: 1-3 hours
        /// before the carpool (the last hour before it, and the hour after, are the Smartest brain's own "before work" rule).
        /// Children: the 2.5 hours before the bus.
        /// </summary>
        private static int DayPlan(VMReduxBrainContext ctx)
        {
            if (!ForBasics(ctx) && !ForSleep(ctx) && !LongFun(ctx)) return 1000; // (cheap way out first)
            bool school;
            int mins = MinutesToLeave(ctx, out school);
            if (mins < 0) return 1000;
            bool getReady = school ? (mins > 0 && mins <= 150) : (mins > 60 && mins <= 180);
            if (!getReady) return 1000;
            if (ForBasics(ctx))
            {
                ctx.Note(school ? "Getting ready for the school bus" : "Getting ready before the carpool");
                return school ? 1500 : 1400;
            }
            if (ForSleep(ctx)) return ctx.Need(VMMotive.Energy) > -40 ? 600 : 1000; // (exhausted: sleep anyway)
            return school ? 700 : 750;
        }

        /// <summary>
        /// ahead-bedtime: an early start tomorrow (a carpool or the school bus, on a day it comes). Bedtime is 9 hours before
        /// the carpool (10 before the bus for a child). From an hour before it bed is a little more appealing (x1.2), from
        /// bedtime for the next 5 hours more so (x1.5) - when energy isn't already high - and TV and games less (x0.8).
        /// </summary>
        private static int Bedtime(VMReduxBrainContext ctx)
        {
            bool sleep = ForSleep(ctx), fun = !sleep && LongFun(ctx);
            if (!sleep && !fun) return 1000;
            bool school;
            int mins = MinutesToLeave(ctx, out school);
            if (mins < 0) return 1000;
            int sleepFor = school ? 600 : 540;
            int sinceBedtime = sleepFor - mins; // (minutes past bedtime; negative = before it)
            if (sinceBedtime < -60 || sinceBedtime >= 300) return 1000;
            if (sleep)
            {
                if (ctx.Need(VMMotive.Energy) >= 80) return 1000;
                ctx.Note("Early start tomorrow - off to bed");
                return sinceBedtime < 0 ? 1200 : 1500;
            }
            return sinceBedtime < 0 ? 1000 : 800;
        }

        // ------------------------------------------------------------------ predicting needs, two needs at once

        // (a need "will be low soon" under these - hunger and the bladder fall fastest, so they're looked after earlier.
        //  Energy is left to bedtime and the time of day; the room to housework.)
        private static readonly VMMotive[] SoonNeeds = { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Hygiene, VMMotive.Fun, VMMotive.Social, VMMotive.Comfort };
        private static readonly int[] SoonBelow = { 40, 40, 30, 30, 30, 30 };
        /// <summary>Under this a need is already low - the brain's own need rules deal with it, not "thinking ahead".</summary>
        private const int AlreadyLow = 5;

        /// <summary>
        /// ahead-predict: act before a need gets low. A choice that serves a need that's still fine but falling towards low
        /// (from its value now: hunger or bladder 5-39, hygiene, fun, social or comfort 5-29) gets a mild boost (x1.2). Not
        /// while a need is in the red - that comes first.
        /// </summary>
        private static int Predict(VMReduxBrainContext ctx)
        {
            if (VMReduxBrain.InRed(ctx.Sim)) return 1000;
            for (int i = 0; i < SoonNeeds.Length; i++)
            {
                var m = SoonNeeds[i];
                int v = ctx.Need(m);
                if (v < AlreadyLow || v >= SoonBelow[i] || ctx.Ad(m) <= 0) continue;
                ctx.Note("Seeing to it before it gets low");
                return 1200;
            }
            return 1000;
        }

        /// <summary>A need counts as "wanted" for two-at-once under this.</summary>
        private const int WantedBelow = 40;
        /// <summary>A choice has to give at least this much for a need to count.</summary>
        private const int MeaningfulAd = 5;

        /// <summary>
        /// ahead-twoneeds: a choice that helps two of the needs the Sim wants now (under 40) is more appealing (x1.25), three
        /// or more x1.45 - a family dinner (hunger and company), the hot tub with a friend (fun, comfort, company).
        /// </summary>
        private static int TwoNeeds(VMReduxBrainContext ctx)
        {
            int n = 0;
            var needs = VMReduxBrain.Needs;
            for (int i = 0; i < needs.Length; i++)
                if (ctx.Need(needs[i]) < WantedBelow && ctx.Ad(needs[i]) >= MeaningfulAd) n++;
            if (n < 2) return 1000;
            ctx.Note("Takes care of more than one need at once");
            return n >= 3 ? 1450 : 1250;
        }

        // ------------------------------------------------------------------ the family: no rushing to the same object

        /// <summary>Can others join in this choice (the TTAB has an entry joining it - a hot tub, a sofa, watching TV)?</summary>
        private static bool Joinable(VMEntity target, TTABInteraction entry)
        {
            var ias = target?.TreeTable?.Interactions;
            if (ias == null || entry == null) return false;
            for (int i = 0; i < ias.Length; i++) if (ias[i].JoiningIndex == entry.TTAIndex) return true;
            return false;
        }

        /// <summary>Food (a plate, a group meal, a pizza box) - shared, never "taken".</summary>
        private static bool IsFood(VMEntity e)
        {
            var label = e?.Object?.OBJ?.ChunkLabel;
            return label != null && label.StartsWith("Food -", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The object (its interaction group's leader, as VMFindBestAction scores it) at the front of a Sim's queue, or null.</summary>
        private static VMEntity FrontObject(VMAvatar a)
        {
            var q = a?.Thread?.Queue;
            if (q == null || q.Count == 0) return null;
            var callee = q[0].Callee;
            if (callee == null || callee is VMAvatar) return null;
            return callee.MultitileGroup?.GetInteractionGroupLeader(callee) ?? callee;
        }

        private static bool Here(VMAvatar a)
        {
            return a != null && a.Position != LotTilePos.OUT_OF_WORLD && a.Container == null;
        }

        /// <summary>
        /// family-busy: another Sim is using this object or on their way to it (the front of their queue) - a shower, a
        /// toilet, the computer: it's less appealing (x0.55; the bathroom x0.4), so a free one nearby wins. Not for choices
        /// others can join, not for food (shared), not for Sims (socials).
        /// </summary>
        private static int NoRushing(VMReduxBrainContext ctx)
        {
            var target = ctx.Target as VMGameObject;
            if (target == null || ctx.VM == null || IsFood(target) || Joinable(target, ctx.Entry)) return 1000;
            var avatars = ctx.VM.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || a == ctx.Sim || !Here(a)) continue;
                if (FrontObject(a) != target) continue;
                if (ctx.Kind == VMReduxBrain.Kind.Bathroom)
                {
                    ctx.Note("The bathroom's taken - using another one");
                    return 400;
                }
                ctx.Note("Someone's already using that");
                return 550;
            }
            return 1000;
        }

        // ------------------------------------------------------------------ the family: one cook

        // (the fridge's cooking choices by their English names - "Have A Meal", "Have Lite Meal", "Serve Meal" and the
        //  cooking menu's breakfast / lunch / dinner / quick ones - and anything that starts "Cook". Snacks aren't cooking.)
        private static readonly string[] CookStarts = { "have a meal", "have lite meal", "serve meal", "have breakfast", "have lunch", "have dinner", "have quick", "serve breakfast", "serve lunch", "serve dinner", "cook" };

        /// <summary>0 not cooking, 1 a meal for one, 2 a meal for everyone (Serve ...).</summary>
        private static int CookKind(VMEntity target, string english)
        {
            if (string.IsNullOrEmpty(english) || target == null || IsFood(target)) return 0;
            var n = english.TrimStart();
            for (int i = 0; i < CookStarts.Length; i++)
                if (n.StartsWith(CookStarts[i], StringComparison.OrdinalIgnoreCase))
                    return n.StartsWith("serve", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            return 0;
        }

        /// <summary>18.8: a choice that's cooking (a meal for one or for everyone - not a snack), by its English name.</summary>
        public static bool CookingChoice(VMEntity target, string english) { return CookKind(target, english) != 0; }

        // ------------------------------------------------------------------ 18.8: a delivery on its way

        /// <summary>Sims 1's own scheduler object ("Schedule" in Global.iff makes one per visit it books - attribute 0 is who's coming).</summary>
        public const uint SchedulerGUID = 0x8535DBF8;
        /// <summary>The NPC Pizza Dude (PeopleObjects/PizzaDude.iff).</summary>
        public const uint PizzaDudeGUID = 0xFC72C0C0;

        /// <summary>
        /// Food the family ordered is on its way: the phone's "Call for pizza" booked the Pizza Dude (a Scheduler whose
        /// attribute 0 is him - the same thing the phone itself checks, "Check Schedule", before letting you order another), or
        /// he's here on the lot with it. (An expansion's delivery person is recognised by name: an NPC called Pizza or Delivery.) Sims 1
        /// has no grocery delivery. Lot state only: the objects, their attributes and their game-file labels.
        /// </summary>
        public static bool FoodOnTheWay(VM vm)
        {
            if (vm == null || !vm.TS1) return false;
            var q = vm.Context.ObjectQueries;
            var sched = q.GetObjectsByGUID(SchedulerGUID);
            if (sched != null)
            {
                for (int i = 0; i < sched.Count; i++)
                {
                    var s = sched[i];
                    if (s == null || s.Dead) continue;
                    if (IsDelivery(vm.GetObjectById(s.GetAttribute(0)))) return true;
                }
            }
            var dudes = q.GetObjectsByGUID(PizzaDudeGUID);
            if (dudes != null)
                for (int i = 0; i < dudes.Count; i++)
                {
                    var d = dudes[i];
                    if (d is VMAvatar && !d.Dead && d.Position != LotTilePos.OUT_OF_WORLD) return true;
                }
            return false;
        }

        private static bool IsDelivery(VMEntity who)
        {
            var obj = who?.Object?.OBJ;
            if (obj == null) return false;
            if (obj.GUID == PizzaDudeGUID) return true;
            var label = obj.ChunkLabel ?? "";
            return label.IndexOf("NPC", StringComparison.OrdinalIgnoreCase) >= 0
                && (label.IndexOf("Pizza", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Deliver", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>What the Sim at the front of their queue is cooking (CookKind), from the queued choice's English name.</summary>
        private static int CookingNow(VMAvatar a)
        {
            var q = a?.Thread?.Queue;
            if (q == null || q.Count == 0) return 0;
            var qa = q[0];
            var callee = qa.Callee;
            if (callee == null || callee is VMAvatar || qa.InteractionNumber < 0 || callee.TreeTable?.InteractionByIndex == null) return 0;
            TTABInteraction entry;
            if (!callee.TreeTable.InteractionByIndex.TryGetValue((uint)qa.InteractionNumber, out entry)) return 0;
            return CookKind(callee, VMReduxBrain.EnglishName(callee, entry));
        }

        /// <summary>Hunger under this counts as hungry for "a meal for everyone".</summary>
        private const int HungryBelow = 30;

        /// <summary>
        /// family-onecook: someone else is already cooking - a meal for everyone: don't start another (x0.35), wait for it;
        /// a meal for one: less appealing (x0.6). Nobody cooking and two or more of the family here hungry: serving a meal
        /// for everyone is more appealing (x1.3). Not while a need is in the red.
        /// </summary>
        private static int OneCook(VMReduxBrainContext ctx)
        {
            int mine = CookKind(ctx.Target, ctx.Name);
            if (mine == 0 || ctx.VM == null) return 1000;
            // (18.8: a pizza's on its way - nobody cooks; VMFindBestAction leaves cooking out altogether then, this is the "why")
            if (FoodOnTheWay(ctx.VM)) { ctx.Note("Pizza's on its way"); return 100; }
            if (VMReduxBrain.InRed(ctx.Sim)) return 1000;
            var avatars = ctx.VM.Context.ObjectQueries.Avatars;
            int others = 0, hungry = 0;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || a.IsPet || !Here(a)) continue;
                if (a != ctx.Sim) others = Math.Max(others, CookingNow(a));
                if (ReduxComputer.IsHouseholdSim(ctx.VM, a) && a.GetMotiveData(VMMotive.Hunger) < HungryBelow) hungry++;
            }
            if (others == 2) { ctx.Note("Someone's already making a meal for everyone"); return 350; }
            if (others == 1) return 600;
            if (mine == 2 && hungry >= 2 && ReduxComputer.IsHouseholdSim(ctx.VM, ctx.Sim)) { ctx.Note("Making a meal for everyone"); return 1300; }
            return 1000;
        }

        // ------------------------------------------------------------------ the family: what's broken

        /// <summary>Broken: any part waiting for a repair (repair state above 0 - what the repairman looks for).</summary>
        private static bool IsBroken(VMEntity target)
        {
            var parts = target?.MultitileGroup?.Objects;
            if (parts == null) return false;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].GetValue(VMStackObjectVariable.RepairState) > 0) return true;
            return false;
        }

        /// <summary>
        /// family-broken: everyone knows it's broken - using it the normal way is less appealing (x0.4), so the nearest
        /// working one wins. Repairing (or cleaning) it isn't affected, nor is anything while a need is in the red (a
        /// broken toilet beats an accident).
        /// </summary>
        private static int Broken(VMReduxBrainContext ctx)
        {
            if (!(ctx.Target is VMGameObject) || ctx.Kind == VMReduxBrain.Kind.Chores) return 1000;
            if (ctx.Name != null && ctx.Name.TrimStart().StartsWith("repair", StringComparison.OrdinalIgnoreCase)) return 1000;
            if (!IsBroken(ctx.Target) || VMReduxBrain.InRed(ctx.Sim)) return 1000;
            ctx.Note("That one's broken - using another");
            return 400;
        }

        // ------------------------------------------------------------------ the family: chores and the baby

        /// <summary>
        /// How busy a Sim is: 25 for each thing the player has queued, 10 for each thing of their own, and half of how far
        /// each physical need is under 30. (Lot state only.)
        /// </summary>
        private static int Busy(VMAvatar a)
        {
            int load = 0;
            var q = a.Thread?.Queue;
            if (q != null)
                for (int i = 0; i < q.Count; i++)
                {
                    var qa = q[i];
                    if (qa.Mode != VMQueueMode.Normal || qa.Priority <= (short)VMQueuePriority.Idle) continue;
                    load += qa.Priority >= (short)VMQueuePriority.UserDriven ? 25 : 10;
                }
            load += Lack(a, VMMotive.Hunger) + Lack(a, VMMotive.Bladder) + Lack(a, VMMotive.Energy) + Lack(a, VMMotive.Hygiene);
            return load;
        }

        private static int Lack(VMAvatar a, VMMotive m)
        {
            int v = a.GetMotiveData(m);
            return v < 30 ? (30 - v) / 2 : 0;
        }

        /// <summary>
        /// Is this Sim the least busy of the family here and awake (grown-ups only if asked)? Ties go to the lowest object id,
        /// so exactly one Sim is "the least busy". gap = how much busier they are than the least busy one (0 if they are it).
        /// </summary>
        private static bool LeastBusy(VM vm, VMAvatar sim, bool grownUpsOnly, out int gap)
        {
            gap = 0;
            int mine = Busy(sim), best = int.MaxValue;
            bool first = true;
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || a == sim || a.IsPet || !Here(a) || a.GetMotiveData(VMMotive.SleepState) != 0) continue;
                if (grownUpsOnly && a.IsChild) continue;
                if (a.GetPersonData(VMPersonDataVariable.IsGhost) != 0 || !ReduxComputer.IsHouseholdSim(vm, a)) continue;
                int b = Busy(a);
                if (b < best) best = b;
                if (b < mine || (b == mine && a.ObjectID < sim.ObjectID)) first = false;
            }
            if (!first) gap = mine - Math.Min(best, mine);
            return first;
        }

        /// <summary>A family Sim who could help: one of the household, here, awake.</summary>
        private static bool CanHelp(VMReduxBrainContext ctx)
        {
            var sim = ctx.Sim;
            return ctx.VM != null && ctx.VM.TS1 && sim != null && !sim.IsPet && ReduxComputer.IsHouseholdSim(ctx.VM, sim)
                && sim.GetMotiveData(VMMotive.SleepState) == 0;
        }

        /// <summary>
        /// family-chores: the least busy one of the family takes the housework (x1.25); someone clearly busier (25 or more
        /// on Busy - a player's order, or a need getting low) leaves it to them (x0.75).
        /// </summary>
        private static int Chores(VMReduxBrainContext ctx)
        {
            if (ctx.Kind != VMReduxBrain.Kind.Chores || !CanHelp(ctx)) return 1000;
            int gap;
            if (LeastBusy(ctx.VM, ctx.Sim, false, out gap)) { ctx.Note("I'm the least busy - doing the chores"); return 1250; }
            return gap >= 25 ? 750 : 1000;
        }

        /// <summary>The game's baby (Baby.iff) - the bassinet part that keeps its state.</summary>
        private const uint BabyGUID = ReduxWooHoo.BabyGUID;
        // (the baby's own attributes, Baby.iff "process": 4 = how full, 0-4 (BCON 0x1001 #3); 3 = attention, 0-2 (#1) - Feed
        //  and Play / Sing raise them, every hour takes one off; 37 = 1 while the baby is being taken away)
        private const int BabyFullAttr = 4, BabyAttentionAttr = 3, BabyTakenAttr = 37;

        /// <summary>The baby in this object's group, or null.</summary>
        private static VMEntity BabyOf(VMEntity target)
        {
            if (target == null || target is VMAvatar) return null;
            var parts = target.MultitileGroup?.Objects;
            if (parts == null) return target.Object?.OBJ?.GUID == BabyGUID ? target : null;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].Object?.OBJ?.GUID == BabyGUID) return parts[i];
            return null;
        }

        /// <summary>
        /// family-baby: the baby is hungry (1 or less of 4) or wants attention (0 of 2): the least busy grown-up of the
        /// family who's awake goes to them (x1.8); the others leave it to them a little (x0.9).
        /// </summary>
        private static int Baby(VMReduxBrainContext ctx)
        {
            var baby = BabyOf(ctx.Target);
            if (baby == null || ctx.Sim == null || ctx.Sim.IsChild || !CanHelp(ctx)) return 1000;
            if (baby.GetAttribute(BabyTakenAttr) == 1) return 1000;
            // (18.5 review: only the choice that answers what the baby needs - Feed when it's hungry; Play, Sing or Rock when its
            //  other need (attribute 3 - the baby's own "View Stats" calls it energy) is low - not every baby choice at once)
            bool hungry = baby.GetAttribute(BabyFullAttr) <= 1, low = baby.GetAttribute(BabyAttentionAttr) <= 0;
            if (!hungry && !low) return 1000;
            var n = ctx.Name;
            if (n == null) return 1000; // (can't tell which choice it is - leave it be)
            bool feed = n.IndexOf("Feed", StringComparison.OrdinalIgnoreCase) >= 0;
            bool soothe = n.IndexOf("Play", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Sing", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Rock", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Hold", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!((hungry && feed) || (low && soothe))) return 1000;
            int gap;
            if (LeastBusy(ctx.VM, ctx.Sim, true, out gap)) { ctx.Note("The baby needs someone - I'll go"); return 1800; }
            return 900;
        }
    }
}
