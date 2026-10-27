namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.1 (P1): the cellphone on the lot. Every grown-up player Sim has a cellphone in their inventory (the
    /// game's UIReduxInventory / UIReduxCellphone); while its menu is open the Sim has it out - a small phone drawn over
    /// their head on every player's screen. That's the only thing the lot keeps: person attribute 105 (the lot's tick
    /// when it came out, +1; 0 = put away). Attributes are already saved with the lot and sent to every player joining,
    /// so nothing in the save format changes. Set only through VMNetReduxPhoneCmd (cmd 83), so every game agrees.
    /// Attributes 106-109 are kept free for the cellphone.
    /// </summary>
    public static class VMReduxPhone
    {
        /// <summary>Person attribute: when the phone came out ((tick % TickWrap) + 1), 0 = put away.</summary>
        public const int OutAttr = 105;
        /// <summary>The tick is kept short (an attribute is 16 bits).</summary>
        public const int TickWrap = 30000;
        /// <summary>How long the phone shows at most (30 ticks a second: about a minute), even if "put away" never came.</summary>
        public const int ShowTicks = 1800;

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
