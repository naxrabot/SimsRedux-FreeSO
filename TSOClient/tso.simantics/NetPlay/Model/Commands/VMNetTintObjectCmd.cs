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
            return obj.IsUserMovable(vm.Context, true) == VMPlacementError.Success;
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
