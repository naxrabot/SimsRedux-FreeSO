using System;
using System.Collections.Generic;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.1 (P1): a Sim takes their cellphone out (Out = true) or puts it away - the phone drawn over their head
    /// while its menu is open (VMReduxPhone, person attribute 105). Always the Sim sending it (online: the lot sets who
    /// sent it, so only a player's own Sim), and only a grown-up. A visiting player's game may send it at most once a
    /// second. What the phone then does goes through the lot's own commands (the house phone's interactions, the visit's
    /// and Redux's own), never this one.
    /// </summary>
    public class VMNetReduxPhoneCmd : VMNetCommandBodyAbstract
    {
        public bool Out;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!Check(vm, caller)) return false;
            VMReduxPhone.Set(vm, caller, Out);
            return true;
        }

        private static readonly Dictionary<uint, DateTime> Last = new Dictionary<uint, DateTime>();

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!Check(vm, caller)) return false;
            if (!FromNet) return true;
            var now = DateTime.UtcNow;
            lock (Last)
            {
                DateTime t;
                if (Last.TryGetValue(caller.PersistID, out t) && now - t < TimeSpan.FromSeconds(1)) return false;
                if (Last.Count > 200) Last.Clear();
                Last[caller.PersistID] = now;
            }
            return true;
        }

        private static bool Check(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1 || caller == null) return false;
            return ReduxWooHoo.Grownup(caller); // (a grown-up, on the lot - not a pet, a child, a ghost or the maid)
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Out);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Out = reader.ReadBoolean();
        }
    }
}
