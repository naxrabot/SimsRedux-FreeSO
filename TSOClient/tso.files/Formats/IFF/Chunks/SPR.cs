using FSO.Common;
using FSO.Common.Rendering;
using FSO.Common.Utils;
using FSO.Files.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FSO.Files.Formats.IFF.Chunks
{
    /// <summary>
    /// This chunk type holds a number of paletted sprites that share a common color palette and lack z-buffers and 
    /// alpha buffers. SPR# chunks can be either big-endian or little-endian, which must be determined by comparing 
    /// the first two bytes to zero (since no version number uses more than two bytes).
    /// </summary>
    public class SPR : IffChunk
    {
        public List<SPRFrame> Frames { get; internal set; }
        public ushort PaletteID;
        private List<uint> Offsets;
        public ByteOrder ByteOrd;
        public bool WallStyle;

        /// <summary>
        /// Reads a SPR chunk from a stream.
        /// </summary>
        /// <param name="iff">An Iff instance.</param>
        /// <param name="stream">A Stream object holding a SPR chunk.</param>
        public override void Read(IffFile iff, Stream stream)
        {
            using (var io = IoBuffer.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                var version1 = io.ReadUInt16();
                var version2 = io.ReadUInt16();
                uint version = 0;

                if (version1 == 0)
                {
                    io.ByteOrder = ByteOrder.BIG_ENDIAN;
                    version = (uint)(((version2|0xFF00)>>8) | ((version2&0xFF)<<8));
                }
                else
                {
                    version = version1;
                }
                ByteOrd = io.ByteOrder;

                var spriteCount = io.ReadUInt32();
                PaletteID = (ushort)io.ReadUInt32();

                Frames = new List<SPRFrame>();
                if (version != 1001)
                {
                    var offsetTable = new List<uint>();
                    for (var i = 0; i < spriteCount; i++)
                    {
                        offsetTable.Add(io.ReadUInt32());
                    }
                    Offsets = offsetTable;
                    for (var i = 0; i < spriteCount; i++)
                    {
                        var frame = new SPRFrame(this);
                        io.Seek(SeekOrigin.Begin, offsetTable[i]);
                        var guessedSize = ((i + 1 < offsetTable.Count) ? offsetTable[i + 1] : (uint)stream.Length) - offsetTable[i];
                        frame.Read(version, io, guessedSize);
                        Frames.Add(frame);
                    }
                }
                else
                {
                    while (io.HasMore)
                    {
                        var frame = new SPRFrame(this);
                        frame.Read(version, io, 0);
                        Frames.Add(frame);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The frame (I.E sprite) of a SPR chunk.
    /// </summary>
    public class SPRFrame : ITextureProvider
    {
        public static PALT DEFAULT_PALT = new PALT(Color.Black);

        public uint Version;
        private SPR Parent;
        private Texture2D PixelCache;
        private Texture2D ZCache;
        private byte[] ToDecode;

        private PALT Palette;

        /// <summary>
        /// Constructs a new SPRFrame instance.
        /// </summary>
        /// <param name="parent">A SPR parent.</param>
        public SPRFrame(SPR parent)
        {
            this.Parent = parent;

            UpdatePalette();
        }

        private void UpdatePalette()
        {
            Palette = Parent.ChunkParent.Get<PALT>(Parent.PaletteID);
            if (Palette == null)
            {
                Palette = DEFAULT_PALT;
            }
        }

        private PALT EnsurePalette()
        {
            if (Palette == null)
            {
                UpdatePalette();
            }

            return Palette;
        }


        /// <summary>
        /// Reads a SPRFrame from a stream.
        /// </summary>
        /// <param name="iff">An Iff instance.</param>
        /// <param name="stream">A Stream object holding a SPRFrame.</param>
        public void Read(uint version, IoBuffer io, uint guessedSize)
        {
            if (version == 1001)
            {
                var spriteFersion = io.ReadUInt32();

                var size = io.ReadUInt32();
                this.Version = spriteFersion;

                ReadHead(io);

                if (IffFile.RETAIN_CHUNK_DATA) ReadDeferred(1001, io);
                else ToDecode = io.ReadBytes(size - 8);
            }
            else
            {
                ReadHead(io);

                this.Version = version;
                if (IffFile.RETAIN_CHUNK_DATA) ReadDeferred(1000, io);
                else ToDecode = io.ReadBytes(guessedSize - 8);
            }
        }

        private void ReadHead(IoBuffer io)
        {
            // Useful to read these early for async loading
            var reserved = io.ReadUInt32();
            Height = io.ReadUInt16();
            Width = io.ReadUInt16();
        }

        public void ReadDeferred(uint version, IoBuffer io)
        {
            this.Init();
            this.Decode(io);
        }

        private int _decoding = 0;

        public void DecodeIfRequired()
        {
            if (ToDecode != null)
            {
                if (Interlocked.CompareExchange(ref _decoding, 1, 0) > 0)
                {
                    // If another thread is already decoding the sprite, spin until it's done.
                    SpinWait w = default;
                    while (Volatile.Read(ref _decoding) > 0)
                    {
                        w.SpinOnce();
                    }

                    DecodeIfRequired();
                }
                else
                {
                    // (SimsRedux: always let go of the "decoding" flag - see SPR2)
                    try
                    {
                        using (IoBuffer buf = IoBuffer.FromStream(new MemoryStream(ToDecode), Parent.ByteOrd))
                        {
                            ReadDeferred(Version, buf);
                        }
                    }
                    finally
                    {
                        ToDecode = null;
                        Interlocked.Exchange(ref _decoding, 0);
                    }
                }
            }
        }

        /// <summary>
        /// Decodes this SPRFrame.
        /// </summary>
        /// <param name="io">IOBuffer used to read a SPRFrame.</param>
        private void Decode(IoBuffer io)
        {
            var palette = EnsurePalette();
            var y = 0;
            var endmarker = false;

            while (!endmarker){
                var command = io.ReadByte();
                var count = io.ReadByte();

                switch (command){
                    /** Start marker **/
                    case 0x00:
                    case 0x10:
                        break;
                    /** Fill row with pixel data **/
                    case 0x04:
                        var bytes = count - 2;
                        var x = 0;

                        while (bytes > 0){
                            var pxCommand = io.ReadByte();
                            var pxCount = io.ReadByte();
                            bytes -= 2;

                            switch (pxCommand){
                                /** Next {n} pixels are transparent **/
                                case 0x01:
                                    x += pxCount;
                                    break;
                                /** Next {n} pixels are the same palette color **/
                                case 0x02:
                                    var index = io.ReadByte();
                                    var padding = io.ReadByte();
                                    bytes -= 2;

                                    var color = palette.Colors[index];
                                    for (var j=0; j < pxCount; j++){
                                        this.SetPixel(x, y, color);
                                        x++;
                                    }
                                    break;
                                /** Next {n} pixels are specific palette colours **/
                                case 0x03:
                                    for (var j=0; j < pxCount; j++){
                                        var index2 = io.ReadByte();
                                        var color2 = palette.Colors[index2];
                                        this.SetPixel(x, y, color2);
                                        x++;
                                    }
                                    bytes -= pxCount;
                                    if (pxCount % 2 != 0){
                                        //Padding
                                        io.ReadByte();
                                        bytes--;
                                    }
                                    break;
                            }
                        }

                        y++;
                        break;
                    /** End marker **/
                    case 0x05:
                        endmarker = true;
                        break;
                    /** Leave next rows transparent **/
                    case 0x09:
                        y += count;
                        continue;
                }

            }
        }

        private Color[] Data;
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        protected void Init()
        {
            Data = new Color[Width * Height];
        }

        public Color GetPixel(int x, int y)
        {
            return Data[(y * Width) + x];
        }

        public void SetPixel(int x, int y, Color color)
        {
            Data[(y * Width) + x] = color;
        }

        public Texture2D GetTexture(GraphicsDevice device)
        {
            if (PixelCache == null)
            {
                var mip = !Parent.WallStyle && FSOEnvironment.Enable3D && FSOEnvironment.EnableNPOTMip;
                var tc = FSOEnvironment.TexCompress;

                if (Width * Height > 0)
                {
                    var w = Math.Max(1, Width);
                    var h = Math.Max(1, Height);
                    if (mip && TextureUtils.OverrideCompression(w, h)) tc = false;
                    if (tc)
                    {
                        PixelCache = new Texture2D(device, ((w+3)/4)*4, ((h+3)/4)*4, mip, SurfaceFormat.Dxt5);

                        AssetStreaming.LoadTexture(PixelCache, AssetStreamingMode.Lot, () =>
                        {
                            DecodeIfRequired();

                            TextureData<byte>[] data;
                            if (mip)
                                data = TextureUtils.GenerateDXT5WithMips(PixelCache, w, h, Data);
                            else
                            {
                                data = new TextureData<byte>[]
                                {
                                    new TextureData<byte>(0, TextureUtils.DXT5Compress(Data, w, h).Item1, 1)
                                };
                            }

                            if (!IffFile.RETAIN_CHUNK_DATA) Data = null;

                            return data;
                        });
                    }
                    else
                    {
                        PixelCache = new Texture2D(device, w, h, mip, SurfaceFormat.Color);

                        AssetStreaming.LoadTexture(PixelCache, AssetStreamingMode.Lot, () =>
                        {
                            DecodeIfRequired();

                            TextureData<Color>[] data;
                            if (mip)
                                data = TextureUtils.GenerateMips(PixelCache, Data);
                            else
                            {
                                data = new TextureData<Color>[]
                                {
                                    new TextureData<Color>(0, Data)
                                };
                            }

                            if (!IffFile.RETAIN_CHUNK_DATA) Data = null;

                            return data;
                        });
                    }
                }
                else
                {
                    PixelCache = new Texture2D(device, Math.Max(1, Width), Math.Max(1, Height), mip, SurfaceFormat.Color);
                    PixelCache.SetData<Color>(new Color[] { Color.Transparent });
                }

                PixelCache.Tag = new TextureInfo(PixelCache, Width, Height);
            }
            return PixelCache;
        }

        public Texture2D GetZTexture(GraphicsDevice device)
        {
            if (ZCache == null)
            {
                var w = Math.Max(1, Width);
                var h = Math.Max(1, Height);

                byte[] data = new byte[w * h];
                int i = 0;

                for (int y = 0; y < h; y++)
                {
                    byte z = 128;
                    for (int x = 0; x < w; x++)
                    {
                        data[i++] = z;
                    }
                }

                Texture2D result = new Texture2D(device, w, h, false, SurfaceFormat.Alpha8);
                result.SetData<byte>(data);

                ZCache = result;
                ZCache.Tag = new TextureInfo(ZCache, Width, Height);
            }

            return ZCache;
        }

        public WorldTexture GetWorldTexture(GraphicsDevice device)
        {
            var result = new WorldTexture
            {
                Pixel = this.GetTexture(device),
                ZBuffer = this.GetZTexture(device)
            };

            return result;
        }
    }
}
