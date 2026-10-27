using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetInteractionCancelCmd : VMNetCommandBodyAbstract
    {
        public ushort ActionUID;
        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (caller == null) return false;

            // SimsRedux 17.4: the owning game learns from the player cancelling one of the Sim's own choices (before it goes)
            if (vm.TS1)
            {
                var q = caller.Thread?.Queue;
                if (q != null)
                    foreach (var a in q)
                        if (a.UID == ActionUID) { FSO.SimAntics.Utils.VMReduxBrain.Heard(vm, this, caller, a.Callee, a.InteractionNumber, a); break; }
            }
            caller.Thread.CancelAction(ActionUID);

            return true;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ActionUID);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ActionUID = reader.ReadUInt16();
        }

        #endregion
    }
}
