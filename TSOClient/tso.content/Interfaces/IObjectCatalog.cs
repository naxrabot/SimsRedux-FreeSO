using System.Collections.Generic;

namespace FSO.Content.Interfaces
{
    public interface IObjectCatalog
    {
        List<ObjectCatalogItem> All();
        List<ObjectCatalogItem> GetItemsByCategory(sbyte category);
        ObjectCatalogItem? GetItemByGUID(uint guid);
        List<uint> GetUntradableGUIDs();
    }

    public struct ObjectCatalogItem
    {
        public uint GUID;
        public sbyte Category;
        public uint Price;
        public string Name;
        public string CatalogName;
        public string Tags;
        public byte DisableLevel; //1 = only shopping, 2 = rare (unsellable?)

        public byte RoomSort;
        public byte Subsort;
        public byte DowntownSort;
        public byte VacationSort;
        public byte CommunitySort;
        public byte StudiotownSort;
        public byte MagictownSort;

        // SimsRedux (Sims 1 mode): worked out once while the objects load (TS1/ReduxCatalogInfo), so Buy and Build
        // mode can list, sort, group and search the catalog without opening a single object file. CatalogName above
        // is the catalog's own name for it (null: use Name).
        /// <summary>The catalog description ("" if none).</summary>
        public string ReduxDescription;
        /// <summary>It has a catalog picture, or one can be drawn from it (no picture: a hidden/test object).</summary>
        public bool ReduxPicture;
        /// <summary>Its shape (every sprite's size and place, facing you, medium zoom): recolours share it. 0 = unknown.</summary>
        public ulong ReduxShape;
        /// <summary>The object it's a recolour of (its OBJD's original GUID), 0 if none.</summary>
        public uint ReduxOriginal;
        /// <summary>Where it's from: 0 the game's own archives, 1 a separate file (Downloads, shipped mods, Creator), 2 The Sims Online.</summary>
        public byte ReduxSource;
        /// <summary>
        /// SimsRedux 14.0: kept out of Buy and Build mode (not in any category list, so not in search or the dropper
        /// either) - it's still a catalog item, so ones already on a lot keep their price, info and sell value.
        /// </summary>
        public bool ReduxHidden;
        /// <summary>
        /// SimsRedux 14.x: the object's own price was §0 (a download or The Sims Online's object that never set one), so
        /// Price is a guess from similar items (ReduxCatalogInfo.GuessPrice) - buying, selling and wear all use it.
        /// </summary>
        public bool ReduxPriceGuessed;
    }
}
