namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: the Skylight (Content/redux/mods/Downloads/redux/redux_skylight.iff) - a plain "dev light":
    /// a small light gray circle up by the ceiling, placed on any tile like a lamp. It lights the area around it softly and is always on;
    /// Sims walk straight through it; and while you play it isn't drawn at all (only in Buy and Build
    /// mode), so all you see is its light. The file has no scripts: everything it does is here.
    /// </summary>
    public static class ReduxSkylight
    {
        public const uint GUID = 0x7C3A5D21;
        /// <summary>How far its light reaches (the same units as the game's lamps, which use 160).</summary>
        public const int Size = 140;
        /// <summary>How strong (a lamp is 1): soft, so it barely shows.</summary>
        public const float Intensity = 0.45f;
        /// <summary>How much it adds to the room's brightness (and the Room need), like a dim lamp.</summary>
        public const short Contribution = 30;

        public static bool Is(VMEntity ent)
        {
            return ent?.Object?.OBJ != null && ent.Object.OBJ.GUID == GUID;
        }
    }
}
