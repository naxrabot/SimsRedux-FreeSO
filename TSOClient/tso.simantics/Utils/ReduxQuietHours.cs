using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using System;
using System.Linq;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.10: nobody rings the doorbell at a home between 10 PM and 6 AM (the lot's own clock).
    ///
    /// Every uninvited visitor who comes to the door does it through Sims 1's own "begin visiting" (PersonGlobals): when a
    /// visitor's "person main" starts with greet status 0 on a home (residential) lot, it sets the status to -1 and calls
    /// "begin visiting" - walk to the front door, ring or knock ("visitor do knock"), wait to be greeted, go home. That covers
    /// Sims 1's own walk-bys and drop-ins from its Visit Generator, the Refresh walk-bys (ReduxVisitors) and a street Sim whose
    /// script starts again. In the quiet hours that call is skipped: the visitor turns round and leaves with Sims 1's own
    /// "Leave" (a Sim walking past on the street just walks on). Never a player's own Sim on a Singleplayer+ visit (they're
    /// invited), never on a community lot. Decided inside the lot's own simulation from its clock, so every game on a visit
    /// does the same.
    /// </summary>
    public static class ReduxQuietHours
    {
        /// <summary>Sims 1's PersonGlobals script that sends a visitor to the front door.</summary>
        public const string BeginVisitingLabel = "begin visiting";

        public static bool IsHookLabel(string label)
        {
            return label != null && label.Trim().TrimEnd('\0').Equals(BeginVisitingLabel, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>10 PM to 6 AM by the lot's clock.</summary>
        public static bool Night(VMClock clock)
        {
            return clock != null && (clock.Hours >= 22 || clock.Hours < 6);
        }

        /// <summary>A home lot - the way Sims 1's own "Is lot a Residential Lot?" asks (the lot's zoning is 0).</summary>
        public static bool Residential(VM vm)
        {
            var zones = Content.Content.Get()?.Neighborhood?.ZoningDictionary;
            short zone;
            return zones != null && zones.TryGetValue(vm.GetGlobalValue(10), out zone) && zone == 0;
        }

        /// <summary>
        /// "begin visiting" is about to run for the Sim running this frame: true to skip it (the quiet hours on a home lot) -
        /// the visitor is sent home instead.
        /// </summary>
        public static bool Skip(VMStackFrame frame)
        {
            try
            {
                var vm = frame.VM;
                var ava = frame.Caller as VMAvatar;
                if (vm == null || !vm.TS1 || ava == null || ava.Dead || ava.IsPet) return false;
                if (!Night(vm.Context.Clock) || !Residential(vm)) return false;
                if (ava.PersistID >= ReduxBetterTogether.FirstPlayerID) return false; // (a player's Sim on a visit: invited)
                if (ava.GetPersonData(VMPersonDataVariable.PersonType) != 1) return false; // (visitors only)
                // a Sim walking past on the street: walks on (its walk is looked after by the game deciding)
                if (ReduxSidewalk.IsStreet(ava)) return true;
                if (ava.Thread != null && !ava.Thread.Queue.Any(x => x.ActionRoutine?.ID == VMNetReduxTownieCmd.LeaveRoutine))
                    VMNetReduxTownieCmd.PushLeave(vm, ava);
                Console.WriteLine("[SimsRedux] quiet hours: " + (ava.Name ?? "a visitor") + " came by at " + vm.Context.Clock.Hours + ":"
                    + vm.Context.Clock.Minutes.ToString("00") + " - not ringing the doorbell at night, going home");
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] quiet hours: " + e.Message);
                return false;
            }
        }
    }
}
