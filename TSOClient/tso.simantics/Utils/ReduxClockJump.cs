using FSO.SimAntics.Model;
using System;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux real-time mode: the clock jumped (to the real time, when a lot loads or the clock catches up).
    ///
    /// Sims 1's car pool counts missed work days from each Sim's "last day at work" (person data 69, a day of the
    /// month; -1 is a day off - ReduxWeek). A jump mustn't make anyone look like they skipped work:
    ///  - when the date moves on, a Sim who was all square (worked that day or the day before) is all square on the
    ///    new day, so the days jumped over aren't missed days (one already behind stays as far behind);
    ///  - when the jump goes past today's car pool (it comes an hour before work starts), today counts as a day
    ///    off: no car pool, no warning, no firing (and no pay). If the car pool is still to come, it comes as usual.
    /// Runs inside VMNetSetTimeCmd, so every player's game on a visit does the same.
    /// </summary>
    public static class ReduxClockJump
    {
        /// <summary>Days since 1 January 1997 on the game's 30-day-month calendar.</summary>
        public static int Abs(VMClock c)
        {
            return (c.Year - 1997) * 360 + (c.Month - 1) * 30 + (c.DayOfMonth - 1);
        }

        private static int Yesterday(int day) { return day > 1 ? day - 1 : 30; }

        public static void After(VM vm, int oldAbs, int oldMinute)
        {
            try
            {
                if (vm == null || !vm.TS1) return;
                var clock = vm.Context.Clock;
                int newAbs = Abs(clock), newMinute = clock.Hours * 60 + clock.Minutes;
                int days = newAbs - oldAbs;
                int today = clock.DayOfMonth;
                int weekday = ReduxWeek.Day(clock);
                foreach (var e in vm.Context.ObjectQueries.Avatars)
                {
                    var a = e as VMAvatar;
                    if (a == null || a.IsChild) continue;
                    int job = a.GetPersonData(VMPersonDataVariable.JobType);
                    if (job <= 0) continue;
                    int last = a.GetPersonData((VMPersonDataVariable)69);
                    if (last == -1) continue; // a day off (ReduxWeek looks after it)
                    if (days != 0 && last >= 1 && last <= 30)
                    {
                        // went to work on the old day or the day before (all square): all square on the new day too -
                        // the car pool comes as usual today. Behind already: they stay as far behind as they were.
                        int oldToday = ((oldAbs % 30) + 30) % 30 + 1;
                        if (days > 0 && (last == oldToday || last == Yesterday(oldToday))) last = Yesterday(today);
                        else last = (((last - 1 + days) % 30) + 30) % 30 + 1;
                    }
                    if (last != today && ReduxWeek.Works(job, weekday))
                    {
                        var lvl = Content.Content.Get().Jobs?.GetJobLevel((short)job, a.GetPersonData(VMPersonDataVariable.JobPromotionLevel));
                        if (lvl != null)
                        {
                            int carPool = Math.Max(0, (lvl.StartTime % 24) * 60 - 60);
                            // (jumped past it: from an earlier day, or from earlier today)
                            bool skipped = newMinute >= carPool && (days > 0 || oldMinute < carPool);
                            if (skipped) last = today;
                        }
                    }
                    if (last != a.GetPersonData((VMPersonDataVariable)69)) a.SetPersonData((VMPersonDataVariable)69, (short)last);
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] real time: couldn't keep the work days right: " + e.Message); }
        }
    }
}
