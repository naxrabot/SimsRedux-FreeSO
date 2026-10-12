using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux online: the game speed (pause, 1, 2, 3) as part of the lot, so every player's
    /// game runs at the host's speed. Only the host can send it.
    ///
    /// Full = true copies the host's exact speed state (filled in on the host just before it runs),
    /// which the host sends every couple of seconds so friends who just joined catch up.
    /// </summary>
    public class VMNetSetSpeedCmd : VMNetCommandBodyAbstract
    {
        public bool Full;
        public int Speed;
        public int LastSpeed;

        public override bool AcceptFromClient { get { return false; } }

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (Full)
            {
                vm.SpeedMultiplier = Speed;
                vm.LastSpeedMultiplier = LastSpeed;
                return true;
            }
            // a question is pausing the game: the new speed starts once it's answered
            if (vm.SpeedMultiplier == -2) vm.LastSpeedMultiplier = Speed;
            else if (vm.SpeedMultiplier >= 0) vm.SpeedMultiplier = Speed;
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (FromNet) return false;
            if (Full)
            {
                // runs on the host right before the command does, so these are exactly the host's
                Speed = vm.SpeedMultiplier;
                LastSpeed = vm.LastSpeedMultiplier;
            }
            return true;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Full);
            writer.Write(Speed);
            writer.Write(LastSpeed);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Full = reader.ReadBoolean();
            Speed = reader.ReadInt32();
            LastSpeed = reader.ReadInt32();
        }
    }
}
