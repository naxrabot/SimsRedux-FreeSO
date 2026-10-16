using System.IO;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: changes an object's colour tint (see FSO.LotView.Utils.ReduxTint).
    /// Free, and works in multiplayer because everyone runs the same command.
    /// </summary>
    public class VMNetTintObjectCmd : VMNetCommandBodyAbstract
    {
        public short ObjectID;
        public uint Tint;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID);
            if (obj == null || !(obj is VMGameObject)) return false;
            foreach (var part in obj.MultitileGroup.Objects)
            {
                var g = part as VMGameObject;
                if (g != null) g.SetReduxTint(Tint);
            }
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID);
            if (obj == null || !(obj is VMGameObject)) return false;
            if (!vm.TS1 && caller == null) return false;
            if (vm.Context.Cheats.MoveObjects) return true;
            // SimsRedux: a colour can change on anything players may move, even with a Sim using it or
            // something sitting on it (moving/selling checks are stricter than a lick of paint needs)
            foreach (var part in obj.MultitileGroup.Objects)
            {
                if (part is VMAvatar) return false;
                var flags = (VMMovementFlags)part.GetValue(VMStackObjectVariable.MovementFlags);
                if ((flags & VMMovementFlags.PlayersCanMove) == 0) return false;
                if (vm.Context.IsUserOutOfBounds(part.Position)) return false;
            }
            return true;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
            writer.Write(Tint);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
            Tint = reader.ReadUInt32();
        }
    }
}
