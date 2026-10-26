using System.IO;
using System.Linq;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 16.7: cooking menus (ReduxCooking). A Sim makes a chosen dish: the fridge's own "Have A Meal" /
    /// "Have Lite Meal" / "Serve Meal" is queued as the pie menu would, with the dish in its 4th argument, so every
    /// player on the lot cooks the same thing. Refused if the Sim's Cooking skill hasn't unlocked the dish, or the dish
    /// isn't one for that choice. Always the Sim sending it (online: the lot sets who sent it).
    /// </summary>
    public class VMNetReduxCookCmd : VMNetCommandBodyAbstract
    {
        public ushort Interaction;
        public short CalleeID;
        public short Param0;
        public byte Dish;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!Check(vm, caller)) return false;
            var fridge = vm.GetObjectById(CalleeID);
            var queue = caller.Thread.Queue;
            int before = queue.Count;
            fridge.PushUserInteraction(Interaction, caller, vm.Context, false, new short[] { Param0, 0, 0, (short)(ReduxCooking.DishArg + Dish) });
            // its name in the queue says which dish (the fridge renames it "Have Dinner" when it starts - ReduxCooking.Rename keeps the dish)
            var dish = ReduxCooking.Find(Dish);
            if (queue.Count > before && dish != null)
            {
                var action = queue[queue.Count - 1];
                if (action.Callee == fridge && ReduxCooking.DishOf(action) == dish) action.Name = (action.Name ?? "") + ": " + dish.Name;
            }
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!Check(vm, caller)) return false;
            if (!FromNet) return true;
            // (as a plain interaction: the fridge's menu for this Sim must offer it)
            var fridge = vm.GetObjectById(CalleeID);
            var oldInUse = fridge.GetFlag(VMEntityFlags.Occupied);
            fridge.SetFlag(VMEntityFlags.Occupied, false);
            var pie = fridge.GetPieMenuForInteraction(vm, caller, Interaction, false, false);
            fridge.SetFlag(VMEntityFlags.Occupied, oldInUse);
            return pie != null && pie.Any(x => x.Param0 == Param0);
        }

        private bool Check(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1 || caller == null || caller.IsPet || caller.Dead) return false;
            if ((caller.Thread?.Queue?.Count ?? 0) >= VMThread.MAX_USER_ACTIONS) return false;
            var fridge = vm.GetObjectById(CalleeID);
            if (fridge == null || fridge.Dead) return false;
            if (fridge is VMGameObject && ((VMGameObject)fridge).Disabled > 0) return false;
            var kind = ReduxCooking.KindOf(fridge, Interaction);
            var dish = ReduxCooking.Find(Dish);
            if (kind == null || dish == null) return false;
            if (dish.Quick != (kind.Value == ReduxCooking.Kind.Quick)) return false;
            // (the game's own grocery check only stops a family already in debt: a dish they can't pay for isn't started)
            var fam = vm.TS1State?.CurrentFamily;
            if (fam != null && fam.Budget < ReduxCooking.Price(dish, kind.Value)) return false;
            return ReduxCooking.Unlocked(dish, caller);
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Interaction);
            writer.Write(CalleeID);
            writer.Write(Param0);
            writer.Write(Dish);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Interaction = reader.ReadUInt16();
            CalleeID = reader.ReadInt16();
            Param0 = reader.ReadInt16();
            Dish = reader.ReadByte();
        }
    }
}
