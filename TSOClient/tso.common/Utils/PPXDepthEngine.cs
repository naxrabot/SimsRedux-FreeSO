using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FSO.Common.Utils
{
    public class PPXDepthEngine
    {
        private static GraphicsDevice GD;
        private static RenderTarget2D BackbufferDepth;
        private static RenderTarget2D Backbuffer;
        private static SpriteBatch SB;
        public static int SSAA = 1;
        public static int MSAA = 0;

        public static void InitGD(GraphicsDevice gd)
        {
            GD = gd;
            SB = new SpriteBatch(gd);
        }
        public static void InitScreenTargets()
        {
            if (GD == null) return;
            if (BackbufferDepth != null) BackbufferDepth.Dispose();
            BackbufferDepth = null;
            if (Backbuffer != null) Backbuffer.Dispose();
            var scale = 1;//FSOEnvironment.DPIScaleFactor;
            if (!FSOEnvironment.Enable3D)
                BackbufferDepth = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, SSAA * GD.Viewport.Width / scale, SSAA * GD.Viewport.Height / scale, DepthFormat.None);
            Backbuffer = CreateRenderTarget(GD, 1, MSAA, SurfaceFormat.Color, SSAA * GD.Viewport.Width / scale, SSAA * GD.Viewport.Height / scale, DepthFormat.Depth24Stencil8);
        }

        private static RenderTarget2D ActiveColor;
        private static RenderTarget2D ActiveDepth;
        private static int StencilValue;

        public static void SetPPXTarget(RenderTarget2D color, RenderTarget2D depth, bool clear)
        {
            SetPPXTarget(color, depth, clear, ColorExtensions.TransparentBlack);
        }

        public static void SetPPXTarget(RenderTarget2D color, RenderTarget2D depth, bool clear, Color clearColor)
        {
            color = Live(color); depth = Live(depth); // SimsRedux: never draw into a thrown-away target
            if (color == null && depth == null && Backbuffer != null) color = Backbuffer;
            ActiveColor = color;
            if (color == Backbuffer && depth == null && BackbufferDepth != null) depth = BackbufferDepth;
            ActiveDepth = depth;

            //if (color != null && depth != null) depth.InheritDepthStencil(color);
            var gd = GD;
            SetTarget(gd, color); //can have null subresource when switching to 2d with supersampling enabled, which is odd since the texture is not disposed
            if (clear)
            {
                StencilValue = 1;

                gd.Clear(clearColor);// FSO.Common.Rendering.Framework.GameScreen.ClearColor);
                if (depth != null)
                {
                    SetTarget(gd, depth);
                    gd.Clear(Color.White);
                }
            }
            if (FSOEnvironment.UseMRT)
            {
                if (depth != null) gd.SetRenderTargets(color, depth);
            }
        }

        /// <summary>SimsRedux: switch render target; in the browser a target that lost its picture falls back to the screen instead of crashing.</summary>
        private static void SetTarget(GraphicsDevice gd, RenderTarget2D rt)
        {
#if WEB
            try { gd.SetRenderTarget(Live(rt)); }
            catch (NullReferenceException)
            {
                Console.WriteLine("[SimsRedux] graphics: a drawing target was lost; drawing to the screen this time");
                gd.SetRenderTarget(null);
            }
#else
            gd.SetRenderTarget(rt);
#endif
        }

        /// <summary>SimsRedux: a render target that has been disposed (e.g. after the screen was resized) counts as none.</summary>
        private static RenderTarget2D Live(RenderTarget2D rt)
        {
            return (rt != null && rt.IsDisposed) ? null : rt;
        }

        public static RenderTarget2D GetBackbuffer()
        {
            return Backbuffer;
        }

        public delegate void RenderPPXProcedureDelegate(bool depthPass);
        public static void RenderPPXDepth(Effect effect, bool forceDepth,
            RenderPPXProcedureDelegate proc)
        {
            var color = Live(ActiveColor);
            var depth = Live(ActiveDepth);
            var gd = GD;
            if (FSOEnvironment.SoftwareDepth && depth != null)
            {
                var oldDS = gd.DepthStencilState;
                //completely special case.
                SetTarget(gd, color);
                gd.DepthStencilState = SWColorState(gd, forceDepth, StencilValue);
                effect.Parameters["depthMap"]?.SetValue(depth);
                effect.Parameters["depthOutMode"]?.SetValue(false);
                proc(false);

                //now draw the depth using the depth test information we got previously.

                //unbind depth map since we are writing to it
                effect.Parameters["depthMap"]?.SetValue((Texture2D)null);
                effect.Parameters["depthOutMode"]?.SetValue(true);
                SetTarget(gd, depth);
                gd.DepthStencilState = SWDepthState(gd, forceDepth, StencilValue);
                proc(true);

                gd.DepthStencilState = oldDS;
                StencilValue++; //can increment up to 254 times. Assume we're not going to be rendering that much between clears.
                if (StencilValue > 255) StencilValue = 1;
                SetTarget(gd, color);
                effect.Parameters["depthOutMode"]?.SetValue(false);
            }
            else if (!FSOEnvironment.UseMRT && depth != null)
            {
                //draw color then draw depth
                SetTarget(gd, color);
                proc(false);
                effect.Parameters["depthOutMode"]?.SetValue(true);
                SetTarget(gd, depth);
                proc(true);
                effect.Parameters["depthOutMode"]?.SetValue(false);
                // SimsRedux: go back to the colour target - otherwise everything drawn next (Sims, moving objects)
                // lands on the hidden depth picture. The software-depth path above already does this.
                SetTarget(gd, color);
            }
            else
            {
                //mrt already bound. draw in both.
                proc(false);
            }
        }

        // SimsRedux: the software-depth states were made new for every sprite drawn (two per sprite, every frame);
        // they only differ by the stencil number (1-255) and whether depth is forced, so each is made once.
        private static DepthStencilState[] SWColorStates = new DepthStencilState[512];
        private static DepthStencilState[] SWDepthStates = new DepthStencilState[512];
        // (a state is tied to the graphics device it was first used on: a new device - the graphics lost and made
        //  again - gets new ones, like TextureUtils' ScaleBatchFor and RebindScratch)
        private static GraphicsDevice SWStatesDevice;

        private static void SWStatesFor(GraphicsDevice gd)
        {
            if (SWStatesDevice == gd) return;
            SWStatesDevice = gd;
            SWColorStates = new DepthStencilState[512];
            SWDepthStates = new DepthStencilState[512];
        }

        private static DepthStencilState SWColorState(GraphicsDevice gd, bool forceDepth, int stencil)
        {
            SWStatesFor(gd);
            int i = (forceDepth ? 256 : 0) + (stencil & 255);
            var s = SWColorStates[i];
            if (s != null) return s;
            s = new DepthStencilState
            {
                StencilEnable = true,
                StencilFunction = CompareFunction.Always,
                StencilFail = StencilOperation.Keep,
                StencilPass = StencilOperation.Replace,
                CounterClockwiseStencilPass = StencilOperation.Replace,
                StencilDepthBufferFail = StencilOperation.Keep,
                DepthBufferEnable = forceDepth, //(ActiveColor == null),
                DepthBufferWriteEnable = forceDepth, //(ActiveColor == null),
                ReferenceStencil = stencil,
#if WEB
                // SimsRedux web: WebGL engine has no two-sided stencil; both sides are the same here anyway
                TwoSidedStencilMode = false
#else
                TwoSidedStencilMode = true
#endif
            };
            SWColorStates[i] = s;
            return s;
        }

        private static DepthStencilState SWDepthState(GraphicsDevice gd, bool forceDepth, int stencil)
        {
            SWStatesFor(gd);
            int i = (forceDepth ? 256 : 0) + (stencil & 255);
            var s = SWDepthStates[i];
            if (s != null) return s;
            s = new DepthStencilState
            {
                StencilEnable = true,
                StencilFunction = CompareFunction.Equal,
                DepthBufferEnable = forceDepth,
                DepthBufferWriteEnable = forceDepth,
                ReferenceStencil = stencil,
            };
            SWDepthStates[i] = s;
            return s;
        }

        public static Action<GraphicsDevice, RenderTarget2D> SSAAFunc;
        public static bool WithOpacity = true;

        public static void DrawBackbuffer(float opacity, float scale)
        {
            if (Backbuffer == null) return; //this gfx mode does not use a rendertarget backbuffer
            if (SSAA > 1)
            {
                SSAAFunc(GD, Backbuffer);
            }
            else
            {
                if (!WithOpacity)
                {
                    SB.Begin(blendState: BlendState.Opaque);
                    opacity = 1;
                }
                else
                    SB.Begin(blendState: BlendState.AlphaBlend);
                SB.Draw(Backbuffer, new Vector2(Backbuffer.Width * (1 - scale) / 2, Backbuffer.Height * (1 - scale) / 2), null, Color.White * opacity, 0f, new Vector2(), scale,
                    SpriteEffects.None, 0);
                SB.End();
            }
        }

        public static Point GetWidthHeight()
        {
            return new Point(Backbuffer.Width, Backbuffer.Height);
        }

        public static RenderTarget2D CreateRenderTarget(GraphicsDevice device, int numberLevels, int multisample, SurfaceFormat surface, int width, int height, DepthFormat dformat)
        {
            //apparently in xna4, there is no way to check device format... (it looks for the closest format if desired is not supported) need to look into if this affects anything.

            /*MultiSampleType type = device.PresentationParameters.MultiSampleType;

            // If the card can't use the surface format
            if (!GraphicsAdapter.DefaultAdapter.CheckDeviceFormat(
                DeviceType.Hardware,
                GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Format,
                TextureUsage.None,
                QueryUsages.None,
                ResourceType.RenderTarget,
                surface))
            {
                // Fall back to current display format
                surface = device.DisplayMode.Format;
            }
            // Or it can't accept that surface format 
            // with the current AA settings
            else if (!GraphicsAdapter.DefaultAdapter.CheckDeviceMultiSampleType(
                DeviceType.Hardware, surface,
                device.PresentationParameters.IsFullScreen, type))
            {
                // Fall back to no antialiasing
                type = MultiSampleType.None;
            }*/

            /*int width, height;

            // See if we can use our buffer size as our texture
            CheckTextureSize(device.PresentationParameters.BackBufferWidth,
                device.PresentationParameters.BackBufferHeight,
                out width, out height);*/

            // Create our render target
            return new RenderTarget2D(device,
                width, height, (numberLevels > 1), surface,
                DepthFormat.Depth24Stencil8, multisample, RenderTargetUsage.PreserveContents);
        }
    }
}
