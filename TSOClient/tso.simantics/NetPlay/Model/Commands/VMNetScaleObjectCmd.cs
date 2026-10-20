using System.IO;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: makes an object (furniture, a painting, a wall lamp...) bigger or smaller.
    /// Only how it's drawn changes - it keeps its normal tiles, so placement, routing and Sims using it are untouched.
    /// Works in multiplayer because everyone runs the same command; the same people who may move it may resize it.
    /// </summary>
    public class VMNetScaleObjectCmd : VMNetCommandBodyAbstract
    {
        public short ObjectID;
        /// <summary>Percent (50-200); 0 or 100 = normal size.</summary>
        public short Scale;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID);
            if (obj == null || !(obj is VMGameObject)) return false;
            if (!CanScale(vm, caller, obj)) return false;
            foreach (var part in obj.MultitileGroup.Objects)
                (part as VMGameObject)?.SetReduxScale(Scale);
            return true;
        }

        /// <summary>The same rules as moving an object (VMNetMoveObjectCmd).</summary>
        private static bool CanScale(VM vm, VMAvatar caller, VMEntity obj)
        {
            if (!vm.TS1)
            {
                if (caller == null) return false;
                if (caller.AvatarState.Permissions < VMTSOAvatarPermissions.Admin
                    && obj.IsUserMovable(vm.Context, false) != VMPlacementError.Success) return false;
                if (!vm.PlatformState.Validator.CanMoveObject(caller, obj)) return false;
            }
            foreach (var part in obj.MultitileGroup.Objects)
            {
                if (part is VMAvatar) return false;
                var flags = (VMMovementFlags)part.GetValue(VMStackObjectVariable.MovementFlags);
                if ((flags & VMMovementFlags.PlayersCanMove) == 0 && !vm.Context.Cheats.MoveObjects) return false;
            }
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID);
            if (obj == null || !(obj is VMGameObject)) return false;
            if (Scale != 0 && (Scale < VMGameObject.MinScale || Scale > VMGameObject.MaxScale)) return false;
            return CanScale(vm, caller, obj);
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
            writer.Write(Scale);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
            Scale = reader.ReadInt16();
        }
    }
}
