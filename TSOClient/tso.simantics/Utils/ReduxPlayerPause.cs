using FSO.SimAntics.Engine;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 18.8: the Sim waits a while for the player after the player's own order. Joey (Oct 29): "The sims jump to doing
    /// things on their own to quickly. i kept having to close out the sim to make food when i just ordered pizza within like 3
    /// seconds of each other multiple times." Sims 1 leaves a Sim you've just told what to do alone for a while; ours went
    /// back to free will a couple of game minutes after the order (VMFindBestAction.ReduxIdleCheck) - a few seconds.
    ///
    /// Now: when the player gives a Sim an order or cancels one of their actions (VMNetInteractionCmd / VMNetInteractionCancelCmd,
    /// through VMReduxBrain.Heard), or sends them to Shop Online (VMReduxShop), the Sim is marked; the mark is kept fresh while
    /// a player's order is still in their queue, and for PauseMinutes game minutes after it the Sim picks nothing on their own
    /// (VMFindBestAction - both Sims 1's own free will and the idle safety net) - unless a need is in the red (VMReduxBrain.InRed)
    /// or the house is on fire. At the faster speeds (single player) the pause is a little longer in game time, so it's still
    /// a pause on the screen: about 25 seconds at speed 1, 13 at speed 2, 6 at speed 3.
    ///
    /// LOCKSTEP: only lot state - person attribute 115 (StampAttr: the game minute of the mark within a two-day window, +1;
    /// 0 = none), the clock and the queue, set inside the commands and the Sim's own tick on every game. The speed is only
    /// looked at in single player (vm.ReduxNoPause is set on every online-style lot - a visit, online, the soak's shadow - where
    /// it's always the plain PauseMinutes).
    /// </summary>
    public static class VMReduxPlayerPause
    {
        public const int StampAttr = 115;
        /// <summary>Game minutes free will waits after the player's order (normal speed).</summary>
        public const int PauseMinutes = 25;
        private const int Window = 2 * 1440;

        private static int Now(VM vm)
        {
            var c = vm.Context.Clock;
            return (c.DayOfMonth & 1) * 1440 + c.Hours * 60 + c.Minutes + 1; // (1..2880)
        }

        /// <summary>How long the pause is on this lot right now (game minutes).</summary>
        public static int Minutes(VM vm)
        {
            if (vm == null || vm.ReduxNoPause) return PauseMinutes;
            int speed = vm.SpeedMultiplier;
            return speed >= 10 ? 60 : (speed >= 3 ? 40 : PauseMinutes);
        }

        /// <summary>The player just gave this Sim an order (or cancelled one of their actions).</summary>
        public static void Mark(VM vm, VMAvatar sim)
        {
            if (vm == null || !vm.TS1 || sim == null || sim.IsPet) return;
            var s = (short)Now(vm);
            if (sim.GetAttribute(StampAttr) != s) sim.SetAttribute(StampAttr, s);
        }

        private static int Ago(VM vm, VMAvatar sim)
        {
            int v = sim.GetAttribute(StampAttr);
            if (v <= 0) return int.MaxValue;
            int ago = Now(vm) - v;
            if (ago < 0) ago += Window;
            return ago;
        }

        /// <summary>The Sim is in the pause after the player's order.</summary>
        public static bool Paused(VM vm, VMAvatar sim)
        {
            return vm != null && vm.TS1 && sim != null && !sim.IsPet && Ago(vm, sim) < Minutes(vm);
        }

        /// <summary>Free will waits: in the pause, no need in the red. (The caller lets a fire through.)</summary>
        public static bool Holds(VM vm, VMAvatar sim)
        {
            return Paused(vm, sim) && !VMReduxBrain.InRed(sim);
        }

        /// <summary>
        /// Every tick, for every Sim (ReduxComputer.Tick): a marked Sim with a player's order still queued stays marked (the
        /// pause counts from when it's done); a mark that's run out is cleared.
        /// </summary>
        public static void Tick(VMAvatar sim)
        {
            if (sim == null || sim.GetAttribute(StampAttr) <= 0) return;
            var vm = sim.Thread?.Context?.VM;
            if (vm == null || !vm.TS1) return;
            if (!Paused(vm, sim)) { sim.SetAttribute(StampAttr, 0); return; }
            var q = sim.Thread.Queue;
            for (int i = 0; i < q.Count; i++)
            {
                var a = q[i];
                if (a.Priority == (short)VMQueuePriority.UserDriven && !a.NotifyIdle) { Mark(vm, sim); return; }
            }
        }
    }
}
