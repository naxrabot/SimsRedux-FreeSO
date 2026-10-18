using FSO.Common.Content;
using FSO.Common.IO;
using FSO.Content.Framework;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FSO.Content
{
    /// <summary>
    /// SimsRedux: makes a recoloured copy of a floor or wallpaper as a real Sims 1 floor (.flr) or wall
    /// (.wll) file - the same pictures with their colour palettes changed. Works on the file's own
    /// bytes, so nothing is lost or re-compressed.
    /// </summary>
    public static class ReduxPatternFile
    {
        public class Chunk
        {
            public string Type;
            public ushort ID;
            public ushort Flags;
            public string Label;
            public byte[] Data;
        }

        /// <summary>The chunks in an IFF file.</summary>
        public static List<Chunk> Read(byte[] d)
        {
            var res = new List<Chunk>();
            int off = 64;
            while (off + 76 <= d.Length)
            {
                var type = Encoding.ASCII.GetString(d, off, 4);
                int size = (d[off + 4] << 24) | (d[off + 5] << 16) | (d[off + 6] << 8) | d[off + 7];
                if (size < 76 || off + size > d.Length) break;
                ushort id = (ushort)((d[off + 8] << 8) | d[off + 9]);
                ushort flags = (ushort)((d[off + 10] << 8) | d[off + 11]);
                int ln = 0; while (ln < 64 && d[off + 12 + ln] != 0) ln++;
                var label = Encoding.ASCII.GetString(d, off + 12, ln);
                var data = new byte[size - 76];
                Array.Copy(d, off + 76, data, 0, data.Length);
                res.Add(new Chunk { Type = type, ID = id, Flags = flags, Label = label, Data = data });
                off += size;
            }
            return res;
        }

        /// <summary>An IFF file made of these chunks.</summary>
        public static byte[] Write(IEnumerable<Chunk> chunks)
        {
            var ms = new MemoryStream();
            var head = new byte[64];
            var sig = Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1");
            Array.Copy(sig, head, Math.Min(60, sig.Length));
            ms.Write(head, 0, 64);
            foreach (var c in chunks)
            {
                int size = c.Data.Length + 76;
                var h = new byte[76];
                var t = Encoding.ASCII.GetBytes((c.Type + "    ").Substring(0, 4));
                Array.Copy(t, h, 4);
                h[4] = (byte)(size >> 24); h[5] = (byte)(size >> 16); h[6] = (byte)(size >> 8); h[7] = (byte)size;
                h[8] = (byte)(c.ID >> 8); h[9] = (byte)c.ID;
                var flags = c.Flags == 0 ? (ushort)0x10 : c.Flags;
                h[10] = (byte)(flags >> 8); h[11] = (byte)flags;
                var l = Encoding.ASCII.GetBytes(c.Label ?? "");
                Array.Copy(l, 0, h, 12, Math.Min(63, l.Length));
                ms.Write(h, 0, 76);
                ms.Write(c.Data, 0, c.Data.Length);
            }
            return ms.ToArray();
        }

        /// <summary>The bytes of a content file (a loose file or one inside a .far).</summary>
        public static byte[] SourceBytes(IContentReference entry)
        {
            switch (entry)
            {
                case FileContentReference<object> f: return FSOFile.ReadAllBytes(f.Filename);
                case FileContentReference<IffFile> f: return FSOFile.ReadAllBytes(f.Filename);
                case Far1ProviderEntry<object> a: return a.Archive.GetEntry(a.FarEntry);
                case Far1ProviderEntry<IffFile> a: return a.Archive.GetEntry(a.FarEntry);
            }
            return null;
        }

        /// <summary>The palettes a sprite chunk (SPR or SPR2) uses.</summary>
        private static IEnumerable<ushort> PalettesOf(Chunk c)
        {
            var d = c.Data;
            var set = new HashSet<ushort>();
            try
            {
                if (c.Type == "SPR2")
                {
                    uint ver = BitConverter.ToUInt32(d, 0);
                    if (ver == 1000)
                    {
                        uint count = BitConverter.ToUInt32(d, 4);
                        set.Add((ushort)BitConverter.ToUInt32(d, 8));
                        for (int i = 0; i < count; i++)
                        {
                            int o = (int)BitConverter.ToUInt32(d, 12 + i * 4);
                            if (o + 10 <= d.Length) set.Add(BitConverter.ToUInt16(d, o + 8));
                        }
                    }
                    else
                    {
                        set.Add((ushort)BitConverter.ToUInt32(d, 4));
                        uint count = BitConverter.ToUInt32(d, 8);
                        int o = 12;
                        for (int i = 0; i < count && o + 18 <= d.Length; i++)
                        {
                            uint size = BitConverter.ToUInt32(d, o + 4);
                            set.Add(BitConverter.ToUInt16(d, o + 16));
                            o += 8 + (int)size;
                        }
                    }
                }
                else if (c.Type == "SPR#")
                {
                    bool big = d[0] == 0 && d[1] == 0;
                    uint pal = big ? (uint)((d[8] << 24) | (d[9] << 16) | (d[10] << 8) | d[11]) : BitConverter.ToUInt32(d, 8);
                    set.Add((ushort)pal);
                }
            }
            catch (Exception) { }
            return set;
        }

        /// <summary>A palette chunk with every colour put through recolour.</summary>
        private static byte[] RecolourPalette(byte[] d, Func<Color, Color> recolour)
        {
            var o = (byte[])d.Clone();
            if (o.Length < 16) return o;
            uint n = BitConverter.ToUInt32(o, 4);
            for (int i = 0; i < n && 16 + i * 3 + 2 < o.Length; i++)
            {
                int p = 16 + i * 3;
                var c = recolour(new Color(o[p], o[p + 1], o[p + 2], (byte)255));
                o[p] = c.R; o[p + 1] = c.G; o[p + 2] = c.B;
            }
            return o;
        }

        /// <summary>
        /// A recoloured floor or wall file.
        /// source: the original file's bytes. sprites: (chunk ID in the source, chunk ID in the new file).
        /// spriteType: "SPR2" for floors, "SPR#" for walls. name / price / description: its catalog text.
        /// </summary>
        public static byte[] MakeTinted(byte[] source, string spriteType, IList<Tuple<ushort, ushort>> sprites,
            Func<Color, Color> recolour, string name, int price, string description)
        {
            var all = Read(source);
            var outChunks = new List<Chunk>();
            var pals = new HashSet<ushort>();
            foreach (var s in sprites)
            {
                var c = all.FirstOrDefault(x => x.Type == spriteType && x.ID == s.Item1);
                if (c == null) throw new InvalidDataException("picture " + s.Item1 + " is missing");
                outChunks.Add(new Chunk { Type = c.Type, ID = s.Item2, Flags = c.Flags, Label = c.Label, Data = c.Data });
                foreach (var p in PalettesOf(c)) pals.Add(p);
            }
            foreach (var p in pals)
            {
                var pc = all.FirstOrDefault(x => x.Type == "PALT" && x.ID == p);
                if (pc != null) outChunks.Add(new Chunk { Type = "PALT", ID = pc.ID, Flags = pc.Flags, Label = pc.Label, Data = RecolourPalette(pc.Data, recolour) });
            }
            // the catalog text: name, price, description
            byte[] strData;
            using (var ms = new MemoryStream())
            {
                // (Sims 1 text: format -3, then language, text and comment for each line)
                ms.WriteByte(0xFD); ms.WriteByte(0xFF);
                ms.WriteByte(3); ms.WriteByte(0);
                foreach (var line in new[] { name ?? "", price.ToString(), description ?? "" })
                {
                    ms.WriteByte(1);
                    var b = Encoding.Latin1.GetBytes(line);
                    ms.Write(b, 0, b.Length); ms.WriteByte(0);
                    ms.WriteByte(0);
                }
                strData = ms.ToArray();
            }
            outChunks.Insert(0, new Chunk { Type = "STR#", ID = 0, Label = "catalog", Data = strData });
            return Write(outChunks);
        }
    }
}
