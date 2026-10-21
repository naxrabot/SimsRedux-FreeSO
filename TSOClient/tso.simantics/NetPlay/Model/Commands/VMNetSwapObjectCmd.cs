using System;
using System.IO;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Model;
using FSO.SimAntics.Entities;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: swaps an object on the lot for another colour/style of it, in the same spot and
    /// facing the same way. The family pays (or gets back) the difference between the two catalog
    /// prices. Refused while a Sim is using the object or something is sitting on it.
    /// If the new version doesn't fit, the old one (the same object, as it was) is put back and the money is returned.
    /// </summary>
    public class VMNetSwapObjectCmd : VMNetCommandBodyAbstract
    {
        public short ObjectID;
        public uint NewGUID;
        public bool Verified;
        private int Difference; // new price - old price (positive = the family pays)

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID) as VMGameObject;
            if (obj == null || CheckSwap(vm, obj, NewGUID) != null) { Refund(vm, caller); return false; }

            var group = obj.MultitileGroup;
            var baseObj = group.BaseObject;
            var pos = baseObj.Position;
            var dir = baseObj.Direction;
            var oldPrice = group.InitialPrice;
            // SimsRedux: the new one remembers what was really paid for it - the old one's remembered price plus the
            // difference charged here (never the full catalog price, or a Buy mode discount + swap + sell makes money).
            // Difference comes from the lot (Verify) with the command, so every game sets the same price.
            var newPrice = Math.Max(0, oldPrice + Difference);

            // SimsRedux: the old one is only set aside (off the lot) while the new one is tried, so if the new
            // one doesn't fit, the very same object goes back - keeping its state (colour, wear, what's in it) -
            // instead of a fresh copy. It's deleted only once the new one is in.
            var setAside = group.ChangePosition(LotTilePos.OUT_OF_WORLD, dir, vm.Context, VMPlaceRequestFlags.Default);
            if (setAside.Status != VMPlacementError.Success || baseObj.Position != LotTilePos.OUT_OF_WORLD)
            {
                // (couldn't even lift it: nothing has changed - just give the money back)
                if (baseObj.Position == LotTilePos.OUT_OF_WORLD) group.ChangePosition(pos, dir, vm.Context, VMPlaceRequestFlags.Default);
                Refund(vm, caller);
                return false;
            }

            bool placed = false;
            var created = vm.Context.CreateObjectInstance(NewGUID, LotTilePos.OUT_OF_WORLD, dir);
            if (created != null && created.Objects.Count > 0)
            {
                created.ChangePosition(pos, dir, vm.Context, VMPlaceRequestFlags.UserPlacement);
                placed = created.BaseObject.Position != LotTilePos.OUT_OF_WORLD;
                if (placed)
                {
                    created.ExecuteEntryPoint(11, vm.Context); // user placement
                    created.InitialPrice = newPrice;
                }
                else created.Delete(vm.Context);
            }

            if (placed)
            {
                baseObj.Delete(true, vm.Context); // (the old one goes for good)
                return true;
            }

            // the new version doesn't fit here: the old one goes back where it was, and the money back
            group.ChangePosition(pos, dir, vm.Context, VMPlaceRequestFlags.Default);
            if (baseObj.Position == LotTilePos.OUT_OF_WORLD)
            {
                // (it couldn't go back either - make sure the family doesn't lose it: the old one is sold
                //  for its full price instead of left off the lot)
                baseObj.Delete(true, vm.Context);
                if (vm.GlobalLink != null && oldPrice > 0)
                {
                    var who = caller?.PersistID ?? uint.MaxValue;
                    vm.GlobalLink.PerformTransaction(vm, false, uint.MaxValue, who, oldPrice,
                        (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) => { });
                }
            }
            Refund(vm, caller);
            return false;
        }

        /// <summary>Why the swap can't happen (null = it can).</summary>
        public static string CheckSwap(VM vm, VMEntity obj, uint newGUID)
        {
            if (obj == null || !(obj is VMGameObject)) return "Nothing to change.";
            if (obj.Position == LotTilePos.OUT_OF_WORLD) return "That object isn't on the lot.";
            if (Content.Content.Get().WorldCatalog.GetItemByGUID(newGUID) == null) return "That colour isn't in the catalog.";
            if (obj.IsUserMovable(vm.Context, true) != VMPlacementError.Success) return "That object can't be changed.";
            var group = obj.MultitileGroup;
            foreach (var part in group.Objects)
            {
                var slots = part.TotalSlots();
                for (int i = 0; i < slots; i++)
                    if (part.GetSlot(i) != null) return "Take the things off it first.";
            }
            if (InUse(vm, group)) return "A Sim is using it right now.";
            return null;
        }

        private static bool InUse(VM vm, VMMultitileGroup group)
        {
            foreach (var ent in vm.Context.ObjectQueries.Avatars)
            {
                var thread = ent.Thread;
                if (thread == null) continue;
                if (thread.Stack.Any(f => f != null && (group.Objects.Contains(f.Callee) || group.Objects.Contains(f.StackObject)))) return true;
                if (thread.Queue.Any(q => q != null && group.Objects.Contains(q.Callee))) return true;
            }
            return false;
        }

        /// <summary>
        /// What the family pays (+) or gets back (-) for swapping this object for newGUID: the difference of the two
        /// catalog prices - but never more back than the object is remembered as costing (it may have been bought with
        /// a discount). The new one then remembers the old price plus this.
        /// </summary>
        public static int PriceDifference(VMEntity obj, uint newGUID)
        {
            var group = obj?.MultitileGroup;
            if (group == null || group.BaseObject == null) return 0;
            var oldGUID = (group.MultiTile) ? group.BaseObject.MasterDefinition.GUID : group.BaseObject.Object.OBJ.GUID;
            var diff = CatalogPrice(newGUID) - CatalogPrice(oldGUID);
            if (diff < 0) diff = Math.Max(diff, -Math.Max(0, group.InitialPrice));
            return diff;
        }

        private static int CatalogPrice(uint guid)
        {
            var item = Content.Content.Get().WorldCatalog.GetItemByGUID(guid);
            return (item == null) ? 0 : (int)item.Value.Price;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (Verified) return true;
            var obj = vm.GetObjectById(ObjectID);
            if (CheckSwap(vm, obj, NewGUID) != null) return false;
            var group = obj.MultitileGroup;
            var oldGUID = (group.MultiTile) ? group.BaseObject.MasterDefinition.GUID : group.BaseObject.Object.OBJ.GUID;
            if (oldGUID == NewGUID) return false;

            Difference = PriceDifference(obj, NewGUID);
            if (Difference == 0 || vm.GlobalLink == null)
            {
                Verified = true;
                return true;
            }

            // charge (or refund) the price difference, then run the swap
            var who = caller?.PersistID ?? uint.MaxValue;
            uint from = (Difference > 0) ? who : uint.MaxValue;
            uint to = (Difference > 0) ? uint.MaxValue : who;
            vm.GlobalLink.PerformTransaction(vm, false, from, to, Math.Abs(Difference),
                (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) =>
                {
                    if (success)
                    {
                        Verified = true;
                        vm.ForwardCommand(this);
                    }
                });
            return false;
        }

        /// <summary>Undo the money part if the swap didn't happen.</summary>
        private void Refund(VM vm, VMAvatar caller)
        {
            if (Difference == 0 || vm.GlobalLink == null) return;
            var who = caller?.PersistID ?? uint.MaxValue;
            uint from = (Difference > 0) ? uint.MaxValue : who;
            uint to = (Difference > 0) ? who : uint.MaxValue;
            vm.GlobalLink.PerformTransaction(vm, false, from, to, Math.Abs(Difference),
                (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) => { });
            Difference = 0;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
            writer.Write(NewGUID);
            writer.Write(Difference);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
            NewGUID = reader.ReadUInt32();
            Difference = reader.ReadInt32();
        }
    }
}
