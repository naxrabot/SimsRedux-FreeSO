using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.9 (Part B #1b): attraction and relationship types - the lot's side. Simitone's Utils/ReduxAttraction.cs
    /// is the game's side (the side file, "Talk about our relationship", Exploring, the Game Rules).
    ///
    /// Who a Sim is attracted to and their relationship type come from their traits (VMReduxTraits, attributes 80-83, put on
    /// the lot by T1's VMNetReduxTraitsCmd): the orientation trait (none = everyone, as in Sims 1), "polyamorous" =
    /// Polyamorous, "free-spirit" = Open, otherwise Monogamous. Exploring and "Talk about our relationship" change the Sim's
    /// traits (the game's ReduxTraits). So a talk counts at once on every game, VMNetReduxAttractionCmd (VM command 75)
    /// Talk puts the new type on the Sim as person-object attribute 78 (OverrideAttr) until the new traits are on the lot;
    /// the game that keeps the Sim's traits then clears it (SetOverride 0):
    ///   low byte  - an orientation code (VMReduxTraits.T, 40-49) used instead of the trait's, 0 = the trait's
    ///   bits 8-9  - 1 Monogamous, 2 Open, 3 Polyamorous instead of the traits', 0 = the traits'
    /// Attributes are saved with the lot and sent to every player joining, so everyone on a visit reads the same thing.
    ///
    /// What it changes in the Sims 1 scripts (only while the lot's game says it's on - SetRules - so every game agrees):
    ///   - romantic choices (flirts, romantic kisses and hugs, slow dance, Ask to Date, Propose...) only show, and are only
    ///     picked by free will, toward Sims the Sim is attracted to (AllowCheck, from VMThread.CheckTS1Action);
    ///   - a Sim who isn't attracted to the one flirting / kissing / proposing turns it down (the social's own "no" -
    ///     Hook, on the social's "Decision" script);
    ///   - two Sims of the same gender can propose (the game's propose test only asked for a man and a woman: the test is
    ///     run with the other Sim counted as the other gender for that one moment - BeginCheck / EndCheck);
    ///   - jealousy: the Game Rule (On / Only if Monogamous / Off) - the Sim who would be jealous isn't when the rule says
    ///     so (SkipJealousy, from VMPushInteraction on the social's "start jealousy").
    /// </summary>
    public static class ReduxAttraction
    {
        public const int OverrideAttr = 78;

        public enum RelType : byte { Monogamous = 0, Open = 1, Polyamorous = 2 }
        public enum Jealousy : byte { On = 0, OnlyMonogamous = 1, Off = 2 }

        // ------------------------------------------------------------------ the lot's rules (VMNetReduxAttractionCmd SetRules)

        /// <summary>
        /// No longer used by the lot (kept so older callers still build). 16.9: a lot that hasn't been told the rules yet
        /// (a guest who just joined, before the host's SetRules arrives) behaves as plain Sims 1 - UntoldOn / UntoldJealousy,
        /// the same on every game - never from this game's own switches, so every game on a visit does the same.
        /// </summary>
        public static bool DefaultOn = true;
        public static Jealousy DefaultJealousy = Jealousy.OnlyMonogamous;

        /// <summary>What every game uses until the lot's game has said (SetRules): off - plain Sims 1, jealousy as the game had it.</summary>
        public const bool UntoldOn = false;
        public const Jealousy UntoldJealousy = Jealousy.On;

        private class LotRules { public bool On; public Jealousy Jealousy; }
        private static readonly ConditionalWeakTable<VM, LotRules> Rules = new ConditionalWeakTable<VM, LotRules>();

        public static bool On(VM vm)
        {
            if (vm == null || !vm.TS1) return false;
            LotRules r;
            lock (Rules) { if (Rules.TryGetValue(vm, out r)) return r.On; }
            return UntoldOn;
        }

        public static Jealousy JealousyRule(VM vm)
        {
            LotRules r;
            if (vm != null) lock (Rules) { if (Rules.TryGetValue(vm, out r)) return r.Jealousy; }
            return UntoldJealousy;
        }

        /// <summary>Has this lot been told (and what)?</summary>
        public static bool Told(VM vm, out bool on, out Jealousy jealousy)
        {
            on = UntoldOn; jealousy = UntoldJealousy;
            if (vm == null) return false;
            LotRules r;
            lock (Rules) { if (!Rules.TryGetValue(vm, out r)) return false; }
            on = r.On; jealousy = r.Jealousy;
            return true;
        }

        public static void SetRules(VM vm, bool on, Jealousy jealousy)
        {
            if (vm == null) return;
            if ((byte)jealousy > 2) jealousy = Jealousy.OnlyMonogamous;
            lock (Rules) { var r = Rules.GetOrCreateValue(vm); r.On = on; r.Jealousy = jealousy; }
        }

        private static VM VMOf(VMAvatar a) { return a?.Thread?.Context?.VM; }

        // ------------------------------------------------------------------ who, and what kind

        private static bool Person(VMAvatar a) { return a != null && !a.IsPet; }

        /// <summary>The orientation code the Sim goes by on the lot (0: none - everyone, as in Sims 1).</summary>
        public static int OrientationCode(VMAvatar a)
        {
            if (!Person(a)) return 0;
            int o = a.GetAttribute(OverrideAttr) & 0xFF;
            if (VMReduxTraits.IsOrientation(o)) return o;
            return VMReduxTraits.Orientation(a);
        }

        /// <summary>The orientation's id ("straight", "gay", "bisexual", "asexual", "aromantic"), or null (everyone).</summary>
        public static string Orientation(VMAvatar a)
        {
            var c = OrientationCode(a);
            return c == 0 ? null : VMReduxTraits.IdOf(c);
        }

        /// <summary>Is a romantically attracted to b? (Off, or no orientation: yes - Sims 1.)</summary>
        public static bool Attracted(VMAvatar a, VMAvatar b)
        {
            if (!Person(a) || !Person(b) || a == b) return false;
            if (!On(VMOf(a))) return true;
            switch ((VMReduxTraits.T)OrientationCode(a))
            {
                case VMReduxTraits.T.Straight: return VMReduxTraits.Female(a) != VMReduxTraits.Female(b);
                case VMReduxTraits.T.Gay: return VMReduxTraits.Female(a) == VMReduxTraits.Female(b);
                case VMReduxTraits.T.Aromantic: return false;
                default: return true;
            }
        }

        /// <summary>Both ways.</summary>
        public static bool Mutual(VMAvatar a, VMAvatar b) { return Attracted(a, b) && Attracted(b, a); }

        /// <summary>Interested in WooHoo with b: attracted, and not Asexual or Aromantic.</summary>
        public static bool WantsWooHoo(VMAvatar a, VMAvatar b)
        {
            if (!Person(a)) return false;
            if (!On(VMOf(a))) return true;
            var o = (VMReduxTraits.T)OrientationCode(a);
            if (o == VMReduxTraits.T.Asexual || o == VMReduxTraits.T.Aromantic) return false;
            return b == null || Attracted(a, b);
        }

        /// <summary>The Sim's relationship type (Off: Monogamous, as in Sims 1).</summary>
        public static RelType TypeOf(VMAvatar a)
        {
            if (!Person(a)) return RelType.Monogamous;
            int o = (a.GetAttribute(OverrideAttr) >> 8) & 3;
            if (o == 1) return RelType.Monogamous;
            if (o == 2) return RelType.Open;
            if (o == 3) return RelType.Polyamorous;
            if (VMReduxTraits.Has(a, VMReduxTraits.T.Polyamorous)) return RelType.Polyamorous;
            if (VMReduxTraits.Has(a, VMReduxTraits.T.FreeSpirit)) return RelType.Open;
            return RelType.Monogamous;
        }

        /// <summary>The value for OverrideAttr: an orientation code (0 none) and a type (null: the traits').</summary>
        public static short Pack(int orientation, RelType? type)
        {
            int v = VMReduxTraits.IsOrientation(orientation) ? orientation : 0;
            if (type != null) v |= ((int)type.Value + 1) << 8;
            return (short)v;
        }

        /// <summary>Would this Sim be jealous of their partner's romance? (the Game Rule and their type)</summary>
        public static bool CanBeJealous(VMAvatar a)
        {
            var vm = VMOf(a);
            if (!On(vm)) return true;
            switch (JealousyRule(vm))
            {
                case Jealousy.Off: return false;
                case Jealousy.OnlyMonogamous: return TypeOf(a) == RelType.Monogamous;
                default: return true;
            }
        }

        // ------------------------------------------------------------------ the choices (VMThread.CheckTS1Action)

        /// <summary>What a choice's test script is to us (worked out once per script, VMRoutine.ReduxAttractionKind).</summary>
        public const sbyte KindNone = 0, KindRomance = 1, KindPropose = 2, KindJealousy = 3;

        /// <summary>The romantic choices on a Sim (their test scripts in PersonGlobals: "Interaction - X TEST").</summary>
        private static readonly HashSet<string> RomanceTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Flirt Check Out", "Flirt Give Back Rub", "Flirt Serenade", "Flirt Sexy Growl", "Flirt Suggestion",
            "Greet Romantic", "Greet Kiss Hand", "Hug Romantic", "Hug Body", "Kiss Dip", "Kiss Face", "Kiss Hand", "Kiss Passionate",
            "Dance Slow", "Ask to Date", "Say Goodbye Passionate", "Magic - Love Me", "Magic - Marry Me",
        };

        public static sbyte CheckKind(VMRoutine check)
        {
            if (check == null) return KindNone;
            var kind = check.ReduxAttractionKind;
            if (kind >= 0) return kind;
            kind = KindNone;
            var label = (check.Chunk?.ChunkLabel ?? "").Trim();
            const string pre = "Interaction - ", post = " TEST";
            if (label.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && label.EndsWith(post, StringComparison.OrdinalIgnoreCase)
                && label.Length > pre.Length + post.Length)
            {
                var mid = label.Substring(pre.Length, label.Length - pre.Length - post.Length).Trim();
                if (mid.Equals("Proposition Propose", StringComparison.OrdinalIgnoreCase)) kind = KindPropose;
                else if (RomanceTests.Contains(mid)) kind = KindRomance;
            }
            else if (label.Equals("start jealousy", StringComparison.OrdinalIgnoreCase)) kind = KindJealousy; // (SkipJealousy)
            check.ReduxAttractionKind = kind;
            return kind;
        }

        /// <summary>
        /// Before a choice's test runs: for a proposal between two Sims of the same gender, the other Sim counts as the
        /// other gender while the test runs (its only gender question is "a man and a woman?"). Returns what to put back
        /// (EndCheck), or null. The test runs to its end at once, so nothing else sees it.
        /// </summary>
        public static VMAvatar BeginCheck(VMEntity caller, VMQueuedAction action, out short gender)
        {
            gender = 0;
            try
            {
                // (the cached kind first - most tests aren't a proposal - then who, then the lot's rule)
                if (action?.CheckRoutine == null || CheckKind(action.CheckRoutine) != KindPropose) return null;
                var me = caller as VMAvatar;
                var other = action.Callee as VMAvatar;
                if (!Person(me) || !Person(other) || me == other || !On(VMOf(me))) return null;
                gender = other.GetPersonData(VMPersonDataVariable.Gender);
                if ((gender & 1) != (me.GetPersonData(VMPersonDataVariable.Gender) & 1)) return null;
                other.SetPersonData(VMPersonDataVariable.Gender, (short)(gender ^ 1));
                return other;
            }
            catch { return null; }
        }

        public static void EndCheck(VMAvatar flipped, short gender)
        {
            if (flipped != null) flipped.SetPersonData(VMPersonDataVariable.Gender, gender);
        }

        /// <summary>After a choice's test said yes: false hides a romantic choice toward a Sim this Sim isn't attracted to.</summary>
        public static bool AllowCheck(VMEntity caller, VMQueuedAction action)
        {
            try
            {
                if (action?.CheckRoutine == null) return true;
                var kind = CheckKind(action.CheckRoutine);
                if (kind != KindRomance && kind != KindPropose) return true;
                var me = caller as VMAvatar;
                var other = action.Callee as VMAvatar;
                if (!Person(me) || !Person(other) || me == other) return true;
                return Attracted(me, other);
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ turning it down (VMRoutine's hook)

        /// <summary>The socials' own "will they say yes?" scripts for romantic socials (Hot Date's HDSI_* objects).</summary>
        private static readonly HashSet<string> DecisionLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Function: Flirt Sexy Growl - Decision", "Function: Flirt Suggestion - Decision", "Function: Flirt Check Out - Decision",
            "Function: Flirt Backrub - Decision", "Function: Kiss - Kiss Face - Decision", "Function: Kiss - Kiss Hand - Decision",
            "Function: Kiss - Passionate - Decision", "Function: Kiss - Dip Kiss - Decision", "Function: Hug - Body - Decision",
            "Function: Hug - Romantic - Decision", "Function: Dance - Slow - Decision", "Function: Greet - Romantic Kiss - Decision",
            "Function: Passionate Kiss - Decision", "choose propose result",
        };

        public static bool IsHookLabel(string label)
        {
            return DecisionLabels.Contains((label ?? "").Trim());
        }

        /// <summary>
        /// A social is about to work out the other Sim's answer ("target"). 0: let it; -1: skip it - the answer is the
        /// social's own "no" (Temp 0 = 1; a proposal's "not close enough", 3) - because the other Sim isn't attracted to
        /// this one. Reads the lot's own state only.
        /// </summary>
        public static int Hook(VMStackFrame frame, VMRoutine target)
        {
            try
            {
                var vm = frame?.VM;
                if (vm == null || !vm.TS1 || frame.Thread == null) return 0;
                bool on = On(vm);
                if (!on && !VMReduxRomance.On(vm)) return 0; // (17.0: romance and closeness uses this answer too)
                var me = frame.Caller as VMAvatar;
                if (!Person(me)) return 0;
                // the other Sim: the stack object when it's a Sim (a proposal), else the social's two people (attributes 0 and 6)
                var other = frame.StackObject as VMAvatar;
                if (other == null && frame.StackObject != null)
                {
                    var s = frame.StackObject;
                    var p0 = vm.GetObjectById(s.GetAttribute(0)) as VMAvatar;
                    var p6 = vm.GetObjectById(s.GetAttribute(6)) as VMAvatar;
                    other = (p0 != null && p0 != me) ? p0 : ((p6 != null && p6 != me) ? p6 : null);
                }
                if (!Person(other) || other == me) return 0;
                bool propose = (target?.Chunk?.ChunkLabel ?? "").Trim().Equals("choose propose result", StringComparison.OrdinalIgnoreCase);
                // (attracted - or attraction off: 17.0's "not in the mood" and a beginner's awkward flirt, Utils/ReduxRomance.cs)
                if (!on || Attracted(other, me))
                {
                    if (!VMReduxRomance.Decide(frame, me, other, propose)) return 0;
                }
                frame.Thread.TempRegisters[0] = (short)(propose ? 3 : 1);
                return -1;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] attraction hook: " + e.Message);
                return 0;
            }
        }

        // ------------------------------------------------------------------ jealousy (VMPushInteraction)

        /// <summary>
        /// The social's "start jealousy" pushes the jealous reaction on each Sim who saw their sweetheart romance someone:
        /// true skips that Sim (they aren't jealous) - the Game Rule "Jealousy" and the Sim's relationship type.
        /// </summary>
        public static bool SkipJealousy(VMStackFrame context)
        {
            try
            {
                // (what the script is first - worked out once per script, a field read after that - then the lot's rule)
                if (context?.Routine == null || CheckKind(context.Routine) != KindJealousy) return false;
                var vm = context.VM;
                if (vm == null || !vm.TS1 || !On(vm)) return false;
                var who = context.StackObject as VMAvatar;
                if (!Person(who)) return false;
                return !CanBeJealous(who);
            }
            catch { return false; }
        }
    }
}
