using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: days of the week, and days off work.
    ///
    /// The Sims 1 game calendar has 30-day months, so the week is counted straight from the date (the
    /// game's first day, 1 June 1997, is a Monday). Every career works five days out of seven: most have
    /// the weekend off; the ones that work weekends (hospitals, police, sports, shows, restaurants...) have
    /// two days off in the week instead.
    ///
    /// On a day off the car pool doesn't come, and the day counts as one they were meant to be away (so
    /// the boss doesn't warn or fire them for it).
    /// </summary>
    public static class ReduxWeek
    {
        public static readonly string[] Names = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        public static readonly string[] Short = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        /// <summary>0 = Monday ... 6 = Sunday.</summary>
        public static int Day(VMClock clock)
        {
            long abs = (long)(clock.Year - 1997) * 360 + (clock.Month - 1) * 30 + (clock.DayOfMonth - 1);
            return (int)(((abs - 150) % 7 + 7) % 7); // (1 June 1997 = Monday)
        }

        /// <summary>The weekday (0 = Monday) of any date on the game's calendar.</summary>
        public static int DayOf(int year, int month, int day)
        {
            long abs = (long)(year - 1997) * 360 + (month - 1) * 30 + (day - 1);
            return (int)(((abs - 150) % 7 + 7) % 7);
        }

        // which days each career works (bit 0 = Monday ... bit 6 = Sunday)
        private const int MonFri = 0x1F, TueSat = 0x3E, WedSun = 0x7C, ThuMon = 0x79, SunThu = 0x4F;
        private static readonly Dictionary<int, int> Careers = new Dictionary<int, int>
        {
            { 1, MonFri },  // Business
            { 2, WedSun },  // Entertainment (shows at the weekend)
            { 3, ThuMon },  // Law Enforcement
            { 4, SunThu },  // Life of Crime
            { 5, TueSat },  // Medicine
            { 6, SunThu },  // Military
            { 7, MonFri },  // Politics
            { 8, WedSun },  // Pro Athlete (games at the weekend)
            { 9, MonFri },  // Science
            { 10, WedSun }, // Xtreme
            { 12, WedSun }, // Musician
            { 13, ThuMon }, // Slacker
            { 14, SunThu }, // Paranormal
            { 15, MonFri }, // Journalism
            { 16, MonFri }, // Hacker
            { 17, TueSat }, // Fashion
            { 18, MonFri }, // Education
            { 19, TueSat }, // Animal Care
            { 20, WedSun }, // Culinary
            { 21, ThuMon }, // Circus
        };

        public static bool Works(int career, int day)
        {
            int mask;
            if (!Careers.TryGetValue(career, out mask)) mask = MonFri;
            return (mask & (1 << day)) != 0;
        }

        /// <summary>The days a career works, as text ("Monday to Friday").</summary>
        public static string Schedule(int career)
        {
            int mask;
            if (!Careers.TryGetValue(career, out mask)) mask = MonFri;
            int first = -1;
            for (int d = 0; d < 7; d++) if ((mask & (1 << d)) != 0 && (mask & (1 << ((d + 6) % 7))) == 0) first = d;
            if (first < 0) return "every day";
            return Names[first] + " to " + Names[(first + 4) % 7];
        }

        private static readonly Dictionary<VMRoutine, int> DueRoutines = new Dictionary<VMRoutine, int>();
        private static readonly object Lock = new object();

        /// <summary>
        /// Called for each script call. Sims 1's car pool asks "was stack object due at work?" - on a day off
        /// the answer is no (and the day is marked as one they were meant to be away): -1, answer FALSE.
        /// Its "car pool comes in 2 hours" warnings are skipped when nobody here works today: +1, answer TRUE.
        /// 0: carry on as normal.
        /// </summary>
        public static int DayOff(VMStackFrame frame, ushort opcode)
        {
            try
            {
                var routine = frame.ScopeResource?.GetRoutine(opcode) as VMRoutine;
                if (routine == null) return 0;
                int kind;
                lock (Lock)
                {
                    if (!DueRoutines.TryGetValue(routine, out kind))
                    {
                        var label = routine.Chunk?.ChunkLabel ?? "";
                        kind = label.IndexOf("due at work", StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                            : (label.IndexOf("warnings car pool", StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                            : (label.IndexOf("school bus schedule", StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : 0));
                        DueRoutines[routine] = kind;
                    }
                }
                if (kind == 0) return 0;
                var vm = frame.VM;
                if (vm == null) return 0;
                int day = Day(vm.Context.Clock);
                // school is Monday to Friday: no school bus at the weekend
                if (kind == 3) return day >= 5 ? 1 : 0;
                if (kind == 2)
                {
                    foreach (var a in vm.Context.ObjectQueries.Avatars)
                    {
                        var ava = a as VMAvatar;
                        if (ava == null) continue;
                        var c = ava.GetPersonData(VMPersonDataVariable.JobType);
                        if (c > 0 && Works(c, day)) return 0;
                    }
                    return 1;
                }
                var sim = frame.StackObject as VMAvatar;
                if (sim == null) return 0;
                var career = sim.GetPersonData(VMPersonDataVariable.JobType);
                if (career <= 0 || Works(career, day)) return 0;
                // a day off: counted as a day they were meant to be away (no warning, no firing)
                sim.SetPersonData((VMPersonDataVariable)69, vm.GetGlobalValue(1));
                return -1;
            }
            catch { return 0; }
        }
    }
}
