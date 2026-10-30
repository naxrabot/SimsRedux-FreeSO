using FSO.SimAntics.Model.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.Model.TS1Platform
{
    public class VMTS1LotState : VMAbstractLotState
    {
        public SIMI SimulationInfo;
        public FAMI CurrentFamily;

        public VMTS1LotState() : base() { }
        public VMTS1LotState(int version) : base(version) { }

        public void ActivateFamily(VM vm, FAMI family)
        {
            if (family == null) return;
            vm.SetGlobalValue(9, (short)family.ChunkID);
            CurrentFamily = family;
        }

        /// <summary>
        /// Ensure all members of the family are present on the lot.
        /// Spawns missing family members at the mailbox.
        /// </summary>
        public void VerifyFamily(VM vm)
        {
            if (CurrentFamily == null)
            {
                vm.SetGlobalValue(32, 1);
                return;
            }
            vm.SetGlobalValue(32, 0);
            vm.SetGlobalValue(9, (short)CurrentFamily.ChunkID);
            // SimsRedux: a Sim the lot is already removing (dead) isn't "here" - they're made again
            var present = new HashSet<uint>();
            foreach (var avatar in vm.Context.ObjectQueries.Avatars)
            {
                if (avatar == null || avatar.Dead || avatar.Object?.OBJ == null) continue;
                present.Add(avatar.Object.OBJ.GUID);
            }

            // SimsRedux 18.9: each member on their own, in the family's order. One Sim that couldn't be made (the
            // object came back null, or threw) used to end the whole loop for everyone after them - and since 11.8 a
            // failing command is skipped quietly (VMNetDriver), so the lot just opened without them (in a family made
            // in Create-A-Family that's the child, made last). Now that Sim is logged and the rest still come.
            var done = new HashSet<uint>();
            foreach (var member in CurrentFamily.RuntimeSubset ?? new uint[0])
            {
                if (member == 0 || !done.Add(member) || present.Contains(member)) continue;
                try
                {
                    if (!BringMember(vm, member))
                        Console.WriteLine("[SimsRedux] family: " + member.ToString("X8") + " couldn't be made on the lot (no character for them)");
                }
                catch (Exception e)
                {
                    Console.WriteLine("[SimsRedux] family: " + member.ToString("X8") + " couldn't be brought onto the lot: " + e);
                }
            }
        }

        /// <summary>
        /// SimsRedux: makes one family member on the lot, by the mailbox. False: nothing could be made for them.
        /// Lot state only (the lot's own random numbers are never used), so every game of a visit does the same.
        /// </summary>
        private bool BringMember(VM vm, uint member)
        {
            var group = vm.Context.CreateObjectInstance(member, LotView.Model.LotTilePos.OUT_OF_WORLD, LotView.Model.Direction.NORTH);
            var sim = group?.BaseObject as VMAvatar;
            if (sim == null)
            {
                group?.Delete(vm.Context);
                return false;
            }
            sim.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, (short)CurrentFamily.ChunkID);
            // (family, never a visitor: a Sim whose neighbour record has another family number came out as a visitor
            //  (person type 1, VMAvatar.InheritNeighbor), and a visitor's own scripts walk them off the lot and remove
            //  them. ReduxMatch and VMNetReduxPetCmd make family members the same way.)
            sim.SetPersonData(VMPersonDataVariable.PersonType, 0);
            var mailbox = vm.Entities.FirstOrDefault(x => !x.Dead && x.Object?.OBJ != null && (x.Object.OBJ.GUID == 0xEF121974 || x.Object.OBJ.GUID == 0x1D95C9B0));
            if (mailbox != null)
            {
                VMFindLocationFor.FindLocationFor(sim, mailbox, vm.Context, VMPlaceRequestFlags.Default);
                // (no room by the mailbox: next to a family member who's already here)
                if (sim.Position == LotView.Model.LotTilePos.OUT_OF_WORLD)
                {
                    var near = vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>()
                        .Where(x => x != sim && !x.Dead && x.Position != LotView.Model.LotTilePos.OUT_OF_WORLD
                            && x.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == CurrentFamily.ChunkID)
                        .OrderBy(x => x.ObjectID).FirstOrDefault();
                    if (near != null) VMFindLocationFor.FindLocationFor(sim, near, vm.Context, VMPlaceRequestFlags.Default);
                }
            }
            sim.AvatarState.Permissions = Model.TSOPlatform.VMTSOAvatarPermissions.Owner;

            vm.Scheduler.RescheduleInterrupt(sim);
            Console.WriteLine("[SimsRedux] family: " + (sim.Name ?? "") + " (" + member.ToString("X8") + ") came onto the lot"
                + (sim.Position == LotView.Model.LotTilePos.OUT_OF_WORLD ? " (not placed yet)" : ""));
            return true;
        }


        public override void Deserialize(BinaryReader reader)
        {
            if (reader.ReadBoolean())
            {
                SimulationInfo = new SIMI() { ChunkID = 1, ChunkLabel = "", ChunkType = "SIMI" };
                SimulationInfo.Read(null, reader.BaseStream);
            }

            //this is really only here for future networking. families should be activated (see abover) when joining lots for the first time
            var famID = reader.ReadUInt16(); 
            if (famID < 65535)
            {
                CurrentFamily = new FAMI() { ChunkID = famID, ChunkLabel = "", ChunkType = "FAMI" };
                CurrentFamily.Read(null, reader.BaseStream);
            }
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            writer.Write(SimulationInfo != null);
            SimulationInfo?.Write(null, writer.BaseStream);
            writer.Write(CurrentFamily?.ChunkID ?? 65535);
            if (CurrentFamily != null) CurrentFamily.Write(null, writer.BaseStream);
        }

        public override void Tick(VM vm, object owner)
        {
        }

        public void UpdateSIMI(VM vm)
        {
            if (SimulationInfo == null) return;

            var objValue = VMArchitectureStats.GetObjectValue(vm);
            SimulationInfo.ArchitectureValue = VMArchitectureStats.GetArchValue(vm.Context.Architecture) + objValue.Item2;
            SimulationInfo.ObjectsValue = objValue.Item1;
            SimulationInfo.Version = 0x3E;
            SimulationInfo.GlobalData = vm.GlobalState;
        }

        public override void ActivateValidator(VM vm)
        {
            Validator = new VMDefaultValidator(vm);
        }
    }
}
