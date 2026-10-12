using System;
using System.IO;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetSimJoinCmd : VMNetCommandBodyAbstract
    {
        public ushort Version = CurVer;

        public override bool AcceptFromClient { get { return false; } }

        public VMNetAvatarPersistState AvatarState;

        public static ushort CurVer = 0xFFEE;

        //variables used locally for deferred avatar loading

        public override bool Execute(VM vm)
        {
            if (vm.TS1 && AvatarState.CustomGUID != 0)
            {
                // SimsRedux multiplayer: a friend visiting with their own Sim. The host has already
                // added the Sim's character file and neighbour data to everyone's game, so it can
                // be created like any other Sim. It appears next to the host's selected Sim.
                if (vm.GetAvatarByPersist(ActorUID) != null) return true;
                var group = vm.Context.CreateObjectInstance(AvatarState.CustomGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
                var visitor = group?.BaseObject as VMAvatar;
                if (visitor == null) return true;
                var near = vm.GetObjectById(vm.GetGlobalValue(3)) ?? vm.Entities.FirstOrDefault(x => x is VMAvatar && x != visitor && x.Position != LotTilePos.OUT_OF_WORLD);
                if (near != null) VMFindLocationFor.FindLocationFor(visitor, near, vm.Context, VMPlaceRequestFlags.Default);
                if (visitor.Position == LotTilePos.OUT_OF_WORLD) visitor.SetPosition(LotTilePos.FromBigTile(2, 2, 1), Direction.NORTH, vm.Context);
                visitor.PersistID = ActorUID;
                // treat the friend's Sim like one of the household, not a visiting neighbour
                // (visiting neighbours' own scripts make them walk off the lot and go home)
                visitor.SetPersonData(VMPersonDataVariable.PersonType, 0);
                if (vm.TS1State.CurrentFamily != null)
                    visitor.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, (short)vm.TS1State.CurrentFamily.ChunkID);
                // never saved into the host's house: the Sim belongs to the friend's own game
                visitor.SetValue(VMStackObjectVariable.MovementFlags,
                    (short)(visitor.GetValue(VMStackObjectVariable.MovementFlags) | (short)VMMovementFlags.FSOExcludeUserSave));
                vm.Context.ObjectQueries.RegisterAvatarPersist(visitor, ActorUID);
                vm.SignalChatEvent(new VMChatEvent(visitor, VMChatEventType.Join, visitor.Name));
                return true;
            }
            if (vm.TS1)
            {
                if (vm.TS1State.CurrentFamily == null) return true;
                var gameState = Content.Content.Get().Neighborhood.GameState;
                var control = vm.Entities.FirstOrDefault(x => x is VMAvatar && !((VMAvatar)x).IsPet && ((VMAvatar)x).GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == vm.TS1State.CurrentFamily?.ChunkID);
                // SimsRedux: on the family's own home lot, never bring in a stand-in Sim. A family
                // that's just moving in walks onto the lot a moment later, and the game then selects
                // one of them. (The stand-in is for Downtown and other trips, where the travelling
                // Sim has to be created.) Without this, the Sim who last travelled joined every
                // new family and dropped out again when you picked one of the real family members.
                bool homeLot = vm.GetGlobalValue(10) == vm.TS1State.CurrentFamily.HouseNumber;
                // and the stand-in must belong to the family being played
                var standIn = (gameState.DowntownSimGUID == 0) ? 0x32AA2056 : gameState.DowntownSimGUID;
                bool standInIsFamily = vm.TS1State.CurrentFamily.FamilyGUIDs?.Contains(standIn) == true;
                if (control == null && !homeLot && standInIsFamily)
                {
                    control = vm.Context.CreateObjectInstance(standIn, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject;
                    control?.SetPosition(LotTilePos.FromBigTile(1, 1, 1), Direction.NORTH, vm.Context);
                }
                if (control != null)
                {
                    var ava = (VMAvatar)control;
                    ava.PersistID = ActorUID;
                    ava.AvatarState.Permissions = VMTSOAvatarPermissions.Admin;
                    vm.Context.ObjectQueries.RegisterAvatarPersist(ava, ava.PersistID);
                    vm.SetGlobalValue(3, control.ObjectID);
                }
                return true;
            }
            
            var name = AvatarState.Name.Substring(0, Math.Min(AvatarState.Name.Length, 64));
            var guid = (AvatarState.CustomGUID == 0) ? VMAvatar.TEMPLATE_PERSON : AvatarState.CustomGUID;

            var sim = vm.Context.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, Direction.NORTH).Objects[0];
            var mailbox = vm.Entities.FirstOrDefault(x => (x.Object.OBJ.GUID == 0xEF121974 || x.Object.OBJ.GUID == 0x1D95C9B0));

            if (VM.UseWorld) FSO.HIT.HITVM.Get().PlaySoundEvent("lot_enter");
            if (mailbox != null) VMFindLocationFor.FindLocationFor(sim, mailbox, vm.Context, VMPlaceRequestFlags.Default);
            else sim.SetPosition(LotTilePos.FromBigTile(3, 3, 1), Direction.NORTH, vm.Context);
            sim.PersistID = ActorUID;

            if (vm.Tuning?.GetTuning("aprilfools", 0, 2019) == 1f)
            {
                var sum = AvatarState.Name.Sum(x => x);
                if (sum % 4 == 0) ((VMAvatar)sim).SetPersonData(VMPersonDataVariable.JobPerformance, 50);
                if (sum % 128 == 127) ((VMAvatar)sim).SetPersonData(VMPersonDataVariable.JobPerformance, 2);
            }

            VMAvatar avatar = (VMAvatar)sim;

            if (vm.TSOState.CommunityLot && AvatarState.Permissions < VMTSOAvatarPermissions.Owner)
            {
                if (vm.TSOState.Roommates.Contains(AvatarState.PersistID))
                {
                    if (vm.TSOState.BuildRoommates.Contains(AvatarState.PersistID))
                    {
                        AvatarState.Permissions = VMTSOAvatarPermissions.BuildBuyRoommate;
                    }
                    else
                    {
                        AvatarState.Permissions = VMTSOAvatarPermissions.Roommate;
                    }
                }
            }

            AvatarState.Apply(avatar);

            var oldRoomCount = vm.TSOState.Roommates.Count;
            //some off lot changes may have occurred. Keep things up to date if we're caught between database sync points (TODO: right now never, but should happen on every roomie change).
            if (AvatarState.Permissions > VMTSOAvatarPermissions.Visitor && AvatarState.Permissions < VMTSOAvatarPermissions.Admin)
            {
                if (!vm.TSOState.Roommates.Contains(AvatarState.PersistID))
                {
                    vm.TSOState.Roommates.Add(AvatarState.PersistID);
                    if (AvatarState.Permissions > VMTSOAvatarPermissions.Roommate)
                        vm.TSOState.BuildRoommates.Add(AvatarState.PersistID);
                    else
                        vm.TSOState.BuildRoommates.Remove(AvatarState.PersistID);
                    VMBuildableAreaInfo.UpdateOverbudgetObjects(vm);
                }
            } else if (AvatarState.Permissions != VMTSOAvatarPermissions.Admin)
            {
                if (vm.TSOState.Roommates.Contains(AvatarState.PersistID))
                {
                    vm.TSOState.Roommates.Remove(AvatarState.PersistID);
                    vm.TSOState.BuildRoommates.Remove(AvatarState.PersistID);
                    VMBuildableAreaInfo.UpdateOverbudgetObjects(vm);
                }
            }

            if (oldRoomCount != vm.TSOState.Roommates.Count)
            {
                //mark objects not owned by roommates for inventory transfer
                foreach (var ent in vm.Entities)
                {
                    if (ent is VMGameObject && ent.PersistID > 0 && ((VMTSOObjectState)ent.TSOState).OwnerID == avatar.PersistID)
                    {
                        var old = ((VMGameObject)ent).Disabled;
                        if (AvatarState.Permissions < VMTSOAvatarPermissions.Roommate) ((VMGameObject)ent).Disabled |= VMGameObjectDisableFlags.PendingRoommateDeletion;
                        else ((VMGameObject)ent).Disabled &= ~VMGameObjectDisableFlags.PendingRoommateDeletion;
                        if (old != ((VMGameObject)ent).Disabled) vm.Scheduler.RescheduleInterrupt(ent);
                        ((VMGameObject)ent).RefreshLight();
                    }
                }
            }

            vm.Context.ObjectQueries.RegisterAvatarPersist(avatar, avatar.PersistID);
            if (ActorUID == uint.MaxValue - 1)
            {
                avatar.SetValue(VMStackObjectVariable.Hidden, 1);
                avatar.SetPosition(LotTilePos.OUT_OF_WORLD, Direction.NORTH, vm.Context);
                avatar.SetFlag(VMEntityFlags.HasZeroExtent, true);
                avatar.SetPersonData(VMPersonDataVariable.IsGhost, 1); //oooooOOooooOo
            }

            vm.SignalChatEvent(new VMChatEvent(avatar, VMChatEventType.Join, avatar.Name));

            var oow = vm.Context.ObjectQueries.GetObjectsAt(LotTilePos.OUT_OF_WORLD);
            if (oow != null)
            {
                foreach (var obj in oow)
                {
                    obj.ExecuteNamedEntryPoint("CT - FSO Player Joined", vm.Context, true, obj, new short[] { avatar.ObjectID, 0, 0, 0 });
                }
            }

            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return !FromNet; //can only be sent out by server
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Version);
            AvatarState.SerializeInto(writer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Version = reader.ReadUInt16();
            AvatarState = new VMNetAvatarPersistState();
            AvatarState.Deserialize(reader);
        }
        #endregion
    }
}
