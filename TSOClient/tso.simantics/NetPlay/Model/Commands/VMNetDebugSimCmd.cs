using FSO.LotView.Model;
using FSO.SimAntics.Model;
using System;
using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux debug menu (cheat "debugredux", then Ctrl + Left Shift + right-click a Sim):
    /// fixes for a Sim that's stuck. Online, a friend can only use it on their own Sim.
    /// </summary>
    public class VMNetDebugSimCmd : VMNetCommandBodyAbstract
    {
        public enum DebugAction : byte
        {
            MoveToFreeSpot = 0, // un-stick from walls/objects: reset, then the nearest open tile
            FillNeeds = 1       // all needs to full
        }

        public short ObjectID;
        public DebugAction Action;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var sim = vm.GetObjectById(ObjectID) as VMAvatar;
            if (sim == null) return false;
            switch (Action)
            {
                case DebugAction.MoveToFreeSpot:
                    if (vm.GlobalBlockingDialog == sim)
                    {
                        vm.GlobalBlockingDialog = null;
                        if (!vm.ReduxNoPause) // (online lots were never paused)
                        {
                            vm.SpeedMultiplier = vm.LastSpeedMultiplier;
                            vm.LastSpeedMultiplier = 0;
                        }
                    }
                    sim.Thread.BlockingState = null;
                    sim.Reset(vm.Context);
                    return MoveToFreeSpot(vm, sim);
                case DebugAction.FillNeeds:
                    foreach (var m in new[] { VMMotive.Energy, VMMotive.Comfort, VMMotive.Hunger, VMMotive.Hygiene,
                                              VMMotive.Bladder, VMMotive.Room, VMMotive.Social, VMMotive.Fun })
                        sim.SetMotiveData(m, 100);
                    return true;
            }
            return false;
        }

        /// <summary>Tries tiles in growing rings around the Sim (same floor first, then the ground floor).</summary>
        private static bool MoveToFreeSpot(VM vm, VMAvatar sim)
        {
            var arch = vm.Context.Architecture;
            var start = sim.Position;
            int sx = start.TileX, sy = start.TileY;
            if (start == LotTilePos.OUT_OF_WORLD || sx <= 0 || sy <= 0) { sx = arch.Width / 2; sy = arch.Height / 2; }
            var levels = (start.Level > 1) ? new sbyte[] { start.Level, 1 } : new sbyte[] { 1 };
            int maxR = Math.Max(arch.Width, arch.Height);
            foreach (var level in levels)
            {
                for (int r = 0; r <= maxR; r++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        for (int dy = -r; dy <= r; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring only
                            int x = sx + dx, y = sy + dy;
                            if (x < 1 || y < 1 || x >= arch.Width - 1 || y >= arch.Height - 1) continue;
                            var pos = LotTilePos.FromBigTile((short)x, (short)y, level);
                            if (sim.SetPosition(pos, sim.Direction, vm.Context).Status == VMPlacementError.Success)
                                return true;
                        }
                    }
                }
            }
            return false;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // SimsRedux online: a friend can only use this on their own Sim
            if (FromNet && (caller == null || caller.ObjectID != ObjectID)) return false;
            return vm.GetObjectById(ObjectID) is VMAvatar;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
            writer.Write((byte)Action);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
            Action = (DebugAction)reader.ReadByte();
        }
    }
}
