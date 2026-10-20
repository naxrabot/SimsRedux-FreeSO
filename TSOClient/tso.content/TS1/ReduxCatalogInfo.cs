using System;
using System.Collections.Generic;
using System.Text;
using FSO.Content.Interfaces;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;

namespace FSO.Content.TS1
{
    /// <summary>
    /// SimsRedux: what Buy and Build mode need to know about a catalog object, worked out while the objects load
    /// (each object file is open then anyway) and kept on its ObjectCatalogItem: its catalog name and description,
    /// whether it has a picture, its shape (for grouping colours) and the object it's a recolour of.
    ///
    /// Before, the catalog opened every object in a category to find these out the first time the category was
    /// shown (and every Buy object for the first search) - for The Sims Online's objects that meant reading their
    /// sprite archives too, megabytes at a time - which froze the game for seconds. Nothing here decodes a sprite:
    /// a frame's size is read from the sprite's raw bytes (SPR2.PeekFrameSize).
    /// </summary>
    public static class ReduxCatalogInfo
    {
        /// <summary>Fills in item's Redux fields from the object's file. `sprites`: a separate sprite file, if it has one.</summary>
        public static void Fill(ref ObjectCatalogItem item, IffFile iff, OBJD obj, List<OBJD> all, byte source, IffFile sprites = null)
        {
            item.ReduxSource = source;
            item.ReduxOriginal = ((uint)obj.OriginalGUID2 << 16) | obj.OriginalGUID1;
            string name = null, desc = "";
            try
            {
                // (straight from the raw bytes when it can be - reading every language's strings was most of the time)
                var raw = iff.Peek<CTSS>(obj.CatalogStringsID);
                var data = raw?.ChunkData;
                if (raw != null && !(raw.ChunkProcessed == false && data != null && STR.PeekFirstTwo(data, out name, out desc)))
                {
                    name = null; desc = "";
                    var ctss = iff.Get<CTSS>(obj.CatalogStringsID);
                    if (ctss != null)
                    {
                        name = ctss.GetString(0);
                        if (ctss.Length > 1) desc = ctss.GetString(1) ?? "";
                    }
                }
            }
            catch (Exception) { }
            item.CatalogName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            item.ReduxDescription = desc ?? "";
            try
            {
                // its own catalog picture; a separate file's object without one gets one drawn from its sprites
                item.ReduxPicture = iff.Peek<BMP>(obj.CatalogStringsID) != null
                    || (sprites != null && sprites.Peek<BMP>(obj.CatalogStringsID) != null)
                    || (source != 0 && CanDraw(iff, sprites, obj, all));
            }
            catch (Exception) { item.ReduxPicture = false; }
            item.ReduxShape = Shape(iff, sprites, obj, all);
        }

        /// <summary>
        /// SimsRedux 14.0: downloads' water, sand, beach, swamp and murky-pool "rugs" (the CT oval water and sand
        /// rugs, sand piles, beach rugs...) are kept out of the catalogs - Joey asked for them gone. Only separate
        /// files (shipped mods, Downloads), never the game's own objects; snow and ice rugs stay. The files aren't
        /// touched, so ones already on a lot keep working.
        /// </summary>
        public static bool HiddenFromCatalog(string catalogName, string label, string fileName)
        {
            var text = ((catalogName ?? "") + " " + (label ?? "") + " " + System.IO.Path.GetFileName(fileName ?? "")).ToLowerInvariant();
            if (text.IndexOf("rug", StringComparison.Ordinal) < 0 && text.IndexOf("sand pile", StringComparison.Ordinal) < 0) return false;
            foreach (var w in HiddenRugWords)
                for (int i = text.IndexOf(w, StringComparison.Ordinal); i >= 0; i = text.IndexOf(w, i + 1, StringComparison.Ordinal))
                    // ("lake" only as a word start: "snowflake rug" is a snow rug, and those stay)
                    if (w != "lake" || i == 0 || !char.IsLetter(text[i - 1])) return true;
            return false;
        }
        // ("_wrug": venegra's water rugs - a plain "wrug" would match "snowrug"; "pool rug", not "pool": a pool-hall rug stays)
        private static readonly string[] HiddenRugWords = { "water", "sand", "beach", "swamp", "pool rug", "poolrug", "ocean", "lake", "pond", "_wrug" };

        private static DGRP GetDGRP(IffFile iff, IffFile sprites, ushort id)
        {
            DGRP result = null;
            try { result = iff.Get<DGRP>(id); } catch (Exception) { }
            if (result == null && sprites != null) try { result = sprites.Get<DGRP>(id); } catch (Exception) { }
            return result;
        }

        /// <summary>The parts of a multi-tile object (or just the object), in tile order.</summary>
        public static List<OBJD> Parts(OBJD obj, List<OBJD> all)
        {
            var parts = new List<OBJD>();
            if (obj.MasterID != 0 && all != null)
                foreach (var o in all)
                    if (o.MasterID == obj.MasterID && o.SubIndex != -1) parts.Add(o);
            if (parts.Count == 0) parts.Add(obj);
            return parts;
        }

        /// <summary>The picture the catalog draws from (medium zoom, facing you), or null - as ReduxCatalogIcons.FirstImage.</summary>
        public static DGRPImage FirstImage(DGRP dgrp)
        {
            if (dgrp?.Images == null) return null;
            var img = dgrp.GetImage(1, 2, 2);
            if (img == null || img.Sprites == null || img.Sprites.Length == 0)
            {
                foreach (var i in dgrp.Images)
                    if (i.Zoom == 2 && i.Sprites != null && i.Sprites.Length > 0) { img = i; break; }
            }
            return (img?.Sprites != null && img.Sprites.Length > 0) ? img : null;
        }

        /// <summary>Can a picture be drawn from the object (it, or one of its tiles, has sprites)?</summary>
        private static bool CanDraw(IffFile iff, IffFile sprites, OBJD obj, List<OBJD> all)
        {
            if (obj.BaseGraphicID != 0 && FirstImage(GetDGRP(iff, sprites, obj.BaseGraphicID)) != null) return true;
            // (a multi-tile object's leader usually has no sprites of its own: its tiles do, and that's what's drawn)
            if (obj.MasterID == 0) return false;
            foreach (var part in Parts(obj, all))
                if (part.BaseGraphicID != 0 && FirstImage(GetDGRP(iff, sprites, part.BaseGraphicID)) != null) return true;
            return false;
        }

        /// <summary>
        /// The object's shape: the size and place of every sprite (medium zoom, facing you, every tile). A recolour
        /// has exactly the same shape; a different design (even with the same name) doesn't. 0 if it can't be read.
        /// </summary>
        public static ulong Shape(IffFile iff, IffFile sprites, OBJD obj, List<OBJD> all)
        {
            var sb = new StringBuilder();
            try
            {
                var parts = Parts(obj, all);
                parts.Sort((a, b) => a.SubIndex.CompareTo(b.SubIndex));
                foreach (var part in parts)
                {
                    sb.Append('[').Append(part.SubIndex).Append(':');
                    if (part.BaseGraphicID == 0) continue;
                    var dgrp = GetDGRP(iff, sprites, part.BaseGraphicID);
                    var img = dgrp?.GetImage(1, 2, 2);
                    if (img?.Sprites == null) continue;
                    foreach (var spr in img.Sprites)
                    {
                        int w, h;
                        if (!FrameSize(dgrp.ChunkParent ?? iff, spr.SpriteID, spr.SpriteFrameIndex, out w, out h)) continue;
                        sb.Append(w).Append('x').Append(h).Append('@')
                          .Append((int)spr.SpriteOffset.X).Append(',').Append((int)spr.SpriteOffset.Y).Append(';');
                    }
                }
            }
            catch (Exception) { }
            return Hash(sb.ToString());
        }

        /// <summary>A sprite frame's size without decoding it (from the raw bytes if the chunk hasn't been read yet).</summary>
        private static bool FrameSize(IffFile file, uint spriteID, uint frame, out int w, out int h)
        {
            w = h = 0;
            var chunk = file.Peek<SPR2>((ushort)spriteID) as SPR2;
            if (chunk == null) return false;
            var raw = chunk.ChunkData;
            if (!chunk.ChunkProcessed && raw != null) return SPR2.PeekFrameSize(raw, frame, out w, out h);
            var spr2 = file.Get<SPR2>((ushort)spriteID);
            if (spr2 == null || frame >= spr2.Frames.Length || spr2.Frames[frame] == null) return false;
            w = spr2.Frames[frame].Width;
            h = spr2.Frames[frame].Height;
            return true;
        }

        // ---------------- SimsRedux 14.x: a price for Buy items that cost §0 ----------------

        /// <summary>A kind of Buy item, the words in a name that mean it, and what such items usually cost in the Sims 1 catalog.</summary>
        private class PriceGuess
        {
            public readonly int Price;
            public readonly string[] Words;
            public PriceGuess(int price, params string[] words) { Price = price; Words = words; }
        }

        // By Buy category (0 Seating ... 7 Lighting), most specific first; whole words only ("bed" isn't in "bedroom").
        // The prices are around the middle of what the game's own items of that kind cost.
        private static readonly PriceGuess[][] PriceWords =
        {
            new[] // 0 Seating
            {
                new PriceGuess(350, "crib", "cribs", "cradle", "bassinet"),
                new PriceGuess(650, "sofa", "sofas", "couch", "couches", "loveseat", "love seat", "settee", "divan", "futon", "sectional"),
                new PriceGuess(1000, "bed", "beds", "daybed", "bunk", "sleeper", "canopy"),
                new PriceGuess(450, "chaise", "recliner", "armchair", "lounge", "lounger", "easy chair", "beanbag", "bean bag", "rocker", "rocking"),
                new PriceGuess(250, "bench", "benches", "stool", "stools", "pew", "ottoman", "hammock"),
                new PriceGuess(200, "chair", "chairs", "seat", "seats"),
            },
            new[] // 1 Surfaces
            {
                new PriceGuess(120, "end table", "endtable", "nightstand", "night stand", "side table", "coffee table", "coffeetable"),
                new PriceGuess(450, "counter", "counters", "island", "bar"),
                new PriceGuess(400, "desk", "desks", "vanity"),
                new PriceGuess(350, "table", "tables"),
            },
            new[] // 2 Appliances
            {
                new PriceGuess(900, "fridge", "refrigerator", "freezer", "icebox"),
                new PriceGuess(700, "stove", "oven", "range", "cooker", "grill", "bbq", "barbecue"),
                new PriceGuess(250, "microwave", "toaster", "coffee", "espresso", "juicer", "blender", "kettle"),
                new PriceGuess(550, "dishwasher", "compactor", "washer", "dryer", "vacuum"),
                new PriceGuess(50, "trash", "garbage", "bin"),
            },
            new[] // 3 Electronics
            {
                new PriceGuess(2000, "computer", "pc", "laptop"),
                new PriceGuess(800, "tv", "television", "projector"),
                new PriceGuess(1200, "arcade", "pinball", "console", "game", "games"),
                new PriceGuess(600, "stereo", "radio", "jukebox", "speaker", "speakers", "boombox", "karaoke", "hifi"),
                new PriceGuess(75, "phone", "telephone"),
                new PriceGuess(50, "smoke", "alarm", "detector", "sprinkler"),
            },
            new[] // 4 Plumbing
            {
                new PriceGuess(4000, "hot tub", "hottub", "jacuzzi", "spa"),
                new PriceGuess(450, "toilet", "toilets", "urinal", "potty"),
                new PriceGuess(300, "sink", "sinks", "basin", "fountain"),
                new PriceGuess(1000, "shower", "showers", "tub", "tubs", "bath", "bathtub"),
            },
            new[] // 5 Decorative
            {
                new PriceGuess(150, "mirror", "mirrors"),
                new PriceGuess(200, "clock", "clocks"),
                new PriceGuess(150, "rug", "rugs", "carpet", "mat"),
                new PriceGuess(100, "curtain", "curtains", "drape", "drapes", "blind", "blinds", "valance"),
                new PriceGuess(300, "painting", "portrait", "poster", "print", "picture", "canvas", "photo", "art", "mural", "tapestry"),
                new PriceGuess(100, "plant", "plants", "flower", "flowers", "fern", "palm", "cactus", "bonsai", "ivy", "orchid", "bouquet", "tree"),
                new PriceGuess(400, "sculpture", "statue", "bust", "figurine", "vase", "urn", "totem", "trophy"),
            },
            new[] // 6 Miscellaneous
            {
                new PriceGuess(500, "bookcase", "bookshelf", "bookshelves", "books", "shelf", "shelves"),
                new PriceGuess(150, "mirror", "mirrors"),
                new PriceGuess(450, "dresser", "dressers", "armoire", "wardrobe", "closet", "chest", "drawers"),
                new PriceGuess(200, "toy", "toys", "doll", "dollhouse", "train", "playground", "slide", "swing", "chalkboard"),
                new PriceGuess(600, "easel", "chess", "telescope", "aquarium", "piano", "guitar", "pool table", "darts", "workbench",
                    "exercise", "treadmill", "weights", "gym", "punching"),
                new PriceGuess(300, "party", "buffet", "punch", "cake", "pinata"),
            },
            new[] // 7 Lighting
            {
                new PriceGuess(250, "ceiling", "chandelier", "hanging", "pendant"),
                new PriceGuess(120, "wall", "sconce"),
                new PriceGuess(200, "floor", "torch", "torchiere", "standing"),
                new PriceGuess(150, "table", "desk"),
                new PriceGuess(150, "outdoor", "garden", "tiki", "lantern", "post", "street"),
            },
        };

        // The game's own subsort flags (bits 0-3) by category, when the name says nothing; then the category's own middle.
        private static readonly int[][] PriceBySubsort =
        {
            new[] { 200, 450, 650, 1000 },   // Seating: dining chairs, living room chairs, sofas, beds
            new[] { 450, 350, 120, 400 },    // Surfaces: counters, tables, end tables, desks
            new[] { 700, 900, 250, 550 },    // Appliances: cooking, fridges, small, large
            new[] { 1200, 1000, 600, 75 },   // Electronics: entertainment, TV/computer, audio, phones
            new[] { 450, 1000, 300, 4000 },  // Plumbing: toilets, showers/tubs, sinks, hot tubs
            new[] { 300, 400, 150, 100 },    // Decorative: paintings, sculptures, rugs, plants
            new[] { 600, 500, 600, 450 },    // Miscellaneous: recreation, knowledge, creativity, dressers
            new[] { 150, 200, 120, 250 },    // Lighting: table, floor, wall, ceiling lamps
        };
        private static readonly int[] PriceByCategory = { 300, 300, 500, 500, 500, 200, 300, 150 };

        /// <summary>
        /// A sensible price for a Buy item (category 0-7) that costs §0: by the words in its catalog name (then its
        /// object label), then its subsort flags, then its category. Only from the item itself and the tables above, so
        /// every game (and every online lot) works out exactly the same price.
        /// </summary>
        public static uint GuessPrice(sbyte category, byte subsort, string name, string label)
        {
            if (category < 0 || category >= PriceWords.Length) return 0;
            var hit = MatchPrice(name, PriceWords[category]);
            if (hit == 0) hit = MatchPrice(label, PriceWords[category]);
            if (hit == 0)
            {
                for (int bit = 0; bit < 4; bit++)
                    if ((subsort & (1 << bit)) != 0) { hit = PriceBySubsort[category][bit]; break; }
            }
            if (hit == 0) hit = PriceByCategory[category];
            return (uint)hit;
        }

        private static int MatchPrice(string text, PriceGuess[] kinds)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            // " aria bed " - every word between spaces, so a word only matches whole
            var sb = new StringBuilder(" ");
            foreach (var c in text.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            sb.Append(' ');
            var padded = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " +", " ");
            foreach (var kind in kinds)
                foreach (var w in kind.Words)
                    if (padded.IndexOf(" " + w + " ", StringComparison.Ordinal) >= 0) return kind.Price;
            return 0;
        }

        /// <summary>
        /// Every catalog-visible Buy item (in a category list, categories 0-7) from a separate file or The Sims Online
        /// (not the game's own archives - nothing of theirs is meant to be priced differently) that costs §0 gets
        /// GuessPrice, marked ReduxPriceGuessed. Build mode, hidden items and objects that aren't in the catalog
        /// (system objects, tools) keep their §0. Returns how many were priced.
        /// </summary>
        public static int FillMissingPrices(List<ObjectCatalogItem>[] byCategory, Dictionary<uint, ObjectCatalogItem> byGuid)
        {
            int count = 0;
            if (byCategory == null || byGuid == null) return 0;
            for (int cat = 0; cat < 8 && cat < byCategory.Length; cat++)
            {
                var list = byCategory[cat];
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item.Price != 0 || item.ReduxSource == 0 || item.ReduxHidden) continue;
                    item.Price = GuessPrice(item.Category, item.Subsort, item.CatalogName, item.Name);
                    if (item.Price == 0) continue;
                    item.ReduxPriceGuessed = true;
                    list[i] = item;
                    if (byGuid.ContainsKey(item.GUID)) byGuid[item.GUID] = item;
                    count++;
                }
            }
            return count;
        }

        /// <summary>64-bit FNV-1a (never 0, which means "unknown").</summary>
        private static ulong Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
            return h == 0 ? 1 : h;
        }
    }
}
