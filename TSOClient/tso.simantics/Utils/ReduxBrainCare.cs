using System;
using System.Collections.Generic;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Model.Commands;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.5 (Part B #9, B5b): caring for others - parenting, pet care, keeping friends, being a good host. Two parts:
    ///
    /// FACTORS (see VMReduxBrainFactors and VMReduxBrain's FACTOR API) - multipliers on choices the Sim already weighs, kept
    /// moderate (about x0.85 - x2.2) so a need in the red still wins:
    ///   care-comfort  a grown-up of the family and a child of the family who's upset (mood under -25, or very lonely / bored):
    ///                 "Cheer Up...", a friendly hug x1.7 (x2 when the child is very upset), tickling, a joke, a game x1.3.
    ///   care-friends  a friend whose friendship is fading (lifetime 50+, today's 10+ under it and under 70, not an enemy) is
    ///                 here: friendly socials with them x1.4 - only while the Sim isn't lonely (goal-friends is lonely Sims').
    ///   care-host     guests on the lot (non-family Sims who aren't service people or pets - Sims 1 visitors, party guests, a
    ///                 visiting player's Sim): greeting one who's just arrived x1.8; serving a meal for everyone while guests are
    ///                 here and someone's peckish x1.5 (7:00-21:59, no pizza on its way); tidying x1.3 while welcomed guests are
    ///                 here, x1.25 when a visit is booked (Sims 1's own Scheduler holding a person who isn't a service NPC); a
    ///                 long round of TV or games on their own x0.85 while welcomed guests are here.
    ///   care-petfeed  filling a dog or cat bowl ("Feed"/"Fill" on a pet bowl - VMReduxChores.ChoreOf Pets; fish tanks aside)
    ///                 while one of the family's pets here is hungry: x1.5 (under 30, or under 50 for an Animal Lover), x2.2 when
    ///                 the pet is in the red (under -40).
    ///   care-petplay  playing with one of the family's pets ("Play With...", "Pet", "Snuggle", "Give..." a toy or treat) whose
    ///                 fun or company is low: x1.4 (same thresholds), x1.9 in the red; with a dog, fetch and tag x1.15 more
    ///                 (Sims 1 Unleashed has no "walk the dog" - a game of fetch outside is the nearest thing).
    ///
    /// ESSENTIALS (FindCare - chosen outright from VMFindBestAction, like ReduxBrain.FindChore, because the Sims 1 choices they
    /// need advertise nothing, so free will never weighs them: the bookcase's "Study for School", the visitor socials "Greet"
    /// and "Say Goodbye...", and pet bowls whose ads we can't count on). Only for the household's Sims with an Adaptive brain,
    /// nothing urgent, free will Normal or High (Needs only: just the pet in the red; Off: none):
    ///   - a family pet here in the red for hunger: the nearest pet bowl that needs filling (once a game hour per Sim; never a
    ///     bowl someone else is already on);
    ///   - a visitor who has just arrived and nobody has greeted: a grown-up greets them ("Greet", else a wave or handshake);
    ///   - a welcomed guest who is leaving (their queue or script is in Sims 1's "Leave" routine): a grown-up says goodbye
    ///     (a wave, a handshake or a hug - never "Shoo" or a kiss);
    ///   - a child of the family on a school day (Monday-Friday) 15:00-19:59 who hasn't done their homework today: "Study for
    ///     School" at the nearest bookcase (or any object's "... Homework" entry) - once a day.
    /// Greeting and goodbye are tried at most twice for the same guest; nobody goes if someone in the family already has it
    /// queued.
    ///
    /// MEMORY (person attributes 170-174 - B5b's care range; written only here, inside VMFindBestAction, in the lot's ticks):
    ///   170 GuestAttr     the guest last greeted / seen off (object id)    171 TriesAttr   tries for that guest
    ///   172 HomeworkAttr  the day (VMReduxRelationships.Today) homework was started
    ///   173 PetFeedAttr   the game hour (VMReduxPhone.HourStamp) a bowl was last looked for    174 HomeworkLookAttr  the same, homework
    ///
    /// Not done (Sims 1 has no such choice): helping with homework (no "Help with Homework" in Sims 1 - a custom object's entry
    /// containing "Homework" is used by children only), walking the dog, and phoning friends on their own - the phone's "Call
    /// Friend" advertises nothing and opens dialogs for the player, so it's left to the player (VMReduxBrainGoals.FriendToCall
    /// says who). LOCKSTEP: lot state only (avatars' needs, person data, queues, attributes, relationships as the game keeps
    /// them, objects' GUIDs, labels and English names, the lot clock); the factors change nothing and allocate nothing on the
    /// common path (a "Why I'm doing this" note with a name is made only for a matching choice).
    /// </summary>
    public static class VMReduxBrainCare
    {
        public const int GuestAttr = 170, TriesAttr = 171, HomeworkAttr = 172, PetFeedAttr = 173, HomeworkLookAttr = 174;

        public static void Register()
        {
            VMReduxBrain.AddFactorPerMille("care-comfort", Comfort);
            VMReduxBrain.AddFactorPerMille("care-friends", Friends);
            VMReduxBrain.AddFactorPerMille("care-host", Host);
            VMReduxBrain.AddFactorPerMille("care-petfeed", PetFeed);
            VMReduxBrain.AddFactorPerMille("care-petplay", PetPlay);
        }

        // ------------------------------------------------------------------ who's who (lot state only)

        private static bool Here(VMAvatar a)
        {
            return a != null && !a.Dead && a.Position != LotTilePos.OUT_OF_WORLD && a.Container == null;
        }

        private static bool Family(VM vm, VMAvatar a)
        {
            return a != null && ReduxComputer.IsHouseholdSim(vm, a);
        }

        /// <summary>A service NPC (maid, mail carrier, pizza dude...) - every Maxis one's object label starts "NPC".</summary>
        private static bool Npc(VMAvatar a)
        {
            var label = a?.Object?.OBJ?.ChunkLabel;
            return label != null && label.StartsWith("NPC", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A guest: a person here who isn't the household, a service NPC, a pet or a ghost - a visitor or a visiting player's Sim.</summary>
        private static bool Guest(VM vm, VMAvatar a)
        {
            if (!Here(a) || a.IsPet || Family(vm, a) || Npc(a) || a.GetPersonData(VMPersonDataVariable.IsGhost) != 0) return false;
            if (ReduxSidewalk.IsStreet(a)) return false; // (19.6: only walking past on the street - not greeted or seen off)
            return a.GetPersonData(VMPersonDataVariable.PersonType) == 1 || VMReduxBrain.VisitingSim(vm, a);
        }

        /// <summary>Greeted (or a visiting player's Sim, who needs no greeting).</summary>
        private static bool Welcomed(VM vm, VMAvatar a)
        {
            return VMReduxBrain.VisitingSim(vm, a) || a.GetPersonData(VMPersonDataVariable.GreetStatus) != 0;
        }

        private static bool Arriving(VM vm, VMAvatar a)
        {
            return a.GetPersonData(VMPersonDataVariable.PersonType) == 1 && a.GetPersonData(VMPersonDataVariable.GreetStatus) == 0
                && !VMReduxBrain.VisitingSim(vm, a);
        }

        /// <summary>On their way out: Sims 1's "Leave" routine is queued or running.</summary>
        private static bool Leaving(VMAvatar a)
        {
            var t = a?.Thread;
            if (t == null) return false;
            var q = t.Queue;
            if (q != null)
                for (int i = 0; i < q.Count; i++)
                    if (q[i].ActionRoutine != null && q[i].ActionRoutine.ID == VMNetReduxTownieCmd.LeaveRoutine) return true;
            var s = t.Stack;
            if (s != null)
                for (int i = 0; i < s.Count; i++)
                    if (s[i]?.Routine != null && s[i].Routine.ID == VMNetReduxTownieCmd.LeaveRoutine) return true;
            return false;
        }

        /// <summary>How many welcomed guests are here, and whether any of them (or the family here) is peckish (hunger under 50).</summary>
        private static int WelcomedGuests(VM vm, out bool peckish)
        {
            peckish = false;
            int n = 0;
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || a.IsPet || !Here(a)) continue;
                bool guest = Guest(vm, a) && Welcomed(vm, a);
                if (guest) n++;
                if ((guest || Family(vm, a)) && a.GetMotiveData(VMMotive.Hunger) < 50) peckish = true;
            }
            return n;
        }

        /// <summary>A visit is booked: Sims 1's own Scheduler holds a person who isn't a service NPC or the family.</summary>
        private static bool GuestsExpected(VM vm)
        {
            var sched = vm.Context.ObjectQueries.GetObjectsByGUID(VMReduxBrainAhead.SchedulerGUID);
            if (sched == null) return false;
            for (int i = 0; i < sched.Count; i++)
            {
                var s = sched[i];
                if (s == null || s.Dead) continue;
                var who = vm.GetObjectById(s.GetAttribute(0)) as VMAvatar;
                if (who == null || who.Dead || who.IsPet || Npc(who) || Family(vm, who)) continue;
                return true;
            }
            return false;
        }

        /// <summary>The family's pet here (awake or not) whose need is lowest - hunger, or the lower of fun and company. Null: none.</summary>
        private static VMAvatar NeediestPet(VM vm, bool hunger, out int value)
        {
            value = int.MaxValue;
            VMAvatar best = null;
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || !a.IsPet || !Here(a) || !Family(vm, a)) continue;
                int v = hunger ? a.GetMotiveData(VMMotive.Hunger) : Math.Min(a.GetMotiveData(VMMotive.Fun), a.GetMotiveData(VMMotive.Social));
                if (v < value) { value = v; best = a; }
            }
            return best;
        }

        private static string NameOf(VMEntity e)
        {
            var n = e?.Name;
            return string.IsNullOrEmpty(n) ? null : n;
        }

        private static bool StartsAny(string n, string[] starts)
        {
            for (int i = 0; i < starts.Length; i++) if (n.StartsWith(starts[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A physical need under this: no caring errands (their own needs first).</summary>
        private const int OwnNeedsBelow = -20;

        private static bool OwnNeedsFine(VMAvatar a)
        {
            return a.GetMotiveData(VMMotive.Hunger) >= OwnNeedsBelow && a.GetMotiveData(VMMotive.Bladder) >= OwnNeedsBelow
                && a.GetMotiveData(VMMotive.Energy) >= OwnNeedsBelow && a.GetMotiveData(VMMotive.Hygiene) >= OwnNeedsBelow;
        }

        // ------------------------------------------------------------------ care-comfort (parenting)

        // (Sims 1's friendly socials - PersonGlobals' English names; none of them romantic)
        private static readonly string[] ComfortStrong = { "cheer up", "hug.../friendly", "hug.../nice" };
        private static readonly string[] ComfortMild = { "tickle", "entertain.../joke", "entertain.../with puppet", "entertain.../perform trick", "play.../tag", "play.../rock, paper", "ask.../how are you", "talk.../about interests" };

        /// <summary>A child is upset under these.</summary>
        private const int UpsetMood = -25, VeryUpsetMood = -50, UpsetNeed = -30;

        private static int Comfort(VMReduxBrainContext ctx)
        {
            var child = ctx.Target as VMAvatar;
            var sim = ctx.Sim;
            if (child == null || child == sim || child.IsPet || !child.IsChild || sim == null || sim.IsChild || ctx.Name == null) return 1000;
            var n = ctx.Name.TrimStart();
            bool strong = StartsAny(n, ComfortStrong) || n.Equals("Hug", StringComparison.OrdinalIgnoreCase);
            if (!strong && !StartsAny(n, ComfortMild)) return 1000;
            var vm = ctx.VM;
            if (vm == null || !Family(vm, sim) || !Family(vm, child) || !Here(child) || child.GetMotiveData(VMMotive.SleepState) != 0) return 1000;
            int mood = child.GetMotiveData(VMMotive.Mood);
            bool upset = mood < UpsetMood || child.GetMotiveData(VMMotive.Social) < UpsetNeed || child.GetMotiveData(VMMotive.Fun) < UpsetNeed;
            if (!upset || VMReduxBrain.InRed(sim)) return 1000;
            var who = NameOf(child);
            ctx.Note(who != null ? who + " is upset - cheering them up" : "A child is upset - cheering them up");
            if (!strong) return 1300;
            return mood < VeryUpsetMood ? 2000 : 1700;
        }

        // ------------------------------------------------------------------ care-friends (keeping friends)

        private static readonly string[] FriendlyStarts = { "talk", "ask.../how", "ask.../what", "ask.../let's hang out", "entertain", "cheer up", "compliment", "hug.../friendly", "tickle", "play.../" };

        /// <summary>Lifetime relationship at or over this is a good friend; "fading" when today's is FadeBy under it (and under FadeBelow).</summary>
        public const int GoodFriendLife = 50, FadeBy = 10, FadeBelow = 70;
        /// <summary>Lonely under this - goal-friends' (VMReduxBrainGoals.LonelyBelow), not doubled here.</summary>
        private const int LonelyBelow = 30;

        /// <summary>a's friendship with b is fading (a good friend, today's relationship well under the lasting one, not an enemy).</summary>
        public static bool Fading(VMAvatar a, VMAvatar b)
        {
            var r = ReduxWooHoo.Rel(a, b);
            if (r == null || r.Count == 0) return false;
            int daily = r[0], life = r.Count > 2 ? r[2] : r[0];
            return life >= GoodFriendLife && daily >= 0 && daily < FadeBelow && daily <= life - FadeBy;
        }

        private static int Friends(VMReduxBrainContext ctx)
        {
            var other = ctx.Target as VMAvatar;
            var sim = ctx.Sim;
            if (other == null || other == sim || other.IsPet || sim == null || ctx.Name == null) return 1000;
            if (ctx.Need(VMMotive.Social) < LonelyBelow) return 1000; // (lonely: goal-friends)
            if (!StartsAny(ctx.Name.TrimStart(), FriendlyStarts)) return 1000;
            short fam = sim.GetPersonData(VMPersonDataVariable.TS1FamilyNumber);
            if (fam > 0 && fam == other.GetPersonData(VMPersonDataVariable.TS1FamilyNumber)) return 1000; // (family see each other every day)
            if (!Fading(sim, other) || VMReduxBrain.InRed(sim)) return 1000;
            var who = NameOf(other);
            ctx.Note(who != null ? "Haven't seen " + who + " in a while - catching up" : "Catching up with an old friend");
            return 1400;
        }

        // ------------------------------------------------------------------ care-host (good host)

        private static int Host(VMReduxBrainContext ctx)
        {
            var vm = ctx.VM; var sim = ctx.Sim;
            if (vm == null || !vm.TS1 || sim == null || sim.IsPet) return 1000;
            var target = ctx.Target;
            var n = ctx.Name;

            // greeting a guest who's just arrived
            var guest = target as VMAvatar;
            if (guest != null)
            {
                if (guest == sim || n == null || !n.TrimStart().StartsWith("Greet", StringComparison.OrdinalIgnoreCase)) return 1000;
                if (!Family(vm, sim) || !Guest(vm, guest) || !Arriving(vm, guest) || VMReduxBrain.InRed(sim)) return 1000;
                ctx.Note("Greeting our guest");
                return 1800;
            }
            if (target == null) return 1000;

            // what kind of choice it is (cheap checks first - most choices are none of these)
            bool serve = n != null && n.TrimStart().StartsWith("Serve", StringComparison.OrdinalIgnoreCase) && VMReduxBrainAhead.CookingChoice(target, n);
            bool tidy = !serve && ctx.Kind == VMReduxBrain.Kind.Chores && ctx.Ad(VMMotive.Room) > 0
                && ctx.Ad(VMMotive.Fun) <= 0 && ctx.Ad(VMMotive.Social) <= 0 && ctx.Ad(VMMotive.Hunger) <= 0;
            bool alone = !serve && !tidy && (ctx.Kind == VMReduxBrain.Kind.Screen || ctx.Kind == VMReduxBrain.Kind.Play)
                && ctx.Ad(VMMotive.Hunger) <= 0 && ctx.Ad(VMMotive.Bladder) <= 0 && ctx.Ad(VMMotive.Hygiene) <= 0 && ctx.Ad(VMMotive.Energy) <= 0;
            if (!serve && !tidy && !alone) return 1000;
            if (!Family(vm, sim) || VMReduxBrain.InRed(sim)) return 1000;

            bool peckish;
            int guests = WelcomedGuests(vm, out peckish);
            if (serve)
            {
                if (guests == 0 || !peckish || ctx.Hour < 7 || ctx.Hour >= 22 || VMReduxBrainAhead.FoodOnTheWay(vm)) return 1000;
                ctx.Note("We have guests - making food");
                return 1500;
            }
            if (tidy)
            {
                if (!OwnNeedsFine(sim)) return 1000;
                if (guests > 0) { ctx.Note("Tidying up for our guests"); return 1300; }
                if (GuestsExpected(vm)) { ctx.Note("Guests are coming - tidying up"); return 1250; }
                return 1000;
            }
            return guests > 0 ? 850 : 1000; // (no note: a lowered choice isn't one to explain)
        }

        // ------------------------------------------------------------------ pet care

        /// <summary>A pet's need under these: low (an Animal Lover notices sooner), in the red.</summary>
        public const int PetLow = 30, PetLowAnimalLover = 50, PetRed = -40;

        private static int PetThreshold(VMAvatar sim)
        {
            return VMReduxTraits.Has(sim, VMReduxTraits.T.AnimalLover) ? PetLowAnimalLover : PetLow;
        }

        /// <summary>A dog or cat bowl's feeding entry ("Feed"/"Fill" - VMReduxChores' Pets chore - not a fish tank).</summary>
        private static bool PetBowlEntry(VMEntity obj, TTABInteraction ia, string english)
        {
            if (obj == null || ia == null || english == null) return false;
            var n = english.TrimStart();
            if (!n.StartsWith("Feed", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Fill", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.IndexOf("fish", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (VMReduxChores.ChoreOf(obj, ia) != VMReduxChores.Chore.Pets) return false;
            return !ReduxWooHoo.LabelWord(obj, "fish", "aquarium", "tank", "fishbowl");
        }

        private static int PetFeed(VMReduxBrainContext ctx)
        {
            if (!(ctx.Target is VMGameObject) || ctx.Name == null || ctx.Sim == null || ctx.VM == null) return 1000;
            var n = ctx.Name.TrimStart();
            if (!n.StartsWith("Feed", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Fill", StringComparison.OrdinalIgnoreCase)) return 1000; // (cheap way out)
            if (VMReduxBrain.InRed(ctx.Sim)) return 1000;
            int hunger;
            var pet = NeediestPet(ctx.VM, true, out hunger);
            if (pet == null || hunger >= PetThreshold(ctx.Sim)) return 1000;
            if (!PetBowlEntry(ctx.Target, ctx.Entry, ctx.Name)) return 1000;
            var who = NameOf(pet);
            ctx.Note(who != null ? "Time to feed " + who : "Time to feed the pets");
            return hunger < PetRed ? 2200 : 1500;
        }

        private static readonly string[] PetPlayStarts = { "play with", "give...", "snuggle" };

        private static int PetPlay(VMReduxBrainContext ctx)
        {
            var pet = ctx.Target as VMAvatar;
            var sim = ctx.Sim;
            if (pet == null || !pet.IsPet || sim == null || sim.IsPet || ctx.Name == null || ctx.VM == null) return 1000;
            var n = ctx.Name.TrimStart();
            if (!StartsAny(n, PetPlayStarts) && !n.Equals("Pet", StringComparison.OrdinalIgnoreCase) && !n.Equals("Play", StringComparison.OrdinalIgnoreCase)) return 1000;
            if (!Family(ctx.VM, pet) || !Here(pet) || pet.GetMotiveData(VMMotive.SleepState) != 0 || VMReduxBrain.InRed(sim)) return 1000;
            int need = Math.Min(pet.GetMotiveData(VMMotive.Fun), pet.GetMotiveData(VMMotive.Social));
            if (need >= PetThreshold(sim)) return 1000;
            int m = need < PetRed ? 1900 : 1400;
            var who = NameOf(pet) ?? "the pet";
            if (pet.IsDog && (n.IndexOf("Fetch", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ctx.Note("Taking " + who + " out for a game");
                return m * 1150 / 1000;
            }
            ctx.Note(need < PetRed ? who + " has been left alone too long - time to play" : who + " wants to play");
            return m;
        }

        // ------------------------------------------------------------------ essentials (VMFindBestAction)

        /// <summary>Homework on school days from this time of day (game minutes) to HomeworkUntil.</summary>
        public const int HomeworkFrom = 15 * 60, HomeworkUntil = 20 * 60;
        /// <summary>Greeting / goodbye tries for one guest.</summary>
        private const int MaxTries = 2;

        private static readonly string[] GreetPrefs = { "greet", "greet.../wave", "greet.../shake hands", "greet.../hug" };
        private static readonly string[] GoodbyePrefs = { "say goodbye.../wave", "say goodbye.../shake hands", "say goodbye.../hug" };

        /// <summary>
        /// Something caring the Sim should simply go and do now (see the class notes), or null. Called from VMFindBestAction for
        /// the household's Sims with an Adaptive brain when nothing is urgent - in the lot's ticks, so its few attribute writes
        /// (170-173, so nothing is tried over and over) are the same on every game.
        /// </summary>
        public static VMPieMenuInteraction FindCare(VM vm, VMAvatar sim, List<VMEntity> ents, bool fwOff, bool fwRestrict)
        {
            if (vm == null || !vm.TS1 || sim == null || sim.IsPet || sim.Dead || fwOff || ents == null) return null;
            if (!Family(vm, sim) || !Here(sim) || sim.GetMotiveData(VMMotive.SleepState) != 0 || VMReduxBrain.InRed(sim)) return null;

            // a pet in the red for hunger - never neglected (also with free will "Needs only")
            short hourStamp = VMReduxPhone.HourStamp(vm);
            if (sim.GetAttribute(PetFeedAttr) != hourStamp)
            {
                int hunger;
                var pet = NeediestPet(vm, true, out hunger);
                if (pet != null && hunger < PetRed)
                {
                    sim.SetAttribute(PetFeedAttr, hourStamp); // (once a game hour, found or not - no lot-wide search on every choice)
                    var feed = FindEntry(vm, sim, ents, 0);
                    if (feed != null) return feed;
                }
            }
            if (fwRestrict || !OwnNeedsFine(sim)) return null;

            // guests: greet the one who's just arrived, see off the one who's leaving (grown-ups)
            if (!sim.IsChild)
            {
                var avatars = vm.Context.ObjectQueries.Avatars;
                for (int i = 0; i < avatars.Count; i++)
                {
                    var g = avatars[i] as VMAvatar;
                    if (g == null || g == sim || !Guest(vm, g)) continue;
                    bool greet = Arriving(vm, g);
                    bool bye = !greet && Welcomed(vm, g) && Leaving(g);
                    if (!greet && !bye) continue;
                    if (FamilyOnIt(vm, sim, g, greet ? "Greet" : "Say Goodbye")) continue;
                    int tries = sim.GetAttribute(GuestAttr) == g.ObjectID ? sim.GetAttribute(TriesAttr) : 0;
                    if (tries >= MaxTries) continue;
                    var choice = SocialEntry(vm, sim, g, greet ? GreetPrefs : GoodbyePrefs);
                    if (choice == null) continue;
                    sim.SetAttribute(GuestAttr, g.ObjectID);
                    sim.SetAttribute(TriesAttr, (short)(tries + 1));
                    return choice;
                }
            }

            // a child's homework, once a school day
            if (sim.IsChild && ReduxWeek.Day(vm.Context.Clock) < 5)
            {
                var clock = vm.Context.Clock;
                int tod = clock.Hours * 60 + clock.Minutes;
                short today = VMReduxRelationships.Today(vm);
                if (tod >= HomeworkFrom && tod < HomeworkUntil && sim.GetAttribute(HomeworkAttr) != today && sim.GetAttribute(HomeworkLookAttr) != hourStamp)
                {
                    sim.SetAttribute(HomeworkLookAttr, hourStamp); // (looked for this hour - no bookcase, no search on every choice)
                    var hw = FindEntry(vm, sim, ents, 1);
                    if (hw != null) { sim.SetAttribute(HomeworkAttr, today); return hw; }
                }
            }
            return null;
        }

        /// <summary>The bookcase's "Study for School" or any object's "... Homework" (not "Help with Homework" - a grown-up's).</summary>
        private static bool HomeworkEntry(string english)
        {
            if (english == null) return false;
            var n = english.Trim();
            if (n.StartsWith("Study for School", StringComparison.OrdinalIgnoreCase)) return true;
            return n.IndexOf("Homework", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Help", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>
        /// The nearest game object with a matching entry (what 0 = a pet bowl to fill, 1 = homework) that nobody else is on and
        /// whose own check lets the Sim do it now - checked as the player clicking it, like ReduxBrain's phone services. Ties go to
        /// the lower object id. The check trees share the Sim's temp registers: they're put back as they were.
        /// </summary>
        private static VMPieMenuInteraction FindEntry(VM vm, VMAvatar sim, List<VMEntity> ents, int what)
        {
            var cands = new List<KeyValuePair<long, KeyValuePair<VMEntity, TTABInteraction>>>();
            var seen = new HashSet<short>();
            var all = vm.Entities; // (every object, not just the ones free will looks at - a pet bowl may advertise nothing to Sims)
            int sx = sim.Position.TileX, sy = sim.Position.TileY, sl = sim.Position.Level;
            for (int ei = 0; ei < all.Count; ei++)
            {
                var e = all[ei];
                if (!(e is VMGameObject) || e.Dead || e.Position == LotTilePos.OUT_OF_WORLD || e.MultitileGroup == null) continue;
                if (((VMGameObject)e).Disabled > 0) continue;
                var obj = e.MultitileGroup.GetInteractionGroupLeader(e);
                if (obj == null || !seen.Add(obj.ObjectID) || obj.TreeTable?.Interactions == null) continue;
                foreach (var ia in obj.TreeTable.Interactions)
                {
                    var english = VMReduxBrain.LotName(obj, ia);
                    if (what == 0 ? !PetBowlEntry(obj, ia, english) : !HomeworkEntry(english)) continue;
                    if (TakenByOther(vm, sim, obj)) break;
                    int dx = obj.Position.TileX - sx, dy = obj.Position.TileY - sy;
                    long d = (long)(dx * dx + dy * dy + (obj.Position.Level != sl ? 400 : 0)) * 65536 + (ushort)obj.ObjectID;
                    cands.Add(new KeyValuePair<long, KeyValuePair<VMEntity, TTABInteraction>>(d, new KeyValuePair<VMEntity, TTABInteraction>(obj, ia)));
                    break;
                }
            }
            if (cands.Count == 0) return null;
            cands.Sort((x, y) => x.Key.CompareTo(y.Key)); // (nearest, then lowest object id - the same order on every game)
            for (int i = 0; i < cands.Count && i < 8; i++)
            {
                var c = Checked(vm, sim, cands[i].Value.Key, cands[i].Value.Value);
                if (c != null) return c;
            }
            return null;
        }

        /// <summary>The first of these social entries (English names, in order of preference) on the guest that the Sim can do now.</summary>
        private static VMPieMenuInteraction SocialEntry(VM vm, VMAvatar sim, VMAvatar guest, string[] prefs)
        {
            var ias = guest.TreeTable?.Interactions;
            if (ias == null) return null;
            for (int p = 0; p < prefs.Length; p++)
            {
                foreach (var ia in ias)
                {
                    var english = VMReduxBrain.LotName(guest, ia);
                    if (english == null || !string.Equals(english.Trim(), prefs[p], StringComparison.OrdinalIgnoreCase)) continue;
                    var c = Checked(vm, sim, guest, ia);
                    if (c != null) return c;
                }
            }
            return null;
        }

        /// <summary>The entry as a choice, if its check lets the Sim do it now (temp registers and HideInteraction put back).</summary>
        private static VMPieMenuInteraction Checked(VM vm, VMAvatar sim, VMEntity obj, TTABInteraction ia)
        {
            var thread = sim.Thread;
            if (thread == null) return null;
            var temps = (short[])thread.TempRegisters.Clone();
            var tempXL = (int[])thread.TempXL.Clone();
            var hide = sim.ObjectData[(int)VMStackObjectVariable.HideInteraction];
            try
            {
                var action = obj.GetAction((int)ia.TTAIndex, sim, vm.Context, false);
                if (action == null) return null;
                sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                var avail = thread.CheckAction(action, false);
                if (avail == null) return null;
                // (the menu's own name only for display - nothing on the lot reads it)
                var shown = (avail.Count > 0 ? avail[0].Name : null) ?? obj.TreeTableStrings?.GetString((int)ia.TTAIndex) ?? "";
                return new VMPieMenuInteraction { Name = shown, ID = (byte)ia.TTAIndex, Param0 = avail.Count > 0 ? avail[0].Param0 : (short)0,
                    Entry = ia, Global = false, Callee = obj, Score = 1 };
            }
            catch (Exception) { return null; } // (a broken object's check never stops the lot - the same on every machine)
            finally
            {
                Array.Copy(temps, thread.TempRegisters, Math.Min(temps.Length, thread.TempRegisters.Length));
                Array.Copy(tempXL, thread.TempXL, Math.Min(tempXL.Length, thread.TempXL.Length));
                sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = hide;
            }
        }

        /// <summary>Another Sim has this object in their queue.</summary>
        private static bool TakenByOther(VM vm, VMAvatar me, VMEntity obj)
        {
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || a == me || a.Thread?.Queue == null) continue;
                var q = a.Thread.Queue;
                for (int j = 0; j < q.Count; j++) if (q[j].Callee == obj) return true;
            }
            return false;
        }

        /// <summary>Someone else of the family already has this social ("Greet", "Say Goodbye") with the guest queued.</summary>
        private static bool FamilyOnIt(VM vm, VMAvatar me, VMAvatar guest, string start)
        {
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || a == me || a.Thread?.Queue == null || !Family(vm, a)) continue;
                var q = a.Thread.Queue;
                for (int j = 0; j < q.Count; j++)
                {
                    var qa = q[j];
                    if (qa.Callee != guest || qa.InteractionNumber < 0 || guest.TreeTable?.InteractionByIndex == null) continue;
                    TTABInteraction entry;
                    if (!guest.TreeTable.InteractionByIndex.TryGetValue((uint)qa.InteractionNumber, out entry)) continue;
                    var n = VMReduxBrain.LotName(guest, entry);
                    if (n != null && n.TrimStart().StartsWith(start, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// "Why I'm doing this" for an essential FindCare picked (display only - nothing on the lot reads it), or null when the
        /// choice isn't one of them.
        /// </summary>
        public static string EssentialWhy(VM vm, VMAvatar sim, VMPieMenuInteraction chosen)
        {
            try
            {
                if (vm == null || chosen?.Callee == null || chosen.Entry == null) return null;
                var english = VMReduxBrain.LotName(chosen.Callee, chosen.Entry);
                if (english == null) return null;
                var n = english.Trim();
                var guest = chosen.Callee as VMAvatar;
                if (guest != null && !guest.IsPet)
                {
                    if (n.StartsWith("Greet", StringComparison.OrdinalIgnoreCase)) return "Greeting our guest";
                    if (n.StartsWith("Say Goodbye", StringComparison.OrdinalIgnoreCase))
                    {
                        var who = NameOf(guest);
                        return who != null ? "Saying goodbye to " + who : "Saying goodbye to our guest";
                    }
                    return null;
                }
                if (HomeworkEntry(n)) return "Homework before play";
                if (PetBowlEntry(chosen.Callee, chosen.Entry, n))
                {
                    int hunger;
                    var pet = NeediestPet(vm, true, out hunger);
                    var who = NameOf(pet);
                    return who != null ? "Time to feed " + who : "Time to feed the pets";
                }
            }
            catch { }
            return null;
        }
    }
}
