using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: Sims get better at things by doing them (The Sims 2 way), not only by studying.
    /// Cooking a meal raises Cooking, repairing raises Mechanical, chatting and joking raise Charisma,
    /// swimming, dancing and shooting hoops raise Body, stargazing raises Logic. Slower than studying, and slower the
    /// better they already are. A game minute at a time while the Sim is actually doing it (not while
    /// walking over), kept on the Sim (person data), so every player in an online house agrees.
    /// </summary>
    public static class ReduxSkillByDoing
    {
        // (the SimsRedux person-data block, see ReduxComputer: 208 = hundredths not yet added, 209 = last minute counted)
        private const int Carry = 208, LastMinute = 209;

        private struct Rule
        {
            public string[] Words;
            /// <summary>If set, only on an object whose name has one of these (the hoop's "Play" isn't the pool table's).</summary>
            public string[] Objects;
            public VMPersonDataVariable Skill;
            public string Name;
            public float Speed; // 1 = one level in 2 game hours for a beginner
            public Rule(VMPersonDataVariable skill, string name, float speed, params string[] words) { Skill = skill; Name = name; Speed = speed; Words = words; Objects = null; }
            public Rule(string[] objects, VMPersonDataVariable skill, string name, float speed, params string[] words) : this(skill, name, speed, words) { Objects = objects; }
        }

        private static readonly Rule[] Rules =
        {
            // (sports objects first: their interactions are just "Play" / "Join")
            new Rule(new[] { "basketball", "volleyball", "tetherball", "trampoline" }, VMPersonDataVariable.BodySkill, "Body", 0.7f, "play", "join", "shoot", "practice", "dunk", "jump", "bounce"),
            new Rule(VMPersonDataVariable.CookingSkill, "Cooking", 1f, "have a meal", "serve meal", "have lite meal", "cook", "prepare", "make breakfast", "make lunch", "make dinner", "grill", "bake", "barbecue", "flip"),
            new Rule(VMPersonDataVariable.MechanicalSkill, "Mechanical", 1f, "repair", "fix", "unclog", "tinker"),
            new Rule(VMPersonDataVariable.CharismaSkill, "Charisma", 0.3f, "talk", "joke", "entertain", "tell ", "flirt", "compliment", "brag", "chat", "gossip"),
            new Rule(VMPersonDataVariable.BodySkill, "Body", 0.7f, "swim", "dance", "jog", "basketball", "shoot hoops", "volleyball", "snowboard", "skate", "hike"),
            // (no Logic rule: the telescope already teaches Logic by itself)
        };

        /// <summary>Raised when a Sim reaches a new level (sim, skill name, level 1-10).</summary>
        public static Action<VMAvatar, string, int> OnLevelUp;

        // (marks the skill bar as this one's, so the game's own balloons are never touched)
        private const ushort BarMark = 0x5B5B;

        private static bool IsOurBar(VMAvatar ava)
        {
            return ava.Headline != null && ava.Headline.Operand != null && ava.Headline.Operand.Flags2 == BarMark;
        }

        private static void HideBar(VMAvatar ava)
        {
            if (!IsOurBar(ava)) return;
            try { ava.HeadlineRenderer?.Dispose(); } catch { }
            ava.Headline = null;
            ava.HeadlineRenderer = null;
        }

        /// <summary>The Sims 1 skill bar (the one studying shows) over a Sim practising a skill.</summary>
        private static void ShowBar(VMAvatar ava, VMPersonDataVariable skill)
        {
            try
            {
                var vm = ava.Thread?.Context?.VM;
                if (vm == null) return;
                int level = Math.Max(0, Math.Min(9, ava.GetPersonData(skill) / 100));
                if (ava.Headline != null && !IsOurBar(ava)) return; // (something else is showing - a talk balloon, say)
                // which skill the bar shows, at full speed
                ava.SetPersonData(VMPersonDataVariable.SkillEfficiency, (short)(((int)skill << 8) | 100));
                if (IsOurBar(ava) && ava.Headline.Index == level) return;
                HideBar(ava);
                var op = new Primitives.VMSetBalloonHeadlineOperand
                {
                    Flags2 = BarMark,
                    Index = (sbyte)level,
                    Group = Primitives.VMSetBalloonHeadlineOperandGroup.Progress,
                    Duration = -1,
                    Type = 3,
                };
                ava.Headline = new VMRuntimeHeadline(op, ava, null, (sbyte)level);
                ava.HeadlineRenderer = vm.Headline?.Get(ava.Headline);
            }
            catch (Exception) { }
        }

        private static int FindRule(string name, VMEntity obj)
        {
            name = name.ToLowerInvariant();
            string objName = null;
            for (int i = 0; i < Rules.Length; i++)
            {
                if (Rules[i].Objects != null)
                {
                    if (objName == null) objName = (obj?.Object?.OBJ?.ChunkLabel ?? "").ToLowerInvariant();
                    bool onIt = false;
                    foreach (var o in Rules[i].Objects) if (objName.Contains(o)) { onIt = true; break; }
                    if (!onIt) continue;
                }
                foreach (var w in Rules[i].Words)
                    if (name.Contains(w)) return i;
            }
            return -1;
        }

        public static void Tick(VMAvatar ava)
        {
            try
            {
                if (ava?.Thread == null || ava.IsPet) return;
                var action = ava.Thread.ActiveAction;
                var name = action?.Name;
                if (string.IsNullOrEmpty(name) || action.Callee == null) { HideBar(ava); return; }
                // (the matching rule is worked out once per action name and kept on the action - this lower-cased
                //  the name and searched it for ~30 words every tick for every Sim)
                if (action.ReduxSkillRule < -1 || !ReferenceEquals(action.ReduxSkillRuleName, name))
                {
                    action.ReduxSkillRule = (sbyte)FindRule(name, action.Callee);
                    action.ReduxSkillRuleName = name;
                }
                int found = action.ReduxSkillRule;
                if (found < 0) { HideBar(ava); return; }
                // the game is already teaching a skill here (its own skill bar is up - studying, say): no extra
                if (ava.Headline != null && !IsOurBar(ava) && ava.Headline.Operand?.Group == Primitives.VMSetBalloonHeadlineOperandGroup.Progress) return;
                // the skill bar over their head while they're at it (like studying)
                ShowBar(ava, Rules[found].Skill);
                // only while actually doing it (not walking from the fridge to the counter, say)
                foreach (var f in ava.Thread.Stack) if (f is VMRoutingFrame) return;
                var clock = ava.Thread.Context?.Clock;
                if (clock == null) return;
                int minute = clock.Hours * 60 + clock.Minutes + 1;
                int last = ReduxComputer.Get(ava, LastMinute);
                if (last == minute) return;
                ReduxComputer.Set(ava, LastMinute, minute);
                if (last == 0) return;

                var rule = Rules[found];
                int skill = ava.GetPersonData(rule.Skill);
                if (skill >= 1000) return;
                int level = skill / 100;
                // hundredths of a skill point this minute: a level in about 2 game hours at first, ~7 at level 9
                int gain = (int)(rule.Speed * 10000.0 / (120.0 * (1.0 + level / 4.0)));
                int carry = ReduxComputer.Get(ava, Carry) + gain;
                int whole = carry / 100;
                ReduxComputer.Set(ava, Carry, carry % 100);
                if (whole <= 0) return;
                int next = Math.Min(1000, skill + whole);
                ava.SetPersonData(rule.Skill, (short)next);
                if (next / 100 > level)
                {
                    try { OnLevelUp?.Invoke(ava, rule.Name, next / 100); } catch { }
                    if (ReduxComputer.IsMyFamily(ava))
                        ReduxComputer.Notice(ava, "Skill Up!", ava.Name + " reached " + rule.Name + " level " + (next / 100) + " - practice makes perfect!");
                }
            }
            catch (Exception) { }
        }
    }
}
