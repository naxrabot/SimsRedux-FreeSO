using FSO.LotView.Model;
using FSO.SimAntics.Model;
using System;
using System.IO;
using System.Linq;

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
            FillNeeds = 1,      // all needs to full
            // SimsRedux: objects that got stuck (can't be picked up, used or deleted)
            ResetObject = 2,    // stop whatever it's doing (and whoever is using it)
            DeleteObject = 3,   // remove it whatever state it's in (the family gets its value back)
            MoveObject = 4      // move it to the nearest free spot
        }

        public short ObjectID;
        public DebugAction Action;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (Action >= DebugAction.ResetObject) return ExecuteObject(vm, caller);
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

        private bool ExecuteObject(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID) as VMGameObject;
            if (obj == null) return false;
            var group = obj.MultitileGroup;
            // whoever is using it lets go first (a Sim stuck in the middle of using it is reset too)
            foreach (var ava in vm.Context.ObjectQueries.Avatars)
            {
                var a = ava as VMAvatar;
                if (a?.Thread?.Queue == null) continue;
                if (a.Thread.Queue.Any(q => q.Callee != null && group.Objects.Contains(q.Callee)) || (a.Container != null && group.Objects.Contains(a.Container)))
                {
                    if (vm.GlobalBlockingDialog == a)
                    {
                        // (its question goes away with it - un-pause, the same as the Sim fix above)
                        vm.GlobalBlockingDialog = null;
                        if (!vm.ReduxNoPause)
                        {
                            vm.SpeedMultiplier = vm.LastSpeedMultiplier;
                            vm.LastSpeedMultiplier = 0;
                        }
                    }
                    a.Thread.BlockingState = null;
                    a.Reset(vm.Context);
                }
            }
            switch (Action)
            {
                case DebugAction.ResetObject:
                    foreach (var o in group.Objects.ToList()) o.Reset(vm.Context);
                    return true;
                case DebugAction.DeleteObject:
                    var value = group.Price;
                    try { obj.ExecuteEntryPoint(12, vm.Context, true); } catch { }
                    obj.Delete(true, vm.Context);
                    // the family gets its value back, like selling it in Buy mode
                    if (vm.GlobalLink != null && value > 0)
                        vm.GlobalLink.PerformTransaction(vm, false, uint.MaxValue, caller?.PersistID ?? uint.MaxValue, value,
                            (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) => { });
                    return true;
                case DebugAction.MoveObject:
                    var arch = vm.Context.Architecture;
                    var start = obj.Position;
                    int sx = start.TileX, sy = start.TileY;
                    if (start == LotTilePos.OUT_OF_WORLD || sx <= 0 || sy <= 0) { sx = arch.Width / 2; sy = arch.Height / 2; }
                    var level = (start == LotTilePos.OUT_OF_WORLD) ? (sbyte)1 : start.Level;
                    int maxR = Math.Max(arch.Width, arch.Height);
                    for (int r = 1; r <= maxR; r++)
                        for (int dx = -r; dx <= r; dx++)
                            for (int dy = -r; dy <= r; dy++)
                            {
                                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                                int x = sx + dx, y = sy + dy;
                                if (x < 1 || y < 1 || x >= arch.Width - 1 || y >= arch.Height - 1) continue;
                                var res = group.ChangePosition(LotTilePos.FromBigTile((short)x, (short)y, level), obj.Direction, vm.Context, VMPlaceRequestFlags.Default);
                                if (res.Status == VMPlacementError.Success) return true;
                            }
                    return false;
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
            if (Action > DebugAction.MoveObject) return false; // (unknown action - nothing to do)
            if (Action >= DebugAction.ResetObject)
            {
                // objects: online, only the family living on the lot (at home) can fix them
                if (FromNet && !vm.ReduxFamilyControl.ContainsKey(ActorUID)) return false;
                return vm.GetObjectById(ObjectID) is VMGameObject;
            }
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
