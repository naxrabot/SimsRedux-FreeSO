using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.1 (Part B #7): the chore board (Utils/ReduxChores.cs). Everything that changes a Sim's chores on the lot
    /// goes through this command (VM command 80), so on a visit every player's game does the same thing at the same moment.
    ///   SetSchedule - a Sim's chores at time A and B (MaskA/TimeA, MaskB/TimeB: half hour of the day 0-47, -1 none) and
    ///                 their pocket money per chore (Pay 0-3), from the board / the side file of the game running the lot.
    ///   StartNow    - "Do Chores Now": starts these chores (MaskA) now.
    ///   Stop        - stops the chores still to do.
    /// Only ever from the game running the lot (single player, or a visit's host) and only for the family living there: a
    /// visiting player can't change the host's Sims, and guests' own Sims don't do chores at someone else's house.
    /// </summary>
    public class VMNetReduxChoresCmd : VMNetCommandBodyAbstract
    {
        public enum ChoreMode : byte { SetSchedule = 0, StartNow = 1, Stop = 2 }

        public ChoreMode Mode;
        public short A;
        public ushort MaskA, MaskB;
        public sbyte TimeA = -1, TimeB = -1;
        public byte Pay;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1) return false;
            try
            {
                var sim = vm.GetObjectById(A) as VMAvatar;
                if (sim == null || sim.IsPet || sim.Dead || !ReduxComputer.IsHouseholdSim(vm, sim)) return false;
                switch (Mode)
                {
                    case ChoreMode.SetSchedule:
                        VMReduxChores.SetSchedule(vm, sim, MaskA, Clamp(TimeA), MaskB, Clamp(TimeB), Math.Min((int)Pay, 3));
                        return true;
                    case ChoreMode.StartNow:
                        return VMReduxChores.Start(vm, sim, MaskA & VMReduxChores.ChoreMask);
                    case ChoreMode.Stop:
                        VMReduxChores.Stop(vm, sim);
                        return true;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] chores command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private static int Clamp(sbyte t) { return (t < 0 || t > 47) ? -1 : t; }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // (the game running the lot only - a visiting player never changes the host family's chores)
            return !FromNet;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(A);
            writer.Write(MaskA);
            writer.Write(MaskB);
            writer.Write(TimeA);
            writer.Write(TimeB);
            writer.Write(Pay);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (ChoreMode)reader.ReadByte();
            A = reader.ReadInt16();
            MaskA = reader.ReadUInt16();
            MaskB = reader.ReadUInt16();
            TimeA = reader.ReadSByte();
            TimeB = reader.ReadSByte();
            Pay = reader.ReadByte();
        }
    }
}
