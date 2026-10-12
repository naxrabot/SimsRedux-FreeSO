using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// Causes a sim to begin being deleted. Can be user initiated, but they will be disconnected when their sim is fully gone.
    /// </summary>
    public class VMNetSimLeaveCmd : VMNetCommandBodyAbstract
    {
        public override bool Execute(VM vm, VMAvatar sim)
        {
            if (vm.TS1 && sim != null && !sim.Dead)
            {
                // SimsRedux multiplayer: a visiting friend left, so their Sim goes with them.
                // (TS1 has no "leave lot" routine for a player's Sim like TSO does.)
                // (only network players ever leave; the host's own id, 65537, never does)
                if (sim.PersistID == ActorUID && sim.PersistID != 65537)
                {
                    vm.Context.ObjectQueries.RemoveAvatarPersist(sim.PersistID);
                    sim.Delete(true, vm.Context);
                }
                return true;
            }
            if (sim != null && !sim.Dead)
            {
                // the user has left the lot with their sim still on it...
                // force leave lot. generate an action with incredibly high priority and cancel current

                sim.UserLeaveLot();
            }
            return true;
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
        }
        #endregion
    }
}
