using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.5 (Part B #9, B5b): long-term goals - brain factors (see VMReduxBrainFactors and VMReduxBrain's FACTOR
    /// API notes). Each is a multiplier (per mille, 1000 = no change) on one choice of one Sim with an Adaptive brain, kept
    /// moderate (about x0.2 - x1.7) so a need in the red still wins:
    ///   goal-career   career sense: a working grown-up of the family leans towards the skill(s) their next promotion is
    ///                 short of (Sims 1's own career tables, work.iff CARR: the NEXT level's row) and, when it's short of
    ///                 friends, towards friendly chats with Sims who aren't friends yet - only in free time (no physical
    ///                 need under 0, no need in the red, not in the 2 hours before the carpool, free will Normal or High);
    ///   goal-friends  social memory: with Social lowish (under 30), friendly socials with the Sims they like most -
    ///                 friends (daily 50+) first, good acquaintances (25+) next;
    ///   goal-avoid    ...and the Sims they dislike (daily -25 or less): fewer socials with them (apologising aside), and
    ///                 doing things right next to them (within 3 tiles) is less appealing;
    ///   goal-mood     mood-aware: a sad Sim looks for comfort (time with friends and family, a hug from family, fun or a
    ///                 comfy seat), an angry Sim cools off (a bath or shower, reading, exercise, a nap - fewer socials, no
    ///                 romance, no picking another fight).
    ///
    /// EMOTIONS: the game's emotions (Simitone's ReduxEmotions - the plumbob and the mood hover) are worked out on each
    /// player's screen, aren't saved and can be switched off, so the lot can't use them. "Sad" and "angry" are worked out
    /// here from the lot itself instead, the same way on every game:
    ///   sad    the Sims 1 mood (VMMotive.Mood) under -25, or Social under -40, or Fun under -50;
    ///   angry  a fall-out with a Sim who's here now: their daily relationship to them is -10 or less AND at least 20 under
    ///          the lifetime one (it dropped recently - an insult, a slap, a fight, a turned-down social; the lifetime score
    ///          catches up over the days), or -40 or less where there's no lifetime score.
    ///   (angry wins over sad.)
    ///
    /// RELATIONSHIPS: as the game keeps them - two Sims with neighbour ids (the family, townies, a visiting player's Sim)
    /// in the neighbourhood's lists (VMRelationship reads and writes them there; a visitor's game plays on the host's
    /// neighbourhood), anyone else in the lot's own (MeToObject). [0] daily, [2] lifetime.
    ///
    /// PHONE CALLS: Sims 1's phone choices to call someone ("Call Friend", "Call Neighbor") advertise nothing, so free will
    /// never weighs them - no factor can make a Sim call. FriendToCall (who they'd call: their closest friend who isn't here)
    /// is ready for whoever adds phoning on their own.
    ///
    /// LOCKSTEP: lot state only (needs, person data, relationships, the clock, the career tables, the choice's English
    /// name and ads); nothing is changed. What each Sim feels and who they dislike is worked out once per tick per Sim
    /// (Memo, kept on the Sim, rebuilt when the tick changes - derived from the lot only); everything else is allocation
    /// free per choice (the per-entry and per-name caches are made once).
    /// </summary>
    public static class VMReduxBrainGoals
    {
        public static void Register()
        {
            VMReduxBrain.AddFactorPerMille("goal-career", Career);
            VMReduxBrain.AddFactorPerMille("goal-friends", Friends);
            VMReduxBrain.AddFactorPerMille("goal-avoid", Avoid);
            VMReduxBrain.AddFactorPerMille("goal-mood", Mood);
        }

        // ------------------------------------------------------------------ thresholds

        /// <summary>Daily relationship: a friend at this (the game's own "friend"), a good acquaintance at GoodAt.</summary>
        public const int FriendAt = 50, GoodAt = 25;
        /// <summary>Daily relationship: disliked at or under this, an enemy at or under EnemyAt.</summary>
        public const int DislikeAt = -25, EnemyAt = -50;
        /// <summary>Social "lowish" for social memory.</summary>
        public const int LonelyBelow = 30;
        /// <summary>How near (tiles) to a disliked Sim counts as "next to them".</summary>
        public const int NearTiles = 3;
        /// <summary>Most disliked Sims remembered per tick (the first ones in the lot's own avatar order).</summary>
        private const int MaxDisliked = 4;

        // ------------------------------------------------------------------ what a choice is (worked out once per entry)

        [Flags]
        private enum S : byte { None = 0, Friendly = 1, Mean = 2, Romance = 4, Hug = 8, Apology = 16, Charisma = 32 }
        private sealed class EntryInfo { public S Social; public sbyte Skill = -1; }
        private static readonly ConditionalWeakTable<TTABInteraction, EntryInfo> Entries = new ConditionalWeakTable<TTABInteraction, EntryInfo>();

        // (English names of the Sims 1 socials, PersonGlobals: "Talk.../Gossip", "Hug.../Friendly", "Kiss.../Peck"...)
        private static readonly string[] MeanStarts = { "attack", "insult", "tease", "annoy", "nag", "scold", "shoo", "cut", "say goodbye.../shoo" };
        private static readonly string[] MeanWords = { "slap", "fight", "shove", "taunt", "raspberry", "poke", "kick shin", "toadification", "banish" };
        private static readonly string[] RomanceStarts = { "flirt", "kiss", "proposition", "ask.../let's date", "woohoo" };
        private static readonly string[] RomanceWords = { "kiss", "romantic", "intimate", "dance.../slow", "back rub", "lovestruck", "cuddle", "serenade", "woohoo", "growl", "sweet talk" };
        private static readonly string[] CharismaStarts = { "talk", "entertain", "brag", "compliment", "cheer up", "tell ", "chat", "gossip" };
        private static readonly string[] CharismaWords = { "joke", "gossip" };

        // the skills a choice builds, in the career tables' order (CARR MinRequired 1-6)
        private static readonly VMPersonDataVariable[] SkillVars =
        {
            VMPersonDataVariable.CookingSkill, VMPersonDataVariable.MechanicalSkill, VMPersonDataVariable.CharismaSkill,
            VMPersonDataVariable.BodySkill, VMPersonDataVariable.LogicSkill, VMPersonDataVariable.CreativitySkill
        };
        private const int Cooking = 0, Mechanical = 1, Charisma = 2, Body = 3, Logic = 4, Creativity = 5;
        private static readonly string[] SkillNotes =
        {
            "Working on Cooking for the next promotion", "Working on Mechanical for the next promotion",
            "Working on Charisma for the next promotion", "Working on Body for the next promotion",
            "Working on Logic for the next promotion", "Working on Creativity for the next promotion"
        };

        private static readonly string[] CookingStarts = { "have a meal", "have lite meal", "serve meal", "have breakfast", "have lunch", "have dinner", "serve breakfast", "serve lunch", "serve dinner", "cook", "grill", "bake", "barbecue", "study cooking", "read cookbook" };
        private static readonly string[] MechanicalStarts = { "repair", "fix", "unclog", "tinker", "study mechanical", "study repair" };
        private static readonly string[] BodyWords = { "swim", "dive", "work out", "workout", "exercise", "jog", "aerobic", "lift weight", "basketball", "shoot hoops", "volleyball" };
        private static readonly string[] SportObjects = { "basketball", "volleyball", "tetherball", "trampoline", "hoop" };
        private static readonly string[] LogicWords = { "stargaze", "gaze", "chess", "telescope" };
        private static readonly string[] LogicObjects = { "chess", "telescope" };
        private static readonly string[] CreativityWords = { "paint", "piano", "guitar", "sculpt", "draw", "compose", "violin", "drum" };
        private static readonly string[] CreativityObjects = { "piano", "guitar", "easel", "drum", "violin", "sculpt" };
        private static readonly string[] PlayStarts = { "play", "join" };

        private static bool Starts(string n, string[] words)
        {
            for (int i = 0; i < words.Length; i++) if (n.StartsWith(words[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool Has(string n, string[] words)
        {
            for (int i = 0; i < words.Length; i++) if (n.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static EntryInfo InfoOf(VMEntity target, TTABInteraction entry, string english)
        {
            if (entry == null) return null;
            EntryInfo box;
            if (Entries.TryGetValue(entry, out box)) return box;
            var info = Classify(target, english ?? VMReduxBrain.LotName(target, entry));
            try { return Entries.GetValue(entry, _ => info); } catch { return info; }
        }

        /// <summary>What a choice is, from its English (lot) name and, for "Play" / "Join", the object's game-file label.</summary>
        private static EntryInfo Classify(VMEntity target, string english)
        {
            var info = new EntryInfo();
            var n = (english ?? "").Trim().ToLowerInvariant();
            if (n.Length == 0) return info;
            if (target is VMAvatar)
            {
                if (n.StartsWith("plead", StringComparison.Ordinal) || n.IndexOf("apologi", StringComparison.Ordinal) >= 0) info.Social |= S.Apology;
                else if (Starts(n, MeanStarts) || Has(n, MeanWords)) info.Social |= S.Mean;
                else if (Starts(n, RomanceStarts) || Has(n, RomanceWords)) info.Social |= S.Romance;
                else
                {
                    info.Social |= S.Friendly;
                    if (n.StartsWith("hug", StringComparison.Ordinal) || n == "cheer up.../comfort" || n.StartsWith("greet.../hug", StringComparison.Ordinal)) info.Social |= S.Hug;
                    if (Starts(n, CharismaStarts) || Has(n, CharismaWords)) { info.Social |= S.Charisma; info.Skill = Charisma; }
                }
                return info;
            }
            var label = (target?.Object?.OBJ?.ChunkLabel ?? "").ToLowerInvariant();
            bool play = Starts(n, PlayStarts);
            if (label.StartsWith("food -", StringComparison.Ordinal)) return info; // (a plate: eating builds nothing)
            if (Starts(n, CookingStarts)) info.Skill = Cooking;
            else if (Starts(n, MechanicalStarts)) info.Skill = Mechanical;
            else if (n.StartsWith("practice speech", StringComparison.Ordinal)) info.Skill = Charisma;
            else if ((play && Has(label, LogicObjects)) || Has(n, LogicWords)) info.Skill = Logic;
            else if ((play && Has(label, CreativityObjects)) || Has(n, CreativityWords)) info.Skill = Creativity;
            else if ((play && Has(label, SportObjects)) || Has(n, BodyWords) || n == "dance" || n.StartsWith("dance", StringComparison.Ordinal)) info.Skill = Body;
            return info;
        }

        // ------------------------------------------------------------------ relationships (lot state)

        /// <summary>One Sim's relationship numbers for another (daily, lifetime; hasLife false when there's no lifetime score).</summary>
        public static bool Rel(VMAvatar me, VMAvatar other, out int daily, out int life, out bool hasLife)
        {
            daily = 0; life = 0; hasLife = false;
            if (me == null || other == null || me == other) return false;
            List<short> r = null;
            short myNid = me.GetPersonData(VMPersonDataVariable.NeighborId), theirNid = other.GetPersonData(VMPersonDataVariable.NeighborId);
            if (myNid > 0 && theirNid > 0)
            {
                var n = Content.Content.Get().Neighborhood?.GetNeighborByID(myNid);
                if (n?.Relationships == null || !n.Relationships.TryGetValue(theirNid, out r)) r = null;
            }
            else if (me.MeToObject != null && !me.MeToObject.TryGetValue((ushort)other.ObjectID, out r)) r = null;
            if (r == null || r.Count == 0) return false;
            daily = r[0];
            if (r.Count > 2) { life = r[2]; hasLife = true; }
            return true;
        }

        /// <summary>One Sim's daily relationship to another (0 if they've never met).</summary>
        public static int Daily(VMAvatar me, VMAvatar other)
        {
            int d, l; bool h;
            return Rel(me, other, out d, out l, out h) ? d : 0;
        }

        /// <summary>
        /// How many friends a Sim has - the game's own test (Neighbour Data "friend count"): neighbours they're at daily 50+
        /// with, both ways. Counted over the neighbourhood's lists (order doesn't matter - a count).
        /// </summary>
        public static int FriendCount(VMAvatar sim)
        {
            if (sim == null) return 0;
            short myNid = sim.GetPersonData(VMPersonDataVariable.NeighborId);
            if (myNid <= 0) return 0;
            var hood = Content.Content.Get().Neighborhood;
            var me = hood?.GetNeighborByID(myNid);
            if (me?.Relationships == null) return 0;
            int count = 0;
            foreach (var kv in me.Relationships)
            {
                var r = kv.Value;
                if (r == null || r.Count == 0 || r[0] < FriendAt || kv.Key == myNid) continue;
                var them = hood.GetNeighborByID((short)kv.Key);
                List<short> back;
                if (them?.Relationships != null && them.Relationships.TryGetValue(myNid, out back) && back != null && back.Count > 0 && back[0] >= FriendAt) count++;
            }
            return count;
        }

        /// <summary>
        /// "Remember who to call": the neighbour id of this Sim's closest friend (highest daily, friends only - 50+ both
        /// ways) who isn't on the lot now, or 0. Ties go to the lowest id (the same on every game). Not used by a factor -
        /// Sims 1's call choices advertise nothing (see the class notes); here for whoever adds phoning on their own.
        /// </summary>
        public static short FriendToCall(VM vm, VMAvatar sim)
        {
            if (vm == null || !vm.TS1 || sim == null) return 0;
            short myNid = sim.GetPersonData(VMPersonDataVariable.NeighborId);
            if (myNid <= 0) return 0;
            var hood = Content.Content.Get().Neighborhood;
            var me = hood?.GetNeighborByID(myNid);
            if (me?.Relationships == null) return 0;
            int best = 0, bestScore = int.MinValue;
            foreach (var kv in me.Relationships)
            {
                var r = kv.Value;
                if (r == null || r.Count == 0 || r[0] < FriendAt || kv.Key == myNid || kv.Key <= 0) continue;
                var them = hood.GetNeighborByID((short)kv.Key);
                List<short> back;
                if (them?.Relationships == null || !them.Relationships.TryGetValue(myNid, out back) || back == null || back.Count == 0 || back[0] < FriendAt) continue;
                if (OnLot(vm, them.GUID)) continue;
                int score = r[0];
                if (score > bestScore || (score == bestScore && kv.Key < best)) { bestScore = score; best = kv.Key; }
            }
            return (short)best;
        }

        private static bool OnLot(VM vm, uint guid)
        {
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i];
                if (a != null && !a.Dead && a.Object?.OBJ != null && a.Object.OBJ.GUID == guid && a.Position != LotTilePos.OUT_OF_WORLD) return true;
            }
            return false;
        }

        private static bool Here(VMAvatar a)
        {
            return a != null && !a.Dead && a.Position != LotTilePos.OUT_OF_WORLD && a.Container == null;
        }

        private static bool SameFamily(VMAvatar a, VMAvatar b)
        {
            short fa = a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber);
            return fa > 0 && fa == b.GetPersonData(VMPersonDataVariable.TS1FamilyNumber);
        }

        // ------------------------------------------------------------------ per Sim, once per tick (lot state only)

        private sealed class Memo
        {
            public VM Vm; public uint Tick; public bool Valid;
            public bool Sad, Angry;
            public short AngryAt;
            public int Friends = -1; // (worked out only when career sense asks)
            public int Disliked;
            public readonly short[] DislikedIds = new short[MaxDisliked];
            public readonly int[] DislikedX = new int[MaxDisliked], DislikedY = new int[MaxDisliked], DislikedLevel = new int[MaxDisliked];
        }
        private static readonly ConditionalWeakTable<VMAvatar, Memo> Memos = new ConditionalWeakTable<VMAvatar, Memo>();

        private static Memo MemoOf(VMReduxBrainContext ctx)
        {
            var sim = ctx.Sim; var vm = ctx.VM;
            Memo m;
            if (!Memos.TryGetValue(sim, out m))
            {
                try { m = Memos.GetValue(sim, _ => new Memo()); } catch { m = new Memo(); }
            }
            uint tick = vm.Scheduler.CurrentTickID;
            if (m.Valid && m.Vm == vm && m.Tick == tick) return m;
            m.Vm = vm; m.Tick = tick; m.Valid = true;
            m.Friends = -1; m.Disliked = 0; m.AngryAt = 0; m.Angry = false;

            // who's here that they've fallen out with or dislike (the lot's own avatar order - the same on every game)
            int worst = int.MaxValue;
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var a = avatars[i] as VMAvatar;
                if (a == null || a == sim || a.IsPet || !Here(a)) continue;
                int d, l; bool hasLife;
                if (!Rel(sim, a, out d, out l, out hasLife)) continue;
                bool fallOut = hasLife ? (d <= -10 && l - d >= 20) : d <= -40;
                if (fallOut && d < worst) { worst = d; m.AngryAt = a.ObjectID; m.Angry = true; }
                if (d <= DislikeAt && m.Disliked < MaxDisliked)
                {
                    int k = m.Disliked++;
                    m.DislikedIds[k] = a.ObjectID;
                    m.DislikedX[k] = a.Position.x; m.DislikedY[k] = a.Position.y; m.DislikedLevel[k] = a.Position.Level;
                }
            }
            int mood = sim.GetMotiveData(VMMotive.Mood);
            m.Sad = !m.Angry && (mood < -25 || sim.GetMotiveData(VMMotive.Social) < -40 || sim.GetMotiveData(VMMotive.Fun) < -50);
            return m;
        }

        // ------------------------------------------------------------------ "Why I'm doing this" with a name (made once per name)

        private sealed class Names { public string Src, Lonely, Down, Hug; }
        private static readonly ConditionalWeakTable<VMAvatar, Names> NoteNames = new ConditionalWeakTable<VMAvatar, Names>();

        private static Names NamesOf(VMAvatar who)
        {
            Names n;
            if (!NoteNames.TryGetValue(who, out n))
            {
                try { n = NoteNames.GetValue(who, _ => new Names()); } catch { n = new Names(); }
            }
            var src = who.Name;
            if (n.Lonely == null || !string.Equals(n.Src, src, StringComparison.Ordinal))
            {
                var first = (src ?? "").Trim();
                int sp = first.IndexOf(' ');
                if (sp > 0) first = first.Substring(0, sp);
                if (first.Length == 0 || first.Length > 20) first = "a friend";
                n.Lonely = "Feeling lonely - time with " + first;
                n.Down = "Feeling down - time with " + first;
                n.Hug = "Feeling down - a hug from " + first;
                n.Src = src;
            }
            return n;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Free time: no need in the red and the body's needs (hunger, bladder, energy, hygiene) all 0 or more.</summary>
        private static bool FreeTime(VMReduxBrainContext ctx)
        {
            return ctx.Need(VMMotive.Hunger) >= 0 && ctx.Need(VMMotive.Bladder) >= 0 && ctx.Need(VMMotive.Energy) >= 0
                && ctx.Need(VMMotive.Hygiene) >= 0 && !VMReduxBrain.InRed(ctx.Sim);
        }

        /// <summary>A social with another person (not a pet, not themselves).</summary>
        private static VMAvatar Person(VMReduxBrainContext ctx)
        {
            var a = ctx.Target as VMAvatar;
            return (a == null || a == ctx.Sim || a.IsPet) ? null : a;
        }

        /// <summary>
        /// Minutes until this Sim's next carpool on a day their career works (an hour before work starts, as
        /// VMReduxBrainAhead has it), or -1 (no job, a day off, already been today).
        /// </summary>
        private static int MinutesToCarpool(VMReduxBrainContext ctx, JobLevel lvl, int job)
        {
            var vm = ctx.VM;
            int tod = ctx.TimeOfDay;
            int today = ReduxWeek.Day(vm.Context.Clock);
            int carPool = Math.Max(0, (lvl.StartTime % 24) * 60 - 60);
            int day = (tod < carPool) ? today : (today + 1) % 7;
            if (!ReduxWeek.Works(job, day)) return -1;
            if (day == today && ctx.Sim.GetPersonData(VMPersonDataVariable.TS1LastDayAtWork) == vm.Context.Clock.DayOfMonth) return -1;
            return (carPool - tod + 1440) % 1440;
        }

        // ------------------------------------------------------------------ goal-career

        /// <summary>No career thoughts this close to the carpool (minutes) - getting ready comes first (ahead-dayplan).</summary>
        public const int CareerQuietBefore = 120;

        /// <summary>
        /// goal-career: a working grown-up of the family, in free time (see FreeTime; free will Normal or High; not in the 2
        /// hours before the carpool). The next promotion's row of the career table (CARR level + 1: friends, then Cooking,
        /// Mechanical, Charisma, Body, Logic, Creativity in hundredths of a level - the carpool's own "test promotion" reads
        /// job data at level + 1 the same way, and the family's friend count, FAMI FamilyFriends):
        ///   - a choice that builds a skill they're short of: x1.3, x1.5 when 2 levels or more short (note "Working on
        ///     Logic for the next promotion");
        ///   - short of friends (their own friend count, or the family's, under what's needed): a friendly social with a
        ///     Sim from outside the family who isn't a friend yet: x1.4 when nearly one (daily 25-49), x1.25 otherwise
        ///     (daily -9 to 24) (note "Making friends for the next promotion"). Never romance.
        /// The top of a career: nothing.
        /// </summary>
        private static int Career(VMReduxBrainContext ctx)
        {
            var sim = ctx.Sim; var vm = ctx.VM;
            if (sim == null || vm == null || !vm.TS1 || sim.IsChild) return 1000;
            int job = sim.GetPersonData(VMPersonDataVariable.JobType);
            if (job <= 0) return 1000;
            if (ctx.FreeWill == VMReduxBrain.FreeWill.Off || ctx.FreeWill == VMReduxBrain.FreeWill.NeedsOnly) return 1000;
            var info = InfoOf(ctx.Target, ctx.Entry, ctx.Name);
            if (info == null) return 1000;
            var other = Person(ctx);
            bool friendly = other != null && (info.Social & S.Friendly) != 0;
            if (info.Skill < 0 && !friendly) return 1000; // (cheap way out: nothing career-ish about it)
            if (!ReduxComputer.IsHouseholdSim(vm, sim) || !FreeTime(ctx)) return 1000;

            var carr = Content.Content.Get().Jobs?.GetJob((ushort)job);
            var levels = carr?.JobLevels;
            if (levels == null || levels.Length == 0) return 1000;
            int level = Math.Max(0, (int)sim.GetPersonData(VMPersonDataVariable.JobPromotionLevel));
            if (level + 1 >= levels.Length) return 1000; // (the top of the career)
            var next = levels[level + 1];
            var now = levels[Math.Min(level, levels.Length - 1)];
            if (next?.MinRequired == null || now == null) return 1000;
            int mins = MinutesToCarpool(ctx, now, job);
            if (mins >= 0 && mins <= CareerQuietBefore) return 1000;

            int m = 1000;
            if (info.Skill >= 0 && info.Skill < SkillVars.Length)
            {
                int need = next.MinRequired[1 + info.Skill];
                int have = sim.GetPersonData(SkillVars[info.Skill]);
                if (need > have)
                {
                    m = need - have >= 200 ? 1500 : 1300;
                    ctx.Note(SkillNotes[info.Skill]);
                }
            }
            if (friendly && m == 1000 && next.MinRequired[0] > 0 && !SameFamily(sim, other))
            {
                var memo = MemoOf(ctx);
                if (memo.Friends < 0)
                {
                    int own = FriendCount(sim);
                    var fam = vm.TS1State?.CurrentFamily;
                    memo.Friends = Math.Max(own, fam != null ? fam.FamilyFriends : 0);
                }
                if (memo.Friends < next.MinRequired[0])
                {
                    int d = Daily(sim, other);
                    if (d >= FriendAt || d < -9) return 1000; // (already a friend - or not someone to make friends with)
                    ctx.Note("Making friends for the next promotion");
                    return d >= GoodAt ? 1400 : 1250;
                }
            }
            return m;
        }

        // ------------------------------------------------------------------ goal-friends

        /// <summary>
        /// goal-friends: Social under 30 (and nothing in the red) - a friendly social (not mean, not romance) with a friend
        /// (daily 50+): x1.5, x1.7 with Social under 0; with a good acquaintance (25-49): x1.2. (Note "Feeling lonely - time
        /// with Bella".) Strangers are left as they are; Sims they dislike are goal-avoid's.
        /// </summary>
        private static int Friends(VMReduxBrainContext ctx)
        {
            var other = Person(ctx);
            if (other == null) return 1000;
            int social = ctx.Need(VMMotive.Social);
            if (social >= LonelyBelow) return 1000;
            var info = InfoOf(ctx.Target, ctx.Entry, ctx.Name);
            if (info == null || (info.Social & S.Friendly) == 0) return 1000;
            if (VMReduxBrain.InRed(ctx.Sim)) return 1000;
            int d = Daily(ctx.Sim, other);
            if (d >= FriendAt)
            {
                ctx.Note(NamesOf(other).Lonely);
                return social < 0 ? 1700 : 1500;
            }
            if (d >= GoodAt) { ctx.Note(NamesOf(other).Lonely); return 1200; }
            return 1000;
        }

        // ------------------------------------------------------------------ goal-avoid

        /// <summary>
        /// goal-avoid: a Sim they dislike (daily -25 or less): socials with them x0.6, x0.35 for an enemy (-50 or less) -
        /// apologising ("Plead.../Apologize") aside. Anything else right next to a disliked Sim who's here (an object within 3
        /// tiles of them): x0.75 - not while a need is in the red.
        /// </summary>
        private static int Avoid(VMReduxBrainContext ctx)
        {
            if (ctx.Sim == null || ctx.VM == null || ctx.Target == null) return 1000;
            var other = ctx.Target as VMAvatar;
            if (other != null)
            {
                if (other == ctx.Sim || other.IsPet) return 1000;
                int d = Daily(ctx.Sim, other);
                if (d > DislikeAt) return 1000;
                var info = InfoOf(ctx.Target, ctx.Entry, ctx.Name);
                if (info != null && (info.Social & S.Apology) != 0) return 1000;
                return d <= EnemyAt ? 350 : 600;
            }
            var memo = MemoOf(ctx);
            if (memo.Disliked == 0 || VMReduxBrain.InRed(ctx.Sim)) return 1000;
            var p = ctx.Target.Position;
            if (p == LotTilePos.OUT_OF_WORLD) return 1000;
            const int near = NearTiles * 16;
            for (int i = 0; i < memo.Disliked; i++)
            {
                if (memo.DislikedLevel[i] != p.Level) continue;
                int dx = memo.DislikedX[i] - p.x, dy = memo.DislikedY[i] - p.y;
                if (dx * dx + dy * dy <= near * near) return 750;
            }
            return 1000;
        }

        // ------------------------------------------------------------------ goal-mood

        /// <summary>
        /// goal-mood (see EMOTIONS in the class notes). Nothing while a need is in the red.
        ///   Angry - cooling off: a bath or shower (washing that's also comfy), reading, exercise, a nap: x1.4 (note
        ///           "Cooling off after that argument"); friendly socials x0.6 (x0.35 with the one they fell out with);
        ///           mean ones x0.5; romance x0.2.
        ///   Sad   - looking for comfort: a hug from family (a hug with someone of their own family): x1.6 (note "Feeling
        ///           down - a hug from Bella"); a friendly social with family or a friend (daily 40+): x1.4 (note "Feeling
        ///           down - time with Bella"); fun or something comfy (sofa, TV, a game - not housework): x1.2 (note
        ///           "Feeling down - something comforting"); mean socials x0.6. Romance is never made more appealing.
        /// </summary>
        private static int Mood(VMReduxBrainContext ctx)
        {
            if (ctx.Sim == null || ctx.VM == null || ctx.Target == null) return 1000;
            var memo = MemoOf(ctx);
            if (!memo.Angry && !memo.Sad) return 1000;
            if (VMReduxBrain.InRed(ctx.Sim)) return 1000;
            var other = Person(ctx);
            var info = InfoOf(ctx.Target, ctx.Entry, ctx.Name);
            S social = (other != null && info != null) ? info.Social : S.None;

            if (memo.Angry)
            {
                if (other != null)
                {
                    if ((social & S.Apology) != 0) return 1000;
                    if ((social & S.Romance) != 0) return 200;
                    if ((social & S.Mean) != 0) return 500;
                    if ((social & S.Friendly) != 0) return other.ObjectID == memo.AngryAt ? 350 : 600;
                    return 1000;
                }
                if (ctx.Target is VMAvatar) return 1000; // (a pet: left be)
                if (Calming(ctx, info))
                {
                    ctx.Note("Cooling off after that argument");
                    return 1400;
                }
                return 1000;
            }

            // sad
            if (other != null)
            {
                if ((social & S.Mean) != 0) return 600;
                if ((social & S.Friendly) == 0) return 1000; // (romance: as it is)
                bool family = SameFamily(ctx.Sim, other);
                if (family && (social & S.Hug) != 0 && Daily(ctx.Sim, other) > DislikeAt)
                {
                    ctx.Note(NamesOf(other).Hug);
                    return 1600;
                }
                int d = Daily(ctx.Sim, other);
                if ((family && d > DislikeAt) || d >= 40)
                {
                    ctx.Note(NamesOf(other).Down);
                    return 1400;
                }
                return 1000;
            }
            if (ctx.Target is VMAvatar) return 1000;
            if (ctx.Kind == VMReduxBrain.Kind.Chores) return 1000;
            if (ctx.Ad(VMMotive.Fun) > 0 || (ctx.Ad(VMMotive.Comfort) > 0 && (ctx.Kind == VMReduxBrain.Kind.Rest || ctx.Kind == VMReduxBrain.Kind.Screen)))
            {
                ctx.Note("Feeling down - something comforting");
                return 1200;
            }
            return 1000;
        }

        // (English names of reading - a book, a newspaper, a magazine)
        private static readonly string[] ReadWords = { "read", "study", "book", "newspaper", "magazine" };

        /// <summary>Something calming to do alone: a bath or shower, reading, exercise, a nap.</summary>
        private static bool Calming(VMReduxBrainContext ctx, EntryInfo info)
        {
            switch (ctx.Kind)
            {
                case VMReduxBrain.Kind.Bathroom: return ctx.Ad(VMMotive.Hygiene) > 0; // (a bath or a shower - not the toilet)
                case VMReduxBrain.Kind.Exercise: return true;
                case VMReduxBrain.Kind.Rest: return ctx.Ad(VMMotive.Energy) > 0;      // (a nap)
            }
            var n = ctx.Name;
            if (string.IsNullOrEmpty(n)) return false;
            for (int i = 0; i < ReadWords.Length; i++)
                if (n.IndexOf(ReadWords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return info != null && info.Skill == Body;
        }
    }
}
