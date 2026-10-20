using System.Threading;

namespace FSO.Common
{
    public static class FSOEnvironment
    {
        public static Thread GameThread;
        /// <summary>SimsRedux: no extra threads (the web browser version). See FSOThreads.</summary>
        public static bool NoThreads = false;

        public static string ContentDir = "Content/";
        public static string UserDir = "Content/";
        public static string GFXContentDir = "Content/OGL";
        public static bool DirectX = false;
        public static bool Linux = false;
        public static bool UseMRT = true;
        /// <summary>
        /// True if system does not support gl_FragDepth (eg. iOS). Uses alternate pipeline that abuses stencil buffer.
        /// </summary>
        public static bool SoftwareDepth = false;
        public static int GLVer = 3;
        public static float UIZoomFactor = 1f;
        public static float DPIScaleFactor = 1;
        /// <summary>
        /// SimsRedux website: the game can be drawn bigger than the page (so menus fit on a phone) and
        /// shrunk to fit; touches and the mouse arrive in page points and are multiplied by this.
        /// </summary>
        public static float InputScaleX = 1, InputScaleY = 1;
        public static bool SoftwareKeyboard = false;
        public static bool NoSound = false;
        public static int RefreshRate = 60;

        /// <summary>
        /// True if 3D features are enabled (like smooth rotation + zoom). Loads some content with mipmaps and other things.
        /// Used to mean "3d camera" as well, though that has been moved to configuration and world state.
        /// </summary>
        public static bool Enable3D;
        public static bool EnableNPOTMip = true;
        public static bool TexCompress = true;
        public static bool TexCompressSupport = true;
        public static bool MSAASupport = true;

        /// <summary>
        /// SimsRedux: can the graphics chip draw into half-float (HalfVector4) targets? Directional lighting needs
        /// them (WorldConfig.Directional is off without). WebGL 2 and OpenGL ES 3 only can with an extension
        /// (EXT_color_buffer_float / EXT_color_buffer_half_float) that the engine doesn't tell us about, so they
        /// count as no - a target that can't be drawn into comes out black. Desktop DirectX and OpenGL can.
        /// (The "lighttest dir" cheat turns it on to try.)
        /// </summary>
        public static bool FloatRenderTargets =
#if WEB || ANDROID || IOS || __IOS__ || __ANDROID__
            false;
#else
            true;
#endif

        public static string Args = "";
    }
}
