using System;
using System.Collections.Generic;
using System.Linq;
using FSO.LotView.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: bugs on the floor (Sims 1's roaches, and the flies around trash). Sims 1 has no
    /// exterminator - roaches only went when a Sim stomped them or the maid cleaned - so click the bugs:
    /// Call Exterminator clears every bug on the lot for a small fee.
    /// </summary>
    public static class ReduxBugs
    {
        public const int ExterminatorPrice = 50;

        /// <summary>Is this object bugs?</summary>
        public static bool Is(VMEntity e)
        {
            if (!(e is VMGameObject)) return false;
            var label = e.Object?.OBJ?.ChunkLabel ?? "";
            return label.IndexOf("roach", StringComparison.OrdinalIgnoreCase) >= 0
                || label.Equals("Flies", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Bugs", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Every bug on the lot.</summary>
        public static List<VMEntity> All(VM vm)
        {
            return vm.Entities.Where(x => Is(x) && x.Position != LotTilePos.OUT_OF_WORLD).ToList();
        }
    }
}
