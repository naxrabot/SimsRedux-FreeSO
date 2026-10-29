using System;
using System.Collections.Generic;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.4 (Part B #9): Adaptive Sims, the "Sim brain" - the lot's side. Simitone's Utils/ReduxSimBrain.cs is the
    /// game's side (the side file, the shipped baseline, learning overnight, free will, "Why I'm doing this"), and
    /// Utils/ReduxBrainTrainer.cs trains the baseline in the headless test (the soak bot, -train).
    ///
    /// WHAT THE BRAIN IS: a small table of learned weights per Sim, applied on top of everything VMFindBestAction already does
    /// (Sims 1's own advertisement scores, the Smarter / Smartest rules, beds, traits). It's kept on the Sim as 36 small numbers
    /// ("cells", -8..7 each, 4 bits) packed into person attributes 121-129, in two parts:
    ///   cells  0-19  object kind x time of day: 10 kinds (food, rest, bathroom, screen, play, social, skill, exercise, chores,
    ///                other) x day (6:00-21:59) / night (22:00-5:59) - "this Sim likes TV in the evening, cooks less at night";
    ///   cells 20-35  need x urgency: 8 needs (hunger, comfort, hygiene, bladder, energy, fun, social, room) x fine (20 and
    ///                up) / low (under 20) - "this Sim deals with a low bladder at once, doesn't eat when full".
    /// A choice's weight = kind cell x (the need cells of the needs it advertises, weighted by how much it gives each), as a
    /// multiplier: a cell of 0 is x1, every step about 9% (Mul: 0.5 at -8, 1.83 at 7). All in whole numbers (per mille), so
    /// every machine gets exactly the same answer. Attributes 121-129 all 0 = no brain = exactly the Smartest Sim.
    /// Attribute 120: the Sim's free will (bits 0-2: FreeWill) and the play style the brain learned (bits 4-5: Style).
    ///
    /// DETERMINISM: the brain only ever reaches the lot through VMNetReduxBrainCmd (VM command 86), sent by the game that
    /// owns the Sim (the lot's game for its own family and townies, a visiting player's game for their own Sim - never read
    /// from anyone's disk inside the lot). Attributes are saved with the lot and sent to every player joining, so every game
    /// on a visit scores every choice the same. Learning is never done here: it's done by the owning game, overnight, while
    /// the Sim sleeps (outside the lot's ticks), and the new numbers come back as a command.
    ///
    /// FACTOR API (for the behaviour extras, B2): AddFactor("id", ctx => 1.2f) or AddFactorPerMille("id", ctx => 1200)
    /// registers one more multiplier on every choice of every Sim with an Adaptive brain. Rules: read only the lot (ctx has the
    /// VM, the Sim, the choice, its kind, needs, clock, distance), never this game's settings, disk or screen; never change
    /// anything; be quick (it runs for every choice of every Sim). The result is clamped to x0.05-x4 and a factor that throws
    /// counts as x1. ctx.Note("...") gives the "Why I'm doing this" reason when it's the strongest one. Register once at start
    /// up, unconditionally (the same in every game of the same build) - see FACTOR DETERMINISM below: the set locks once a lot
    /// is played, factors combine in id order, and their hash is checked when a visitor joins. ctx.Name is the English name
    /// (never the player's language); KindOf is language-independent.
    /// </summary>
    public static class VMReduxBrain
    {
        /// <summary>18.5 (B5a): the behaviour factors register here, once, before anything reads the factor set - the same in every
        /// game of the same build (see FACTOR DETERMINISM).</summary>
        static VMReduxBrain() { VMReduxBrainFactors.RegisterAll(); }

        public const int FreeWillAttr = 120, FirstWeightAttr = 121, WeightAttrs = 9, Cells = 36;
        public const int KindCount = 10, NeedCount = 8, NeedCellsStart = 20;
        /// <summary>A need under this counts as "low" for the need cells.</summary>
        public const int LowBelow = 20;
        /// <summary>The smallest and biggest cell value (4 bits, signed).</summary>
        public const int MinCell = -8, MaxCell = 7;

        public enum Kind : byte { Food = 0, Rest = 1, Bathroom = 2, Screen = 3, Play = 4, Social = 5, Skill = 6, Exercise = 7, Chores = 8, Other = 9 }
        public static readonly string[] KindIds = { "food", "rest", "bathroom", "screen", "play", "social", "skill", "exercise", "chores", "other" };
        public static readonly VMMotive[] Needs = { VMMotive.Hunger, VMMotive.Comfort, VMMotive.Hygiene, VMMotive.Bladder, VMMotive.Energy, VMMotive.Fun, VMMotive.Social, VMMotive.Room };
        public static readonly string[] NeedIds = { "hunger", "comfort", "hygiene", "bladder", "energy", "fun", "social", "room" };

        /// <summary>How often a Sim acts on their own (the player's choice per Sim). 0 = Normal, so a Sim without the attribute is Normal.</summary>
        public enum FreeWill : byte { Normal = 0, Off = 1, NeedsOnly = 2, High = 3 }
        /// <summary>What the brain learned about how the Sim is played (the owning game works it out overnight).</summary>
        public enum Style : byte { Usual = 0, LeansOnPlayer = 1, SelfReliant = 2 }

        /// <summary>The multiplier (per mille) for each cell value -8..7 (index value + 8): 2^(value/8).</summary>
        public static readonly int[] Mul = { 500, 545, 595, 648, 707, 771, 841, 917, 1000, 1091, 1189, 1297, 1414, 1542, 1682, 1834 };

        public static int KindCell(int kind, bool night) { return kind * 2 + (night ? 1 : 0); }
        public static int NeedCell(int need, bool low) { return NeedCellsStart + need * 2 + (low ? 1 : 0); }
        public static bool Night(int hour) { return hour >= 22 || hour < 6; }

        /// <summary>A cell's id as the baseline file and the side file write it: "kind.food.day", "need.bladder.low".</summary>
        public static string CellId(int cell)
        {
            if (cell < 0 || cell >= Cells) return null;
            if (cell < NeedCellsStart) return "kind." + KindIds[cell / 2] + ((cell & 1) == 0 ? ".day" : ".night");
            int n = cell - NeedCellsStart;
            return "need." + NeedIds[n / 2] + ((n & 1) == 0 ? ".fine" : ".low");
        }

        public static int CellOf(string id)
        {
            for (int i = 0; i < Cells; i++) if (string.Equals(CellId(i), id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public static int NeedIndex(VMMotive m)
        {
            for (int i = 0; i < Needs.Length; i++) if (Needs[i] == m) return i;
            return -1;
        }

        // ------------------------------------------------------------------ on the Sim (attributes 120-129)

        /// <summary>36 cells (-8..7) into the 9 attribute values.</summary>
        public static short[] Pack(sbyte[] cells)
        {
            var r = new short[WeightAttrs];
            if (cells == null) return r;
            for (int i = 0; i < Cells && i < cells.Length; i++)
            {
                int v = Math.Max(MinCell, Math.Min(MaxCell, (int)cells[i])) & 0xF;
                r[i / 4] = (short)(r[i / 4] | (v << ((i % 4) * 4)));
            }
            return r;
        }

        public static sbyte[] Unpack(short[] packed)
        {
            var r = new sbyte[Cells];
            if (packed == null) return r;
            for (int i = 0; i < Cells; i++)
            {
                if (i / 4 >= packed.Length) break;
                int n = (packed[i / 4] >> ((i % 4) * 4)) & 0xF;
                r[i] = (sbyte)(n >= 8 ? n - 16 : n);
            }
            return r;
        }

        public static int Cell(VMAvatar a, int cell)
        {
            if (a == null || cell < 0 || cell >= Cells) return 0;
            int n = (a.GetAttribute(FirstWeightAttr + cell / 4) >> ((cell % 4) * 4)) & 0xF;
            return n >= 8 ? n - 16 : n;
        }

        public static sbyte[] CellsOf(VMAvatar a)
        {
            var r = new sbyte[Cells];
            for (int i = 0; i < Cells; i++) r[i] = (sbyte)Cell(a, i);
            return r;
        }

        /// <summary>Anything learned on this Sim (any cell not 0).</summary>
        public static bool HasBrain(VMAvatar a)
        {
            if (a == null || a.IsPet) return false;
            for (int i = 0; i < WeightAttrs; i++) if (a.GetAttribute(FirstWeightAttr + i) != 0) return true;
            return false;
        }

        public static FreeWill FreeWillOf(VMAvatar a)
        {
            if (a == null || a.IsPet) return FreeWill.Normal;
            int v = a.GetAttribute(FreeWillAttr) & 7;
            return v <= 3 ? (FreeWill)v : FreeWill.Normal;
        }

        public static Style StyleOf(VMAvatar a)
        {
            if (a == null || a.IsPet) return Style.Usual;
            int v = (a.GetAttribute(FreeWillAttr) >> 4) & 3;
            return v <= 2 ? (Style)v : Style.Usual;
        }

        public static short Settings(FreeWill fw, Style style) { return (short)(((int)fw & 7) | (((int)style & 3) << 4)); }

        /// <summary>Is this already on the Sim? (So the game doesn't send the same brain again and again.)</summary>
        public static bool Same(VMAvatar a, short[] packed, short settings)
        {
            if (a == null) return false;
            for (int i = 0; i < WeightAttrs; i++)
                if (a.GetAttribute(FirstWeightAttr + i) != ((packed != null && i < packed.Length) ? packed[i] : (short)0)) return false;
            return a.GetAttribute(FreeWillAttr) == Clean(settings);
        }

        private static short Clean(short settings)
        {
            int fw = settings & 7, st = (settings >> 4) & 3;
            if (fw > 3) fw = 0;
            if (st > 2) st = 0;
            return (short)(fw | (st << 4));
        }

        /// <summary>Puts a brain and free will on the Sim (only VMNetReduxBrainCmd calls this).</summary>
        public static void Set(VMAvatar a, short[] packed, short settings)
        {
            if (a == null || a.IsPet) return;
            for (int i = 0; i < WeightAttrs; i++) a.SetAttribute(FirstWeightAttr + i, (packed != null && i < packed.Length) ? packed[i] : (short)0);
            a.SetAttribute(FreeWillAttr, Clean(settings));
        }

        /// <summary>Only the free will (the rest of attribute 120, the learned style, is kept). VMNetReduxBrainCmd only.</summary>
        public static void SetFreeWill(VMAvatar a, FreeWill fw)
        {
            if (a == null || a.IsPet) return;
            a.SetAttribute(FreeWillAttr, Settings(fw, StyleOf(a)));
        }

        // ------------------------------------------------------------------ what kind of choice it is (language-independent)
        //
        // LOCKSTEP: the kind decides the weight, so it must come out the same on every game on a visit, whatever language each
        // player's game is in. So it NEVER uses the name the pie menu shows (first.Name - the player's language). Only:
        //   1. the choice's ENGLISH name, read from the object's own strings as English US and only when the file tags its
        //      strings with their language (STR.TaggedEnglish - formats -3/-4; null for untagged files, whose one set of strings
        //      could be in any language, so those are never used);
        //   2. the skill the choice builds (the TTAB entry's personality modifiers - numbers in the object file);
        //   3. the need it advertises most (TTAB motive ads plus the check tree's changes - the lot's own numbers), with the
        //      object's Buy-mode category (OBJD FunctionFlags: Electronics) telling TV/stereo/computer fun from other fun.
        // 1 and 2 belong to the TTAB entry itself and are worked out once per entry and kept - including "no keyword".

        /// <summary>"No kind yet" in the per-entry cache.</summary>
        private const Kind NoKind = (Kind)255;
        private sealed class KindBox { public Kind K; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TTABInteraction, KindBox> EntryKinds
            = new System.Runtime.CompilerServices.ConditionalWeakTable<TTABInteraction, KindBox>();

        // (word lists as shared arrays - no params array per check)
        private static readonly string[] FoodWords = { "cook", "meal", "snack", "breakfast", "lunch", "dinner", "serve", "grill", "leftover", "drink", "coffee", "espresso" };
        private static readonly string[] ChoreWords = { "clean", "mop", "empty trash", "take out trash", "pay bills", "repair", "water", "tidy", "scrub", "wash dish", "unclog", "make bed" };
        private static readonly string[] ExerciseWords = { "work out", "workout", "exercise", "jog", "swim", "dive", "aerobic", "lift weight", "basketball", "jump", "dance" };
        private static readonly string[] SkillWords = { "study", "read", "practice", "practise", "paint", "sculpt", "write", "piano", "guitar", "chess", "telescope", "stargaze", "research" };
        private static readonly string[] ScreenWords = { "watch", "tv", "television", "radio", "stereo", "listen", "turn on" };
        private static readonly string[] PlayWords = { "play", "game", "pinball", "arcade", "toy", "joystick", "puzzle", "train set", "doll" };
        private static readonly string[] BathroomWords = { "use toilet", "toilet", "shower", "bath", "wash hands", "brush", "sponge", "pee" };
        private static readonly string[] RestWords = { "relax", "lounge", "recline" };

        private static bool Has(string n, string[] words)
        {
            for (int i = 0; i < words.Length; i++) if (n.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// The choice's English name from the object's own strings - the same on every game whatever its language - or null
        /// (the object's strings aren't tagged with their language). Use this, never the pie menu's name, for anything the lot does.
        /// </summary>
        public static string EnglishName(VMEntity target, TTABInteraction entry)
        {
            if (target == null || entry == null) return null;
            try { return target.TreeTableStrings?.TaggedEnglish((int)entry.TTAIndex); } catch { return null; }
        }

        /// <summary>
        /// 18.5 (B5a): the name the LOT may go by for a string of an object's own (a menu entry's, most often) - the same on every
        /// game of a visit, whatever language each game is set to:
        ///   - a file that tags its strings with their language (formats -3/-4 - every Maxis object): the English US string
        ///     (STR.TaggedEnglish), or null when the file has no English one for it;
        ///   - an untagged file (formats 0/-1/-2 - some downloads): its own one set of strings. The player's language can't pick
        ///     anything else there (STR.GetString falls back to that same set), so every game with the same file reads the same
        ///     words - the "old name match" kept as the fallback, without ever depending on the player's language.
        /// Use this (never the pie menu's name or STR.GetString) for anything that can change what happens on the lot.
        /// </summary>
        public static string LotName(STR strings, int index)
        {
            if (strings == null || index < 0) return null;
            try
            {
                if (strings.ReduxFormat == -3 || strings.ReduxFormat == -4) return strings.TaggedEnglish(index);
                var sets = strings.LanguageSets;
                var set = (sets != null && sets.Length > 0) ? sets[0] : null;
                if (set?.Strings == null || index >= set.Strings.Length) return null;
                return set.Strings[index]?.Value;
            }
            catch { return null; }
        }

        /// <summary>18.5 (B5a): a menu entry's lot name (LotName) - its English name, or an untagged file's own name.</summary>
        public static string LotName(VMEntity target, TTABInteraction entry)
        {
            if (target == null || entry == null) return null;
            return LotName(target.TreeTableStrings, (int)entry.TTAIndex);
        }

        /// <summary>
        /// The kind of a choice - see the notes above: who it's done to, its English name, the skill it builds, else the need it
        /// gives most. Language-independent: the same on every game on a visit.
        /// </summary>
        public static Kind KindOf(VMEntity target, TTABInteraction entry, VMPieMenuInteraction first)
        {
            if (target is VMAvatar ta) return ta.IsPet ? Kind.Play : Kind.Social;
            if (entry != null)
            {
                KindBox box;
                if (!EntryKinds.TryGetValue(entry, out box)) box = NewKindBox(target, entry);
                if (box.K != NoKind) return box.K;
            }
            return ByAds(target, entry, first);
        }

        /// <summary>(Kept for older callers: the name is ignored - see KindOf(target, entry, first).)</summary>
        public static Kind KindOf(VMEntity target, string name, TTABInteraction entry, VMPieMenuInteraction first)
        {
            return KindOf(target, entry, first);
        }

        private static KindBox NewKindBox(VMEntity target, TTABInteraction entry)
        {
            var k = FixedKind(EnglishName(target, entry), entry);
            try { return EntryKinds.GetValue(entry, _ => new KindBox { K = k }); }
            catch { return new KindBox { K = k }; }
        }

        /// <summary>What the entry itself says (its English name, else the skill it builds), or NoKind.</summary>
        private static Kind FixedKind(string english, TTABInteraction entry)
        {
            var n = (english ?? "").Trim().ToLowerInvariant();
            if (n.Length > 0)
            {
                if (n.StartsWith("eat") || Has(n, FoodWords)) return Kind.Food;
                if (Has(n, ChoreWords)) return Kind.Chores;
                if (Has(n, ExerciseWords)) return Kind.Exercise;
                if (Has(n, SkillWords)) return Kind.Skill;
                if (Has(n, ScreenWords)) return Kind.Screen;
                if (Has(n, PlayWords)) return Kind.Play;
                if (Has(n, BathroomWords)) return Kind.Bathroom;
                if (n == "sleep" || n.StartsWith("sleep") || n.StartsWith("nap") || n.StartsWith("sit") || n.StartsWith("lie") || n.StartsWith("rest") || Has(n, RestWords)) return Kind.Rest;
            }
            // the skill it builds (VMFindBestAction.VaryByTypes 13-22) - unless it's for a physical need (eating builds no skill)
            var mes = entry?.MotiveEntries;
            if (mes == null) return NoKind;
            int skill = -1;
            for (int i = 0; i < mes.Length; i++)
            {
                var e = mes[i];
                int ad = e.EffectRangeMinimum + e.EffectRangeDelta;
                if (ad <= 0) continue;
                if (i == (int)VMMotive.Hunger || i == (int)VMMotive.Bladder || i == (int)VMMotive.Energy || i == (int)VMMotive.Hygiene) return NoKind;
                if (e.PersonalityModifier >= 13 && e.PersonalityModifier <= 22 && skill < 0) skill = e.PersonalityModifier;
            }
            switch (skill)
            {
                case -1: return NoKind;
                case 13: case 17: return Kind.Chores;   // cleaning, gardening
                case 14: return Kind.Food;              // cooking
                case 21: return Kind.Exercise;          // body
                default: return Kind.Skill;             // charisma, mechanical, music, creativity, literacy, logic
            }
        }

        /// <summary>From the need the choice gives most (with its check tree's changes) and the object's Buy-mode category.</summary>
        private static Kind ByAds(VMEntity target, TTABInteraction entry, VMPieMenuInteraction first)
        {
            int best = -1, bestAd = 0;
            for (int i = 0; i < Needs.Length; i++)
            {
                int ad = AdOf(entry, first, Needs[i]);
                if (ad > bestAd) { bestAd = ad; best = i; }
            }
            switch (best)
            {
                case 0: return Kind.Food;
                case 1: case 4: return Kind.Rest;
                case 2: case 3: return Kind.Bathroom;
                case 5:
                    // fun from Electronics (TV, stereo, computer) is screen time (OBJD FunctionFlags bit 3 - from the object file)
                    var objd = target?.Object?.OBJ;
                    return (objd != null && (objd.FunctionFlags & (1 << 3)) != 0) ? Kind.Screen : Kind.Play;
                case 6: return Kind.Social;
                case 7: return Kind.Chores;
            }
            return Kind.Other;
        }

        /// <summary>How much the choice advertises for a need (the top of its range, after its check tree's changes; 0 = nothing).</summary>
        public static int AdOf(TTABInteraction entry, VMPieMenuInteraction first, VMMotive m)
        {
            if (entry?.MotiveEntries == null || (int)m >= entry.MotiveEntries.Length) return 0;
            var e = entry.MotiveEntries[(int)m];
            short min = e.EffectRangeMinimum, max = e.EffectRangeDelta, v;
            if (first?.MotiveAdChanges != null)
            {
                if (first.MotiveAdChanges.TryGetValue((0 << 16) | (int)m, out v)) min = v;
                if (first.MotiveAdChanges.TryGetValue((1 << 16) | (int)m, out v)) max = v;
            }
            if (max == 0 && min > 0) { max = min; min = 0; }
            return Math.Max(0, max + min);
        }

        // ------------------------------------------------------------------ the weight (VMFindBestAction)

        // FACTOR DETERMINISM (lockstep):
        //  - Register factors UNCONDITIONALLY at start-up (a static constructor or the game's start-up code), never depending on
        //    settings, the platform, a file, the language or anything else that can differ between two games of the same build.
        //  - The set is LOCKED the first time it's used on a lot (Weight) or its hash is read (a visit's join): AddFactor and
        //    RemoveFactor after that are refused (and say so in the log), so it can't change while games play together.
        //  - They're COMBINED IN ID ORDER (ordinal), whatever order they were registered in, so the whole-number rounding of
        //    w * m / 1000 is the same everywhere.
        //  - FactorsHash (the ids, in that order) goes with a visitor's join (ReduxOnline): a host and guest with different
        //    factor sets - mismatched builds - can't play the same lot.

        /// <summary>One factor (B2's behaviour extras): an id and a multiplier in per mille.</summary>
        private sealed class Factor { public string Id; public Func<VMReduxBrainContext, int> PerMille; }
        private static Factor[] Factors = new Factor[0];
        private static readonly object FactorLock = new object();
        private static volatile bool FactorsLocked;
        private static int FactorHashValue = HashIds(new Factor[0]);

        /// <summary>How many factors are registered (no array made - use this on hot paths).</summary>
        public static int FactorCount { get { return Factors.Length; } }

        /// <summary>A hash of the registered factor ids in combining order (FNV-1a over the ids' characters) - the same in every game of the same build. Locks the set.</summary>
        public static int FactorsHash { get { FactorsLocked = true; return FactorHashValue; } }

        private static int HashIds(Factor[] fs)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (var f in fs)
                {
                    foreach (var c in f.Id) { h ^= c; h *= 16777619; }
                    h ^= 0x0A; h *= 16777619; // (a separator, so "ab"+"c" isn't "a"+"bc")
                }
                return (int)h;
            }
        }

        /// <summary>Adds a factor: a multiplier on every choice (1 = no change). See the class notes and FACTOR DETERMINISM for the rules.</summary>
        public static void AddFactor(string id, Func<VMReduxBrainContext, float> multiplier)
        {
            if (multiplier == null) return;
            AddFactorPerMille(id, ctx =>
            {
                float m = multiplier(ctx);
                if (float.IsNaN(m) || float.IsInfinity(m)) return 1000;
                return (int)Math.Round(Math.Max(0, Math.Min(10, m)) * 1000);
            });
        }

        /// <summary>Adds a factor in whole numbers: 1000 = no change, 1500 = x1.5, 500 = half.</summary>
        public static void AddFactorPerMille(string id, Func<VMReduxBrainContext, int> perMille)
        {
            if (string.IsNullOrEmpty(id) || perMille == null) return;
            lock (FactorLock)
            {
                if (FactorsLocked) { Console.WriteLine("[SimsRedux] brain: factor \"" + id + "\" refused - factors must be added at start-up, before any lot is played"); return; }
                var l = new List<Factor>();
                foreach (var f in Factors) if (f.Id != id) l.Add(f);
                l.Add(new Factor { Id = id, PerMille = perMille });
                l.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id)); // (combined in id order, whatever order they came in)
                var arr = l.ToArray();
                FactorHashValue = HashIds(arr);
                Factors = arr; // (a new array: scoring reads it without a lock)
            }
        }

        public static void RemoveFactor(string id)
        {
            lock (FactorLock)
            {
                if (FactorsLocked) { Console.WriteLine("[SimsRedux] brain: removing factor \"" + id + "\" refused - the factors are fixed once a lot is played"); return; }
                var l = new List<Factor>();
                foreach (var f in Factors) if (f.Id != id) l.Add(f);
                var arr = l.ToArray();
                FactorHashValue = HashIds(arr);
                Factors = arr;
            }
        }

        /// <summary>The factor ids in combining order (makes an array - not for hot paths: FactorCount).</summary>
        public static string[] FactorIds()
        {
            var f = Factors;
            var r = new string[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = f[i].Id;
            return r;
        }

        /// <summary>The heaviest and lightest a choice can be made (per mille).</summary>
        public const int MinWeight = 100, MaxWeight = 5000;

        /// <summary>
        /// How much the brain likes this choice, per mille (1000 = no change). Only lot state: the Sim's attributes and needs, the
        /// lot's clock, the choice's kind and advertisements (language-independent - KindOf), and the factors. ctx can be null
        /// (made here only when there are factors).
        /// </summary>
        public static int Weight(VM vm, VMAvatar sim, VMEntity target, TTABInteraction entry, VMPieMenuInteraction first, float distance, int level, VMReduxBrainContext ctx = null)
        {
            if (vm == null || sim == null || sim.IsPet || target == null) return 1000;
            int hour = vm.Context.Clock.Hours;
            bool night = Night(hour);
            var kind = KindOf(target, entry, first);
            long w = Mul[Cell(sim, KindCell((int)kind, night)) + 8];

            long sum = 0, wsum = 0;
            for (int i = 0; i < Needs.Length; i++)
            {
                int ad = AdOf(entry, first, Needs[i]);
                if (ad <= 0) continue;
                bool low = sim.GetMotiveData(Needs[i]) < LowBelow;
                sum += (long)ad * Mul[Cell(sim, NeedCell(i, low)) + 8];
                wsum += ad;
            }
            if (wsum > 0) w = w * (sum / wsum) / 1000;

            if (!FactorsLocked) FactorsLocked = true; // (the factor set is fixed from the first choice weighed on a lot)
            var fs = Factors;
            if (fs.Length > 0)
            {
                if (ctx == null) ctx = new VMReduxBrainContext();
                ctx.Fill(vm, sim, target, EnglishName(target, entry), entry, first, kind, distance, level);
                int strongest = 0;
                foreach (var f in fs)
                {
                    int m;
                    ctx.PendingNote = null;
                    try { m = f.PerMille(ctx); } catch { m = 1000; }
                    m = Math.Max(50, Math.Min(4000, m));
                    if (ctx.PendingNote != null && Math.Abs(m - 1000) > strongest) { strongest = Math.Abs(m - 1000); ctx.Why = ctx.PendingNote; }
                    w = w * m / 1000;
                }
            }
            return (int)Math.Max(MinWeight, Math.Min(MaxWeight, w));
        }

        // ------------------------------------------------------------------ free will (VMFindBestAction)

        /// <summary>A need counts as "needed" for Needs only under this.</summary>
        public const int NeedsOnlyBelow = 30;
        /// <summary>Red: a physical need this low is an emergency even with free will Off.</summary>
        public const int RedBelow = -40;
        private static readonly VMMotive[] Physical = { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene };

        /// <summary>
        /// A visiting player's Sim on this lot (or a Sim brought over from another house): given the lot family's number so the
        /// game lets them in, but marked FSOExcludeUserSave - not the household (ReduxComputer.IsHouseholdSim).
        /// </summary>
        public static bool VisitingSim(VM vm, VMAvatar sim)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            return fam != null && sim != null && !sim.IsPet && sim.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == fam.ChunkID
                && (sim.GetValue(VMStackObjectVariable.MovementFlags) & (short)VMMovementFlags.FSOExcludeUserSave) != 0;
        }

        /// <summary>
        /// The free will that really applies to this Sim on this lot (VMFindBestAction uses it; the badge and the Sim panel show
        /// it): the household's Sims and a visiting player's own Sim (attribute 120 - only their own game sets it, with command
        /// 86) - Off and Needs only work everywhere; High (Sims act sooner when idle) only at home, since a visitor's idle time
        /// is the original game's. Everyone else: Normal.
        /// </summary>
        public static FreeWill EffectiveFreeWill(VM vm, VMAvatar sim)
        {
            if (vm == null || !vm.TS1 || sim == null || sim.IsPet) return FreeWill.Normal;
            var fw = FreeWillOf(sim);
            if (fw == FreeWill.Normal) return fw;
            if (ReduxComputer.IsHouseholdSim(vm, sim)) return fw;
            if (VisitingSim(vm, sim)) return fw == FreeWill.High ? FreeWill.Normal : fw;
            return FreeWill.Normal;
        }

        /// <summary>A physical need in the red.</summary>
        public static bool InRed(VMAvatar sim)
        {
            if (sim == null) return false;
            foreach (var m in Physical) if (sim.GetMotiveData(m) < RedBelow) return true;
            return false;
        }

        /// <summary>
        /// May the Sim take this choice on their own, with this free will? Off: only what helps a need in the red. Needs only: what
        /// helps a need under NeedsOnlyBelow. Normal and High: anything.
        /// </summary>
        public static bool Allowed(FreeWill fw, VMAvatar sim, TTABInteraction entry, VMPieMenuInteraction first)
        {
            if (fw == FreeWill.Normal || fw == FreeWill.High) return true;
            if (fw == FreeWill.Off)
            {
                foreach (var m in Physical)
                    if (sim.GetMotiveData(m) < RedBelow && AdOf(entry, first, m) > 0) return true;
                return false;
            }
            for (int i = 0; i < Needs.Length; i++)
                if (sim.GetMotiveData(Needs[i]) < NeedsOnlyBelow && AdOf(entry, first, Needs[i]) > 0) return true;
            return false;
        }

        /// <summary>The idle wait (game minutes) for this Sim's free will and learned style, from the usual one.</summary>
        public static int IdleWait(VMAvatar sim, int usual)
        {
            int w = usual;
            if (FreeWillOf(sim) == FreeWill.High) w = Math.Max(1, usual / 2);
            switch (StyleOf(sim))
            {
                case Style.LeansOnPlayer: w += 3; break;  // (a hands-on player: the Sim waits a little longer for orders)
                case Style.SelfReliant: w = Math.Max(1, w - 1); break;
            }
            return w;
        }

        // ------------------------------------------------------------------ "Why I'm doing this" (display only)

        /// <summary>What the Sim chose on their own and why. Display only: nothing on the lot ever reads it.</summary>
        public sealed class WhyEntry { public short Callee; public int Interaction; public string Text; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VMAvatar, WhyEntry> Whys
            = new System.Runtime.CompilerServices.ConditionalWeakTable<VMAvatar, WhyEntry>();

        public static void SetWhy(VMAvatar sim, VMEntity callee, int interaction, string text)
        {
            if (sim == null || callee == null) return;
            try
            {
                var e = Whys.GetValue(sim, _ => new WhyEntry());
                lock (e) { e.Callee = callee.ObjectID; e.Interaction = interaction; e.Text = text; }
            }
            catch { }
        }

        /// <summary>
        /// Why the Sim is doing what they're doing now (their current action): their own reason when it's still the choice they
        /// made, "Doing what you asked" for an order, or null.
        /// </summary>
        public static string Why(VMAvatar sim)
        {
            try
            {
                var q = sim?.Thread?.Queue;
                if (q == null || q.Count == 0) return null;
                var head = q[0];
                if (head.Priority >= (short)VMQueuePriority.UserDriven && head.Mode == VMQueueMode.Normal) return "Doing what you asked";
                WhyEntry e;
                if (!Whys.TryGetValue(sim, out e)) return null;
                lock (e)
                {
                    foreach (var a in q)
                        if (a.Callee != null && a.Callee.ObjectID == e.Callee && a.InteractionNumber == e.Interaction) return e.Text;
                }
            }
            catch { }
            return null;
        }

        private static readonly string[] LowWords = { "Hungry", "Uncomfortable", "Feeling grubby", "Needs the bathroom", "Tired", "Bored", "Lonely", "This place is a mess" };
        private static readonly string[] RedWords = { "Starving", "Very uncomfortable", "Really needs a wash", "Bursting for the bathroom", "Exhausted", "Very bored", "Very lonely", "This place is filthy" };
        private static readonly string[] SoonWords = { "Peckish", "Wants to get comfy", "Wants to freshen up", "Going before it gets urgent", "Getting sleepy", "Wants some fun", "Wants some company", "Tidying before it gets messy" };

        /// <summary>The reason line for a choice the Sim made on their own (plain English, short enough for a phone).</summary>
        public static string Explain(VM vm, VMAvatar sim, VMPieMenuInteraction chosen, int choreCode, bool bed, bool chore, bool idle, string factorNote)
        {
            if (sim == null || chosen == null) return null;
            var what = (chosen.Name ?? "").Trim();
            string why;
            if (choreCode == 101) why = "There's a fire - calling for help";
            else if (choreCode == 102) why = "There's a fire - putting it out";
            else if (choreCode == 100) why = "Something's broken - calling the repairman";
            else if (bed) why = sim.GetMotiveData(VMMotive.Energy) < RedBelow ? "Exhausted" : "Bedtime";
            else if (chore) why = (LotName(chosen.Callee, chosen.Entry) ?? "").IndexOf("Pay Bills", StringComparison.OrdinalIgnoreCase) >= 0 ? "The bills need paying" : "Keeping the house in order"; // (18.5: by its English name)
            else
            {
                // the lowest need it helps with
                int need = -1; int lowest = int.MaxValue;
                for (int i = 0; i < Needs.Length; i++)
                {
                    if (AdOf(chosen.Entry, chosen, Needs[i]) <= 0) continue;
                    int v = sim.GetMotiveData(Needs[i]);
                    if (v < lowest) { lowest = v; need = i; }
                }
                if (idle || need < 0) why = "Nothing needed - something fun to do";
                else if (lowest < RedBelow) why = RedWords[need];
                else if (lowest < LowBelow) why = LowWords[need];
                else if (lowest < 60) why = SoonWords[need];
                else why = "Just feels like it";
            }
            if (factorNote != null) why = factorNote;
            else if (!bed && !chore && choreCode == 0 && HasBrain(sim))
            {
                var kind = KindOf(chosen.Callee, chosen.Entry, chosen);
                int c = Cell(sim, KindCell((int)kind, Night(vm.Context.Clock.Hours)));
                if (c >= 2) why += " (and likes this)";
            }
            return what.Length > 0 ? why + " - " + what : why;
        }

        // ------------------------------------------------------------------ the player's orders (learning from the player)

        /// <summary>
        /// Raised (on every game) when a player's order runs on the lot: VMNetInteractionCmd (an order: the callee and the
        /// interaction) or VMNetInteractionCancelCmd (a cancel: the action being cancelled, before it goes). The game that sent it
        /// (cmd.ActorUID == vm.MyUID) learns from it - the lot itself never does. Handlers must not change the lot.
        /// </summary>
        public static event Action<VM, VMNetCommandBodyAbstract, VMAvatar, VMEntity, int, VMQueuedAction> PlayerAct;

        /// <summary>Called by the two commands (display/learning only; any error is swallowed).</summary>
        public static void Heard(VM vm, VMNetCommandBodyAbstract cmd, VMAvatar sim, VMEntity callee, int interaction, VMQueuedAction cancelled)
        {
            var h = PlayerAct;
            if (h == null || vm == null || !vm.TS1) return;
            try { h(vm, cmd, sim, callee, interaction, cancelled); } catch { }
        }
    }

    /// <summary>
    /// What a brain factor sees about one choice (VMReduxBrain.AddFactor). One object is reused for every choice of a decision:
    /// read it, don't keep it.
    /// </summary>
    public sealed class VMReduxBrainContext
    {
        public VM VM;
        public VMAvatar Sim;
        /// <summary>The object (or Sim) the choice is on.</summary>
        public VMEntity Target;
        /// <summary>
        /// The choice's ENGLISH name from the object's own strings ("Have Snack", "Watch TV/Romance") - the same in every game
        /// whatever its language - or null when the object's strings aren't tagged with their language (VMReduxBrain.EnglishName).
        /// Never the pie menu's name (that's in the player's language and would split a visit). Prefer Kind / Ad() where you can.
        /// </summary>
        public string Name;
        public TTABInteraction Entry;
        public VMPieMenuInteraction Pie;
        public VMReduxBrain.Kind Kind;
        /// <summary>Tiles to the target.</summary>
        public float Distance;
        /// <summary>Sim Smarts on this lot: 1 Smarter, 2 Smartest, 3 Adaptive.</summary>
        public int Level;
        public int Hour, Minute;
        public VMReduxBrain.FreeWill FreeWill;
        /// <summary>The reason a factor gave with Note (the strongest one wins) - for "Why I'm doing this".</summary>
        public string Why;
        internal string PendingNote;

        internal void Fill(VM vm, VMAvatar sim, VMEntity target, string name, TTABInteraction entry, VMPieMenuInteraction pie, VMReduxBrain.Kind kind, float distance, int level)
        {
            VM = vm; Sim = sim; Target = target; Name = name; Entry = entry; Pie = pie; Kind = kind; Distance = distance; Level = level;
            Hour = vm.Context.Clock.Hours; Minute = vm.Context.Clock.Minutes;
            FreeWill = VMReduxBrain.FreeWillOf(sim);
            Why = null; PendingNote = null;
        }

        /// <summary>The Sim's need now (-100..100).</summary>
        public int Need(VMMotive m) { return Sim?.GetMotiveData(m) ?? 0; }
        /// <summary>How much the choice gives for a need (0 = nothing).</summary>
        public int Ad(VMMotive m) { return VMReduxBrain.AdOf(Entry, Pie, m); }
        /// <summary>A short reason for "Why I'm doing this", used when this factor is the strongest on the chosen action.</summary>
        public void Note(string why) { PendingNote = why; }
        /// <summary>The time of day in game minutes (0-1439).</summary>
        public int TimeOfDay { get { return Hour * 60 + Minute; } }
    }
}
