using FSO.SimAntics.Engine;
using FSO.Files.Utils;
using System.IO;

namespace FSO.SimAntics.Primitives
{
    public class VMSpecialEffect : VMPrimitiveHandler
    {
        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            //string table 305 used by this primitive for screenshot caption
            var operand = (VMSpecialEffectOperand)args;
            if (context.VM.TS1 && VM.UseWorld)
            {
                // SimsRedux: the camera is each player's own. With other players on the lot (a visit, the online
                // town), it only jumps for an effect on your own Sim - the others' moments don't pull it away.
                if (((byte)operand.Flags & (1<<3)) > 0 && CameraIsMine(context.VM, context.StackObject))
                    context.VM.Context.World.CenterTo(context.StackObject.WorldUI);
            }
            return VMPrimitiveExitCode.GOTO_TRUE;
        }

        /// <summary>SimsRedux: may something about this object move this player's camera? Always alone (single
        /// player); with other players on the lot, only for this player's own Sim.</summary>
        public static bool CameraIsMine(VM vm, VMEntity obj)
        {
            if (obj == null) return false;
            var me = vm.MyUID;
            bool others = false;
            foreach (var a in vm.Context.ObjectQueries.Avatars)
                if (a.PersistID != 0 && a.PersistID != me) { others = true; break; }
            if (!others) return true;
            return obj is VMAvatar && obj.PersistID == me;
        }
    }

    public class VMSpecialEffectOperand : VMPrimitiveOperand
    {
        public ushort Timeout;
        public sbyte Size;
        public sbyte Zoom;
        public sbyte Flags;
        public sbyte StrIndex;

        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN)){
                Timeout = io.ReadUInt16();
                Size = (sbyte)io.ReadByte();
                Zoom = (sbyte)io.ReadByte();
                Flags = (sbyte)io.ReadByte();
                StrIndex = (sbyte)io.ReadByte();
            }
        }

        public void Write(byte[] bytes) {
            using (var io = new BinaryWriter(new MemoryStream(bytes)))
            {
                io.Write(Timeout);
                io.Write(Size);
                io.Write(Zoom);
                io.Write(Flags);
                io.Write(StrIndex);
            }
        }
        #endregion
    }
}
