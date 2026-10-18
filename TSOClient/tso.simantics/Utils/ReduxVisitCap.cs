using FSO.SimAntics.Engine;
using System;
using System.Runtime.CompilerServices;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: at most a couple of uninvited visitors a day at a home (Sims 1's walk-bys and drop-ins from
    /// its Visit Generator, and the Refresh walk-bys). Friends you invite on the phone, the carpool, the maid,
    /// the police and so on are not counted. Single player only (online lots keep the game's own rules, so
    /// every player's game stays the same).
    /// </summary>
    public static class ReduxVisitCap
    {
        public const int PerDay = 2;

        private class Day { public int Date = -1; public int Count; }
        private static readonly ConditionalWeakTable<VM, Day> Days = new ConditionalWeakTable<VM, Day>();

        private static int Today(VM vm)
        {
            var c = vm.Context.Clock;
            return (c.Year * 12 + c.Month) * 31 + c.DayOfMonth;
        }

        /// <summary>A visitor wants to come over: true if there's room for one more today (and counts them).</summary>
        public static bool TryVisit(VM vm)
        {
            if (vm == null || vm.ReduxNoPause) return true;
            var d = Days.GetOrCreateValue(vm);
            int today = Today(vm);
            if (d.Date != today) { d.Date = today; d.Count = 0; }
            if (d.Count >= PerDay) return false;
            d.Count++;
            return true;
        }

        /// <summary>
        /// Called for each global script call: the Visit Generator scheduling someone to come over
        /// ("Schedule with param"). True: skip it - enough visitors today.
        /// </summary>
        public static bool Skip(VMStackFrame frame, ushort opcode)
        {
            try
            {
                if (opcode != 369) return false;
                var vm = frame.VM;
                if (vm == null || !vm.TS1 || vm.ReduxNoPause) return false;
                var label = frame.CodeOwner?.OBJ?.ChunkLabel ?? frame.Callee?.Object?.OBJ?.ChunkLabel ?? "";
                if (label.IndexOf("Visit", StringComparison.OrdinalIgnoreCase) < 0 || label.IndexOf("Generat", StringComparison.OrdinalIgnoreCase) < 0) return false;
                return !TryVisit(vm);
            }
            catch { return false; }
        }
    }
}
