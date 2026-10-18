using System.IO;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: moves a wall item (a painting, a shelf, a lamp...) up or down the wall.
    /// Works in multiplayer because everyone runs the same command.
    /// </summary>
    public class VMNetLiftObjectCmd : VMNetCommandBodyAbstract
    {
        public short ObjectID;
        public short Lift;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID);
            if (obj == null || !(obj is VMGameObject)) return false;
            foreach (var part in obj.MultitileGroup.Objects)
                (part as VMGameObject)?.SetReduxLift(Lift);
            return true;
        }

        /// <summary>Hangs on a wall (paintings, shelves, wall lamps...).</summary>
        public static bool IsWallItem(VMEntity obj)
        {
            if (obj == null) return false;
            // (the Sims 1 marks things hung on a wall to be hidden when walls are cut away; doors and
            //  windows sit in the wall instead)
            var wallFlags = obj.GetValue(VMStackObjectVariable.WallPlacementFlags);
            var flags = (VMEntityFlags)obj.GetValue(VMStackObjectVariable.Flags);
            var flags2 = (VMEntityFlags2)obj.GetValue(VMStackObjectVariable.FlagField2);
            return (wallFlags & 15) != 0 && (flags & VMEntityFlags.HideForCutaway) != 0
                && (flags2 & (VMEntityFlags2.ArchitectualWindow | VMEntityFlags2.ArchitectualDoor)) == 0;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID);
            if (obj == null || !(obj is VMGameObject)) return false;
            if (!vm.TS1 && caller == null) return false;
            foreach (var part in obj.MultitileGroup.Objects)
            {
                if (part is VMAvatar) return false;
                if (!IsWallItem(part)) return false;
                var flags = (VMMovementFlags)part.GetValue(VMStackObjectVariable.MovementFlags);
                if ((flags & VMMovementFlags.PlayersCanMove) == 0 && !vm.Context.Cheats.MoveObjects) return false;
            }
            return true;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
            writer.Write(Lift);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
            Lift = reader.ReadInt16();
        }
    }
}
