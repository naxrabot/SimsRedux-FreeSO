using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: more to do on the computer, like The Sims 3 - Practice Writing, Write a Novel (in seven
    /// genres, kept from start to finish: "Continue Novel: <title>" picks it up again, and a finished one goes
    /// into the Sim's inventory to be sold), Surf the Web, Chat Online and Hack - plus the Writing skill they build.
    ///
    /// No game file is changed. When a computer is loaded, its pie menu (interaction table) gets the new
    /// entries in memory. Each one uses the computer's own "Play" routine for the Sim's part (walking
    /// over, sitting down, typing - the same animations), and this class adds what the new activity
    /// does, a game minute at a time, while the Sim is at the keyboard: skill, novel progress, money,
    /// social. Everything it keeps is stored on the Sim (person data), so it's saved with the house and
    /// every player in an online house works it out the same way.
    /// </summary>
    public static class ReduxComputer
    {
        // ---- where it's kept on each Sim: SimsRedux's own person data block (200-219), marked at 200 ----
        // It's saved with the house, kept in the neighbourhood file (NBRS keeps the marked block) and
        // travels with the Sim online, like the Sims 1 skills.
        private const int Mark = 200;
        /// <summary>The Writing skill, 0-1000 like the other skills (shown as 0-10).</summary>
        public const int WritingSkill = 201;
        /// <summary>How far through the current novel, 0-1000.</summary>
        public const int NovelProgress = 202;
        /// <summary>The current novel's genre (1-5), 0 = none started.</summary>
        public const int NovelGenre = 203;
        /// <summary>Novels finished.</summary>
        public const int NovelsWritten = 204;
        /// <summary>Hundredths of a skill point not yet added (so slow learning still adds up).</summary>
        private const int SkillCarry = 205;
        /// <summary>The last game minute that was counted (+1, so 0 means never).</summary>
        private const int LastMinute = 206;
        /// <summary>Hacking: minutes since the last pay-out.</summary>
        private const int HackMinutes = 207;
        // (208-209: ReduxSkillByDoing)
        /// <summary>The current novel's title (its seed + 1, 1-256), 0 = not picked yet.</summary>
        public const int NovelSeed = 210;
        // (211: kept free)
        /// <summary>The finished novels not sold yet, one a slot (0 = empty): genre &lt;&lt; 12 | quality &lt;&lt; 8 | title seed.</summary>
        public const int ShelfFirst = 212, ShelfLast = 219;

        /// <summary>
        /// Before the neighbourhood is saved: each Sim's block goes into their neighbourhood entry, so it's
        /// there next time they're loaded somewhere else and when they travel online.
        /// </summary>
        public static void CopyToNeighbourhood(VM vm)
        {
            try
            {
                var neigh = Content.Content.Get().Neighborhood;
                if (vm == null || neigh == null) return;
                foreach (var e in vm.Entities)
                {
                    var ava = e as VMAvatar;
                    if (ava == null || ava.GetPersonData((VMPersonDataVariable)Mark) != FSO.Files.Formats.IFF.Chunks.Neighbour.ReduxBlockMark) continue;
                    var nb = neigh.GetNeighborByID(ava.GetPersonData(VMPersonDataVariable.NeighborId));
                    if (nb?.PersonData == null) continue;
                    int end = FSO.Files.Formats.IFF.Chunks.Neighbour.ReduxBlockEnd;
                    if (nb.PersonData.Length <= end)
                    {
                        var bigger = new short[end + 1];
                        Array.Copy(nb.PersonData, bigger, nb.PersonData.Length);
                        nb.PersonData = bigger;
                    }
                    for (int i = Mark; i <= end; i++) nb.PersonData[i] = ava.GetPersonData((VMPersonDataVariable)i);
                }
            }
            catch (Exception ex) { Console.WriteLine("[SimsRedux] computer: couldn't copy the Writing skill to the neighbourhood: " + ex.Message); }
        }

        /// <summary>A Sim's Writing skill, 0-1000 (for the Job page).</summary>
        public static int Writing(VMAvatar ava) { return ava == null ? 0 : Get(ava, WritingSkill); }

        public enum Kind { None, PracticeWriting, WriteNovel, SurfWeb, ChatOnline, Hack, PlayTwistedMetal2 }

        // (the numbers are saved on the Sims: new genres only ever go on the end)
        public static readonly string[] Genres = { "", "Romance", "Sci-Fi", "Mystery", "Fantasy", "Children's", "Horror", "Comedy" };
        private static readonly float[] GenrePay = { 1f, 1.2f, 1f, 1.1f, 1f, 0.8f, 1.05f, 1f };
        /// <summary>The pie menu entry that picks up the novel on the go (shown as "Continue Novel: title").</summary>
        public const string ContinueNovelLabel = "Continue Novel";

        private class Entry { public Kind Kind; public int Genre; public uint PlayIndex; public string Label; }
        // per kind of computer: its new entries by pie menu number, and by name (a running action is
        // recognised by its name - it runs as the computer's own "Play", see Adjust)
        private static readonly ReduxWeakMap<TTAB, Dictionary<uint, Entry>> Patched = new ReduxWeakMap<TTAB, Dictionary<uint, Entry>>();
        private static readonly ReduxWeakMap<TTAB, Dictionary<string, Entry>> ByName = new ReduxWeakMap<TTAB, Dictionary<string, Entry>>();
        // each computer's own "Play" (its pie menu number), which is shown under Play Games
        private static readonly ReduxWeakMap<TTAB, uint> PlayIndex = new ReduxWeakMap<TTAB, uint>();
        /// <summary>The computer's own Play, as it shows in the Play Games menu.</summary>
        public const string ComputerGamesLabel = "Play Games/Computer Games";
        private static readonly object Lock = new object();

        /// <summary>Called as each object is made: a computer gets the new pie menu entries (once per kind of computer).</summary>
        public static void Patch(VMEntity ent)
        {
            try
            {
                var ttab = ent?.TreeTable;
                var ttas = ent?.TreeTableStrings;
                if (ttab == null || ttas == null || ent is VMAvatar) return;
                lock (Lock)
                {
                    if (Patched.ContainsKey(ttab)) return;
                    // a computer: "Play" plus a "Job" entry (the newspaper has no Play, arcade games no Find Job)
                    TTABInteraction play = null;
                    bool job = false;
                    foreach (var ia in ttab.Interactions)
                    {
                        var s = ttas.GetString((int)ia.TTAIndex) ?? "";
                        if (play == null && s.StartsWith("Play", StringComparison.OrdinalIgnoreCase)) play = ia;
                        if (s.IndexOf("Job", StringComparison.OrdinalIgnoreCase) >= 0) job = true;
                    }
                    var label = ent.Object?.OBJ?.ChunkLabel ?? "";
                    bool looksLike = label.IndexOf("omputer", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (play == null || !(job || looksLike)) { Patched[ttab] = null; return; }

                    var map = new Dictionary<uint, Entry>();
                    Add(ttab, ttas, play, map, "Practice Writing", Kind.PracticeWriting, 0);
                    for (int g = 1; g <= 5; g++)
                        Add(ttab, ttas, play, map, "Write a Novel/" + Genres[g], Kind.WriteNovel, g);
                    Add(ttab, ttas, play, map, "Surf the Web", Kind.SurfWeb, 0);
                    Add(ttab, ttas, play, map, "Chat Online", Kind.ChatOnline, 0);
                    Add(ttab, ttas, play, map, "Hack", Kind.Hack, 0);
                    // games are kept together under "Play Games" (more can be added the same way: "Play Games/<name>")
                    Add(ttab, ttas, play, map, "Play Games/Twisted Metal 2", Kind.PlayTwistedMetal2, 0);
                    // (added later: kept after the others so the older entries keep their numbers)
                    for (int g = 6; g < Genres.Length; g++)
                        Add(ttab, ttas, play, map, "Write a Novel/" + Genres[g], Kind.WriteNovel, g);
                    Add(ttab, ttas, play, map, ContinueNovelLabel, Kind.WriteNovel, 0); // (genre 0: the one on the go)
                    Patched[ttab] = map;
                    PlayIndex[ttab] = play.TTAIndex;
                    ByName[ttab] = map.Values.GroupBy(x => x.Label).ToDictionary(g => g.Key, g => g.First());
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] computer: couldn't add the new activities: " + e.Message);
            }
        }

        private static void Add(TTAB ttab, TTAs ttas, TTABInteraction play, Dictionary<uint, Entry> map, string label, Kind kind, int genre)
        {
            var index = AppendLabel(ttas, label);
            if (index < 0 || index > 255 || ttab.InteractionByIndex.ContainsKey((uint)index)) return; // (the pie menu numbers them 0-255)
            var ia = new TTABInteraction
            {
                ActionFunction = play.ActionFunction,
                TestFunction = 0, // (always offered; the computer's own routine copes with a broken one)
                MotiveEntries = new TTABMotiveEntry[play.MotiveEntries?.Length ?? 0], // (no autonomy: only when you ask)
                Flags = play.Flags,
                Flags2 = play.Flags2,
                TTAIndex = (uint)index,
                AttenuationCode = play.AttenuationCode,
                AttenuationValue = play.AttenuationValue,
                AutonomyThreshold = play.AutonomyThreshold,
                JoiningIndex = -1,
            };
            for (int i = 0; i < ia.MotiveEntries.Length; i++) ia.MotiveEntries[i] = new TTABMotiveEntry();
            ttab.InsertInteraction(ia, ttab.Interactions.Length);
            map[(uint)index] = new Entry { Kind = kind, Genre = genre, PlayIndex = play.TTAIndex, Label = label };
        }

        /// <summary>Adds a label at the end of the pie menu strings (every language they're in).</summary>
        private static int AppendLabel(STR s, string text)
        {
            int index = s.Length;
            foreach (var set in s.LanguageSets)
            {
                if (set == null || set.Strings.Length == 0) continue;
                var list = set.Strings.ToList();
                while (list.Count < index) list.Add(new STRItem { Value = "", Comment = "" });
                list.Insert(index, new STRItem { Value = text, Comment = "SimsRedux", LanguageCode = (list.Count > 0 ? list[0].LanguageCode : (byte)1) });
                set.Strings = list.ToArray();
            }
            return index;
        }

        /// <summary>
        /// When one of the new entries is chosen, the action is the computer's own "Play" in every way but
        /// its name: the computer's routine checks which of its interactions it's running, and an
        /// unknown one made the Sim stand up again after a few seconds.
        /// </summary>
        public static void Adjust(VMEntity obj, int interaction, VMQueuedAction action)
        {
            if (obj?.TreeTable == null || action == null || interaction < 0) return;
            Dictionary<uint, Entry> map;
            lock (Lock) { if (!Patched.TryGetValue(obj.TreeTable, out map) || map == null) return; }
            Entry e;
            if (!map.TryGetValue((uint)interaction, out e)) return;
            action.InteractionNumber = (int)e.PlayIndex;
            action.Name = e.Label;
        }

        // (which of the computers' own routines is "tweak play happy enough" is kept on the routine: VMRoutine.ReduxKind)

        /// <summary>
        /// Called as a routine is about to call one of its object's own routines. A computer's play loop
        /// asks "tweak play happy enough" after every round (it answers TRUE for "not yet, keep playing"),
        /// and stops as soon as the Sim's Fun reaches a goal worked out from their personality - which for
        /// most Sims is only a few game minutes away. Returning true here answers TRUE without asking,
        /// which keeps them going:
        ///   writing, a novel, chatting, hacking: until you stop them (or a need, the novel or getting caught does)
        ///   playing (Play, Twisted Metal 2, Surf the Web): until Fun is nearly full
        /// </summary>
        public static bool KeepGoing(VMThread thread, VMStackFrame frame, ushort opcode)
        {
            return Decide(thread, frame, opcode) > 0;
        }

        /// <summary>
        /// A need low enough that the Sim must stop: hungry, tired, needing the toilet or a wash, or
        /// lonely and unhappy (-100 is empty). Sims 1's own check only looks at Fun, and with the computer
        /// kept going they used to sit there until every bar was empty.
        /// </summary>
        public const short StopBelow = -35;

        public static bool NeedsSomething(VMAvatar ava)
        {
            return ava.GetMotiveData(VMMotive.Hunger) < StopBelow || ava.GetMotiveData(VMMotive.Energy) < StopBelow
                || ava.GetMotiveData(VMMotive.Bladder) < StopBelow || ava.GetMotiveData(VMMotive.Hygiene) < StopBelow - 15
                || ava.GetMotiveData(VMMotive.Social) < StopBelow - 25 || ava.GetMotiveData(VMMotive.Comfort) < StopBelow - 25;
        }

        /// <summary>1: keep going (answer TRUE); -1: stop now (answer FALSE); 0: let the game's own check decide.</summary>
        public static int Decide(VMThread thread, VMStackFrame frame, ushort opcode)
        {
            int r = DecideInner(thread, frame, opcode);
            return r;
        }

        private static int DecideInner(VMThread thread, VMStackFrame frame, ushort opcode)
        {
            try
            {
                var ava = frame?.Caller as VMAvatar;
                var callee = frame?.Callee;
                if (ava == null || callee?.TreeTable == null || callee is VMAvatar) return 0;
                // (the cheap check first: is the script being called "happy enough"? - see VMRoutine.ReduxKind)
                var routine = frame.ScopeResource?.GetRoutine(opcode) as VMRoutine;
                if (routine == null || routine.GetReduxKind() != VMRoutine.ReduxKindHappyEnough) return 0;
                lock (Lock)
                {
                    Dictionary<uint, Entry> map;
                    if (!Patched.TryGetValue(callee.TreeTable, out map) || map == null) return 0; // (only computers)
                }
                // (a need is getting low: they get up, whatever they're doing)
                if (NeedsSomething(ava)) return -1;

                int genre;
                VMQueuedAction action;
                var current = Current(ava, out genre, out action);
                if (action != null && action.NotifyIdle) return -1; // (cancelled: get up)
                switch (current)
                {
                    case Kind.PracticeWriting:
                    case Kind.WriteNovel:
                    case Kind.ChatOnline:
                    case Kind.Hack:
                        return 1;
                    default:
                        return ava.GetMotiveData(VMMotive.Fun) < 90 ? 1 : 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>
        /// The pie menu for an object: on a computer, its own "Play" moves into the Play Games menu (as
        /// Computer Games), next to Twisted Metal 2 and any games added later. "Continue Novel" shows the
        /// title of the novel the Sim has on the go, and isn't there when they have none. (Also used when
        /// an interaction is checked before it's queued, so it's only ever offered with a novel to continue.)
        /// </summary>
        public static void AdjustPie(VMEntity obj, List<VMPieMenuInteraction> pie, VMEntity caller = null)
        {
            try
            {
                if (pie == null || obj?.TreeTable == null || obj is VMAvatar) return;
                uint play;
                lock (Lock) { if (!PlayIndex.TryGetValue(obj.TreeTable, out play)) return; }
                foreach (var p in pie)
                {
                    if (p == null || p.Global || p.ID != play) continue;
                    if (p.Name != null && p.Name.Contains("/")) continue;
                    p.Name = ComputerGamesLabel;
                }
                if (pie.Any(p => p != null && !p.Global && p.Name == ContinueNovelLabel))
                {
                    var title = CurrentNovelTitle(caller as VMAvatar);
                    if (title == null) pie.RemoveAll(p => p != null && !p.Global && p.Name == ContinueNovelLabel);
                    else foreach (var p in pie) if (p != null && !p.Global && p.Name == ContinueNovelLabel) p.Name = ContinueNovelLabel + ": " + title;
                }
            }
            catch { }
        }

        /// <summary>What a Sim is doing at a computer right now (None if not one of the new activities).</summary>
        public static Kind Current(VMAvatar ava, out int genre, out VMQueuedAction action)
        {
            genre = 0;
            action = ava?.Thread?.ActiveAction;
            var callee = action?.Callee;
            if (callee == null || callee.TreeTable == null || action.Name == null) return Kind.None;
            Dictionary<string, Entry> map;
            lock (Lock) { if (!ByName.TryGetValue(callee.TreeTable, out map) || map == null) return Kind.None; }
            Entry e;
            if (!map.TryGetValue(action.Name, out e)) return Kind.None;
            genre = e.Genre;
            return e.Kind;
        }

        /// <summary>Every tick, for every Sim: the new activities do their part once a game minute.</summary>
        public static void Tick(VMAvatar ava)
        {
            if (ava?.Thread == null) return;
            int genre;
            VMQueuedAction action;
            var kind = Current(ava, out genre, out action);
            if (kind == Kind.None) return;
            // (cancelled: they're getting up - no more questions or skill while they do)
            if (action != null && action.NotifyIdle) return;
            var ctx = ava.Thread.Context;
            var clock = ctx?.Clock;
            if (clock == null) return;

            // only while actually at the keyboard (not walking over)
            if (!AtKeyboard(ava, action)) return;

            // the progress bar over their head (like studying): the next Writing level, or the novel
            if (kind == Kind.PracticeWriting) ShowProgress(ava, Get(ava, WritingSkill) >= 1000 ? 99 : Get(ava, WritingSkill) % 100);
            else if (kind == Kind.WriteNovel) ShowProgress(ava, (genre == 0 || Get(ava, NovelGenre) == genre) ? Get(ava, NovelProgress) / 10 : 0);

            // work, not play: the computer's Play routine stops once Fun is full, so while writing,
            // chatting or hacking Fun is held just below that and the Sim keeps at it until told to stop
            if (kind == Kind.PracticeWriting || kind == Kind.WriteNovel || kind == Kind.ChatOnline || kind == Kind.Hack)
            {
                var fun = ava.GetMotiveData(VMMotive.Fun);
                if (fun > 80) ava.SetMotiveData(VMMotive.Fun, 80);
            }

            int minute = clock.Hours * 60 + clock.Minutes + 1;
            int last = Get(ava, LastMinute);
            if (last == minute) return;
            Set(ava, LastMinute, minute);
            if (last == 0) return; // (the first minute just starts the count)

            int level = Get(ava, WritingSkill) / 100;
            switch (kind)
            {
                case Kind.PracticeWriting:
                    AddSkill(ava, SkillGain(level));
                    break;

                case Kind.WriteNovel:
                    WriteNovel(ava, action, genre, level, ctx);
                    break;

                case Kind.SurfWeb:
                    // (the computer's own routine gives the fun) - and reading up a little on everything
                    AddSkill(ava, SkillGain(level) / 6);
                    break;

                case Kind.ChatOnline:
                    {
                        var social = ava.GetMotiveData(VMMotive.Social);
                        if (social < 100) ava.SetMotiveData(VMMotive.Social, (short)Math.Min(100, social + 1));
                    }
                    break;

                case Kind.Hack:
                    {
                        var logic = ava.GetPersonData(VMPersonDataVariable.LogicSkill);
                        if (logic < 700)
                        {
                            Notice(ava, "Not Yet", ava.Name + " needs a Logic skill of 7 to hack. Practise with the chess set or telescope.");
                            ava.Thread.CancelAction(action.UID);
                            break;
                        }
                        int m = Get(ava, HackMinutes) + 1;
                        if (m < 30) { Set(ava, HackMinutes, m); break; }
                        Set(ava, HackMinutes, 0);
                        if (ctx.NextRandom(100) < 25)
                        {
                            int fine = 150 + (int)ctx.NextRandom(151);
                            GiveMoney(ava, -fine);
                            Notice(ava, "Caught!", ava.Name + " was caught hacking" + (IsMyFamily(ava) ? " and fined §" + fine + "." : "."));
                            ava.Thread.CancelAction(action.UID);
                        }
                        else
                        {
                            int pay = 50 + logic / 10 + (int)ctx.NextRandom(101);
                            GiveMoney(ava, pay);
                            if (IsMyFamily(ava)) Notice(ava, "Hacked!", ava.Name + " hacked into a bank and moved §" + pay + " across.");
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// A game minute of novel writing. "Write a Novel/genre" carries on with the novel on the go if it's
        /// that genre, or puts it aside and starts a new one; "Continue Novel" (genre 0) carries on with the
        /// one on the go. How fast depends on Writing and Creativity (and a little Logic); a finished novel
        /// goes on the Sim's shelf of unsold novels (their inventory), to be sold - nothing is paid until then.
        /// </summary>
        private static void WriteNovel(VMAvatar ava, VMQueuedAction action, int genre, int level, VMContext ctx)
        {
            int current = Get(ava, NovelGenre);
            if (genre <= 0)
            {
                genre = current;
                if (genre <= 0 || genre >= Genres.Length)
                {
                    Notice(ava, "No Novel", ava.Name + " has no novel on the go. Start one with Write a Novel.");
                    ava.Thread.CancelAction(action.UID);
                    return;
                }
            }
            if (genre >= Genres.Length) return;
            if (current != genre)
            {
                // a different story: the one on the go is put aside and this one started
                if (current > 0 && current < Genres.Length && Get(ava, NovelProgress) > 0)
                    Notice(ava, "A New Story", ava.Name + " put \"" + NovelTitle(current, Math.Max(0, Get(ava, NovelSeed) - 1)) + "\" aside and started a new " + Genres[genre] + " novel.");
                Set(ava, NovelGenre, genre);
                Set(ava, NovelProgress, 0);
                Set(ava, NovelSeed, 1 + (int)ctx.NextRandom(256));
                Notice(ava, "A New Novel", ava.Name + " started writing \"" + NovelTitle(genre, Get(ava, NovelSeed) - 1) + "\", a " + Genres[genre] + " novel.");
            }
            else if (Get(ava, NovelSeed) <= 0) Set(ava, NovelSeed, 1 + (int)ctx.NextRandom(256)); // (one started before novels had titles)

            AddSkill(ava, SkillGain(level) / 3);
            // about 9 game hours for a beginner, down to 2.5 for a master writer with lots of Creativity
            int creativity = Clamp(ava.GetPersonData(VMPersonDataVariable.CreativitySkill) / 100, 0, 10);
            int logic = Clamp(ava.GetPersonData(VMPersonDataVariable.LogicSkill) / 100, 0, 10);
            double hours = Math.Max(2.5, 9.0 - 0.35 * Math.Min(10, level) - 0.3 * creativity - 0.1 * logic);
            int step = Math.Max(1, (int)Math.Round(1000.0 / (60.0 * hours)));
            int progress = Get(ava, NovelProgress) + step;
            if (progress < 1000) { Set(ava, NovelProgress, progress); return; }

            // finished: how good it is comes from Creativity and Writing (and a little Logic), plus luck
            int quality = Clamp((creativity * 45 + Math.Min(10, level) * 40 + logic * 15) / 100 + (int)ctx.NextRandom(4), 0, 15);
            int seed = Clamp(Get(ava, NovelSeed) - 1, 0, 255);
            var title = NovelTitle(genre, seed);
            Set(ava, NovelsWritten, Math.Min(30000, Get(ava, NovelsWritten) + 1));
            Set(ava, NovelProgress, 0);
            Set(ava, NovelGenre, 0);
            Set(ava, NovelSeed, 0);
            int slot = -1;
            for (int i = ShelfFirst; i <= ShelfLast; i++) if (Get(ava, i) == 0) { slot = i; break; }
            if (slot < 0)
            {
                // (no room for another unsold one: this one sells straight away)
                int pay = NovelPrice(genre, quality);
                GiveMoney(ava, pay);
                Notice(ava, "Novel Finished!", ava.Name + " finished \"" + title + "\"" + (IsMyFamily(ava) ? " and sold it for §" + pay + "." : "."));
            }
            else
            {
                Set(ava, slot, (genre << 12) | (quality << 8) | seed);
                Notice(ava, "Novel Finished!", ava.Name + " finished \"" + title + "\", a " + Genres[genre] + " novel. It's in "
                    + ava.Name + "'s inventory - sell it from there for about §" + NovelPrice(genre, quality) + ".");
            }
            ava.Thread.CancelAction(action.UID);
        }

        // ---- novels: titles, prices and the unsold ones ----

        private static readonly string[][] TitleAdj =
        {
            new string[0],
            new[] { "Tender", "Secret", "Forbidden", "Endless", "Midnight", "Summer", "Stolen", "Sweet", "Wild", "Golden", "Reckless", "Silken" },
            new[] { "Final", "Silent", "Distant", "Quantum", "Neon", "Frozen", "Hollow", "Infinite", "Crimson", "Lost", "Electric", "Fallen" },
            new[] { "Silent", "Missing", "Last", "Crooked", "Hidden", "Poisoned", "Locked", "Broken", "Velvet", "Twelfth", "Shadowed", "Quiet" },
            new[] { "Dragon's", "Emerald", "Sorcerer's", "Ancient", "Shattered", "Enchanted", "Silver", "Iron", "Wandering", "Thorned", "Moonlit", "Seventh" },
            new[] { "Very Hungry", "Brave Little", "Sleepy", "Silly", "Magic", "Giant", "Tiny", "Curious", "Grumpy", "Happy", "Purple", "Lost" },
            new[] { "Crawling", "Hungry", "Bleeding", "Whispering", "Rotting", "Faceless", "Cursed", "Restless", "Grinning", "Drowned", "Buried", "Hollow" },
            new[] { "Accidental", "Awkward", "Unlikely", "Hopeless", "Ridiculous", "Totally Normal", "Slightly Haunted", "Overdue", "Misplaced", "Fabulous", "Unpaid", "Second-Hand" },
        };
        private static readonly string[][] TitleNoun =
        {
            new string[0],
            new[] { "Kiss", "Heart", "Promise", "Embrace", "Rose", "Longing", "Desire", "Vow", "Waltz", "Affair", "Letter", "Garden" },
            new[] { "Frontier", "Signal", "Colony", "Machine", "Star", "Horizon", "Android", "Orbit", "Nebula", "Protocol", "Moon", "Fleet" },
            new[] { "Witness", "Key", "Alibi", "Clue", "Verdict", "Butler", "Inheritance", "Room", "Shadow", "Confession", "Diamond", "Stranger" },
            new[] { "Crown", "Sword", "Kingdom", "Prophecy", "Tower", "Quest", "Throne", "Grimoire", "Forest", "Oath", "Realm", "Heir" },
            new[] { "Bear", "Dragon", "Caterpillar", "Robot", "Pony", "Llama", "Puppy", "Tugboat", "Monster", "Penguin", "Treehouse", "Kitten" },
            new[] { "House", "Thing", "Doll", "Hunger", "Cellar", "Visitor", "Night", "Grave", "Mirror", "Asylum", "Lake", "Attic" },
            new[] { "Wedding", "Llama", "Vacation", "Plumber", "Neighbor", "Diet", "Mustache", "Landlord", "Haircut", "Barbecue", "Pizza", "Gnome" },
        };
        private static readonly string[][] TitleTail =
        {
            new string[0],
            new[] { "in Paris", "by Moonlight", "at Dawn", "Under the Stars", "on Sim Lane", "After Dark" },
            new[] { "of Andromeda", "Beyond Tomorrow", "in Zero Gravity", "of the Void", "on Mars", "from Planet X" },
            new[] { "at Midnight", "on the Night Train", "in the Library", "at Pleasantview Manor", "of the Old Pier", "in Room 13" },
            new[] { "of the North", "of Ashes", "Beyond the Mountains", "of the Elves", "in the Mist", "of Seven Kingdoms" },
            new[] { "Goes to School", "Finds a Friend", "Learns to Share", "Saves the Day", "Can't Sleep", "Goes to the Moon" },
            new[] { "Beneath the Floor", "in the Dark", "Next Door", "That Wouldn't Die", "at the End of the Road", "Under the Bed" },
            new[] { "Goes Wrong", "Strikes Again", "for Dummies", "Ruins Everything", "on Vacation", "and Other Disasters" },
        };

        /// <summary>A novel's title from its genre and title seed (0-255): the same seed always gives the same title.</summary>
        public static string NovelTitle(int genre, int seed)
        {
            if (genre <= 0 || genre >= TitleAdj.Length) return "Untitled";
            seed &= 255;
            var adj = TitleAdj[genre]; var noun = TitleNoun[genre]; var tail = TitleTail[genre];
            int h = seed * 2654435 + 12345;
            string a = adj[(seed + h / 7 % 13) % adj.Length], n = noun[(seed * 7 + 3) % noun.Length], t = tail[(seed / 3 + seed * 5) % tail.Length];
            switch ((seed / 5) % 4)
            {
                case 0: return "The " + a + " " + n;
                case 1: return "The " + n + " " + t;
                case 2: return a + " " + n + " " + t;
                default: return "The " + a + " " + n + " " + t;
            }
        }

        /// <summary>What a finished novel sells for: its quality (0-15) and genre.</summary>
        public static int NovelPrice(int genre, int quality)
        {
            if (genre <= 0 || genre >= GenrePay.Length) return 0;
            return (int)((150 + Clamp(quality, 0, 15) * 95) * GenrePay[genre]);
        }

        /// <summary>The title of the novel a Sim has on the go, or null if none.</summary>
        public static string CurrentNovelTitle(VMAvatar ava)
        {
            if (ava == null) return null;
            int genre = Get(ava, NovelGenre);
            if (genre <= 0 || genre >= Genres.Length) return null;
            int seed = Get(ava, NovelSeed);
            // (one started before novels had titles gets its title as soon as they write on it again)
            if (seed <= 0) return "a " + Genres[genre] + " novel (" + Get(ava, NovelProgress) / 10 + "%)";
            return NovelTitle(genre, seed - 1) + " (" + Get(ava, NovelProgress) / 10 + "%)";
        }

        /// <summary>A finished novel not sold yet.</summary>
        public class Novel
        {
            public int Slot, Genre, Quality, Seed, Price;
            public string Title;
            public string GenreName { get { return Genres[Genre]; } }
        }

        /// <summary>A Sim's finished novels not sold yet (shown in their inventory).</summary>
        public static List<Novel> Novels(VMAvatar ava)
        {
            var list = new List<Novel>();
            if (ava == null) return list;
            for (int i = ShelfFirst; i <= ShelfLast; i++)
            {
                var n = ReadNovel(ava, i);
                if (n != null) list.Add(n);
            }
            return list;
        }

        private static Novel ReadNovel(VMAvatar ava, int slot)
        {
            if (slot < ShelfFirst || slot > ShelfLast) return null;
            int v = Get(ava, slot);
            if (v <= 0) return null;
            int genre = (v >> 12) & 7, quality = (v >> 8) & 15, seed = v & 255;
            if (genre <= 0 || genre >= Genres.Length) return null;
            return new Novel { Slot = slot, Genre = genre, Quality = quality, Seed = seed, Title = NovelTitle(genre, seed), Price = NovelPrice(genre, quality) };
        }

        /// <summary>Can this Sim sell the novel in this slot? (VMNetReduxNovelCmd)</summary>
        public static bool CanSellNovel(VMAvatar ava, int slot)
        {
            return ava != null && !ava.Dead && ReadNovel(ava, slot) != null;
        }

        /// <summary>Sells a finished novel from a Sim's inventory: the family gets its price. (VMNetReduxNovelCmd, run by every player's game alike.)</summary>
        public static bool SellNovel(VMAvatar ava, int slot)
        {
            var n = ReadNovel(ava, slot);
            if (n == null) return false;
            Set(ava, slot, 0);
            GiveMoney(ava, n.Price);
            Notice(ava, "Novel Sold", ava.Name + " sold \"" + n.Title + "\"" + (IsMyFamily(ava) ? " for §" + n.Price + "." : "."));
            return true;
        }

        /// <summary>
        /// Writing, a novel, chatting and hacking are typed: these start from the computer's own Play,
        /// whose animations reach for the joystick and play a game - swapped here for the keyboard ones the
        /// computer uses for Study ("...-computer-getkeyboard", "...-computer-type").
        /// </summary>
        public static FSO.Vitaboy.Animation TypingAnimation(VMAvatar ava, FSO.Vitaboy.Animation anim)
        {
            try
            {
                if (anim?.Name == null || ava == null) return anim;
                var name = anim.Name.ToLowerInvariant();
                string swap = null;
                if (name.Contains("computer-playgame")) swap = "computer-playgame";
                else if (name.Contains("computer-getjoystick")) swap = "computer-getjoystick";
                if (swap == null) return anim;
                int genre; VMQueuedAction action;
                var kind = Current(ava, out genre, out action);
                if (kind != Kind.PracticeWriting && kind != Kind.WriteNovel && kind != Kind.ChatOnline && kind != Kind.Hack) return anim;
                var to = swap == "computer-playgame" ? "computer-type" : "computer-getkeyboard";
                var start = name.IndexOf(swap);
                var newName = anim.Name.Substring(0, start) + to + anim.Name.Substring(start + swap.Length);
                var typed = FSO.Content.Content.Get().AvatarAnimations.Get(newName + ".anim");
                return typed ?? anim;
            }
            catch { return anim; }
        }

        /// <summary>Is the Sim sitting at the computer for this action (not still walking over)?</summary>
        public static bool AtKeyboard(VMAvatar ava, VMQueuedAction action)
        {
            if (ava?.Thread == null || action?.Callee == null) return false;
            var c = action.Callee.Position;
            var p = ava.Position;
            if (c.Level != p.Level || Math.Abs(c.x - p.x) > 28 || Math.Abs(c.y - p.y) > 28) return false;
            return !ava.Thread.Stack.Any(f => f is VMRoutingFrame);
        }

        /// <summary>
        /// The computers someone is playing Twisted Metal 2 on right now (for the picture on the monitor).
        /// </summary>
        public static List<VMEntity> PlayingTwistedMetal(VM vm)
        {
            var list = new List<VMEntity>();
            // (SimsRedux: only the Sims, not every object on the lot)
            var avatars = vm?.Context?.ObjectQueries?.Avatars;
            if (avatars == null) return list;
            for (int i = 0; i < avatars.Count; i++)
            {
                var ava = avatars[i] as VMAvatar;
                if (ava == null) continue;
                int genre;
                VMQueuedAction action;
                if (Current(ava, out genre, out action) == Kind.PlayTwistedMetal2 && AtKeyboard(ava, action) && !list.Contains(action.Callee))
                    list.Add(action.Callee);
            }
            return list;
        }

        /// <summary>
        /// The Sims 1 progress bar over a Sim's head (the one studying shows), at 0-99 percent. Kept up
        /// while they're at it and gone a couple of seconds after they stop. Anything else over their
        /// head (a thought, a need) is left alone.
        /// </summary>
        private static void ShowProgress(VMAvatar ava, int percent)
        {
            try
            {
                var vm = ava.Thread?.Context?.VM;
                if (vm?.Headline == null) return;
                var current = ava.Headline;
                if (current != null && current.Operand?.Group != VMSetBalloonHeadlineOperandGroup.Progress) return;
                int index = Clamp(percent / 10, 0, 9);
                if (current != null && current.Index == index && ava.HeadlineRenderer != null)
                {
                    current.Duration = 60;
                    return;
                }
                ava.HeadlineRenderer?.Dispose();
                var op = new VMSetBalloonHeadlineOperand
                {
                    Group = VMSetBalloonHeadlineOperandGroup.Progress,
                    Index = (sbyte)index,
                    Duration = 60,
                    Type = 255, // (no balloon behind it)
                };
                ava.Headline = new VMRuntimeHeadline(op, ava, null, (sbyte)index) { Duration = 60 };
                ava.HeadlineRenderer = vm.Headline.Get(ava.Headline);
            }
            catch { }
        }

        /// <summary>Hundredths of a Writing point a minute: a level takes an hour at first, longer as it rises.</summary>
        private static int SkillGain(int level)
        {
            return (int)(10000.0 / (60.0 * (1.0 + Math.Min(10, level) / 4.0)));
        }

        private static void AddSkill(VMAvatar ava, int hundredths)
        {
            int skill = Get(ava, WritingSkill);
            if (skill >= 1000) return;
            int carry = Get(ava, SkillCarry) + hundredths;
            int whole = carry / 100;
            Set(ava, SkillCarry, carry % 100);
            if (whole <= 0) return;
            int next = Math.Min(1000, skill + whole);
            Set(ava, WritingSkill, next);
            if (next / 100 > skill / 100)
                Notice(ava, "Skill Up!", ava.Name + " is now a level " + (next / 100) + " writer.");
        }

        internal static bool IsMyFamily(VMAvatar ava)
        {
            return IsHouseholdSim(ava.Thread.Context.VM, ava);
        }

        /// <summary>
        /// SimsRedux: is this Sim one of the family living on this lot? A guest player's Sim (and a townie
        /// brought over) is given the lot family's number so the game treats them as allowed in, but is marked
        /// FSOExcludeUserSave - they are not the household (not paid, not picking chores, not the brain's).
        /// </summary>
        public static bool IsHouseholdSim(VM vm, VMAvatar ava)
        {
            if (vm == null || ava == null) return false;
            var fam = vm.TS1State?.CurrentFamily;
            return fam != null && ava.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == fam.ChunkID
                && (ava.GetValue(VMStackObjectVariable.MovementFlags) & (short)VMMovementFlags.FSOExcludeUserSave) == 0;
        }

        private static void GiveMoney(VMAvatar ava, int amount)
        {
            if (!IsMyFamily(ava)) return; // (only the family who lives here earns or pays)
            var fam = ava.Thread.Context.VM.TS1State.CurrentFamily;
            fam.Budget = Math.Max(0, fam.Budget + amount);
        }

        internal static void Notice(VMAvatar ava, string title, string message)
        {
            try
            {
                ava.Thread.Context.VM.SignalDialog(new VMDialogInfo { Block = false, Caller = ava, Icon = ava, Title = title, Message = message, Yes = "OK" });
            }
            catch { }
        }

        private static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
        internal static int Get(VMAvatar ava, int slot)
        {
            // (nothing until the block has been marked as ours)
            if (ava.GetPersonData((VMPersonDataVariable)Mark) != FSO.Files.Formats.IFF.Chunks.Neighbour.ReduxBlockMark) return 0;
            return ava.GetPersonData((VMPersonDataVariable)slot);
        }
        internal static void Set(VMAvatar ava, int slot, int v)
        {
            if (ava.GetPersonData((VMPersonDataVariable)Mark) != FSO.Files.Formats.IFF.Chunks.Neighbour.ReduxBlockMark)
            {
                for (int i = Mark + 1; i <= FSO.Files.Formats.IFF.Chunks.Neighbour.ReduxBlockEnd; i++) ava.SetPersonData((VMPersonDataVariable)i, 0);
                ava.SetPersonData((VMPersonDataVariable)Mark, FSO.Files.Formats.IFF.Chunks.Neighbour.ReduxBlockMark);
            }
            ava.SetPersonData((VMPersonDataVariable)slot, (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v)));
        }
    }
}
