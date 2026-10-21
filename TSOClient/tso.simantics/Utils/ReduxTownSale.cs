using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 15.2 Events &amp; Sales: the Buy mode sale on now (the server's events - Tools > Events &amp; Sales), set by
    /// the game (Simitone.Client's ReduxRules) from the server's answer. Not part of any lot's state: it's only used
    /// where the lot has the town entry (VMReduxPerks.TownKey) - which only single player ever has - so online lots
    /// never sell at a sale price, whatever is set here. ReduxPerkRules.BuyPrice / ShownPrice / SaleOff read it, so
    /// the price on the catalog and the price paid are the same sum.
    /// </summary>
    public static class ReduxTownSale
    {
        private sealed class Table
        {
            public int All;
            public Dictionary<int, int> Cats = new Dictionary<int, int>();
            public Dictionary<uint, int> Items = new Dictionary<uint, int>();
        }

        // (swapped whole - a reader never sees half a table)
        private static volatile Table Current = new Table();

        /// <summary>A short text that changes whenever the sale does (so the game only sets it when it's new).</summary>
        public static string Stamp { get; private set; } = "";

        /// <summary>
        /// The sale: percent off everything (all), per Buy mode category (0-7), per item GUID. Percents are kept to
        /// 0-100; null / empty = no sale.
        /// </summary>
        public static void Set(int all, Dictionary<int, int> cats, Dictionary<uint, int> items, string stamp)
        {
            var t = new Table { All = Clamp(all) };
            if (cats != null) foreach (var kv in cats) { var p = Clamp(kv.Value); if (p > 0) t.Cats[kv.Key] = p; }
            if (items != null) foreach (var kv in items) { var p = Clamp(kv.Value); if (p > 0 && kv.Key != 0) t.Items[kv.Key] = p; }
            Current = t;
            Stamp = stamp ?? "";
        }

        public static void Clear() { Set(0, null, null, ""); }

        public static bool Any { get { var t = Current; return t.All > 0 || t.Cats.Count > 0 || t.Items.Count > 0; } }

        private static int Clamp(int p) { return Math.Max(0, Math.Min(100, p)); }

        /// <summary>The biggest percent off for this item: everything, its Buy mode category, or the item itself.</summary>
        public static int PercentOff(uint guid)
        {
            var t = Current;
            if (t.All <= 0 && t.Cats.Count == 0 && t.Items.Count == 0) return 0;
            int best = t.All;
            int p;
            if (t.Items.TryGetValue(guid, out p) && p > best) best = p;
            if (t.Cats.Count > 0)
            {
                try
                {
                    var item = Content.Content.Get()?.WorldCatalog?.GetItemByGUID(guid);
                    if (item != null && t.Cats.TryGetValue(item.Value.Category, out p) && p > best) best = p;
                }
                catch { }
            }
            return best;
        }
    }
}
