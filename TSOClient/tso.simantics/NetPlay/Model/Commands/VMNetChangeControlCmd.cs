using System.Linq;
using System.IO;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetChangeControlCmd : VMNetCommandBodyAbstract
    {
        public short TargetID;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            //switch this client's controlled sim to another
            //only used for TS1.

            //not suitable for networking right now. If you want to do anything weird with ts1 multiplayer, needs these additions:
            // - block stealing sims controlled by others
            // - block assuming control of npcs (this is enforced by simitone ui)
            // - multiplayer handling of "selected sim" global

            // SimsRedux: the server's "everyone in the family comes home" (nobody of the owner's
            // family is on their lot): missing family members walk in at the mailbox, on every game at once
            if (TargetID == EveryoneHome)
            {
                if (!vm.TS1 || vm.TS1State.CurrentFamily == null) return false;
                vm.TS1State.CurrentFamily.SelectWholeFamily();
                vm.TS1State.VerifyFamily(vm);
                return true;
            }

            var target = vm.GetObjectById(TargetID);
            if (target == null || target is VMGameObject) return false;

            if (caller != null && caller.PersistID == ActorUID)
            {
                //relinquish previous control
                vm.Context.ObjectQueries.RemoveAvatarPersist(caller.PersistID);
                caller.PersistID = 0;
            }

            // SimsRedux: a player controls one Sim. Any other Sim still holding this player's id (the
            // lot's start-up gave it out twice, or the id lookup had lost track of the old Sim) lets go
            // too - two Sims with one id left the plumbob, face and needs on different Sims, or none.
            if (ActorUID != 0)
            {
                foreach (var other in vm.Context.ObjectQueries.Avatars)
                    if (other != target && other.PersistID == ActorUID) other.PersistID = 0;
                vm.Context.ObjectQueries.RemoveAvatarPersist(ActorUID);
            }

            // (a leftover id from an earlier visit is forgotten, so it can't point at this Sim any more)
            if (target.PersistID != 0 && target.PersistID != ActorUID) vm.Context.ObjectQueries.RemoveAvatarPersist(target.PersistID);
            target.PersistID = ActorUID;
            vm.Context.ObjectQueries.RegisterAvatarPersist((VMAvatar)target, target.PersistID);
            vm.SetGlobalValue(3, target.ObjectID);

            // (a Sim who is away - at work, say - isn't on the lot: there's nothing to look at)
            if (VM.UseWorld && target.Position != FSO.LotView.Model.LotTilePos.OUT_OF_WORLD)
            {
                vm.Context.World.CenterTo(target.WorldUI);
            }

            return true;
        }

        /// <summary>SimsRedux: TargetID meaning "the lot's whole family comes home" (only the server sends it).</summary>
        public const short EveryoneHome = -2;

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (TargetID == EveryoneHome) return vm.TS1 && !FromNet;
            // SimsRedux multiplayer: friends can't take over other Sims, and nobody can take a Sim
            // someone else is controlling.
            var target = vm.GetObjectById(TargetID);
            if (FromNet)
            {
                // the family's owner may switch between their own family's Sims (never anyone else's)
                if (!vm.ReduxFamilyControl.TryGetValue(ActorUID, out var family)) return false;
                var ava = target as VMAvatar;
                if (ava == null || ava.IsPet || ava.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) != family) return false;
                if ((ava.GetValue(VMStackObjectVariable.MovementFlags) & (short)VMMovementFlags.FSOExcludeUserSave) != 0) return false; // a visitor
            }
            // (online players get ids from 100001 up; family Sims can carry other leftover ids from
            //  the save, and those must stay selectable or the player can't pick their Sim at all)
            // SimsRedux online town: a Sim a family's owner is controlling right now is theirs - nothing
            // on the server (its own copy of the game included) may take it away from them
            if (!FromNet && target != null && target.PersistID != ActorUID && vm.ReduxFamilyControl.ContainsKey(target.PersistID)
                && !vm.ReduxFamilyControl.ContainsKey(ActorUID)) return false;
            // (the server itself may hand over a Sim still carrying the id of a player who has left)
            if (FromNet && target != null && target.PersistID >= 100001 && target.PersistID != ActorUID) return false;
            return vm.TS1;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(TargetID);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            TargetID = reader.ReadInt16();
        }

        #endregion
    }
}
