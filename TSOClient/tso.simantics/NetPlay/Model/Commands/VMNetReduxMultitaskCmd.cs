using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.2 (Part B #3): multitasking (Utils/ReduxMultitask.cs), VM command 84. Everything that changes it on the lot
    /// goes through here, so on a visit every player's game does the same thing at the same moment.
    ///   SetSwitch - the lot's game says whether multitasking is on (On): kept on every Sim on the lot (attribute 114), so a
    ///               player joining a visit gets it with the lot. Sent again by the lot's game when a Sim arrives who doesn't
    ///               have it yet.
    /// Only ever from the game running the lot (single player, or a visit's host): the host's Feature Switch decides on a visit.
    /// </summary>
    public class VMNetReduxMultitaskCmd : VMNetCommandBodyAbstract
    {
        public enum MultitaskMode : byte { SetSwitch = 0 }

        public MultitaskMode Mode;
        public bool On;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1) return false;
            try
            {
                switch (Mode)
                {
                    case MultitaskMode.SetSwitch:
                        VMReduxMultitask.SetSwitch(vm, On);
                        return true;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] multitask command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // (the game running the lot only - a visiting player never turns it on or off for the host's lot)
            return !FromNet;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(On);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (MultitaskMode)reader.ReadByte();
            On = reader.ReadBoolean();
        }
    }
}
