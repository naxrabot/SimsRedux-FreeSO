using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.1 (P1): the cellphone on the lot (cmd 83).
    ///   Out   - a Sim takes their cellphone out (Out = true) or puts it away: the phone drawn over their head while its
    ///           menu is open (VMReduxPhone, person attribute 105). Always the Sim sending it (online: the lot sets who sent
    ///           it, so only a player's own Sim), only a grown-up; a visiting player's game at most once a second.
    ///   Buy   - a family grown-up orders a cellphone (VMReduxPhone.BuyPrice from the family's money) on the home computer's
    ///           Shop Online (Place) for themselves or another family grown-up (For); they spend a moment there. It comes
    ///           as a package the next day (the player's own game - Simitone's ReduxPackages).
    ///   Sell  - a family grown-up sells their cellphone back (VMReduxPhone.SellPrice to the family's money), at home.
    ///   Place - (17.2) a Shop Online package box (VMReduxPhone.PackageGUID) put down by the mailbox (Place) with the
    ///           game's own placing, holding order Tag (object attribute 0); visitors see it like any object.
    ///   Remove- (17.2) the package box (Place) goes - opened by the family, or a box with no order any more.
    /// Buy, Sell, Place and Remove only from the game running the lot (single player, or the visit's host) - a visitor's game can't send
    /// them - and only change the family's money; who owns a phone is kept by the player's own game (its side file), never
    /// on the lot, so nothing here depends on it and every game stays in step. Bought / Sold tell the games what happened.
    /// What the phone then does goes through the lot's own commands (the house phone's interactions, the visit's and
    /// Redux's own), never this one.
    /// </summary>
    public class VMNetReduxPhoneCmd : VMNetCommandBodyAbstract
    {
        public enum PhoneMode : byte { Out = 0, Buy = 1, Sell = 2, Place = 3, Remove = 4 }

        public PhoneMode Mode = PhoneMode.Out;
        public bool Out;
        /// <summary>Buy: the computer it's bought at (0: none). Place: the mailbox. Remove: the package box.</summary>
        public short Place;
        /// <summary>Place: the order the box holds (object attribute 0). Remove: filled in from the box, for Done.</summary>
        public short Tag;
        /// <summary>Buy: who it's for (their GUID - only the buyer's own game uses it; the lot just passes it on).</summary>
        public uint For;

        /// <summary>Buy / Sell / Place / Remove went through (why == null) or not (why: the reason, "" for none to show) - on every game.</summary>
        public static event Action<VM, VMNetReduxPhoneCmd, VMAvatar, string> Done;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!Check(vm, caller)) return false;
            switch (Mode)
            {
                case PhoneMode.Out:
                    VMReduxPhone.Set(vm, caller, Out);
                    return true;
                case PhoneMode.Buy:
                case PhoneMode.Sell:
                    return Trade(vm, caller);
                case PhoneMode.Place: return PutDown(vm, caller);
                case PhoneMode.Remove: return TakeAway(vm, caller);
            }
            return false;
        }

        private bool Trade(VM vm, VMAvatar who)
        {
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null || !(fam.FamilyGUIDs ?? new uint[0]).Contains(who.Object.OBJ.GUID)) return Tell(vm, who, "");
            if (Mode == PhoneMode.Buy)
            {
                if (fam.Budget < VMReduxPhone.BuyPrice) return Tell(vm, who, "Your family doesn't have §" + VMReduxPhone.BuyPrice + ".");
                fam.Budget -= VMReduxPhone.BuyPrice;
                var at = Place != 0 ? vm.GetObjectById(Place) : null;
                if (at != null && !(at is VMAvatar)) ReduxWooHoo.PushWait(vm, who, at, Math.Max(1, vm.Context.Clock.TicksPerMinute) * 3, "Shopping");
            }
            else
            {
                // (at home only - the family's own lot)
                if (fam.HouseNumber <= 0 || vm.GetGlobalValue(10) != fam.HouseNumber) return Tell(vm, who, "Sell the cellphone from your own home.");
                // 17.2: the lot can't tell who owns a phone (that's the player's own game's side file), so a Sim can sell
                // at most once per game hour on the lot - person attribute 106 (VMReduxPhone.SoldAttr), the same on every game.
                short stamp = VMReduxPhone.HourStamp(vm);
                if (who.GetAttribute(VMReduxPhone.SoldAttr) == stamp) return Tell(vm, who, "");
                who.SetAttribute(VMReduxPhone.SoldAttr, stamp);
                fam.Budget += VMReduxPhone.SellPrice;
            }
            Tell(vm, who, null);
            return true;
        }

        /// <summary>17.2: a package box by the mailbox (VMFindLocationFor, the game's own placing) holding order Tag.</summary>
        private bool PutDown(VM vm, VMAvatar who)
        {
            var mailbox = vm.GetObjectById(Place);
            if (mailbox == null || mailbox.Dead || mailbox is VMAvatar || mailbox.Position == LotTilePos.OUT_OF_WORLD || Tag <= 0) return Tell(vm, who, "");
            if (vm.Entities.Any(e => !e.Dead && VMReduxPhone.IsPackage(e) && e.GetAttribute(VMReduxPhone.TagAttr) == Tag)) return Tell(vm, who, ""); // (already there)
            VMMultitileGroup group = null;
            try { group = vm.Context.CreateObjectInstance(VMReduxPhone.PackageGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH); } catch { group = null; }
            if (group?.BaseObject == null) return Tell(vm, who, "");
            VMFindLocationFor.FindLocationFor(group.BaseObject, mailbox, vm.Context, VMPlaceRequestFlags.Default);
            if (group.BaseObject.Position == LotTilePos.OUT_OF_WORLD) { group.Delete(vm.Context); return Tell(vm, who, ""); }
            foreach (var o in group.Objects) o.SetAttribute(VMReduxPhone.TagAttr, Tag);
            Tell(vm, who, null);
            return true;
        }

        /// <summary>17.2: the package box goes (opened, or a box with no order) - its order's tag goes to Done.</summary>
        private bool TakeAway(VM vm, VMAvatar who)
        {
            var box = vm.GetObjectById(Place);
            if (box == null || box.Dead || !VMReduxPhone.IsPackage(box)) return Tell(vm, who, "");
            Tag = box.GetAttribute(VMReduxPhone.TagAttr);
            if (box.MultitileGroup != null) box.MultitileGroup.Delete(vm.Context);
            else box.Delete(true, vm.Context);
            Tell(vm, who, null);
            return true;
        }

        private bool Tell(VM vm, VMAvatar who, string why)
        {
            try { Done?.Invoke(vm, this, who, why); } catch { }
            return false;
        }

        private static readonly Dictionary<uint, DateTime> Last = new Dictionary<uint, DateTime>();

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!Check(vm, caller)) return false;
            if (!FromNet) return true; // (this game runs the lot)
            if (Mode != PhoneMode.Out) return false; // (a visitor never spends or earns the host's money)
            var now = DateTime.UtcNow;
            lock (Last)
            {
                DateTime t;
                if (Last.TryGetValue(caller.PersistID, out t) && now - t < TimeSpan.FromSeconds(1)) return false;
                if (Last.Count > 200) Last.Clear();
                Last[caller.PersistID] = now;
            }
            return true;
        }

        private bool Check(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1 || caller == null) return false;
            if (Mode > PhoneMode.Remove) return false;
            if (Mode == PhoneMode.Place || Mode == PhoneMode.Remove) return true; // (the lot's own game - whoever it's playing)
            return ReduxWooHoo.Grownup(caller); // (a grown-up, on the lot - not a pet, a child, a ghost or the maid)
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(Out);
            writer.Write(Place);
            writer.Write(For);
            writer.Write(Tag);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (PhoneMode)reader.ReadByte();
            Out = reader.ReadBoolean();
            Place = reader.ReadInt16();
            For = reader.ReadUInt32();
            Tag = reader.ReadInt16();
        }
    }
}
