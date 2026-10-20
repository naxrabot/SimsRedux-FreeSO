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

        /// <summary>64-bit FNV-1a (never 0, which means "unknown").</summary>
        private static ulong Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
            return h == 0 ? 1 : h;
        }
    }
}
