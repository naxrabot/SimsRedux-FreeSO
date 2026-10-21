using System.Collections.Generic;

namespace FSO.Content.TS1
{
    /// <summary>
    /// SimsRedux: items made on the server from pictures (the panel's Item Editor > New item from picture). The game
    /// downloads their files (Simitone.Client's ReduxCustomItems - they load from Content.ReduxCustomDir) and gets two
    /// lists of GUIDs with them:
    ///   locked  - once placed, only an Admin can move, sell or delete one (VMEntity.IsUserMovable asks LocksOut);
    ///             everybody else still sees it on the house. Whether this game's player is an Admin is the Item
    ///             Editor's own answer (ReduxCatalogOverrides.ShowDev, from GET /api/catalog).
    ///   details - wall details: they share their wall with a painting (not "exclusive"), but still hide with the wall
    ///             when walls are cut away (VMEntity.SetWallUse).
    /// Single player only for now: a town lot worker never has these lists, so online nothing is locked.
    /// </summary>
    public static class ReduxCustomObjects
    {
        private static readonly object Lock = new object();
        private static HashSet<uint> Locked = new HashSet<uint>();
        private static HashSet<uint> Details = new HashSet<uint>();

        /// <summary>The lists from the server (or the copy kept from last time). Any thread.</summary>
        public static void Set(IEnumerable<uint> locked, IEnumerable<uint> details)
        {
            var l = locked == null ? new HashSet<uint>() : new HashSet<uint>(locked);
            var d = details == null ? new HashSet<uint>() : new HashSet<uint>(details);
            lock (Lock) { Locked = l; Details = d; }
        }

        public static bool IsLocked(uint guid) { lock (Lock) return Locked.Count > 0 && Locked.Contains(guid); }
        public static bool IsDetail(uint guid) { lock (Lock) return Details.Count > 0 && Details.Contains(guid); }

        /// <summary>A locked item, and this game's player isn't an Admin: it can't be moved, sold or deleted.</summary>
        public static bool LocksOut(uint guid)
        {
            return IsLocked(guid) && !ReduxCatalogOverrides.ShowDev;
        }
    }
}
