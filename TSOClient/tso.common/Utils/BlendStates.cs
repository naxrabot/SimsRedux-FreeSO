using Microsoft.Xna.Framework.Graphics;

namespace FSO.Common.Utils
{
    /// <summary>
    /// SimsRedux: "keep the brighter / darker colour" blending (used by the lighting).
    /// The browser engine refuses BlendFunction.Max/Min even though WebGL supports them, so in the
    /// web build these use a marker combination of blend factors (BlendFactor / InverseBlendFactor)
    /// that the page's webglfix.js turns into WebGL's MAX / MIN. Elsewhere they're the real thing.
    /// </summary>
    public static class BlendStates
    {
        public static BlendState Max(ColorWriteChannels channels = ColorWriteChannels.All)
        {
#if WEB
            return new BlendState
            {
                AlphaBlendFunction = BlendFunction.Add,
                ColorBlendFunction = BlendFunction.Add,
                ColorSourceBlend = Blend.BlendFactor, ColorDestinationBlend = Blend.InverseBlendFactor,
                AlphaSourceBlend = Blend.BlendFactor, AlphaDestinationBlend = Blend.InverseBlendFactor,
                ColorWriteChannels = channels
            };
#else
            return new BlendState
            {
                AlphaBlendFunction = BlendFunction.Max,
                ColorBlendFunction = BlendFunction.Max,
                ColorDestinationBlend = Blend.One,
                ColorWriteChannels = channels
            };
#endif
        }

        public static BlendState Min(ColorWriteChannels channels = ColorWriteChannels.All)
        {
#if WEB
            return new BlendState
            {
                AlphaBlendFunction = BlendFunction.Add,
                ColorBlendFunction = BlendFunction.Add,
                ColorSourceBlend = Blend.InverseBlendFactor, ColorDestinationBlend = Blend.BlendFactor,
                AlphaSourceBlend = Blend.InverseBlendFactor, AlphaDestinationBlend = Blend.BlendFactor,
                ColorWriteChannels = channels
            };
#else
            return new BlendState
            {
                AlphaBlendFunction = BlendFunction.Min,
                ColorBlendFunction = BlendFunction.Min,
                ColorDestinationBlend = Blend.One,
                ColorWriteChannels = channels
            };
#endif
        }
    }
}
