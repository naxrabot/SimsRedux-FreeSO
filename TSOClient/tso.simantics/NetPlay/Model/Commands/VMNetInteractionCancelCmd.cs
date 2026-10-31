using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetInteractionCancelCmd : VMNetCommandBodyAbstract
    {
        public ushort ActionUID;
        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (caller == null) return false;

            // SimsRedux 19.6: a Sim away from the lot (at work, at school, in a car) can't have what they're doing now
            // cancelled - it was the only thing that would ever bring them back (Utils/ReduxAway.cs). Refused the same
            // way on every game; the waiting orders behind it can still be cancelled.
            if (vm.TS1 && FSO.SimAntics.Utils.ReduxAway.CancelRefused(caller, ActionUID))
            {
                System.Console.WriteLine("[SimsRedux] away: " + caller.Name + " is off the lot - the cancel is ignored");
                return true;
            }

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
