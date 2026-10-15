using FSO.SimAntics.Primitives;
using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetDialogResponseCmd : VMNetCommandBodyAbstract
    {
        public byte ResponseCode;
        public string ResponseText;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (ResponseText.Length > 1024) ResponseText = ResponseText.Substring(0, 1024);

            VMEntity owner = caller;
            if (vm.TS1)
            {
                owner = vm.GlobalBlockingDialog;
                vm.SpeedMultiplier = vm.LastSpeedMultiplier;
                vm.LastSpeedMultiplier = 0;
                vm.GlobalBlockingDialog = null;
            }
            if (owner == null ||
                owner.Thread.BlockingState == null || !(owner.Thread.BlockingState is VMDialogResult)) return false;
            var state = (VMDialogResult)owner.Thread.BlockingState;
            state.Responded = true;
            state.ResponseCode = ResponseCode;
            state.ResponseText = ResponseText;
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // SimsRedux online: a visiting player only answers questions about their own Sim -
            // other Sims' questions (the lot's family, NPCs) are answered by the lot itself
            if (FromNet && vm.TS1)
            {
                var asker = vm.GlobalBlockingDialog;
                if (asker == null) return false;
                if (caller != null && asker == caller) return true;
                // 2C: a family's owner is at home - they answer for their family and the house itself
                // (the carpool, the phone...), just never for another player's Sim
                if (vm.ReduxFamilyControl.ContainsKey(ActorUID))
                    return !(asker is VMAvatar) || asker.PersistID < 100001 || asker.PersistID == ActorUID;
                return false;
            }
            return true;
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            if (ResponseText.Length > 1024) ResponseText = ResponseText.Substring(0, 1024);
            base.SerializeInto(writer);
            writer.Write(ResponseCode);
            writer.Write(ResponseText);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ResponseCode = reader.ReadByte();
            ResponseText = reader.ReadString();
        }
        #endregion
    }
}
