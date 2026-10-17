using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: more to do on the computer, like The Sims 3 - Practice Writing, Write a Novel (in five
    /// genres), Surf the Web, Chat Online and Hack - plus the Writing skill they build.
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
        // ---- where it's kept on each Sim: SimsRedux's own person data block (200-209), marked at 200 ----
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

        public static readonly string[] Genres = { "", "Romance", "Sci-Fi", "Mystery", "Fantasy", "Children's" };
        private static readonly float[] GenrePay = { 1f, 1.2f, 1f, 1.1f, 1f, 0.8f };

        private class Entry { public Kind Kind; public int Genre; }
        private static readonly Dictionary<TTAB, Dictionary<uint, Entry>> Patched = new Dictionary<TTAB, Dictionary<uint, Entry>>();
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
                    for (int g = 1; g < Genres.Length; g++)
                        Add(ttab, ttas, play, map, "Write a Novel/" + Genres[g], Kind.WriteNovel, g);
                    Add(ttab, ttas, play, map, "Surf the Web", Kind.SurfWeb, 0);
                    Add(ttab, ttas, play, map, "Chat Online", Kind.ChatOnline, 0);
                    Add(ttab, ttas, play, map, "Hack", Kind.Hack, 0);
                    Add(ttab, ttas, play, map, "Play Twisted Metal 2", Kind.PlayTwistedMetal2, 0);
                    Patched[ttab] = map;
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
            map[(uint)index] = new Entry { Kind = kind, Genre = genre };
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

        /// <summary>What a Sim is doing at a computer right now (None if not one of the new activities).</summary>
        public static Kind Current(VMAvatar ava, out int genre, out VMQueuedAction action)
        {
            genre = 0;
            action = ava?.Thread?.ActiveAction;
            var callee = action?.Callee;
            if (callee == null || callee.TreeTable == null || action.InteractionNumber < 0) return Kind.None;
            Dictionary<uint, Entry> map;
            lock (Lock) { if (!Patched.TryGetValue(callee.TreeTable, out map) || map == null) return Kind.None; }
            Entry e;
            if (!map.TryGetValue((uint)action.InteractionNumber, out e)) return Kind.None;
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
            var ctx = ava.Thread.Context;
            var clock = ctx?.Clock;
            if (clock == null) return;

            // only while actually at the keyboard (not walking over)
            if (!AtKeyboard(ava, action)) return;

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
                    {
                        if (Get(ava, NovelGenre) != genre)
                        {
                            // a different story: the one on the go is put aside and this one started
                            if (Get(ava, NovelGenre) != 0 && Get(ava, NovelProgress) > 0)
                                Notice(ava, "A New Story", ava.Name + " put the " + Genres[Clamp(Get(ava, NovelGenre), 0, 5)] + " novel aside and started a " + Genres[genre] + " one.");
                            Set(ava, NovelGenre, genre);
                            Set(ava, NovelProgress, 0);
                        }
                        AddSkill(ava, SkillGain(level) / 3);
                        // about 8 hours for a beginner, 3 for a master
                        int step = Math.Max(1, (int)Math.Round(1000.0 / (60.0 * (8.0 - 0.5 * Math.Min(10, level)))));
                        int progress = Get(ava, NovelProgress) + step;
                        if (progress >= 1000)
                        {
                            int pay = (int)((200 + level * 120) * GenrePay[genre]) + (int)ctx.NextRandom(101);
                            GiveMoney(ava, pay);
                            Set(ava, NovelsWritten, Math.Min(30000, Get(ava, NovelsWritten) + 1));
                            Set(ava, NovelProgress, 0);
                            Set(ava, NovelGenre, 0);
                            Notice(ava, "Novel Finished!", ava.Name + " finished a " + Genres[genre] + " novel" +
                                (IsMyFamily(ava) ? " and sold it for §" + pay + "." : "."));
                            ava.Thread.CancelAction(action.UID);
                        }
                        else Set(ava, NovelProgress, progress);
                    }
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
            if (vm == null) return list;
            foreach (var e in vm.Entities)
            {
                var ava = e as VMAvatar;
                if (ava == null) continue;
                int genre;
                VMQueuedAction action;
                if (Current(ava, out genre, out action) == Kind.PlayTwistedMetal2 && AtKeyboard(ava, action) && !list.Contains(action.Callee))
                    list.Add(action.Callee);
            }
            return list;
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

        private static bool IsMyFamily(VMAvatar ava)
        {
            var fam = ava.Thread.Context.VM.TS1State?.CurrentFamily;
            return fam != null && ava.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == fam.ChunkID;
        }

        private static void GiveMoney(VMAvatar ava, int amount)
        {
            if (!IsMyFamily(ava)) return; // (only the family who lives here earns or pays)
            var fam = ava.Thread.Context.VM.TS1State.CurrentFamily;
            fam.Budget = Math.Max(0, fam.Budget + amount);
        }

        private static void Notice(VMAvatar ava, string title, string message)
        {
            try
            {
                ava.Thread.Context.VM.SignalDialog(new VMDialogInfo { Block = false, Caller = ava, Icon = ava, Title = title, Message = message, Yes = "OK" });
            }
            catch { }
        }

        private static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
        private static int Get(VMAvatar ava, int slot)
        {
            // (nothing until the block has been marked as ours)
            if (ava.GetPersonData((VMPersonDataVariable)Mark) != FSO.Files.Formats.IFF.Chunks.Neighbour.ReduxBlockMark) return 0;
            return ava.GetPersonData((VMPersonDataVariable)slot);
        }
        private static void Set(VMAvatar ava, int slot, int v)
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
