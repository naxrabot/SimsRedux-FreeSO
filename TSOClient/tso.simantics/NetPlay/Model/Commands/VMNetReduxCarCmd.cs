using System;
using System.IO;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux car pack round 3 (cmd 87): a family's own car - bought in Buy mode (Miscellaneous > Cars), placed on their
    /// lot, driven to another lot (Utils/ReduxCarShop.cs has what a car is on the lot).
    ///   Place    - 21.1: the family buys car Car, version Variant, paint Paint for Price (worked out by the game buying - the
    ///              lot's own game - from the pack's manifest or the built-in table, so every game takes the same money), put
    ///              where the player placed it in Buy mode: its base tile at X, Y (1/16 tiles, level 1), facing Facing - if
    ///              that spot is still free for it (ReduxCarShop.SpotError). Home lot only; nothing if they haven't the money.
    ///              (X, Y and Facing travel in CarID, SimID and Skin, so the command's bytes are the same as 21.0's.)
    ///   Buy      - 21.0's Showroom (no longer sent - kept so a 21.0 game's command still does what it did): bought as Place,
    ///              parked in the free 3 x 6 spot nearest the street (ReduxCarShop.FindSpot).
    ///   Park     - the family (Family) drove here in their car (Skin) - 21.7: it drops them off at the kerb in front of the
    ///              lot and drives on (ReduxCarShop.DropOff - a passing car with its look; nothing stays parked, Joey: a car
    ///              on the street "causes traffic"). Any car left parked from a 21.6 trip goes first. Never on their home lot.
    ///   Walk     - Sim SimID walks to car CarID (Sims 1's own Go Here), to get in and drive.
    ///   DriveOff - car CarID drives off with its driver: a passing car with its look pulls out of the spot into the street
    ///              (ReduxTraffic.DriveAway). The family's own car stays on the lot (their game hides it while they leave);
    ///              21.7: a car that only pulled up to pick them up (VisitMarker) goes with them.
    ///   Guest    - 21.6 "Drive to Player": a visiting player's Sim (the command's sender) came in their family's car (Skin) -
    ///              21.7: it drops them off at the kerb and drives on, like Park.
    ///   Kerb     - 21.7 "Drive Home": Sim SimID walks (never runs) to the sidewalk by the kerb in front of the lot.
    ///   Pickup   - 21.7 "Drive Home": the family's car (Skin, Family) drives up the street and stops level with Sim SimID
    ///              (ReduxTraffic.DriveUp), a parked car marked with the Sim (ReduxCarShop.PickupTag) - then DriveOff takes them
    ///              (single player), or the visitor goes home and it drives off by itself once their Sim has left the lot
    ///              (ReduxCarShop.TidyGuestCars, the same minute on every game).
    /// A visiting player's game may send only Guest, Kerb and Pickup, and only for their own Sim; the others only the game
    /// running the lot sends (single player, or a visit's host). Everything here looks only at the lot, so every game of
    /// the lot does the same. Done tells each game what happened.
    /// </summary>
    public class VMNetReduxCarCmd : VMNetCommandBodyAbstract
    {
        public enum CarMode : byte { Buy = 0, Park = 1, Walk = 2, DriveOff = 3, Place = 4, Guest = 5, Kerb = 6, Pickup = 7 }

        public CarMode Mode;
        public short Car, Variant, Paint;
        public int Price;
        public short Skin, Family;
        public short CarID, SimID;

        /// <summary>21.1 Place: where the car's base tile goes (1/16 tiles, level 1) and the way it faces - carried in CarID, SimID and Skin.</summary>
        public short X { get { return CarID; } set { CarID = value; } }
        public short Y { get { return SimID; } set { SimID = value; } }
        public Direction Facing { get { return (Direction)(Skin & 0xFF); } set { Skin = (short)(byte)value; } }

        /// <summary>On every game: what happened (why == null: it went through; else why not, "" for nothing to show).</summary>
        public static event Action<VM, VMNetReduxCarCmd, string> Done;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1 || vm.Context?.Architecture == null) return false;
            try
            {
                switch (Mode)
                {
                    case CarMode.Buy: return Buy(vm);
                    case CarMode.Place: return Place(vm);
                    case CarMode.Park: return Park(vm);
                    case CarMode.Guest: return GuestDropOff(vm, caller);
                    case CarMode.Kerb:
                        {
                            var sim = vm.GetObjectById(SimID) as VMAvatar;
                            if (sim == null || sim.Dead || sim.IsPet) return Tell(vm, "");
                            if (!ReduxCarShop.WalkToKerb(vm, sim)) return Tell(vm, "There's no way to get to the street from here.");
                            return Tell(vm, null, true);
                        }
                    case CarMode.Pickup:
                        {
                            var sim = vm.GetObjectById(SimID) as VMAvatar;
                            if (sim == null || sim.Dead || sim.IsPet || !GuestSkinOk(Skin)) return Tell(vm, "");
                            // (a visiting player's family number means nothing on this lot)
                            var family = sim.PersistID >= ReduxCarShop.FirstGuestID ? (short)0 : Family;
                            if (!ReduxCarShop.Pickup(vm, sim, Skin, family)) return Tell(vm, "The car can't get to you here - walk to the street by the front of the lot.");
                            return Tell(vm, null, true);
                        }
                    case CarMode.Walk:
                        {
                            var sim = vm.GetObjectById(SimID) as VMAvatar;
                            var car = vm.GetObjectById(CarID);
                            if (sim == null || sim.Dead || sim.IsPet || car == null || car.Dead || !ReduxCarShop.IsParkedCar(car)) return Tell(vm, "");
                            if (!ReduxCarShop.WalkTo(vm, sim, car)) return Tell(vm, "There's no way to get to the car.");
                            return Tell(vm, null, true);
                        }
                    case CarMode.DriveOff:
                        {
                            var car = vm.GetObjectById(CarID);
                            if (car == null || car.Dead || !ReduxCarShop.IsParkedCar(car)) return Tell(vm, "");
                            var b = ReduxCarShop.BaseOf(car);
                            bool away = ReduxTraffic.DriveAway(vm, b, b.GetAttribute(ReduxCarSkins.SkinAttribute), b.ObjectID);
                            // (21.7: a car that only pulled up for the pick-up leaves with them - never left in the street)
                            if (ReduxCarShop.IsVisitCar(b)) b.MultitileGroup?.Delete(vm.Context);
                            return away ? Tell(vm, null, true) : Tell(vm, "");
                        }
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] car " + Mode + " failed: " + e.Message); }
            return false;
        }

        private bool Buy(VM vm)
        {
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null || fam.HouseNumber <= 0 || vm.GetGlobalValue(10) != fam.HouseNumber) return Tell(vm, "Cars can only be bought for your own home.");
            int variant;
            if (Price <= 0 || !ReduxCarSkins.Decode(ReduxCarSkins.Code(Car, Variant, Paint), out int _, out variant, out int _)) return Tell(vm, "");
            if (Car < 0 || Car >= ReduxCarSkins.FirstService) return Tell(vm, "");
            if (fam.Budget < Price) return Tell(vm, "Your family doesn't have §" + Price.ToString("N0") + ".");
            int x0, y0; Direction dir;
            if (!ReduxCarShop.FindSpot(vm, out x0, out y0, out dir))
                return Tell(vm, "There's no room to park it. Clear a space 3 by 6 tiles in your yard near the street.");
            var group = ReduxCarShop.Park(vm, x0, y0, dir, ReduxCarSkins.Code(Car, Variant, Paint), (short)fam.ChunkID, ReduxCarShop.OwnedMarker, Price);
            if (group == null) return Tell(vm, "There's no room to park it. Clear a space 3 by 6 tiles in your yard near the street.");
            fam.Budget -= Price;
            CarID = group.BaseObject.ObjectID;
            return Tell(vm, null, true);
        }

        /// <summary>21.1: bought in Buy mode and put where the player placed it (the spot is checked again here, on the lot).</summary>
        private bool Place(VM vm)
        {
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null || fam.HouseNumber <= 0 || vm.GetGlobalValue(10) != fam.HouseNumber) return Tell(vm, "Cars can only be bought for your own home.");
            var code = ReduxCarSkins.Code(Car, Variant, Paint);
            if (Price <= 0 || Car < 0 || Car >= ReduxCarSkins.FirstService || Variant < 0 || Variant > ReduxCarSkins.MaxVariant || Paint < 0 || Paint > 15) return Tell(vm, "");
            if (!ReduxCarSkins.Decode(code, out int _, out int _, out int _)) return Tell(vm, "");
            if (fam.Budget < Price) return Tell(vm, "Your family doesn't have §" + Price.ToString("N0") + ".");
            var dir = Facing;
            VMPlacementError why;
            var group = ReduxCarShop.PlaceOwned(vm, new LotTilePos(X, Y, 1), dir, code, (short)fam.ChunkID, Price, out why);
            if (group == null) return Tell(vm, why == VMPlacementError.CantIntersectOtherObjects ? "Something's in the way there now - try another spot." : "The car doesn't fit there.");
            fam.Budget -= Price;
            CarID = group.BaseObject.ObjectID; // (Done's listeners: the car that was placed)
            return Tell(vm, null, true);
        }

        private bool Park(VM vm)
        {
            ReduxCarShop.ClearVisitCars(vm);
            if (Skin <= 0 || Family <= 0) return Tell(vm, "", true);
            var fam = vm.TS1State?.CurrentFamily;
            if (fam != null && fam.HouseNumber > 0 && vm.GetGlobalValue(10) == fam.HouseNumber) return Tell(vm, "", true); // (their own car is at home)
            if (!ReduxCarSkins.Decode(Skin, out int _, out int _, out int _)) return Tell(vm, "", true);
            // (21.7: drops them off and drives on - nothing left in the street)
            return Tell(vm, ReduxCarShop.DropOff(vm, Skin) ? null : "", true);
        }

        /// <summary>21.6 / 21.7: a visiting player arrived in their own car - it drops their Sim off and drives on.</summary>
        private bool GuestDropOff(VM vm, VMAvatar caller)
        {
            if (caller == null || caller.Dead || caller.IsPet || caller.PersistID < ReduxCarShop.FirstGuestID) return Tell(vm, "");
            if (!GuestSkinOk(Skin)) return Tell(vm, "");
            return Tell(vm, ReduxCarShop.DropOff(vm, Skin) ? null : "", true);
        }

        /// <summary>A family car's look (an everyday or traffic car - never a service vehicle).</summary>
        private static bool GuestSkinOk(short skin)
        {
            int car;
            return skin > 0 && ReduxCarSkins.Decode(skin, out car, out int _, out int _) && car >= 0 && car < ReduxCarSkins.FirstService;
        }

        private bool Tell(VM vm, string why, bool result = false)
        {
            try { Done?.Invoke(vm, this, why); } catch { }
            return result;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // only the lot's own game - never a visiting player's (a visitor never spends the host's money or moves their car)
            if (!FromNet) return Mode <= CarMode.Pickup;
            // 21.6 / 21.7: a visiting player's game - only their own Sim, and their own car arriving (Guest) or picking them up
            if (caller == null || caller.Dead || caller.IsPet || caller.PersistID < ReduxCarShop.FirstGuestID) return false;
            if (Mode != CarMode.Guest && Mode != CarMode.Kerb && Mode != CarMode.Pickup) return false;
            if (Mode != CarMode.Kerb && !GuestSkinOk(Skin)) return false;
            if (Mode != CarMode.Guest && SimID != caller.ObjectID) return false;
            // (one of each every few seconds at most - no street full of a visitor's cars)
            var now = DateTime.UtcNow;
            long key = ((long)caller.PersistID << 8) | (byte)Mode;
            lock (LastFromGuest)
            {
                DateTime t;
                if (LastFromGuest.TryGetValue(key, out t) && now - t < TimeSpan.FromSeconds(Mode == CarMode.Kerb ? 2 : 10)) return false;
                if (LastFromGuest.Count > 400) LastFromGuest.Clear();
                LastFromGuest[key] = now;
            }
            return true;
        }

        /// <summary>When each visiting player's game last sent each of its car commands (the host's Verify only - never the lot).</summary>
        private static readonly System.Collections.Generic.Dictionary<long, DateTime> LastFromGuest = new System.Collections.Generic.Dictionary<long, DateTime>();

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(Car);
            writer.Write(Variant);
            writer.Write(Paint);
            writer.Write(Price);
            writer.Write(Skin);
            writer.Write(Family);
            writer.Write(CarID);
            writer.Write(SimID);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (CarMode)reader.ReadByte();
            Car = reader.ReadInt16();
            Variant = reader.ReadInt16();
            Paint = reader.ReadInt16();
            Price = reader.ReadInt32();
            Skin = reader.ReadInt16();
            Family = reader.ReadInt16();
            CarID = reader.ReadInt16();
            SimID = reader.ReadInt16();
        }
    }
}
