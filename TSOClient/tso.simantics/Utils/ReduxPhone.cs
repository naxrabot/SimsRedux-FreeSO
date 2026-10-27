using System.Linq;
using FSO.SimAntics.Engine;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.1 (P1): the cellphone on the lot. Every grown-up player Sim has a cellphone in their inventory (the
    /// game's UIReduxInventory / UIReduxCellphone); while its menu is open the Sim has it out - a small phone drawn over
    /// their head on every player's screen. That's the only thing the lot keeps: person attribute 105 (the lot's tick
    /// when it came out, +1; 0 = put away). Attributes are already saved with the lot and sent to every player joining,
    /// so nothing in the save format changes. Set only through VMNetReduxPhoneCmd (cmd 83), so every game agrees. Who owns
    /// a phone is the player's own game's business (Simitone's ReduxCellphone side file) - the lot never needs it.
    /// Attributes 106-109 are kept free for the cellphone (17.2: 106 = SoldAttr).
    /// </summary>
    public static class VMReduxPhone
    {
        /// <summary>Person attribute: when the phone came out ((tick % TickWrap) + 1), 0 = put away.</summary>
        public const int OutAttr = 105;
        /// <summary>The tick is kept short (an attribute is 16 bits).</summary>
        public const int TickWrap = 30000;
        /// <summary>How long the phone shows at most (30 ticks a second: about a minute), even if "put away" never came.</summary>
        public const int ShowTicks = 1800;
        /// <summary>A new cellphone (the home computer's Shop Online, a Pharmacy Shelf), and what selling one back gives.</summary>
        public const int BuyPrice = 100, SellPrice = 20;
        /// <summary>17.2: person attribute - the game hour (HourStamp) the Sim last sold a cellphone on this lot (0 = never).</summary>
        public const int SoldAttr = 106;

        /// <summary>17.2: this game hour on the lot, as a short (day of month * 24 + hour + 1) - the same on every game.</summary>
        public static short HourStamp(VM vm)
        {
            var c = vm.Context.Clock;
            return (short)(System.Math.Max(1, System.Math.Min(31, c.DayOfMonth)) * 24 + System.Math.Max(0, System.Math.Min(23, c.Hours)) + 1);
        }

        // ---------------------------------------------------------------- 17.2: packages by the mailbox

        /// <summary>
        /// The Redux Package (Content/redux/mods/Downloads/redux/redux_package.iff - our own drawn cardboard box, no scripts,
        /// in no catalog): a Shop Online order waiting by the mailbox. Put down and taken away only by VMNetReduxPhoneCmd
        /// (Place / Remove) from the game running the lot, so every game has the same box. Object attribute 0 is the order
        /// it holds (its tag in the player's own redux_packages.txt - what's inside is only that game's business).
        /// </summary>
        public const uint PackageGUID = 0x7C3A5E30;
        public const int TagAttr = 0;

        public static bool IsPackage(VMEntity e) { return e?.Object?.OBJ != null && e.Object.OBJ.GUID == PackageGUID; }

        /// <summary>
        /// The mail carrier has just put the day's mail in the mailbox (every game, at the same moment - the mailbox's own
        /// "Deliver Mail" calls its "display help for bills"): the game running the lot brings out the packages due.
        /// </summary>
        public static event System.Action<VM, VMEntity> MailDelivered;

        public static bool IsHookLabel(string label)
        {
            return string.Equals((label ?? "").Trim(), "display help for bills", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>VMRoutine's hook (only tells - never changes the lot, so it can't put games out of step).</summary>
        public static void Hook(VMStackFrame frame)
        {
            try
            {
                var vm = frame?.VM;
                var carrier = frame?.Caller as VMAvatar;
                if (vm == null || !vm.TS1 || carrier == null || frame.Callee == null) return;
                // (the carrier - not a family Sim checking the mail)
                var fam = vm.TS1State?.CurrentFamily;
                if (fam != null && (fam.FamilyGUIDs ?? new uint[0]).Contains(carrier.Object.OBJ.GUID)) return;
                MailDelivered?.Invoke(vm, frame.Callee);
            }
            catch { }
        }

        /// <summary>The Sim has their cellphone out now (drawing only - nothing on the lot depends on it).</summary>
        public static bool IsOut(VM vm, VMAvatar ava)
        {
            if (vm == null || ava == null || ava.Dead) return false;
            int v = ava.GetAttribute(OutAttr);
            if (v <= 0) return false;
            int now = (int)(vm.Scheduler.CurrentTickID % TickWrap);
            int since = ((now - (v - 1)) % TickWrap + TickWrap) % TickWrap;
            return since < ShowTicks;
        }

        /// <summary>Takes the phone out (or puts it away). Run by the command, the same on every game.</summary>
        public static void Set(VM vm, VMAvatar ava, bool on)
        {
            if (vm == null || ava == null) return;
            short v = on ? (short)(vm.Scheduler.CurrentTickID % TickWrap + 1) : (short)0;
            if (ava.GetAttribute(OutAttr) != v) ava.SetAttribute(OutAttr, v);
        }
    }
}
