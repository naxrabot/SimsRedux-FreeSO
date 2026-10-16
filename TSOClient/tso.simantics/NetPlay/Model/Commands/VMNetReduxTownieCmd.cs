using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.IO;
using System.Linq;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: townies and walk-bys. Everything that changes the lot (a townie arriving, heading
    /// home, leaving, or being marked as greeted) happens through this command, so on an online lot
    /// every player's game does exactly the same thing at the same moment. Only the lot's own game
    /// (single player, or the server's lot) ever sends it.
    /// </summary>
    public class VMNetReduxTownieCmd : VMNetCommandBodyAbstract
    {
        public enum TownieMode : byte
        {
            /// <summary>Make the Sim GUID and put them by object AnchorID.</summary>
            Arrive = 0,
            /// <summary>Sim ObjectID heads home with Sims 1's own global "Leave".</summary>
            Leave = 1,
            /// <summary>Object ObjectID is taken off the lot.</summary>
            Remove = 2,
            /// <summary>Sim ObjectID gets person type PersonType and greet status Greet (-1 = leave as is).</summary>
            SetPerson = 3,
        }

        /// <summary>Sims 1's global "Leave" routine (route to a random portal, then go).</summary>
        public const ushort LeaveRoutine = 368;

        public TownieMode Mode;
        public uint GUID;
        public short AnchorID;
        public short ObjectID;
        public short PersonType = -1;
        public short Greet = -1;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            try
            {
                switch (Mode)
                {
                    case TownieMode.Arrive: return Arrive(vm);
                    case TownieMode.Leave:
                        {
                            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
                            return ava != null && PushLeave(vm, ava);
                        }
                    case TownieMode.Remove:
                        {
                            var ent = vm.GetObjectById(ObjectID);
                            if (ent == null || ent.Dead) return false;
                            ent.Delete(true, vm.Context);
                            return true;
                        }
                    case TownieMode.SetPerson:
                        {
                            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
                            if (ava == null) return false;
                            if (PersonType >= 0) ava.SetPersonData(VMPersonDataVariable.PersonType, PersonType);
                            if (Greet >= 0) ava.SetPersonData(VMPersonDataVariable.GreetStatus, Greet);
                            return true;
                        }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] townie command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private bool Arrive(VM vm)
        {
            if (vm.Context.ObjectQueries.Avatars.Any(x => x.Object?.OBJ?.GUID == GUID)) return false; // already here
            var ava = vm.Context.CreateObjectInstance(GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
            if (ava == null) return false;
            if (ava.IsPet)
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            // a visitor (made the way the game makes any neighbour, with their neighbour data)
            ava.SetPersonData(VMPersonDataVariable.PersonType, 1);
            if (Greet >= 0) ava.SetPersonData(VMPersonDataVariable.GreetStatus, Greet);
            // passing through: never saved into the lot (Sims 1 sends visitors home when you leave)
            ava.SetValue(VMStackObjectVariable.MovementFlags,
                (short)(ava.GetValue(VMStackObjectVariable.MovementFlags) | (short)VMMovementFlags.FSOExcludeUserSave));

            var anchor = vm.GetObjectById(AnchorID);
            if (anchor != null && !anchor.Dead) VMFindLocationFor.FindLocationFor(ava, anchor, vm.Context, VMPlaceRequestFlags.Default);
            if (ava.Position == LotTilePos.OUT_OF_WORLD)
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            vm.Scheduler.RescheduleInterrupt(ava);
            return true;
        }

        /// <summary>Queues Sims 1's own global "Leave" for this Sim (they walk off the lot and go).</summary>
        public static bool PushLeave(VM vm, VMAvatar ava)
        {
            var tree = ava.GetRoutineWithOwner(LeaveRoutine, vm.Context);
            if (tree?.routine == null) return false;
            var label = tree.bhav?.ChunkLabel ?? "";
            if (label.IndexOf("Leave", StringComparison.OrdinalIgnoreCase) < 0) return false; // not the routine we expect
            ava.Thread.EnqueueAction(new VMQueuedAction
            {
                Callee = ava,
                StackObject = ava,
                IconOwner = ava,
                CodeOwner = tree.owner,
                ActionRoutine = tree.routine,
                Name = "Go Home",
                Priority = (short)VMQueuePriority.UserDriven,
                Flags = TTABFlags.FSOSkipPermissions,
            });
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // only the lot's own game sends these - never a player
            return !FromNet;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(GUID);
            writer.Write(AnchorID);
            writer.Write(ObjectID);
            writer.Write(PersonType);
            writer.Write(Greet);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (TownieMode)reader.ReadByte();
            GUID = reader.ReadUInt32();
            AnchorID = reader.ReadInt16();
            ObjectID = reader.ReadInt16();
            PersonType = reader.ReadInt16();
            Greet = reader.ReadInt16();
        }
    }
}
