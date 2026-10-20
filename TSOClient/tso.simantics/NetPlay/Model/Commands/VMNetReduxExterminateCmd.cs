using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: Call Exterminator (click the bugs). Every bug on the lot goes, for a small fee from the
    /// family's funds (free when there's no family money on this lot). Same on every player's game.
    /// </summary>
    public class VMNetReduxExterminateCmd : VMNetCommandBodyAbstract
    {
        /// <summary>
        /// The exterminator really came (on every game, when the command runs): the lot, who called him and how
        /// many bugs went. For the "got rid of the bugs" message - only shown when it actually happened.
        /// </summary>
        public static event Action<VM, uint, int> Exterminated;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            var bugs = ReduxBugs.All(vm);
            if (bugs.Count == 0) return false;
            var fam = vm.TS1State?.CurrentFamily;
            if (fam != null)
            {
                if (fam.Budget < ReduxBugs.ExterminatorPrice) return false;
                fam.Budget -= ReduxBugs.ExterminatorPrice;
            }
            int removed = 0;
            foreach (var b in bugs)
            {
                if (b.Dead) continue;
                b.Delete(true, vm.Context);
                removed++;
            }
            try { Exterminated?.Invoke(vm, ActorUID, removed); } catch { }
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            // online: only the family living on the lot (at home) spends the family's money on the exterminator
            if (FromNet && !vm.ReduxFamilyControl.ContainsKey(ActorUID)) return false;
            return true;
        }

        public override void SerializeInto(BinaryWriter writer) { base.SerializeInto(writer); }
        public override void Deserialize(BinaryReader reader) { base.Deserialize(reader); }
    }
}
