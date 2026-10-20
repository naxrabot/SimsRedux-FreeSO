using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: sell a finished novel from a Sim's inventory (Utils/ReduxComputer.cs). Only ever the Sim
    /// sending it (on an online lot: a player's own Sim - the lot sets who sent it), and only a novel that
    /// Sim really has, so every player's game pays the same family the same amount.
    /// </summary>
    public class VMNetReduxNovelCmd : VMNetCommandBodyAbstract
    {
        /// <summary>Which of the Sim's unsold novels (its person data slot, ReduxComputer.ShelfFirst-ShelfLast).</summary>
        public short Slot;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (caller == null || !vm.TS1) return false;
            return FSO.SimAntics.Utils.ReduxComputer.SellNovel(caller, Slot);
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (caller == null || caller.IsPet || !vm.TS1) return false;
            return FSO.SimAntics.Utils.ReduxComputer.CanSellNovel(caller, Slot);
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Slot);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Slot = reader.ReadInt16();
        }
    }
}
