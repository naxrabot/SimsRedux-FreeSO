using System;
using System.Collections.Generic;
using FSO.Content.Interfaces;

namespace FSO.Content.TS1
{
    /// <summary>
    /// SimsRedux: the server's Item Editor (the panel's Tools > Item Editor) - changes to Buy and Build items made on the
    /// server, applied to the catalog without touching any game file. Every game gets them when it starts (GET /api/catalog
    /// - Simitone.Client's ReduxCatalogSync), a town lot from the server's own file (-catalog...).
    ///
    ///   name / price / description: shown and used instead of the item's own (the price is what buying costs and what
    ///       a placed one's value starts from - on an online lot the lot works it out, so every player pays the same)
    ///   dev:     a developer item - only Admin accounts see it in Buy and Build mode and search, and buy it
    ///   deleted: nobody sees or buys it any more (Admins neither); placed ones stay and can still be sold
    ///
    /// A hidden item stays a catalog item (GetItemByGUID) like the water rugs (ObjectCatalogItem.ReduxHidden): placed
    /// ones keep their price, info and sell value; it's only taken out of the category lists, so Buy and Build mode,
    /// search and the dropper never offer it. VMNetBuyObjectCmd.Verify refuses buying it (Refuses).
    ///
    /// The catalog as loaded is kept (Attach), so changes can be applied again and again (Set) - each time from the
    /// original items. Set is called on the game thread once the game is running (the lists are the catalog's own).
    /// </summary>
    public static class ReduxCatalogOverrides
    {
        public class Entry
        {
            public string Name, Desc;
            /// <summary>-1: the item's own price.</summary>
            public int Price = -1;
            public bool Dev, Deleted;
        }

        private static readonly object Lock = new object();
        private static Dictionary<uint, Entry> Items = new Dictionary<uint, Entry>();
        private static List<ObjectCatalogItem>[] ByCategory;
        private static Dictionary<uint, ObjectCatalogItem> ByGuid;
        private static List<ObjectCatalogItem>[] OriginalLists;
        private static Dictionary<uint, ObjectCatalogItem> OriginalItems;

        /// <summary>The changes' version on the server (0 = none). Part of what a lot and its players compare.</summary>
        public static int Version { get; private set; }

        /// <summary>This game's player is an Admin: developer items are shown (and may be bought here).</summary>
        public static bool ShowDev { get; private set; }

        /// <summary>"cat7" - the changes this game has (empty: none). A lot tells its players its own (ReduxOnline's Welcome).</summary>
        public static string Stamp { get { return Version > 0 ? "cat" + Version : ""; } }

        /// <summary>After the catalog changed (on whatever thread Set ran on): caches made from it are let go.</summary>
        public static event Action Changed;

        /// <summary>How many items are hidden right now (developer-only for this player, or deleted).</summary>
        public static int HiddenCount { get; private set; }

        /// <summary>Called by TS1ObjectProvider.Init once the catalog is loaded (and priced): keeps it as loaded, applies the changes.</summary>
        public static void Attach(List<ObjectCatalogItem>[] byCategory, Dictionary<uint, ObjectCatalogItem> byGuid)
        {
            lock (Lock)
            {
                ByCategory = byCategory;
                ByGuid = byGuid;
                OriginalLists = new List<ObjectCatalogItem>[byCategory.Length];
                for (int i = 0; i < byCategory.Length; i++) OriginalLists[i] = byCategory[i] == null ? null : new List<ObjectCatalogItem>(byCategory[i]);
                OriginalItems = new Dictionary<uint, ObjectCatalogItem>(byGuid);
                Apply();
            }
        }

        /// <summary>New changes (version, the items, whether this player is an Admin). Applied straight away if the catalog is loaded.</summary>
        public static void Set(int version, Dictionary<uint, Entry> items, bool showDev)
        {
            bool applied;
            lock (Lock)
            {
                Version = Math.Max(0, version);
                Items = items ?? new Dictionary<uint, Entry>();
                ShowDev = showDev;
                applied = Apply();
            }
            if (applied)
            {
                try { Changed?.Invoke(); }
                catch (Exception e) { Console.WriteLine("[SimsRedux] item changes: " + e.Message); }
            }
        }

        /// <summary>Is this item deleted (for everyone)?</summary>
        public static bool IsDeleted(uint guid)
        {
            lock (Lock) { Entry e; return Items.TryGetValue(guid, out e) && e.Deleted; }
        }

        /// <summary>Is this a developer item (Admin accounts only)?</summary>
        public static bool IsDev(uint guid)
        {
            lock (Lock) { Entry e; return Items.TryGetValue(guid, out e) && e.Dev; }
        }

        /// <summary>May this not be bought? Deleted: never. A developer item: only by an Admin.</summary>
        public static bool Refuses(uint guid, bool admin)
        {
            lock (Lock)
            {
                Entry e;
                if (!Items.TryGetValue(guid, out e)) return false;
                return e.Deleted || (e.Dev && !admin);
            }
        }

        /// <summary>The catalog as loaded, with the changes on top (the lists keep their order). False if it isn't loaded yet.</summary>
        private static bool Apply()
        {
            if (ByCategory == null || ByGuid == null || OriginalLists == null || OriginalItems == null) return false;
            int hidden = 0;
            var hiddenNow = new HashSet<uint>();
            foreach (var kv in OriginalItems)
            {
                var item = kv.Value;
                Entry e;
                if (Items.TryGetValue(kv.Key, out e))
                {
                    item = WithChanges(item, e);
                    if (e.Deleted || (e.Dev && !ShowDev)) { item.ReduxHidden = true; hiddenNow.Add(kv.Key); hidden++; }
                }
                ByGuid[kv.Key] = item;
            }
            for (int c = 0; c < ByCategory.Length && c < OriginalLists.Length; c++)
            {
                var list = ByCategory[c];
                var orig = OriginalLists[c];
                if (list == null || orig == null) continue;
                list.Clear();
                foreach (var item in orig)
                {
                    if (hiddenNow.Contains(item.GUID)) continue;
                    Entry e;
                    list.Add(Items.TryGetValue(item.GUID, out e) ? WithChanges(item, e) : item);
                }
            }
            HiddenCount = hidden;
            return true;
        }

        private static ObjectCatalogItem WithChanges(ObjectCatalogItem item, Entry e)
        {
            if (!string.IsNullOrEmpty(e.Name)) item.CatalogName = e.Name;
            if (e.Desc != null) item.ReduxDescription = e.Desc;
            if (e.Price >= 0) { item.Price = (uint)e.Price; item.ReduxPriceGuessed = false; }
            return item;
        }
    }
}
