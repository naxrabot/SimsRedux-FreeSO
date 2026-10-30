using FSO.LotView.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.2: which objects hang on a wall (paintings, posters, mirrors, wall lamps, wall phones, clocks,
    /// smoke alarms, medicine cabinets...), and which sides of their tile those walls are on. Drawing only: in Live
    /// mode a wall hanging is hidden while its wall is cut away or down (ObjectComponent.ReduxWallSides), so its
    /// back doesn't float over the room. Never used by the simulation.
    ///
    /// The Sims 1's own wall hangings need a wall and have "zero extent" (Sims walk past them) - they are not marked
    /// "hide for cutaway", which is why 19.0 (VMNetLiftObjectCmd.IsWallItem) missed them. Things that stand on the
    /// floor against a wall (toilets, fireplaces) need a wall too but do take up their tile, so they stay.
    /// </summary>
    public static class ReduxWallHanging
    {
        private const int HideForCutaway = 1 << 10, HasZeroExtent = 1 << 2,
            DisallowPersonIntersection = 1 << 1, AllowPersonIntersection = 1 << 4;
        private const int DiagonalRequired = 1 << 6;
        private const int ArchitectualWindow = 1 << 14, ArchitectualDoor = 1 << 15;

        /// <summary>Does an object with these Flags, FlagField2 and WallPlacementFlags hang on a wall?</summary>
        public static bool HangsOnWall(short flags, short flags2, int wallPlace)
        {
            if ((wallPlace & 15) == 0) return false; // (needs no wall)
            if ((wallPlace & DiagonalRequired) != 0) return false; // (diagonal walls: left as they were)
            if (((ushort)flags2 & (ArchitectualWindow | ArchitectualDoor)) != 0) return false; // (sits in the wall)
            if ((flags & (HideForCutaway | HasZeroExtent)) != 0) return true;
            return (flags & DisallowPersonIntersection) == 0 && (flags & AllowPersonIntersection) != 0; // (Sims walk under it)
        }

        /// <summary>
        /// The sides of its tile (WallSegments bits, lot directions) that an object with these WallPlacementFlags,
        /// facing `dir`, hangs on - as VMEntity.SetWallUse, but only the four "wall required" bits (SetWallUse's
        /// "% 15" mixes in other flags, e.g. 0x804 "wall behind, none on the left" became behind AND left).
        /// </summary>
        public static int Sides(int wallPlace, Direction dir)
        {
            int off;
            switch (dir)
            {
                case Direction.NORTH: off = 0; break;
                case Direction.EAST: off = 1; break;
                case Direction.SOUTH: off = 2; break;
                case Direction.WEST: off = 3; break;
                default: return 0; // (facing diagonally: not on a straight wall)
            }
            int rotate = (8 - (off + 1)) % 4;
            int ws = wallPlace & 15;
            return ((ws << (4 - rotate)) & 15) | (ws >> rotate);
        }
    }
}
