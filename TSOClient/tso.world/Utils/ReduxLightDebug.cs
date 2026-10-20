using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// SimsRedux: shows the lot's light map in the top-left corner of the screen (the "lightmap" cheat), to see
    /// why the floors or ground come out black on a phone's graphics chip.
    ///   Mode 1: the light map's colours (each floor's lighting, laid out 3 x 2).
    ///   Mode 2: its alpha - the floor shadow channel the floor and terrain shaders divide by (black there = black floor).
    /// The alpha view is read back from the graphics card twice a second (it's a debug view, not for play).
    /// </summary>
    public static class ReduxLightDebug
    {
        public static int Mode;

        private static SpriteBatch Batch;
        private static Texture2D AlphaView;
        private static Color[] Pixels;
        private static int Countdown;

        public static void Draw(GraphicsDevice gd, WorldState state)
        {
            if (Mode == 0 || gd == null || state == null) return;
            try
            {
                var lm = state.Light?.LightMap;
                if (lm == null || lm.IsDisposed) return;

                Texture2D show = lm;
                if (Mode == 2)
                {
                    if (AlphaView == null || AlphaView.IsDisposed || AlphaView.Width != lm.Width || AlphaView.Height != lm.Height)
                    {
                        AlphaView?.Dispose();
                        AlphaView = new Texture2D(gd, lm.Width, lm.Height, false, SurfaceFormat.Color);
                        Pixels = new Color[lm.Width * lm.Height];
                        Countdown = 0;
                    }
                    if (Countdown-- <= 0)
                    {
                        Countdown = 30;
                        TextureUtils.GetDataKeepTarget(gd, lm, Pixels);
                        for (int i = 0; i < Pixels.Length; i++)
                        {
                            var a = Pixels[i].A;
                            Pixels[i] = new Color(a, a, a, (byte)255);
                        }
                        AlphaView.SetData(Pixels);
                    }
                    show = AlphaView;
                }

                if (Batch == null || Batch.IsDisposed || Batch.GraphicsDevice != gd) Batch = new SpriteBatch(gd);
                var vp = gd.Viewport;
                float scale = Math.Min(vp.Width * 0.45f / show.Width, vp.Height * 0.45f / show.Height);
                var size = new Point(Math.Max(1, (int)(show.Width * scale)), Math.Max(1, (int)(show.Height * scale)));
                Batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.PointClamp);
                Batch.Draw(show, new Rectangle(8, 8, size.X, size.Y), Color.White);
                Batch.End();
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] lightmap view: " + e.Message);
                Mode = 0;
            }
        }

        /// <summary>Turns the view off and lets go of its picture.</summary>
        public static void Off()
        {
            Mode = 0;
            AlphaView?.Dispose();
            AlphaView = null;
            Pixels = null;
        }
    }
}
