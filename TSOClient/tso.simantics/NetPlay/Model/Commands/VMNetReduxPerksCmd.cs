using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux Level Rewards: the lot's perk table (who has which boosts, plumbob and name colours, free services), and
    /// money or a free object claimed on the server - see Utils/ReduxPerks.cs. Only ever sent by the lot itself: single
    /// player, the game (from the server's answer); online, the town lot (from the server's perk-effects.json and its
    /// "money" / "perkgift" lines). A player's game can't send one: it isn't taken from the network at all.
    /// </summary>
    public class VMNetReduxPerksCmd : VMNetCommandBodyAbstract
    {
        /// <summary>
        /// 16.0 mail between players (Utils/ReduxMailItems.cs), before the letter is sent:
        ///   TakeMoney  - Amount leaves the family, only if it has that much (ReduxMailItems.Taken says whether it did);
        ///   TakeItem   - one inventory item leaves (Amount = the Sim's neighbour id in the low 16 bits, the item's
        ///                inventory type above them; Taken says whether it was there);
        ///   ReturnItem - the letter couldn't be sent: the item goes back to that Sim (Amount packed like TakeItem);
        ///                money goes back with Money.
        /// And when a letter's gift is collected: GiveItem (Amount = its type) into a family member's inventory, or Money.
        /// </summary>
        public enum PerkAction : byte { SetTable = 0, Money = 1, Gift = 2, TakeItem = 3, GiveItem = 4, TakeMoney = 5, ReturnItem = 6 }

        public PerkAction Action;
        /// <summary>SetTable: the whole table (replaces the lot's; the free days already used here are kept).</summary>
        public VMReduxPerks Table;
        /// <summary>Money / Gift: the family it's for (it has to live on this lot), the amount, the object.</summary>
        public short Family;
        public int Amount;
        public uint GUID;

        public override bool AcceptFromClient { get { return false; } }

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            switch (Action)
            {
                case PerkAction.SetTable:
                    if (Table == null) return false;
                    var next = Table.Clone();
                    // (a free day used on this lot since the server last heard keeps counting as used)
                    foreach (var kv in next.Families)
                    {
                        var had = vm.ReduxPerks?.Get(kv.Key);
                        if (had == null) continue;
                        kv.Value.MaidDay = had.MaidDay; kv.Value.GardenerDay = had.GardenerDay;
                        kv.Value.MaidUsed = had.MaidUsed; kv.Value.GardenerUsed = had.GardenerUsed;
                    }
                    // (a family that drops out of the table - e.g. its free days ran out and it has no other perks -
                    //  keeps its counters here, so if it comes back its free days used on this lot still count as used)
                    if (vm.ReduxPerks != null)
                        foreach (var kv in vm.ReduxPerks.Families)
                        {
                            if (next.Families.ContainsKey(kv.Key) || kv.Key == VMReduxPerks.TownKey) continue; // (15.2: the town entry is never kept over)
                            var had = kv.Value;
                            if (had.MaidUsed == 0 && had.GardenerUsed == 0 && had.MaidDay < 0 && had.GardenerDay < 0) continue;
                            next.Families[kv.Key] = new VMReduxPerks.Entry
                            {
                                MaidUsed = had.MaidUsed, GardenerUsed = had.GardenerUsed, MaidDay = had.MaidDay, GardenerDay = had.GardenerDay,
                            };
                        }
                    vm.ReduxPerks = next;
                    return true;
                case PerkAction.Money:
                    return ReduxPerkRules.GiveMoney(vm, Family, Amount);
                case PerkAction.Gift:
                    var what = ReduxPerkRules.GiveObject(vm, Family, GUID);
                    Console.WriteLine("[SimsRedux] level reward: " + what);
                    return true;
                case PerkAction.TakeItem:
                    return ReduxMailItems.TakeItem(vm, Family, (short)(Amount & 0xFFFF), GUID, Amount >> 16);
                case PerkAction.GiveItem:
                    return ReduxMailItems.GiveItem(vm, Family, GUID, Amount);
                case PerkAction.TakeMoney:
                    return ReduxMailItems.TakeMoney(vm, Family, Amount);
                case PerkAction.ReturnItem:
                    return ReduxMailItems.ReturnItem(vm, Family, (short)(Amount & 0xFFFF), GUID, Amount >> 16);
            }
            return false;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (FromNet) return false; // (only the lot itself - never a player's game)
            if (Action == PerkAction.Money && (Amount == 0 || Math.Abs((long)Amount) > 100000000)) return false;
            if (Action == PerkAction.TakeMoney && (Amount <= 0 || Amount > 100000000)) return false;
            return Action <= PerkAction.ReturnItem;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Action);
            writer.Write(Family);
            writer.Write(Amount);
            writer.Write(GUID);
            writer.Write(Table != null);
            if (Table != null) Table.SerializeInto(writer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Action = (PerkAction)reader.ReadByte();
            Family = reader.ReadInt16();
            Amount = reader.ReadInt32();
            GUID = reader.ReadUInt32();
            Table = reader.ReadBoolean() ? VMReduxPerks.Read(reader) : null;
        }
    }
}
