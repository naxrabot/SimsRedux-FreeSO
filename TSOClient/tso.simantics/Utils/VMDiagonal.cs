using FSO.LotView.Model;

namespace FSO.SimAntics
{
    /// <summary>
    /// SimsRedux: helpers for objects placed diagonally (only possible in 3D, with Alt).
    /// </summary>
    public static class VMDiagonal
    {
        public static bool IsDiagonal(Direction dir)
        {
            return dir == Direction.NORTHEAST || dir == Direction.SOUTHEAST || dir == Direction.SOUTHWEST || dir == Direction.NORTHWEST;
        }

        /// <summary>For a diagonal direction, gives the two straight directions either side of it.</summary>
        public static bool SplitDiagonal(Direction dir, out Direction a, out Direction b)
        {
            switch (dir)
            {
                case Direction.NORTHEAST: a = Direction.NORTH; b = Direction.EAST; return true;
                case Direction.SOUTHEAST: a = Direction.EAST; b = Direction.SOUTH; return true;
                case Direction.SOUTHWEST: a = Direction.SOUTH; b = Direction.WEST; return true;
                case Direction.NORTHWEST: a = Direction.WEST; b = Direction.NORTH; return true;
                default: a = dir; b = dir; return false;
            }
        }
    }
}
