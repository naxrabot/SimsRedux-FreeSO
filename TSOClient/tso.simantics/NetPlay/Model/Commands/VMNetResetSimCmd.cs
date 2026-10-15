using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: un-sticks a Sim. If you cancel an action and the Sim ignores it (stuck in a
    /// broken script, e.g. typing at the computer forever), the game resets the Sim the same way it
    /// does after a script error: they drop what they're doing and stand up, ready for new orders.
    /// Only works on Sims, and if the whole game was paused waiting on them, it carries on.
    /// </summary>
    public class VMNetResetSimCmd : VMNetCommandBodyAbstract
    {
        public short ObjectID;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var sim = vm.GetObjectById(ObjectID) as VMAvatar;
            if (sim == null) return false;
            if (vm.GlobalBlockingDialog == sim)
            {
                vm.GlobalBlockingDialog = null;
                if (!vm.ReduxNoPause) // (online lots were never paused)
                {
                    vm.SpeedMultiplier = vm.LastSpeedMultiplier;
                    vm.LastSpeedMultiplier = 0;
                }
            }
            sim.Thread.BlockingState = null;
            sim.Reset(vm.Context);
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // SimsRedux online: a friend can only reset their own Sim
            if (FromNet && (caller == null || caller.ObjectID != ObjectID)) return false;
            return vm.GetObjectById(ObjectID) is VMAvatar;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
        }
    }
}
