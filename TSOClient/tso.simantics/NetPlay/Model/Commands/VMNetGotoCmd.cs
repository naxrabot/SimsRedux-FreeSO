using System.IO;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetGotoCmd : VMNetCommandBodyAbstract
    {
        public ushort Interaction;
        public short Param0;

        public short x;
        public short y;
        public sbyte level;

        private static uint GOTO_GUID = 0x000007C4;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (caller == null) return false;
            if (caller.Thread.Queue.Count >= VMThread.MAX_USER_ACTIONS) return false;
            // SimsRedux: null-guarded, and a marker that couldn't be placed (off the lot) is deleted and the order
            // dropped - it used to be given the deleted marker to walk to
            var group = vm.Context.CreateObjectInstance(GOTO_GUID, new LotTilePos(x, y, level), Direction.NORTH);
            VMEntity callee = (group != null && group.Objects.Count > 0) ? group.Objects[0] : null;
            if (callee == null) return false;
            if (callee.Position == LotTilePos.OUT_OF_WORLD)
            {
                callee.Delete(true, vm.Context);
                return false;
            }
            callee.PushUserInteraction(Interaction, caller, vm.Context, false, new short[] { Param0, 0, 0, 0 });

            return true;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Interaction);
            writer.Write(Param0);
            writer.Write(x);
            writer.Write(y);
            writer.Write(level);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Interaction = reader.ReadUInt16();
            Param0 = reader.ReadInt16();
            x = reader.ReadInt16();
            y = reader.ReadInt16();
            level = reader.ReadSByte();
        }

        #endregion
    }
}
