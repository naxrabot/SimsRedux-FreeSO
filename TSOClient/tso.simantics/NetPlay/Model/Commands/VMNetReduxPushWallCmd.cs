using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 16.7: Build mode's Push Wall - pushes a straight stretch of wall in or out (Utils/ReduxPushWall.cs).
    /// Every game works the push out again from the lot as it is when the command runs, so they all end up the same;
    /// the family pays (or gets back) the difference. On a visit, guests can only send it when the host lets them
    /// build (ReduxOnline's TrustedCommands, as with walls and floors).
    /// </summary>
    public class VMNetReduxPushWallCmd : VMNetCommandBodyAbstract
    {
        /// <summary>The tile whose side is the piece of wall picked (its TopRight side when AlongX, else its TopLeft).</summary>
        public short X, Y;
        public sbyte Level;
        /// <summary>The wall runs along x (true) or along y (false).</summary>
        public bool AlongX;
        /// <summary>Tiles to push it: + towards higher y (along x) / higher x (along y), - the other way.</summary>
        public sbyte Offset;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!Sane(vm)) return false;
            string why;
            try { why = ReduxPushWall.Run(vm, X, Y, Level, AlongX, Offset); }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] push wall: " + e);
                why = "That didn't work.";
            }
            if (why != null)
            {
                ReduxPushWall.SignalRefused(vm, ActorUID, why);
                return false;
            }
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!Sane(vm)) return false;
            // (as walls and floors: online, the sender must be on the lot and allowed to build)
            if (!vm.TS1 && (caller == null || !vm.PlatformState.Validator.CanBuildTool(caller))) return false;
            return true;
        }

        private bool Sane(VM vm)
        {
            var arch = vm?.Context?.Architecture;
            if (arch == null || !arch.BuildBuyEnabled) return false;
            if (Offset == 0 || Math.Abs((int)Offset) > ReduxPushWall.MaxPush) return false;
            if (Level < 1 || Level > arch.Stories) return false;
            if (X < 0 || Y < 0 || X >= arch.Width || Y >= arch.Height) return false;
            return true;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(X);
            writer.Write(Y);
            writer.Write(Level);
            writer.Write(AlongX);
            writer.Write(Offset);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            X = reader.ReadInt16();
            Y = reader.ReadInt16();
            Level = reader.ReadSByte();
            AlongX = reader.ReadBoolean();
            Offset = reader.ReadSByte();
        }
    }
}
