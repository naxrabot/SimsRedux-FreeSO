using System.IO;
using FSO.LotView.Model;

namespace FSO.SimAntics.Marshals
{
    public class VMGameObjectMarshal : VMEntityMarshal
    {
        public Direction Direction;
        public VMGameObjectDisableFlags Disabled;
        public uint Tint; // SimsRedux: colour tint (version 39+)
        public short Lift; // SimsRedux: how far a wall item is moved up (+) or down (-), in hundredths (version 41+)

        public VMGameObjectMarshal() { }
        public VMGameObjectMarshal(int version, bool ts1) : base(version, ts1) { }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Direction = (Direction)reader.ReadByte();
            if (Version > 9) Disabled = (VMGameObjectDisableFlags)reader.ReadByte();
            if (Version > 38) Tint = reader.ReadUInt32();
            if (Version > 40) Lift = reader.ReadInt16();
        }
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Direction);
            writer.Write((byte)Disabled);
            writer.Write(Tint);
            writer.Write(Lift);
        }
    }
}
