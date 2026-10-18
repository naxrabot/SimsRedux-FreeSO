using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: Sims get better at things by doing them (The Sims 2 way), not only by studying.
    /// Cooking a meal raises Cooking, repairing raises Mechanical, chatting and joking raise Charisma,
    /// swimming and dancing raise Body, stargazing raises Logic. Slower than studying, and slower the
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
            public VMPersonDataVariable Skill;
            public string Name;
            public float Speed; // 1 = one level in 2 game hours for a beginner
            public Rule(VMPersonDataVariable skill, string name, float speed, params string[] words) { Skill = skill; Name = name; Speed = speed; Words = words; }
        }

        private static readonly Rule[] Rules =
        {
            new Rule(VMPersonDataVariable.CookingSkill, "Cooking", 1f, "cook", "prepare", "make breakfast", "make lunch", "make dinner", "grill", "bake", "barbecue", "flip"),
            new Rule(VMPersonDataVariable.MechanicalSkill, "Mechanical", 1f, "repair", "fix", "unclog", "tinker"),
            new Rule(VMPersonDataVariable.CharismaSkill, "Charisma", 0.3f, "talk", "joke", "entertain", "tell ", "flirt", "compliment", "brag", "chat", "gossip"),
            new Rule(VMPersonDataVariable.BodySkill, "Body", 0.7f, "swim", "dance", "jog"),
            new Rule(VMPersonDataVariable.LogicSkill, "Logic", 0.7f, "stargaze", "look through"),
        };

        /// <summary>Raised when a Sim reaches a new level (sim, skill name, level 1-10).</summary>
        public static Action<VMAvatar, string, int> OnLevelUp;

        public static void Tick(VMAvatar ava)
        {
            try
            {
                if (ava?.Thread == null || ava.IsPet) return;
                var action = ava.Thread.ActiveAction;
                var name = action?.Name;
                if (string.IsNullOrEmpty(name) || action.Callee == null) return;
                name = name.ToLowerInvariant();
                int found = -1;
                for (int i = 0; i < Rules.Length && found < 0; i++)
                    foreach (var w in Rules[i].Words)
                        if (name.Contains(w)) { found = i; break; }
                if (found < 0) return;
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
