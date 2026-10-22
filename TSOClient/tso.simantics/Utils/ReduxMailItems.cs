using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.0: mail between players - a gift leaving the family with a letter, coming back when the letter
    /// couldn't be sent, or arriving with one. A Sims 1 Sim's inventory (gifts, souvenirs) is kept in the neighbourhood
    /// by the Sim's neighbour id, exactly where the game's own Inventory Operations primitive keeps it
    /// (VMTS1InventoryOperations - Add Token / Remove Token), so it's saved with the neighbourhood as before: nothing new
    /// in any file. Only ever reached through VMNetReduxPerksCmd (TakeMoney / TakeItem / ReturnItem / GiveItem), which
    /// only the lot's own game can send - single player.
    ///
    /// Taking happens FIRST (before the letter goes to the server): the game sends the command, waits for Taken to say
    /// whether the family really had the money or the item, and only then sends the letter (refunding it if the server
    /// says no). One take at a time (the game's mail keeps a single "sending" flag).
    /// </summary>
    public static class ReduxMailItems
    {
        /// <summary>A take ran on the lot: (the lot, money or not, did the family have it).</summary>
        public static event Action<VM, bool, bool> Taken;

        /// <summary>The types a letter may carry: gifts and souvenirs (not skills, the game's hidden counters, magic or ingredients).</summary>
        public static bool Sendable(int type)
        {
            return type >= 0 && type <= 255 && type != 2 && type != 6 && type != 7 && type != 8;
        }

        /// <summary>
        /// A person of the family who is on the lot right now (not at work or school, not a pet) - the youngest-numbered,
        /// so every game picks the same one. Null when nobody of the family is here.
        /// </summary>
        public static VMAvatar HomeSim(VM vm, int family)
        {
            if (vm?.Context?.ObjectQueries == null) return null;
            return vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>()
                .Where(a => !a.IsPet && !a.Dead && a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == family && a.Position != LotTilePos.OUT_OF_WORLD)
                .OrderBy(a => a.ObjectID).FirstOrDefault();
        }

        private static bool IsFamily(VM vm, int family)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            return fam != null && fam.ChunkID == family;
        }

        private static void Report(VM vm, bool money, bool ok)
        {
            try { Taken?.Invoke(vm, money, ok); } catch (Exception e) { Console.WriteLine("[SimsRedux] mail: " + e.Message); }
        }

        /// <summary>Takes money from the family - only if it has that much (never clamped at 0). Reports through Taken.</summary>
        public static bool TakeMoney(VM vm, int family, int amount)
        {
            bool ok = false;
            var fam = vm?.TS1State?.CurrentFamily;
            if (IsFamily(vm, family) && amount > 0 && fam.Budget >= amount)
            {
                fam.Budget -= amount;
                ok = true;
                try { HomeSim(vm, family)?.ShowMoneyHeadline(-amount); } catch { }
            }
            Report(vm, true, ok);
            return ok;
        }

        /// <summary>Takes one of an item out of a family Sim's inventory. Reports through Taken (false when it isn't there).</summary>
        public static bool TakeItem(VM vm, int family, short neighbour, uint guid, int type)
        {
            bool ok = false;
            if (IsFamily(vm, family) && guid != 0 && Sendable(type) && FamilySim(vm, family, neighbour))
            {
                var inv = Content.Content.Get().Neighborhood.GetInventoryByNID(neighbour);
                var item = inv?.FirstOrDefault(x => x.GUID == guid && x.Type == type && x.Count > 0);
                if (item != null)
                {
                    item.Count -= 1;
                    if (item.Count == 0) inv.Remove(item);
                    ok = true;
                }
            }
            Report(vm, false, ok);
            return ok;
        }

        /// <summary>A family Sim (on the lot or away) with this neighbour id.</summary>
        private static bool FamilySim(VM vm, int family, short neighbour)
        {
            return vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>().Any(a => !a.IsPet
                && a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == family
                && a.GetPersonData(VMPersonDataVariable.NeighborId) == neighbour);
        }

        private static void AddOne(short neighbour, uint guid, int type)
        {
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
        }

        /// <summary>A taken item goes back to the same Sim (the letter couldn't be sent).</summary>
        public static bool ReturnItem(VM vm, int family, short neighbour, uint guid, int type)
        {
            if (!IsFamily(vm, family) || guid == 0 || !Sendable(type) || !FamilySim(vm, family, neighbour)) return false;
            AddOne(neighbour, guid, type);
            Console.WriteLine("[SimsRedux] mail: an item (" + guid.ToString("X8") + ") went back to the Sim it was taken from");
            return true;
        }

        /// <summary>
        /// Puts one of an item into the inventory of a family member on the lot (HomeSim). False when nobody of the
        /// family is here - the game only sends it when HomeSim finds someone.
        /// </summary>
        public static bool GiveItem(VM vm, int family, uint guid, int type)
        {
            if (!IsFamily(vm, family) || guid == 0 || !Sendable(type)) return false;
            var sim = HomeSim(vm, family);
            if (sim == null) return false;
            AddOne(sim.GetPersonData(VMPersonDataVariable.NeighborId), guid, type);
            Console.WriteLine("[SimsRedux] mail: an item (" + guid.ToString("X8") + ") went into " + (sim.Name ?? "a Sim") + "'s inventory");
            return true;
        }
    }
}
