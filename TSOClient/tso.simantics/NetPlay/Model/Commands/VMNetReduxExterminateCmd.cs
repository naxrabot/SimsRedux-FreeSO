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
            foreach (var b in bugs)
            {
                if (b.Dead) continue;
                b.Delete(true, vm.Context);
            }
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return vm.TS1;
        }

        public override void SerializeInto(BinaryWriter writer) { base.SerializeInto(writer); }
        public override void Deserialize(BinaryReader reader) { base.Deserialize(reader); }
    }
}
