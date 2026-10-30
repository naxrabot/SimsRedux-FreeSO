using System;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 18.8: Shop Online on the home computer takes a moment. Joey (Oct 29): "dont make the purchase instant, make
    /// the sim goto the computer and type for a few seconds and then its purchased." Picking a purchase (the cellphone -
    /// VMNetReduxPhoneCmd Buy - or the WooHoo supplies - VMNetReduxWooHooCmd Buy, Source 2) only sends the Sim to the
    /// computer: they walk over, sit down and type (the computer's own "Play" - the same animations as the game's own
    /// computer use - shown as "Shop Online" in their queue). After TypeMinutes game minutes at the keyboard the order
    /// goes through: the family's money is taken then, and Done tells the games (the buyer's game puts the package on its
    /// way - Simitone's ReduxPackages). Cancelled or interrupted before that: nothing is bought.
    ///
    /// LOCKSTEP: started only from those two commands (the game running the lot), finished from VMAvatar's tick
    /// (ReduxComputer.Tick) - only lot state: the Sim's queue, the clock and three person attributes, which are saved with
    /// the lot and sent to anyone joining, so a reload or a visitor arriving mid-order changes nothing:
    ///   107 OrderAttr  the order: item + 1 (bits 0-3; 0 = not shopping) | count &lt;&lt; 4
    ///   108 PriceAttr  its price (§)
    ///   109 TypedAttr  ticks typed so far
    /// (105-109 are the cellphone's attributes - VMReduxPhone.)
    /// </summary>
    public static class VMReduxShop
    {
        public const int OrderAttr = 107, PriceAttr = 108, TypedAttr = 109;
        /// <summary>The order in the Sim's queue (the computer's own "Play" under this name).</summary>
        public const string ActionName = "Shop Online";
        /// <summary>The items: 0 protection, 1 birth control pills, 2 pregnancy tests (VMNetReduxWooHooCmd), 3 a cellphone.</summary>
        public const int ItemPhone = 3;
        /// <summary>Game minutes at the keyboard before the order goes through (a few seconds at normal speed).</summary>
        public const int TypeMinutes = 5;

        public enum Result { Bought, Cancelled, Failed }

        /// <summary>
        /// An order finished, on every game at the same tick: (the lot, the Sim, item, count, price, what happened, why it
        /// failed - plain English, "" for nothing to say). Only the buyer's game acts on it.
        /// </summary>
        public static event Action<VM, VMAvatar, int, int, int, Result, string> Done;

        /// <summary>This Sim is on their way to (or at) the computer with an order.</summary>
        public static bool Shopping(VMAvatar sim) { return sim != null && sim.GetAttribute(OrderAttr) != 0; }

        private static string FirstName(VMAvatar sim)
        {
            var n = (sim?.Name ?? "").Trim();
            if (n.IndexOf(' ') > 0) n = n.Substring(0, n.IndexOf(' '));
            return n.Length == 0 ? "Your Sim" : n;
        }

        /// <summary>
        /// The Sim goes to the computer to order (the command's part). Null when they're on their way; otherwise why not
        /// (plain English, "" for nothing to say). No money is taken here.
        /// </summary>
        public static string Start(VM vm, VMAvatar sim, VMEntity computer, int item, int count, int price)
        {
            if (vm == null || !vm.TS1 || sim?.Thread == null || sim.Dead || computer == null || computer.Dead || computer is VMAvatar) return "";
            if (item < 0 || item > 14 || count < 1 || price < 0) return "";
            if (Shopping(sim)) return FirstName(sim) + " is already ordering something on the computer.";
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null) return "";
            if (fam.Budget < price) return "Your family doesn't have §" + price.ToString("N0") + ".";

            var target = computer.MultitileGroup?.GetInteractionGroupLeader(computer) ?? computer;
            bool queued = false;
            uint play;
            if (ReduxComputer.PlayOf(target, out play))
            {
                // the computer's own "Play": walk over, sit down, type (kept going by ReduxComputer.Decide until the order's in)
                var action = target.GetAction((int)play, sim, vm.Context, false, new short[4]);
                if (action != null)
                {
                    action.Name = ActionName;
                    action.Priority = (short)VMQueuePriority.UserDriven;
                    sim.Thread.EnqueueAction(action);
                    queued = true;
                }
            }
            // (a computer without the usual "Play": the Sim stands at it for the moment instead)
            if (!queued) queued = ReduxWooHoo.PushWait(vm, sim, target, 30000, ActionName);
            if (!queued) return "";
            sim.SetAttribute(OrderAttr, (short)((item + 1) | (Math.Min(count, 2047) << 4)));
            sim.SetAttribute(PriceAttr, (short)Math.Min(price, short.MaxValue));
            sim.SetAttribute(TypedAttr, 0);
            VMReduxPlayerPause.Mark(vm, sim); // (the player's own order: free will waits a while after it)
            return null;
        }

        /// <summary>Every tick, for every Sim (ReduxComputer.Tick): an order in progress is counted, finished or dropped.</summary>
        public static void Tick(VMAvatar sim)
        {
            if (sim == null || sim.GetAttribute(OrderAttr) == 0) return;
            var thread = sim.Thread;
            var vm = thread?.Context?.VM;
            if (vm == null) return;
            VMQueuedAction action = null;
            var q = thread.Queue;
            for (int i = 0; i < q.Count; i++)
                if (q[i].Name == ActionName) { action = q[i]; break; }
            int typed = sim.GetAttribute(TypedAttr);
            // gone from the queue: taken off before they got there (nothing bought) - or the computer's routine ended on
            // its own after they'd typed (bought: they did use the computer)
            if (action == null) { Finish(vm, sim, typed > 0, null); return; }
            // cancelled or interrupted (a fire, a need, the player): nothing is bought
            if (action.NotifyIdle) { Finish(vm, sim, false, null); return; }
            if (action != thread.ActiveAction || !ReduxComputer.AtKeyboard(sim, action)) return;
            int need = TypeMinutes * Math.Max(1, vm.Context.Clock.TicksPerMinute);
            typed++;
            ReduxComputer.ShowShopProgress(sim, typed * 100 / Math.Max(1, need));
            if (typed < need) { sim.SetAttribute(TypedAttr, (short)Math.Min(short.MaxValue, typed)); return; }
            Finish(vm, sim, true, null);
            thread.CancelAction(action.UID); // (the order's in: they get up)
        }

        private static void Finish(VM vm, VMAvatar sim, bool buy, string why)
        {
            int code = sim.GetAttribute(OrderAttr), price = Math.Max(0, (int)sim.GetAttribute(PriceAttr));
            sim.SetAttribute(OrderAttr, 0);
            sim.SetAttribute(PriceAttr, 0);
            sim.SetAttribute(TypedAttr, 0);
            int item = (code & 15) - 1, count = (code >> 4) & 0x7FF;
            var result = Result.Cancelled;
            if (buy)
            {
                var fam = vm.TS1State?.CurrentFamily;
                bool family = fam != null && sim.Object?.OBJ != null && Array.IndexOf(fam.FamilyGUIDs ?? new uint[0], sim.Object.OBJ.GUID) >= 0;
                if (!family) { result = Result.Failed; why = ""; }
                else if (fam.Budget < price) { result = Result.Failed; why = "Your family doesn't have §" + price.ToString("N0") + " any more - nothing was bought."; }
                else
                {
                    fam.Budget -= price;
                    result = Result.Bought;
                    if (price > 0) { try { sim.ShowMoneyHeadline(-price); } catch { } }
                }
            }
            try { Done?.Invoke(vm, sim, item, count, price, result, why ?? ""); } catch (Exception e) { Console.WriteLine("[SimsRedux] shop: " + e.Message); }
        }
    }
}
