using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 15.7: a Sim takes a bed (or one side of a double bed) as their own, or gives it up - "Assign Bed",
    /// "Assign Left Side", "Assign Right Side", "Unassign" on a bed. Always the Sim sending it (online: the lot sets
    /// who sent it, so only a player's own Sim). Kept on the bed (ReduxBeds), so it's saved with the lot.
    /// </summary>
    public class VMNetReduxBedCmd : VMNetCommandBodyAbstract
    {
        /// <summary>Any piece of the bed.</summary>
        public short ObjectID;
        /// <summary>0 = the left side (or the whole single bed), 1 = the right side.</summary>
        public byte Side;
        /// <summary>true = take it, false = give it up.</summary>
        public bool Assign;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!Check(vm, caller)) return false;
            var bed = vm.GetObjectById(ObjectID);
            if (Assign) ReduxBeds.Assign(vm, caller, bed, Side);
            else ReduxBeds.Unassign(vm, caller, bed);
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return Check(vm, caller);
        }

        private bool Check(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1 || caller == null || caller.IsPet || caller.Dead) return false;
            if (Side > 1) return false;
            var bed = vm.GetObjectById(ObjectID);
            if (bed == null || bed.Dead || !ReduxBeds.IsBed(bed)) return false;
            if (Side == 1 && !ReduxBeds.IsDouble(bed)) return false;
            return true;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
            writer.Write(Side);
            writer.Write(Assign);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
            Side = reader.ReadByte();
            Assign = reader.ReadBoolean();
        }
    }
}
