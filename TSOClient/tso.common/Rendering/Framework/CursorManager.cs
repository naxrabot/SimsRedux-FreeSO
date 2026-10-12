using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Utils;

using FSO.Common.IO;
namespace FSO.Common.Rendering.Framework
{
    public enum CursorType
    {
        Normal,
        ArrowUp,
        ArrowUpLeft,
        ArrowUpRight,
        ArrowDown,
        ArrowDownLeft,
        ArrowDownRight,
        ArrowLeft,
        ArrowRight,
        LiveNothing,
        LiveObjectUnavail,
        LivePerson,
        IBeam,

        SimsRotate,
        SimsRotateNE,
        SimsRotateSE,
        SimsRotateSW,
        SimsRotateNW,

        SimsMove,
        SimsPlace,

        Hourglass,

        LiveObjectAvail,
        LiveObject1Star,
        LiveObject2Star,
        LiveObject3Star,
        LiveObject4Star,
        LiveObject5Star,
        LiveObjectSpecial,

        // SimsRedux: more of the original Sims 1 cursors
        Pan,
        Hand,
        Help,
        Water,
        Hole,
        Bulldoze,
        SimsRaise,
        SimsLower,
        SimsLevel,
        SimsDelete,
        NoDrop,
        MoveIn,
        Finger,
        LivePet,
    }

    /// <summary>
    /// Manages cursors in the game.
    /// </summary>
    public class CursorManager
    {
        public static CursorManager INSTANCE;

        private Dictionary<CursorType, CursorGroup> m_CursorMap;
        private GraphicsDevice GD;
        public CursorType CurrentCursor { get; internal set;} = CursorType.Normal;
        public int CurrentPriority { get; private set; } = 0;

        public CursorManager(GraphicsDevice gd)
        {
            INSTANCE = this;
            m_CursorMap = new Dictionary<CursorType, CursorGroup>();
            this.GD = gd;
        }

        public void SetCursorPriority(int priority)
        {
            CurrentPriority = priority;
        }

        public void SetCursor(CursorType type, int priority = 0)
        {
            if (CurrentCursor != type && priority >= CurrentPriority && m_CursorMap.ContainsKey(type))
            {
                CurrentCursor = type;
                Mouse.SetCursor(m_CursorMap[type].MouseCursor);
            }
        }

        public Dictionary<CursorType, string> GenMap()
        {
            return new Dictionary< CursorType, string> (){
                //{CursorType.Normal, "arrow.cur"},
                { CursorType.ArrowUp, "up.cur"},
                { CursorType.ArrowUpLeft, "upleft.cur"},
                { CursorType.ArrowUpRight, "upright.cur"},
                { CursorType.ArrowDown, "down.cur"},
                { CursorType.ArrowDownLeft, "downleft.cur"},
                { CursorType.ArrowDownRight, "downright.cur"},
                { CursorType.ArrowLeft, "left.cur"},
                { CursorType.ArrowRight, "right.cur"},
                { CursorType.LiveNothing, "livenothing.cur"},
                { CursorType.LiveObjectAvail, "liveobjectavail.cur"},
                { CursorType.LiveObjectUnavail, "liveobjectunavail.cur"},
                { CursorType.LivePerson, "liveperson.cur"},

                { CursorType.SimsRotate, "simsrotate.cur" },
                { CursorType.SimsRotateNE, "simsrotatene.cur" },
                { CursorType.SimsRotateNW, "simsrotatenw.cur" },
                { CursorType.SimsRotateSE, "simsrotatese.cur" },
                { CursorType.SimsRotateSW, "simsrotatesw.cur" },

                { CursorType.SimsMove, "simsmove.cur" },
                { CursorType.SimsPlace, "simsplace.cur" },

                { CursorType.Hourglass, "hourglass.cur" },

                // SimsRedux
                { CursorType.Pan, "pan.cur" },
                { CursorType.Hand, "hand.cur" },
                { CursorType.Help, "help.cur" },
                { CursorType.Water, "water.cur" },
                { CursorType.Hole, "hole.cur" },
                { CursorType.Bulldoze, "bulldoze.cur" },
                { CursorType.SimsRaise, "simsraise.cur" },
                { CursorType.SimsLower, "simslower.cur" },
                { CursorType.SimsLevel, "simslevel.cur" },
                { CursorType.SimsDelete, "simsdelete.cur" },
                { CursorType.NoDrop, "nodrop.cur" },
                { CursorType.MoveIn, "movein.cur" },
                { CursorType.Finger, "finger.cur" },
                { CursorType.LivePet, "livepet.cur" },
            };
        }

        /// <summary>
        /// SimsRedux: where to get a cursor file when it isn't a loose file on disk (The Sims 1
        /// keeps its cursors inside UIGraphics.far). Gets the file name, returns its bytes or null.
        /// </summary>
        public static Func<string, byte[]> ExtraSource;

        private Stream OpenCursor(string basepath, string curPath, string name)
        {
            var path = Path.Combine(basepath ?? "", curPath, name);
            if (FSOFile.Exists(path)) return FSOFile.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = ExtraSource?.Invoke(name);
            return (bytes == null) ? null : new MemoryStream(bytes);
        }

        public void Init(string basepath, bool ts1)
        {
            // SimsRedux: never crash over a missing cursor; skip it and keep the normal one
            var map = GenMap();
            var curPath = "UIGraphics/Shared/cursors/";
            if (!ts1) curPath = curPath.ToLowerInvariant();
            foreach (var item in map)
            {
                try
                {
                    using (var stream = OpenCursor(basepath, curPath, item.Value))
                    {
                        if (stream == null) continue;
                        m_CursorMap[item.Key] = CurLoader.LoadMonoCursor(GD, stream);
                    }
                }
                catch (Exception e)
                {
                    System.Console.WriteLine("[SimsRedux] Cursor " + item.Value + ": " + e.Message);
                }
            }

            try
            {
                var starMax = 5;
                using (var stream = OpenCursor(basepath, curPath, "liveobjectavail.cur"))
                {
                    if (stream != null)
                    {
                        var stars = CurLoader.LoadUpgradeCursors(GD, stream, starMax);
                        for (int i = 0; i < starMax; i++) m_CursorMap[CursorType.LiveObject1Star + i] = stars[i];
                    }
                }
            }
            catch (Exception e)
            {
                System.Console.WriteLine("[SimsRedux] Upgrade cursors: " + e.Message);
            }

            m_CursorMap[CursorType.IBeam] = new CursorGroup(MouseCursor.IBeam);
            // SimsRedux: the original Sims 1 arrow when we have it
            CursorGroup? arrow = null;
            try
            {
                using (var stream = OpenCursor(basepath, curPath, "arrow.cur"))
                    if (stream != null) arrow = CurLoader.LoadMonoCursor(GD, stream);
            }
            catch { }
            m_CursorMap[CursorType.Normal] = arrow ?? new CursorGroup(MouseCursor.Arrow);
            CurrentCursor = (CursorType)(-1); // force the next SetCursor to apply
            SetCursor(CursorType.Normal);
        }

        public CursorGroup GetCurrentGroup()
        {
            if (m_CursorMap.TryGetValue(CurrentCursor, out CursorGroup value))
            {
                return value;
            }

            return default;
        }

        private CursorGroup[] LoadUpgradeCursors(string path, int maxStars)
        {
            return CurLoader.LoadUpgradeCursors(GD, FSOFile.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read), maxStars);
        }

        private CursorGroup LoadCustomCursor(string path)
        {
            return CurLoader.LoadMonoCursor(GD, FSOFile.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
    }
}
