using System;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.0 (Part A #10): WooHoo. Everything that changes the lot for a WooHoo goes through this command, so on a
    /// visit every player's game does the same thing at the same moment (the lot's side is Utils/ReduxWooHoo.cs).
    ///   Ask      - a player asks another player's Sim (Singleplayer+): changes nothing, each game's screen decides who
    ///              is asked (Asked). Sent by the asker's game - a guest only for their own Sim (A).
    ///   Answer   - that player's Accept / Decline: changes nothing (Answered). A guest only for their own Sim (B).
    ///   Start    - the two Sims (A, B) go to the spot (Place, Kind); Chance (0-100) is the chance of a baby, worked
    ///              out by the lot's game (0: none possible), Protected says protection was used.
    ///   Finish   - it's over (Yes: it happened, so needs, relationship and the baby chance count; no: called off).
    ///   Birth    - at home: the baby arrives, in the game's own bassinet, by the mother (A); B the father if he's here.
    ///   Buy      - a family Sim (A) buys protection (Item 0) or a pack of pills (Item 1) at the store display, the
    ///              medicine cabinet or the computer (Source 0/1/2, Place the object): the money comes off the family.
    ///   SetSwitch- the lot's game says whether WooHoo is on (On) - while it is, the game's own "Should we have a
    ///              baby?" question is off.
    /// Only the game running the lot sends Start, Finish, Birth, Buy and SetSwitch (single player, or the visit's host).
    /// </summary>
    public class VMNetReduxWooHooCmd : VMNetCommandBodyAbstract
    {
        public enum WooMode : byte
        {
            Ask = 0,
            Answer = 1,
            Start = 2,
            Finish = 3,
            Birth = 4,
            Buy = 5,
            SetSwitch = 6,
        }

        public enum ItemKind : byte { Protection = 0, Pills = 1 }

        /// <summary>The chance of a baby with protection, or after the pill (near zero).</summary>
        public const int ProtectedChance = 1;

        public WooMode Mode;
        public short A, B, Place;
        public byte Kind;
        public bool Protected, Yes, On;
        public byte Chance;
        /// <summary>Ask: the asker's Sim took the pill / their fertility today (0-100; 255 = no baby possible). Answer: the same for the answerer's Sim.</summary>
        public bool APill, BPill;
        public byte AFert, BFert;
        public byte Item, Source;
        public short Count;
        public int Price;

        /// <summary>Ask, Answer: on every game (the screen shows the question or the answer to the right player).</summary>
        public static event Action<VM, VMNetReduxWooHooCmd> Asked, Answered;
        /// <summary>Start went ahead, or not (why: null when it did).</summary>
        public static event Action<VM, VMNetReduxWooHooCmd, string> Started;
        /// <summary>Finish: the two Sims (either can be null if gone), whether it happened, whether there's a baby on the way (the carrier).</summary>
        public static event Action<VM, VMNetReduxWooHooCmd, VMAvatar, VMAvatar, bool, VMAvatar> Finished;
        /// <summary>Birth: the mother, and why not (null: the baby arrived).</summary>
        public static event Action<VM, VMNetReduxWooHooCmd, VMAvatar, string> Born;
        /// <summary>Buy: why not (null: bought - the money is gone and the buyer's game adds it to the family's things).</summary>
        public static event Action<VM, VMNetReduxWooHooCmd, string> Bought;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            try
            {
                switch (Mode)
                {
                    case WooMode.Ask: Fire(Asked, vm); return true;
                    case WooMode.Answer: Fire(Answered, vm); return true;
                    case WooMode.Start: return Start(vm);
                    case WooMode.Finish: return Finish(vm);
                    case WooMode.Birth: return Birth(vm);
                    case WooMode.Buy: return Buy(vm);
                    case WooMode.SetSwitch: ReduxWooHoo.SetOn(vm, On); return true;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] woohoo command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private void Fire(Action<VM, VMNetReduxWooHooCmd> ev, VM vm)
        {
            try { ev?.Invoke(vm, this); } catch (Exception e) { Console.WriteLine("[SimsRedux] woohoo: " + e.Message); }
        }

        private bool Refuse(VM vm, string why)
        {
            try { Started?.Invoke(vm, this, why); } catch { }
            return false;
        }

        // ------------------------------------------------------------------

        private bool Start(VM vm)
        {
            var a = vm.GetObjectById(A) as VMAvatar;
            var b = vm.GetObjectById(B) as VMAvatar;
            var spot = vm.GetObjectById(Place);
            var kind = (ReduxWooHoo.Place)Kind;
            if (a == null || b == null || a == b || spot == null) return Refuse(vm, "Someone isn't here any more.");
            if (!ReduxWooHoo.Grownup(a) || !ReduxWooHoo.Grownup(b)) return Refuse(vm, "WooHoo is for grown-ups only.");
            if (ReduxWooHoo.Busy(a) || ReduxWooHoo.Busy(b)) return Refuse(vm, "One of them is busy with that already.");
            if (!ReduxWooHoo.InLove(a, b)) return Refuse(vm, "They need to be in love first.");
            if (kind == ReduxWooHoo.Place.None || ReduxWooHoo.KindOf(spot) != kind) return Refuse(vm, "That spot can't be used.");
            var base0 = spot.MultitileGroup?.BaseObject ?? spot;
            // (one WooHoo per spot)
            foreach (var o in vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>())
                if (ReduxWooHoo.Busy(o) && o.GetAttribute(ReduxWooHoo.PlaceAttr) == base0.ObjectID) return Refuse(vm, "Someone's already using that.");

            bool ok;
            switch (kind)
            {
                case ReduxWooHoo.Place.Bed:
                    {
                        var sides = ReduxBeds.Sides(spot);
                        if (sides.Count != 2) return Refuse(vm, "That isn't a double bed.");
                        ok = PushSleep(vm, a, sides[0]) && PushSleep(vm, b, sides[1]);
                        break;
                    }
                case ReduxWooHoo.Place.HotTub:
                    {
                        TTABInteraction ia;
                        var piece = ReduxWooHoo.FindInteraction(spot, "Get In", out ia);
                        ok = piece != null && ReduxWooHoo.PushOwn(vm, a, piece, ia) && ReduxWooHoo.PushOwn(vm, b, piece, ia);
                        break;
                    }
                case ReduxWooHoo.Place.Shower:
                    {
                        TTABInteraction ia;
                        var piece = ReduxWooHoo.FindInteraction(spot, "Take a Shower", out ia) ?? ReduxWooHoo.FindInteraction(spot, "Take Shower", out ia);
                        int perMinute = Math.Max(1, vm.Context.Clock.TicksPerMinute);
                        ok = piece != null && ReduxWooHoo.PushOwn(vm, a, piece, ia)
                            && ReduxWooHoo.PushWait(vm, b, piece, perMinute * 90, ReduxWooHoo.ActionName);
                        break;
                    }
                default: ok = false; break;
            }
            if (!ok)
            {
                ReduxWooHoo.CancelActions(a);
                ReduxWooHoo.CancelActions(b);
                return Refuse(vm, "They couldn't get to it.");
            }
            short start = (short)(ReduxWooHoo.MinuteOfDay(vm) + 1);
            foreach (var pair in new[] { new[] { a, b }, new[] { b, a } })
            {
                var s = pair[0];
                s.SetAttribute(ReduxWooHoo.StateAttr, ReduxWooHoo.StateOn);
                s.SetAttribute(ReduxWooHoo.PartnerAttr, pair[1].ObjectID);
                s.SetAttribute(ReduxWooHoo.PlaceAttr, base0.ObjectID);
                s.SetAttribute(ReduxWooHoo.KindAttr, Kind);
                s.SetAttribute(ReduxWooHoo.StartAttr, start);
                s.SetAttribute(ReduxWooHoo.InAttr, 0);
                s.SetAttribute(ReduxWooHoo.ChanceAttr, (short)(Math.Min((int)Chance, 100) + 1));
                s.SetAttribute(ReduxWooHoo.FlagsAttr, (short)(Protected ? 1 : 0));
            }
            try { Started?.Invoke(vm, this, null); } catch { }
            return true;
        }

        private static bool PushSleep(VM vm, VMAvatar sim, VMEntity side)
        {
            if (side?.TreeTable == null) return false;
            foreach (var ia in side.TreeTable.Interactions)
                if (ReduxBeds.IsSleep(side, (int)ia.TTAIndex)) return ReduxWooHoo.PushOwn(vm, sim, side, ia);
            return false;
        }

        private bool Finish(VM vm)
        {
            var a = vm.GetObjectById(A) as VMAvatar;
            if (a == null || !ReduxWooHoo.Busy(a)) return false;
            var b = vm.GetObjectById(a.GetAttribute(ReduxWooHoo.PartnerAttr)) as VMAvatar;
            if (b != null && (!ReduxWooHoo.Busy(b) || b.GetAttribute(ReduxWooHoo.PartnerAttr) != a.ObjectID)) b = null;
            int chance = Math.Max(0, a.GetAttribute(ReduxWooHoo.ChanceAttr) - 1);
            var kind = (ReduxWooHoo.Place)a.GetAttribute(ReduxWooHoo.KindAttr);
            foreach (var s in new[] { a, b })
            {
                if (s == null) continue;
                ReduxWooHoo.Clear(s);
                ReduxWooHoo.CancelActions(s);
            }
            bool happened = Yes && b != null && !a.Dead && !b.Dead;
            VMAvatar carrier = null;
            if (happened)
            {
                foreach (var s in new[] { a, b })
                {
                    Motive(s, VMMotive.Fun, 35);
                    Motive(s, VMMotive.Social, 30);
                    Motive(s, VMMotive.Comfort, 10);
                    Motive(s, VMMotive.Energy, -8);
                    if (kind == ReduxWooHoo.Place.Bed) Motive(s, VMMotive.Hygiene, -10);
                    if (kind == ReduxWooHoo.Place.Shower) Motive(s, VMMotive.Hygiene, 30);
                }
                Closer(a, b);
                Closer(b, a);
                // the baby: the woman of a man and a woman (the lot's game said 0 when there can't be one)
                var roll = (int)vm.Context.NextRandom(100);
                if (ReduxWooHoo.Female(a) != ReduxWooHoo.Female(b) && roll < chance)
                    carrier = ReduxWooHoo.Female(a) ? a : b;
            }
            try { Finished?.Invoke(vm, this, a, b, happened, carrier); } catch (Exception e) { Console.WriteLine("[SimsRedux] woohoo: " + e.Message); }
            return true;
        }

        private static void Motive(VMAvatar s, VMMotive m, int by)
        {
            var v = s.GetMotiveData(m) + by;
            s.SetMotiveData(m, (short)Math.Max(-100, Math.Min(100, v)));
        }

        /// <summary>A little closer: daily +10, lifetime +4 (the game's own relationship numbers, -100 to 100).</summary>
        private static void Closer(VMAvatar from, VMAvatar to)
        {
            var r = ReduxWooHoo.Rel(from, to);
            if (r == null) return;
            while (r.Count < 3) r.Add(0);
            r[0] = (short)Math.Min(100, r[0] + 10);
            r[2] = (short)Math.Min(100, r[2] + 4);
        }

        private bool Birth(VM vm)
        {
            var mother = vm.GetObjectById(A) as VMAvatar;
            var fam = vm.TS1State?.CurrentFamily;
            string why = null;
            if (mother == null || mother.Dead || fam == null) why = "";
            else if (!(fam.FamilyGUIDs ?? new uint[0]).Contains(mother.Object.OBJ.GUID)) why = "";
            else if ((fam.FamilyGUIDs?.Length ?? 0) + ReduxWooHoo.BabiesOnLot(vm) >= ReduxWooHoo.MaxFamily)
                why = "There's no room for the baby yet - a family can have 8 Sims and pets at most.";
            if (why != null) return NotBorn(vm, mother, why);

            var group = vm.Context.CreateObjectInstance(ReduxWooHoo.BabyGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
            var baby = group?.BaseObject;
            if (baby == null) return NotBorn(vm, mother, "");
            VMFindLocationFor.FindLocationFor(baby, mother, vm.Context, VMPlaceRequestFlags.Default);
            if (baby.Position == LotTilePos.OUT_OF_WORLD)
            {
                group.Delete(vm.Context);
                return NotBorn(vm, mother, "There was no room by the mother for the bassinet - it will come when there is.");
            }
            // the baby's DNA from the parents, the way the game's own "have baby" does it (the father, if he's here)
            var part = group.Objects.FirstOrDefault(x => x?.Object?.OBJ?.GUID == ReduxWooHoo.BabyPartGUID) ?? baby;
            var father = vm.GetObjectById(B) as VMAvatar;
            if (father == null || father.Dead || father.IsPet) father = mother;
            try
            {
                if (part.TreeByName != null && part.TreeByName.TryGetValue("create DNA", out var tree))
                {
                    var routine = tree.bhav as VMRoutine;
                    if (routine != null)
                        mother.Thread.RunInMyStack(routine, part.Object, new short[] { mother.ObjectID, father.ObjectID, 0, 0 }, part);
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] woohoo: the baby's DNA: " + e.Message); }
            try { Born?.Invoke(vm, this, mother, null); } catch { }
            return true;
        }

        private bool NotBorn(VM vm, VMAvatar mother, string why)
        {
            try { Born?.Invoke(vm, this, mother, why ?? ""); } catch { }
            return false;
        }

        private bool Buy(VM vm)
        {
            var who = vm.GetObjectById(A) as VMAvatar;
            var fam = vm.TS1State?.CurrentFamily;
            var at = vm.GetObjectById(Place);
            if (who == null || fam == null || at == null || Item > 1 || Count < 1 || Price < 0) return NotBought(vm, "");
            if (!(fam.FamilyGUIDs ?? new uint[0]).Contains(who.Object.OBJ.GUID) || !ReduxWooHoo.Grownup(who)) return NotBought(vm, "");
            if (fam.Budget < Price) return NotBought(vm, "Your family doesn't have §" + Price.ToString("N0") + ".");
            fam.Budget -= Price;
            // the Sim goes over: the computer's own "Surf the Web" when it has one, else a moment at the shelf or cabinet
            bool pushed = false;
            if (Source == 2)
            {
                TTABInteraction ia;
                var piece = ReduxWooHoo.FindInteraction(at, "Surf the Web", out ia);
                if (piece != null)
                {
                    var action = piece.GetAction((int)ia.TTAIndex, who, vm.Context, false, new short[4]);
                    if (action != null) { who.Thread.EnqueueAction(action); pushed = true; }
                }
            }
            if (!pushed) ReduxWooHoo.PushWait(vm, who, at, Math.Max(1, vm.Context.Clock.TicksPerMinute) * 3, "Shopping");
            try { Bought?.Invoke(vm, this, null); } catch { }
            return true;
        }

        private bool NotBought(VM vm, string why)
        {
            try { Bought?.Invoke(vm, this, why ?? ""); } catch { }
            return false;
        }

        // ------------------------------------------------------------------

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!FromNet) return true; // (this game runs the lot)
            // a guest only asks for, or answers for, their own Sim
            if (caller == null) return false;
            if (Mode == WooMode.Ask) return A == caller.ObjectID;
            if (Mode == WooMode.Answer) return B == caller.ObjectID;
            return false;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(A);
            writer.Write(B);
            writer.Write(Place);
            writer.Write(Kind);
            writer.Write(Protected);
            writer.Write(Yes);
            writer.Write(On);
            writer.Write(Chance);
            writer.Write(APill);
            writer.Write(BPill);
            writer.Write(AFert);
            writer.Write(BFert);
            writer.Write(Item);
            writer.Write(Source);
            writer.Write(Count);
            writer.Write(Price);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (WooMode)reader.ReadByte();
            A = reader.ReadInt16();
            B = reader.ReadInt16();
            Place = reader.ReadInt16();
            Kind = reader.ReadByte();
            Protected = reader.ReadBoolean();
            Yes = reader.ReadBoolean();
            On = reader.ReadBoolean();
            Chance = reader.ReadByte();
            APill = reader.ReadBoolean();
            BPill = reader.ReadBoolean();
            AFert = reader.ReadByte();
            BFert = reader.ReadByte();
            Item = reader.ReadByte();
            Source = reader.ReadByte();
            Count = reader.ReadInt16();
            Price = reader.ReadInt32();
        }
    }
}
