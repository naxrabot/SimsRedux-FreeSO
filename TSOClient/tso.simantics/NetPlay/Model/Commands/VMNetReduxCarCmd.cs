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
    ///   Park     - a car parked for a trip: the family (Family) drove here in their car (Skin) - parked by the street if
    ///              there's room, else not shown. Any car left from an earlier trip goes first. Never on their own home lot.
    ///   Walk     - Sim SimID walks to car CarID (Sims 1's own Go Here), to get in and drive.
    ///   DriveOff - car CarID drives off with its driver: a passing car with its look pulls out of the spot into the street
    ///              (ReduxTraffic.DriveAway). The parked car stays on the lot (the family's game hides it while they leave).
    /// Only the game running the lot sends these (single player, or a visit's host) - never a visiting player's game - and
    /// everything here looks only at the lot, so every game of the lot does the same. Done tells each game what happened.
    /// </summary>
    public class VMNetReduxCarCmd : VMNetCommandBodyAbstract
    {
        public enum CarMode : byte { Buy = 0, Park = 1, Walk = 2, DriveOff = 3, Place = 4 }

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
                            if (!ReduxTraffic.DriveAway(vm, b, b.GetAttribute(ReduxCarSkins.SkinAttribute), b.ObjectID)) return Tell(vm, "");
                            return Tell(vm, null, true);
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
            int x0, y0; Direction dir;
            if (!ReduxCarShop.FindSpot(vm, out x0, out y0, out dir)) return Tell(vm, "", true);
            var group = ReduxCarShop.Park(vm, x0, y0, dir, Skin, Family, ReduxCarShop.VisitMarker, 0);
            if (group != null) CarID = group.BaseObject.ObjectID;
            return Tell(vm, group == null ? "" : null, true);
        }

        private bool Tell(VM vm, string why, bool result = false)
        {
            try { Done?.Invoke(vm, this, why); } catch { }
            return result;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // only the lot's own game - never a visiting player's (a visitor never spends the host's money or moves their car)
            return !FromNet && Mode <= CarMode.Place;
        }

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
