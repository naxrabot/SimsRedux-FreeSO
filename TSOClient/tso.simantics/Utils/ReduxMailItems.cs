using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.0: mail between players - an inventory item leaving with a letter, or arriving with one. A Sims 1
    /// Sim's inventory (gifts, souvenirs) is kept in the neighbourhood by the Sim's neighbour id, exactly where the
    /// game's own Inventory Operations primitive keeps it (VMTS1InventoryOperations - Add Token / Remove Token), so it's
    /// saved with the neighbourhood as before: nothing new in any file. Only ever reached through VMNetReduxPerksCmd
    /// (TakeItem / GiveItem), which only the lot's own game can send - single player.
    /// </summary>
    public static class ReduxMailItems
    {
        /// <summary>The types a letter may carry: gifts and souvenirs (not skills, the game's hidden counters, magic or ingredients).</summary>
        public static bool Sendable(int type)
        {
            return type != 2 && type != 6 && type != 7 && type != 8;
        }

        /// <summary>A Sim of this family on the lot with this neighbour id (null if there isn't one).</summary>
        private static VMAvatar SimOf(VM vm, int family, short neighbour)
        {
            return vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>().FirstOrDefault(a => !a.IsPet
                && a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == family
                && a.GetPersonData(VMPersonDataVariable.NeighborId) == neighbour);
        }

        /// <summary>Takes one of an item out of a family Sim's inventory. False when it isn't there.</summary>
        public static bool TakeItem(VM vm, int family, short neighbour, uint guid, int type)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || fam.ChunkID != family || guid == 0 || !Sendable(type)) return false;
            if (SimOf(vm, family, neighbour) == null) return false;
            var inv = Content.Content.Get().Neighborhood.GetInventoryByNID(neighbour);
            var item = inv?.FirstOrDefault(x => x.GUID == guid && x.Type == type && x.Count > 0);
            if (item == null) return false;
            item.Count -= 1;
            if (item.Count == 0) inv.Remove(item);
            return true;
        }

        /// <summary>
        /// Puts one of an item into the inventory of a Sim of the family living here (the youngest-numbered person on
        /// the lot, so every game picks the same one). False when nobody of the family is on the lot.
        /// </summary>
        public static bool GiveItem(VM vm, int family, uint guid, int type)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || fam.ChunkID != family || guid == 0 || !Sendable(type)) return false;
            var sim = vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>()
                .Where(a => !a.IsPet && a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == family && a.Position != LotTilePos.OUT_OF_WORLD)
                .OrderBy(a => a.ObjectID).FirstOrDefault();
            if (sim == null) return false;
            var neighbour = sim.GetPersonData(VMPersonDataVariable.NeighborId);
            var hood = Content.Content.Get().Neighborhood;
            var inv = hood.GetInventoryByNID(neighbour);
            if (inv == null)
            {
                inv = new List<InventoryItem>();
                hood.SetInventoryForNID(neighbour, inv);
            }
            var item = inv.FirstOrDefault(x => x.GUID == guid && x.Type == type);
            if (item == null) inv.Add(new InventoryItem() { Count = 1, GUID = guid, Type = type });
            else if (item.Count < ushort.MaxValue) item.Count += 1;
            Console.WriteLine("[SimsRedux] mail: an item (" + guid.ToString("X8") + ") went into " + (sim.Name ?? "a Sim") + "'s inventory");
            return true;
        }
    }
}
