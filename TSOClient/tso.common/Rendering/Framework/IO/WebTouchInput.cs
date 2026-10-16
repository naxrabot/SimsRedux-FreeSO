using Microsoft.Xna.Framework;
using System.Collections.Generic;

namespace FSO.Common.Rendering.Framework.IO
{
    /// <summary>
    /// SimsRedux website: fingers on the screen, read by the page itself (js in index.html) and
    /// already turned into the game's own pixels, measured from where the game's picture really is
    /// on the page. Used instead of the engine's touch panel, whose positions can be off on phones.
    /// </summary>
    public static class WebTouchInput
    {
        /// <summary>True once the page has started sending touches.</summary>
        public static bool Enabled;

        private class Finger
        {
            public Vector2 Pos;
            public bool Released;
            public bool Seen;
        }

        private static Dictionary<int, Finger> Fingers = new Dictionary<int, Finger>();
        private static readonly object Lock = new object();

        /// <summary>phase: 0 = down, 1 = moved, 2 = up, 3 = cancelled.</summary>
        public static void Set(int id, float x, float y, int phase)
        {
            lock (Lock)
            {
                Enabled = true;
                // on PC controls the touch is a click (the browser turns it into the mouse): nothing kept here
                if (!FSOEnvironment.SoftwareKeyboard) { Fingers.Clear(); return; }
                Finger f;
                if (!Fingers.TryGetValue(id, out f))
                {
                    if (phase >= 2) return;
                    f = new Finger();
                    Fingers[id] = f;
                }
                f.Pos = new Vector2(x, y);
                if (phase >= 2) f.Released = true;
                else if (phase == 0) { f.Released = false; f.Seen = false; }
            }
        }

        /// <summary>
        /// The fingers down this frame (id, position). A tap that went down and up between two
        /// frames is still reported down for one frame, so it isn't lost.
        /// </summary>
        public static List<KeyValuePair<int, Vector2>> Take()
        {
            var result = new List<KeyValuePair<int, Vector2>>();
            lock (Lock)
            {
                var gone = new List<int>();
                foreach (var pair in Fingers)
                {
                    var f = pair.Value;
                    if (f.Released && f.Seen) { gone.Add(pair.Key); continue; }
                    result.Add(new KeyValuePair<int, Vector2>(pair.Key, f.Pos));
                    f.Seen = true;
                }
                foreach (var id in gone) Fingers.Remove(id);
            }
            return result;
        }
    }
}
