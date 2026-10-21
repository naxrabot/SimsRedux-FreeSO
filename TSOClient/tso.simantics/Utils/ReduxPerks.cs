using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux Level Rewards (the perk tree - the server's Perks.cs): what each player family on this lot has claimed,
    /// by its family number. Part of the lot's state: saved with it and sent to everyone joining (VMMarshal 44), and only
    /// ever changed by VMNetReduxPerksCmd - which only the lot itself sends (single player: the game; online: the town
    /// lot, from the server's perk-effects.json) - so every game on a lot applies exactly the same effects.
    /// </summary>
    public class VMReduxPerks
    {
        public class Entry
        {
            /// <summary>Percent: faster skill gain, slower need decay, cheaper bills, Buy mode discount, higher job pay.</summary>
            public short Skill, Decay, Bills, Buy, Job;
            /// <summary>0xRRGGBB + 0x01000000 when set (0 = the classic green).</summary>
            public uint Plumbob, NameColor;
            /// <summary>Online name tag: the player's name and badge; the title on their profile.</summary>
            public string Name = "", Badge = "", Title = "";
            /// <summary>Free service days left (maid, gardener), how many this lot used, and the game day each was last used.</summary>
            public short Maid, Gardener, MaidUsed, GardenerUsed;
            public int MaidDay = -1, GardenerDay = -1;

            public Entry Clone() { return (Entry)MemberwiseClone(); }

            /// <summary>Any effect at all (false: only the lot's own free-day counters are kept - see VMNetReduxPerksCmd).</summary>
            public bool HasEffects
            {
                get
                {
                    return Skill != 0 || Decay != 0 || Bills != 0 || Buy != 0 || Job != 0 || Plumbob != 0 || NameColor != 0
                        || !string.IsNullOrEmpty(Badge) || !string.IsNullOrEmpty(Title) || Maid > 0 || Gardener > 0;
                }
            }

            /// <summary>The same effects (the used counters and days aside - those are the lot's own).</summary>
            public bool SameAs(Entry o)
            {
                return o != null && Skill == o.Skill && Decay == o.Decay && Bills == o.Bills && Buy == o.Buy && Job == o.Job
                    && Plumbob == o.Plumbob && NameColor == o.NameColor && Name == o.Name && Badge == o.Badge && Title == o.Title
                    && Maid == o.Maid && Gardener == o.Gardener;
            }
        }

        /// <summary>Family number -> its perks. Families without any aren't listed.</summary>
        public Dictionary<short, Entry> Families = new Dictionary<short, Entry>();

        public Entry Get(int family)
        {
            if (family <= 0 || family > short.MaxValue) return null;
            Families.TryGetValue((short)family, out var e);
            return e;
        }

        public VMReduxPerks Clone()
        {
            var c = new VMReduxPerks();
            foreach (var kv in Families) c.Families[kv.Key] = kv.Value.Clone();
            return c;
        }

        /// <summary>The same effects for the same families (families with no effects - only free-day counters kept - aside).</summary>
        public bool SameAs(VMReduxPerks o)
        {
            if (o == null) return false;
            foreach (var kv in Families)
                if (kv.Value.HasEffects && (!o.Families.TryGetValue(kv.Key, out var e) || !kv.Value.SameAs(e))) return false;
            foreach (var kv in o.Families)
                if (kv.Value.HasEffects && (!Families.TryGetValue(kv.Key, out var e) || !e.SameAs(kv.Value))) return false;
            return true;
        }

        public void SerializeInto(BinaryWriter w)
        {
            w.Write((byte)1); // (this block's own version)
            var keys = Families.Keys.OrderBy(k => k).ToList(); // (the same order everywhere)
            w.Write(keys.Count);
            foreach (var k in keys)
            {
                var e = Families[k];
                w.Write(k);
                w.Write(e.Skill); w.Write(e.Decay); w.Write(e.Bills); w.Write(e.Buy); w.Write(e.Job);
                w.Write(e.Plumbob); w.Write(e.NameColor);
                w.Write(e.Name ?? ""); w.Write(e.Badge ?? ""); w.Write(e.Title ?? "");
                w.Write(e.Maid); w.Write(e.Gardener); w.Write(e.MaidUsed); w.Write(e.GardenerUsed);
                w.Write(e.MaidDay); w.Write(e.GardenerDay);
            }
        }

        public static VMReduxPerks Read(BinaryReader r)
        {
            var p = new VMReduxPerks();
            r.ReadByte();
            int n = r.ReadInt32();
            if (n < 0 || n > 4096) throw new IOException("bad perk table");
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadInt16();
                var e = new Entry
                {
                    Skill = r.ReadInt16(), Decay = r.ReadInt16(), Bills = r.ReadInt16(), Buy = r.ReadInt16(), Job = r.ReadInt16(),
                    Plumbob = r.ReadUInt32(), NameColor = r.ReadUInt32(),
                    Name = r.ReadString(), Badge = r.ReadString(), Title = r.ReadString(),
                    Maid = r.ReadInt16(), Gardener = r.ReadInt16(), MaidUsed = r.ReadInt16(), GardenerUsed = r.ReadInt16(),
                    MaidDay = r.ReadInt32(), GardenerDay = r.ReadInt32(),
                };
                p.Families[k] = e;
            }
            return p;
        }
    }

    /// <summary>
    /// SimsRedux Level Rewards: where each perk acts. All of it is worked out from the lot's own state (the perk table,
    /// the clock, the Sims), so every game on the lot gets the same result.
    ///   skills  VMMemory (a script raising a skill) and ReduxSkillByDoing: the rise is bigger by Skill %
    ///   needs   VMTS1MotiveDecay: needs drop Decay % slower (refilling is unchanged)
    ///   bills   VMTS1Budget: paying the bills costs Bills % less
    ///   buy     VMNetBuyObjectCmd: Buy mode costs Buy % less (worked out by the lot, sent with the purchase)
    ///   jobs    VMMemory (a script reading the job's pay): the pay is Job % more
    ///   service VMTS1Budget: the maid's / gardener's charge is free on up to Maid / Gardener game days
    /// Only the family living on the lot pays bills, buys and is paid (the game's one budget); skills and needs follow
    /// each Sim's own family (a visiting player's Sims too).
    /// </summary>
    public static class ReduxPerkRules
    {
        private static VMReduxPerks.Entry Of(VM vm, VMAvatar ava)
        {
            if (vm?.ReduxPerks == null || ava == null || ava.IsPet) return null;
            return vm.ReduxPerks.Get(ava.GetPersonData(VMPersonDataVariable.TS1FamilyNumber));
        }

        private static VMReduxPerks.Entry OfLotFamily(VM vm)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            return fam == null ? null : vm.ReduxPerks?.Get(fam.ChunkID);
        }

        private static bool IsSkill(VMPersonDataVariable v)
        {
            return v == VMPersonDataVariable.CookingSkill || v == VMPersonDataVariable.MechanicalSkill || v == VMPersonDataVariable.CharismaSkill
                || v == VMPersonDataVariable.BodySkill || v == VMPersonDataVariable.LogicSkill || v == VMPersonDataVariable.CreativitySkill;
        }

        /// <summary>
        /// A script writing a person-data value: a skill going up rises Skill % more. The part of a point that doesn't
        /// come out whole is given or not by a fixed roll from the lot's clock and the Sim (the same on every game).
        /// </summary>
        public static short SkillWrite(VMStackFrame context, VMAvatar ava, VMPersonDataVariable v, short value)
        {
            try
            {
                if (ava == null || !IsSkill(v) || context?.VM == null || !context.VM.TS1) return value;
                var e = Of(context.VM, ava);
                if (e == null || e.Skill <= 0) return value;
                int old = ava.GetPersonData(v);
                if (value <= old || value > 1000 || old < 0) return value;
                int extra = (value - old) * e.Skill;
                int bonus = extra / 100;
                long ticks = context.VM.Context?.Clock?.Ticks ?? 0;
                if (Roll(ticks, ava.ObjectID, (int)v) < extra % 100) bonus++;
                return (short)Math.Min(1000, value + bonus);
            }
            catch { return value; }
        }

        /// <summary>ReduxSkillByDoing's gain (hundredths of a point a minute) for this Sim, with their Skill %.</summary>
        public static int SkillGain(VMAvatar ava, int gain)
        {
            try
            {
                var e = Of(ava?.Thread?.Context?.VM, ava);
                if (e == null || e.Skill <= 0 || gain <= 0) return gain;
                return gain + gain * e.Skill / 100;
            }
            catch { return gain; }
        }

        /// <summary>0-99, the same on every game for this tick, Sim and value.</summary>
        private static int Roll(long ticks, short objectId, int salt)
        {
            unchecked
            {
                uint h = (uint)ticks * 2654435761u ^ (uint)objectId * 40503u ^ (uint)salt * 97u;
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
                return (int)(h % 100);
            }
        }

        /// <summary>VMTS1MotiveDecay: a need's fall this step (thousandths), Decay % slower. Rises are left alone.</summary>
        public static int Decay(VMAvatar ava, VMContext context, int frac)
        {
            if (frac <= 0) return frac;
            try
            {
                var e = Of(context?.VM, ava);
                if (e == null || e.Decay <= 0) return frac;
                return frac * (100 - Math.Min((int)e.Decay, 100)) / 100;
            }
            catch { return frac; }
        }

        /// <summary>A script reading a job's pay (job data 1): Job % more, for the Sim it's about.</summary>
        public static short Salary(VMStackFrame context, short pay)
        {
            try
            {
                if (pay <= 0 || context?.VM == null) return pay;
                var ava = (context.Caller as VMAvatar) ?? (context.StackObject as VMAvatar);
                var e = Of(context.VM, ava) ?? OfLotFamily(context.VM);
                if (e == null || e.Job <= 0) return pay;
                return (short)Math.Min(short.MaxValue, pay + pay * e.Job / 100);
            }
            catch { return pay; }
        }

        /// <summary>VMNetBuyObjectCmd (on the lot, before it's sent to everyone): Buy % off for the family living here.</summary>
        public static int BuyPrice(VM vm, int value)
        {
            try
            {
                var e = OfLotFamily(vm);
                if (e == null || e.Buy <= 0 || value <= 0) return value;
                return value - value * Math.Min((int)e.Buy, 100) / 100;
            }
            catch { return value; }
        }

        /// <summary>The Buy mode price the player sees (the same sum as BuyPrice) - for the catalog's price labels.</summary>
        public static int ShownPrice(VM vm, int price) { return BuyPrice(vm, price); }

        /// <summary>
        /// VMTS1Budget: an amount about to come off the family's money (amount &gt; 0). The bills cost Bills % less; the
        /// maid's or gardener's charge is free while there are free days left (one day is used up the first time they're
        /// paid on a game day - any more that day are free too). justTest: only asking, nothing is used up.
        /// </summary>
        public static int Budget(VMStackFrame context, int amount, bool justTest)
        {
            if (amount <= 0) return amount;
            try
            {
                var vm = context?.VM;
                var e = OfLotFamily(vm);
                if (e == null) return amount;
                var what = Payee(context);
                if (what == PayeeKind.Bills)
                    return (e.Bills > 0) ? amount - amount * Math.Min((int)e.Bills, 100) / 100 : amount;
                bool maid = what == PayeeKind.Maid;
                bool gardener = what == PayeeKind.Gardener;
                if (!maid && !gardener) return amount;
                var clock = vm.Context.Clock;
                int day = clock.Year * 400 + clock.Month * 32 + clock.DayOfMonth;
                if (maid)
                {
                    if (e.MaidDay == day) return 0;
                    if (e.Maid <= 0) return amount;
                    if (!justTest) { e.Maid--; e.MaidUsed++; e.MaidDay = day; }
                    return 0;
                }
                if (e.GardenerDay == day) return 0;
                if (e.Gardener <= 0) return amount;
                if (!justTest) { e.Gardener--; e.GardenerUsed++; e.GardenerDay = day; }
                return 0;
            }
            catch { return amount; }
        }

        private enum PayeeKind { Other, Bills, Maid, Gardener }

        // (from the game files - the same in every language and on every game: object GUIDs and BHAV labels,
        //  never a Sim's name or an interaction's (translated) name, which a player could pick or that differ by language)
        private const uint BillsGUID = 0xD840F488;      // Bills.iff "Bills" - its "Pay Bills" trees, the overdue/evict ones
        private const string MailboxPayBills = "CT - Pay Bills"; // MailBox.iff: where the bills are actually paid
        private const uint MaidGUID = 0xC61931DC;       // People/maid.iff "NPC Maid"
        private const uint GardenerGUID = 0xD62B91B4;   // People/gardener.iff "NPC Gardener"

        private static uint GuidOf(VMEntity ent)
        {
            try { return ent?.Object?.OBJ?.GUID ?? 0; } catch { return 0; }
        }

        /// <summary>
        /// Who this money is for, from the game files only: the bills (the Bills object's own code, or the mailbox's
        /// "CT - Pay Bills" tree), or the maid / gardener (their own code - "get paid", "leave", "Dismiss" - or code
        /// run by them: the NPC controller's "normal take" that the game's "NPC Get Paid" runs on their thread).
        /// </summary>
        private static PayeeKind Payee(VMStackFrame context)
        {
            uint owner = 0;
            try { owner = context.CodeOwner?.OBJ?.GUID ?? 0; } catch { owner = 0; }
            var label = (context.Routine?.Chunk?.ChunkLabel ?? "").TrimEnd('\0').Trim();
            if (owner == BillsGUID || label == MailboxPayBills) return PayeeKind.Bills;
            var caller = GuidOf(context.Caller); // (the Sim whose thread this is)
            if (owner == MaidGUID || caller == MaidGUID) return PayeeKind.Maid;
            if (owner == GardenerGUID || caller == GardenerGUID) return PayeeKind.Gardener;
            return PayeeKind.Other;
        }

        // ---------------------------------------------------------------- money and gifts (VMNetReduxPerksCmd)

        /// <summary>Money for the family living on this lot (only theirs - it's the game's one budget). False if it isn't here.</summary>
        public static bool GiveMoney(VM vm, int family, int amount)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || fam.ChunkID != family || amount == 0) return false;
            fam.Budget = (int)Math.Max(0, Math.Min(999999999L, (long)fam.Budget + amount));
            try
            {
                // ("+§250" over one of the family, like the money cheat)
                var sim = vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>().FirstOrDefault(a => a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == family && a.Position != LotTilePos.OUT_OF_WORLD);
                sim?.ShowMoneyHeadline(amount);
            }
            catch { }
            return true;
        }

        /// <summary>
        /// A free object for the family living here: put down by the mailbox (or the nearest free spot around it, then
        /// around the middle of the lot). No room anywhere (or an object this game doesn't have): its catalog price
        /// instead. Returns what happened, for the log.
        /// </summary>
        public static string GiveObject(VM vm, int family, uint guid)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || fam.ChunkID != family) return "not this lot's family";
            var catalog = Content.Content.Get().WorldCatalog;
            var item = catalog?.GetItemByGUID(guid);
            int price = item != null ? (int)item.Value.Price : 0;
            VMMultitileGroup group = null;
            try { group = vm.Context.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, Direction.NORTH); } catch { group = null; }
            if (group == null || group.BaseObject == null)
            {
                if (price > 0) GiveMoney(vm, family, price);
                return "object " + guid.ToString("X8") + " isn't in this game - gave its price (§" + price + ") instead";
            }
            var mailbox = vm.Entities.FirstOrDefault(x => x.Object?.OBJ != null && (x.Object.OBJ.GUID == 0xEF121974 || x.Object.OBJ.GUID == 0x1D95C9B0) && x.Position != LotTilePos.OUT_OF_WORLD);
            var arch = vm.Context.Architecture;
            var spots = new List<LotTilePos>();
            if (mailbox != null) spots.Add(mailbox.Position);
            spots.Add(LotTilePos.FromBigTile((short)(arch.Width / 2), (short)(arch.Height / 2), 1));
            foreach (var at in spots)
            {
                for (int r = 1; r <= 10; r++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        for (int dy = -r; dy <= r; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // (the ring at this distance)
                            int x = at.TileX + dx, y = at.TileY + dy;
                            if (x < 1 || y < 1 || x >= arch.Width - 1 || y >= arch.Height - 1) continue;
                            var pos = LotTilePos.FromBigTile((short)x, (short)y, at.Level);
                            for (int d = 0; d < 4; d++)
                            {
                                var dir = (Direction)(1 << (d * 2));
                                if (group.ChangePosition(pos, dir, vm.Context, VMPlaceRequestFlags.Default).Status == VMPlacementError.Success)
                                {
                                    group.ExecuteEntryPoint(11, vm.Context); // (as if placed from Buy mode)
                                    group.InitialPrice = price; // (it sells like one that was bought)
                                    return "placed " + (item?.Name ?? guid.ToString("X8")) + " at " + x + "," + y;
                                }
                            }
                        }
                    }
                }
            }
            group.Delete(vm.Context);
            if (price > 0) GiveMoney(vm, family, price);
            return "no room for " + guid.ToString("X8") + " - gave its price (§" + price + ") instead";
        }
    }
}
