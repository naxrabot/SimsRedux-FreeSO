using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: when a Sim gets up from a skill object (studying, chess, painting, the workbench...).
    /// Sims 1's "Exit Skill Object" only lets a family Sim stop when hunger or energy is at -80 or the
    /// bladder at -85 - nearly empty. Smarter and Smartest Sims stop sooner (the same limits as fun
    /// objects, see VMMemory.GetTuningVariable). Online lots are always Smartest.
    /// </summary>
    public static class ReduxExit
    {
        private static readonly Dictionary<VMRoutine, bool> IsSkillExit = new Dictionary<VMRoutine, bool>();
        private static readonly object Lock = new object();

        /// <summary>True: answer the script's "should I stop?" with yes (a need is low).</summary>
        public static bool StopSkill(VMStackFrame frame, ushort opcode)
        {
            try
            {
                var vm = frame.VM;
                if (vm == null || !vm.TS1) return false;
                int level = vm.ReduxNoPause ? 2 : FSO.SimAntics.Primitives.VMFindBestAction.ReduxLevel;
                if (level < 1) return false;
                var routine = frame.Global?.Resource?.GetRoutine(opcode) as VMRoutine; // (a global script)
                if (routine == null) return false;
                bool skill;
                lock (Lock)
                {
                    if (!IsSkillExit.TryGetValue(routine, out skill))
                    {
                        skill = (routine.Chunk?.ChunkLabel ?? "").Equals("Exit Skill Object", StringComparison.OrdinalIgnoreCase);
                        IsSkillExit[routine] = skill;
                    }
                }
                if (!skill) return false;
                var sim = frame.Caller as VMAvatar;
                if (sim == null || sim.IsPet) return false;
                if (sim.GetPersonData((VMPersonDataVariable)32) != 0) return false; // (visitors: the game's own rules)
                int h = level >= 2 ? -45 : -60, b = level >= 2 ? -50 : -65, e = level >= 2 ? -55 : -65;
                var hour = vm.Context.Clock.Hours;
                if (hour >= 22 || hour < 6) e = level >= 2 ? 10 : -15; // (bedtime: see VMMemory.GetTuningVariable)
                return sim.GetMotiveData(VMMotive.Hunger) <= h || sim.GetMotiveData(VMMotive.Bladder) <= b || sim.GetMotiveData(VMMotive.Energy) <= e;
            }
            catch { return false; }
        }
    }
}
