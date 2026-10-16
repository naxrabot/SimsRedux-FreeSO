using Microsoft.Xna.Framework.Graphics;
using System;
using System.IO;

namespace FSO.Common.Utils
{
    /// <summary>
    /// SimsRedux: turns an image file (png, bmp, jpg...) into a texture. On desktop this is just
    /// Texture2D.FromStream; platforms whose engine can't decode images themselves (the web
    /// browser version) set Decoder to their own, which returns RGBA bytes, width and height.
    /// </summary>
    public static class TextureLoad
    {
        public static Func<Stream, Tuple<byte[], int, int>> Decoder;

        public static Texture2D FromStream(GraphicsDevice gd, Stream stream)
        {
            if (Decoder == null) return Texture2D.FromStream(gd, stream);
            var img = Decoder(stream);
            if (img == null) throw new InvalidOperationException("This image format is not supported");
            var tex = new Texture2D(gd, img.Item2, img.Item3);
            try { tex.SetData(img.Item1); }
            catch { tex.Dispose(); throw; } // (bad pixel data: don't leave the GPU texture behind)
            return tex;
        }
    }
}
