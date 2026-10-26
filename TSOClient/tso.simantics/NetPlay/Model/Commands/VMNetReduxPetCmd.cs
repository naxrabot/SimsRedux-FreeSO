using System.Collections.Generic;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;
using System;
using System.IO;
using System.Linq;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.0 (Part A #3, #3b): pets. Everything that changes the lot or the family for a pet goes through this
    /// command, so on a visit every player's game does the same thing at the same moment. Only the game running the lot
    /// sends it (single player, or the host of a visit) - a guest's is refused (Verify), so guests never adopt into the
    /// host's family or spend the host's money.
    ///   StrayArrive - a stray cat or dog (a neighbour of the special family number 4000, as in the original game) comes
    ///                 onto the lot as a visitor, by object AnchorID (a portal or the mailbox).
    ///   Adopt       - stray ObjectID joins the lot's family (8 at most) and stays: saved with the lot from now on.
    ///   PhoneAdopt  - the phone's "Adopt a Pet": a new cat or dog (Coat, Female, Name) is made, joins the family, the
    ///                 adoption fee comes out of the family's money and the pet turns up by the mailbox.
    ///   PetsHome    - at home: the family's pets that aren't on the lot (adopted or bought while away) turn up by the
    ///                 mailbox, and a family pet still marked as a visitor (a stray adopted by the game's own scripts) is
    ///                 made one of the family (saved with the lot).
    /// Nothing here changes a save format: pets are neighbours and family members exactly as the game's own pet shop
    /// makes them.
    /// </summary>
    public class VMNetReduxPetCmd : VMNetCommandBodyAbstract
    {
        public enum PetMode : byte
        {
            StrayArrive = 0,
            Adopt = 1,
            PhoneAdopt = 2,
            PetsHome = 3,
        }

        /// <summary>The family number the original game gives stray cats and dogs (Global tuning "Special Family Numbers", 1).</summary>
        public const short StrayFamily = 4000;
        /// <summary>The most Sims (pets included) a family can have, as in the game.</summary>
        public const int MaxFamily = 8;
        /// <summary>The phone's adoption fee: what a pet shop pen asks for a pet (its tuning, 399).</summary>
        public const int AdoptionFee = 399;
        /// <summary>The game's own pet templates (Unleashed).</summary>
        public const uint DogTemplate = 0x4A70DF92, CatTemplate = 0x7BEA0977;
        private static readonly uint[] MailboxGUIDs = { 0xEF121974, 0x1D95C9B0 };

        /// <summary>
        /// A pet joined the family (on every game, when the command runs): the lot, who did it, the pet's name and
        /// "cat"/"dog". For the "joined the family" message - shown only when it really happened.
        /// </summary>
        public static event Action<VM, uint, string, string> Adopted;
        /// <summary>A pet couldn't join (family full, not enough money...): the lot, who asked, and why (plain words).</summary>
        public static event Action<VM, uint, string> Refused;

        public PetMode Mode;
        public uint GUID;
        public short AnchorID;
        public short ObjectID;
        public bool Dog;
        public bool Female;
        public string Coat = "";
        public string Name = "";

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            try
            {
                switch (Mode)
                {
                    case PetMode.StrayArrive: return StrayArrive(vm);
                    case PetMode.Adopt: return AdoptStray(vm);
                    case PetMode.PhoneAdopt: return PhoneAdopt(vm);
                    case PetMode.PetsHome: return PetsHome(vm);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] pet command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private void Refuse(VM vm, string why)
        {
            try { Refused?.Invoke(vm, ActorUID, why); } catch { }
        }

        // ------------------------------------------------------------------

        private bool StrayArrive(VM vm)
        {
            if (vm.Context.ObjectQueries.Avatars.Any(x => x.Object?.OBJ?.GUID == GUID)) return false; // already here
            var neigh = Content.Content.Get().Neighborhood;
            var n = neigh.Neighbors?.Entries?.FirstOrDefault(x => x != null && x.GUID == GUID);
            if (n?.PersonData == null || n.PersonData.Length <= (int)VMPersonDataVariable.TS1FamilyNumber
                || n.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber] != StrayFamily) return false; // (only a stray)
            var ava = vm.Context.CreateObjectInstance(GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
            if (ava == null) return false;
            if (!ava.IsPet)
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            // a stray, the way the game's own scripts see one: a visitor (person type 1) nobody has greeted (0) - it keeps
            // to the outdoors (VMFindBestAction)
            ava.SetPersonData(VMPersonDataVariable.PersonType, 1);
            ava.SetPersonData(VMPersonDataVariable.GreetStatus, 0);
            foreach (var m in new[] { VMMotive.Energy, VMMotive.Comfort, VMMotive.Hunger, VMMotive.Hygiene, VMMotive.Bladder, VMMotive.Fun, VMMotive.Social })
                ava.SetMotiveData(m, (short)(40 + (int)vm.Context.NextRandom(40)));
            // passing through: never saved into the lot (until adopted)
            ava.SetValue(VMStackObjectVariable.MovementFlags,
                (short)(ava.GetValue(VMStackObjectVariable.MovementFlags) | (short)VMMovementFlags.FSOExcludeUserSave));
            var anchor = vm.GetObjectById(AnchorID);
            if (anchor != null && !anchor.Dead) VMFindLocationFor.FindLocationFor(ava, anchor, vm.Context, VMPlaceRequestFlags.Default);
            if (ava.Position == LotTilePos.OUT_OF_WORLD)
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            vm.Scheduler.RescheduleInterrupt(ava);
            return true;
        }

        private bool AdoptStray(VM vm)
        {
            var fam = vm.TS1State?.CurrentFamily;
            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
            if (fam == null || ava == null || ava.Dead || !ava.IsPet) return false;
            if ((fam.FamilyGUIDs ?? new uint[0]).Contains(ava.Object.OBJ.GUID)) return false; // already ours
            var neigh = Content.Content.Get().Neighborhood;
            var n = neigh.GetNeighborByID(ava.GetPersonData(VMPersonDataVariable.NeighborId));
            if (n == null || n.GUID != ava.Object.OBJ.GUID) return false;
            short famNum = (n.PersonData != null && n.PersonData.Length > (int)VMPersonDataVariable.TS1FamilyNumber) ? n.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber] : (short)0;
            var oldFam = (famNum > 0 && famNum != StrayFamily) ? neigh.GetFamily((ushort)famNum) : null;
            if (oldFam != null && oldFam.ChunkID != 0) return false; // (someone else's pet - never taken)
            if ((fam.FamilyGUIDs?.Length ?? 0) >= MaxFamily)
            {
                Refuse(vm, "Your family is full - a family can have 8 Sims and pets at most.");
                return false;
            }
            JoinFamily(vm, fam, n, ava);
            try { Adopted?.Invoke(vm, ActorUID, ava.Name ?? "", ava.IsCat ? "cat" : "dog"); } catch { }
            return true;
        }

        private bool PhoneAdopt(VM vm)
        {
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null) return false;
            if ((fam.FamilyGUIDs?.Length ?? 0) >= MaxFamily)
            {
                Refuse(vm, "Your family is full - a family can have 8 Sims and pets at most.");
                return false;
            }
            if (fam.Budget < AdoptionFee)
            {
                Refuse(vm, "You need §" + AdoptionFee + " for the adoption fee.");
                return false;
            }
            var objects = Content.Content.Get().WorldObjects;
            var template = Dog ? DogTemplate : CatTemplate;
            if (objects.Get(template) == null) return false; // (no Unleashed pets)
            var coats = VMTS1PurchasableOutfitHelper.GetValidOutfits(null, (short)(Dog ? -1 : -2));
            if (coats == null || coats.Length == 0) return false;
            // the coat by its name (each game's list can be in a different order); one this game doesn't have: the first
            // in a fixed order, so every game on the lot still picks the same
            var coat = coats.FirstOrDefault(x => string.Equals(x.Item1, Coat, StringComparison.OrdinalIgnoreCase))?.Item1
                ?? coats.Select(x => x.Item1 ?? "").OrderBy(x => x, StringComparer.Ordinal).First();

            Func<int, int> next = (max) => (int)vm.Context.NextRandom((ulong)Math.Max(0, max));
            var name = CleanName(Name);
            if (name.Length == 0) name = Dog ? "Buddy" : "Mittens";
            var info = new SimTemplateCreateInfo(Dog ? "dog" : "kat", Female);
            info.CustomGUID = template;
            info.BodyStringReplace[1] = coat;
            info.Name = name;
            info.Bio = "";
            info.FamilyID = (short)fam.ChunkID;
            for (int i = 0; i < 5; i++) info.PersonalityPoints[i] = 500;
            var guid = SimitoneNeighbourGenerator.GenerateGUID(fam.FamilyGUIDs, next);
            var n = SimitoneNeighbourGenerator.CreateNeighbor(guid, info, next);
            if (n == null) return false;
            MarkPet(n, Dog, Female);
            fam.Budget -= AdoptionFee;

            fam.FamilyGUIDs = (fam.FamilyGUIDs ?? new uint[0]).Concat(new[] { guid }).ToArray();
            if (!(fam.RuntimeSubset ?? new uint[0]).Contains(guid))
                fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Concat(new[] { guid }).ToArray();
            // (no room by the door right now: they turn up the next time the family is home - PetsHome)
            SpawnAtDoor(vm, fam, guid);
            try { Adopted?.Invoke(vm, ActorUID, name, Dog ? "dog" : "cat"); } catch { }
            return true;
        }

        private bool PetsHome(VM vm)
        {
            var fam = vm.TS1State?.CurrentFamily;
            if (fam?.FamilyGUIDs == null) return false;
            var neigh = Content.Content.Get().Neighborhood;
            bool any = false;
            foreach (var guid in fam.FamilyGUIDs)
            {
                var onLot = vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>().FirstOrDefault(x => !x.Dead && x.Object?.OBJ?.GUID == guid);
                if (onLot != null)
                {
                    // (home again after a trip of one: the pets are part of the family playing here - their faces show)
                    if (onLot.IsPet && !(fam.RuntimeSubset ?? new uint[0]).Contains(guid))
                    {
                        fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Concat(new[] { guid }).ToArray();
                        any = true;
                    }
                    // (a pet the game's own scripts adopted: still marked as a visitor passing through - it's family now)
                    if (onLot.IsPet && ((onLot.GetValue(VMStackObjectVariable.MovementFlags) & (short)VMMovementFlags.FSOExcludeUserSave) != 0
                        || onLot.GetPersonData(VMPersonDataVariable.PersonType) != 0))
                    {
                        var n0 = neigh.GetNeighborByID(onLot.GetPersonData(VMPersonDataVariable.NeighborId));
                        if (n0 != null) JoinFamily(vm, fam, n0, onLot);
                        any = true;
                    }
                    continue;
                }
                var n = neigh.Neighbors?.Entries?.FirstOrDefault(x => x != null && x.GUID == guid);
                if (n == null || !IsPetNeighbour(n)) continue; // (only pets - the family's Sims come and go as the game says)
                if (!(fam.RuntimeSubset ?? new uint[0]).Contains(guid))
                    fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Concat(new[] { guid }).ToArray();
                if (SpawnAtDoor(vm, fam, guid) != null) any = true;
            }
            return any;
        }

        // ------------------------------------------------------------------

        /// <summary>The neighbour (and the pet on the lot, if any) become one of the family: saved with the lot from now on.</summary>
        private static void JoinFamily(VM vm, FAMI fam, Neighbour n, VMAvatar ava)
        {
            if (n.PersonData != null && n.PersonData.Length > (int)VMPersonDataVariable.TS1FamilyNumber)
                n.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber] = (short)fam.ChunkID;
            if (!(fam.FamilyGUIDs ?? new uint[0]).Contains(n.GUID))
                fam.FamilyGUIDs = (fam.FamilyGUIDs ?? new uint[0]).Concat(new[] { n.GUID }).ToArray();
            if (!(fam.RuntimeSubset ?? new uint[0]).Contains(n.GUID))
                fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Concat(new[] { n.GUID }).ToArray();
            if (ava == null) return;
            ava.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, (short)fam.ChunkID);
            ava.SetPersonData(VMPersonDataVariable.PersonType, 0);
            ava.SetPersonData(VMPersonDataVariable.GreetStatus, 1);
            ava.SetValue(VMStackObjectVariable.MovementFlags,
                (short)(ava.GetValue(VMStackObjectVariable.MovementFlags) & ~(short)VMMovementFlags.FSOExcludeUserSave));
            ava.AvatarState.Permissions = VMTSOAvatarPermissions.Owner;
            // (a stray already heading off: it stays now)
            if (ava.Thread?.Queue != null)
                foreach (var q in ava.Thread.Queue.Where(x => x.ActionRoutine?.ID == VMNetReduxTownieCmd.LeaveRoutine).ToList())
                    ava.Thread.CancelAction(q.UID);
        }

        /// <summary>Makes the family member GUID on the lot, by the mailbox (or by the Sim who asked), as VerifyFamily does.</summary>
        private VMAvatar SpawnAtDoor(VM vm, FAMI fam, uint guid)
        {
            var ava = vm.Context.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
            if (ava == null) return null;
            ava.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, (short)fam.ChunkID);
            ava.SetPersonData(VMPersonDataVariable.PersonType, 0);
            ava.AvatarState.Permissions = VMTSOAvatarPermissions.Owner;
            var mailbox = vm.Entities.FirstOrDefault(x => !x.Dead && x.Position != LotTilePos.OUT_OF_WORLD && MailboxGUIDs.Contains(x.Object.OBJ.GUID));
            VMEntity anchor = mailbox ?? (VMEntity)vm.GetAvatarByPersist(ActorUID);
            if (anchor != null) VMFindLocationFor.FindLocationFor(ava, anchor, vm.Context, VMPlaceRequestFlags.Default);
            if (ava.Position == LotTilePos.OUT_OF_WORLD)
            {
                // (nowhere to stand right now: they'll turn up the next time the family is home - PetsHome)
                ava.Delete(true, vm.Context);
                return null;
            }
            vm.Scheduler.RescheduleInterrupt(ava);
            return ava;
        }

        /// <summary>
        /// The game's "Create Purchased Pets Near Owner" away from home (a pet shop): the family's pets that aren't on the lot
        /// come, by "near" (the Sim who bought them) - only the pets, never the rest of the family. Runs inside the lot's own
        /// script, so it's the same on every game.
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VM, HashSet<uint>> Joined =
            new System.Runtime.CompilerServices.ConditionalWeakTable<VM, HashSet<uint>>();

        /// <summary>A pet just joined the family on this lot (VMGenericTS1Call.AddToFamily - inside the lot's own script).</summary>
        public static void JustJoined(VM vm, uint guid)
        {
            if (vm == null) return;
            lock (Joined) Joined.GetOrCreateValue(vm).Add(guid);
        }

        public static void ForgetJoined(VM vm)
        {
            if (vm == null) return;
            lock (Joined) { if (Joined.TryGetValue(vm, out var set)) set.Clear(); }
        }

        public static void BringPets(VM vm, FAMI fam, VMEntity near)
        {
            if (vm == null || fam?.FamilyGUIDs == null) return;
            // only the pets just bought here: the family's other pets are at home (bringing them would make a copy)
            HashSet<uint> just;
            lock (Joined)
            {
                if (!Joined.TryGetValue(vm, out var set) || set.Count == 0) return;
                just = new HashSet<uint>(set);
                set.Clear();
            }
            var neigh = Content.Content.Get().Neighborhood;
            foreach (var guid in fam.FamilyGUIDs)
            {
                if (!just.Contains(guid)) continue;
                if (vm.Context.ObjectQueries.Avatars.Any(x => x.Object?.OBJ?.GUID == guid)) continue;
                var n = neigh.Neighbors?.Entries?.FirstOrDefault(x => x != null && x.GUID == guid);
                if (n == null || !IsPetNeighbour(n)) continue;
                if (!(fam.RuntimeSubset ?? new uint[0]).Contains(guid))
                    fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Concat(new[] { guid }).ToArray();
                var ava = vm.Context.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
                if (ava == null) continue;
                ava.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, (short)fam.ChunkID);
                ava.SetPersonData(VMPersonDataVariable.PersonType, 0);
                ava.AvatarState.Permissions = VMTSOAvatarPermissions.Owner;
                VMEntity anchor = near ?? vm.Entities.FirstOrDefault(x => !x.Dead && x.Position != LotTilePos.OUT_OF_WORLD && MailboxGUIDs.Contains(x.Object.OBJ.GUID));
                if (anchor != null) VMFindLocationFor.FindLocationFor(ava, anchor, vm.Context, VMPlaceRequestFlags.Default);
                if (ava.Position == LotTilePos.OUT_OF_WORLD)
                {
                    ava.Delete(true, vm.Context); // (they'll be at home - PetsHome)
                    continue;
                }
                vm.Scheduler.RescheduleInterrupt(ava);
            }
        }

        /// <summary>A pet's neighbour record says so: the pet flags (dog 8, cat 16) the game keeps in its gender.</summary>
        public static void MarkPet(Neighbour n, bool dog, bool female)
        {
            var i = (int)VMPersonDataVariable.Gender;
            if (n?.PersonData != null && n.PersonData.Length > i)
                n.PersonData[i] = (short)((dog ? 8 : 16) | (female ? 1 : 0));
        }

        /// <summary>Is this neighbour a cat or a dog (its pet flags, or the game's pet template it was made from)?</summary>
        public static bool IsPetNeighbour(Neighbour n)
        {
            if (n == null) return false;
            var i = (int)VMPersonDataVariable.Gender;
            if (n.PersonData != null && n.PersonData.Length > i && (n.PersonData[i] & (8 | 16)) != 0) return true;
            try
            {
                var obj = Content.Content.Get().WorldObjects.Get(n.GUID);
                var body = obj?.Resource?.Get<STR>(obj.OBJ.BodyStringID)?.GetString(0) ?? "";
                return body == "dog" || body == "kat" || body == "cat";
            }
            catch { return false; }
        }

        /// <summary>A pet's name as the player typed it: plain text, one line, 32 letters at most.</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var chars = name.Where(c => !char.IsControl(c)).ToArray();
            var clean = new string(chars).Trim();
            if (clean.Length > 32) clean = clean.Substring(0, 32).Trim();
            return clean;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // only the lot's own game sends these (single player, or the visit's host) - never a guest
            return !FromNet;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(GUID);
            writer.Write(AnchorID);
            writer.Write(ObjectID);
            writer.Write(Dog);
            writer.Write(Female);
            writer.Write(Coat ?? "");
            writer.Write(Name ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (PetMode)reader.ReadByte();
            GUID = reader.ReadUInt32();
            AnchorID = reader.ReadInt16();
            ObjectID = reader.ReadInt16();
            Dog = reader.ReadBoolean();
            Female = reader.ReadBoolean();
            Coat = reader.ReadString();
            Name = reader.ReadString();
            if (Coat.Length > 256) Coat = Coat.Substring(0, 256);
            if (Name.Length > 64) Name = Name.Substring(0, 64);
        }
    }
}
