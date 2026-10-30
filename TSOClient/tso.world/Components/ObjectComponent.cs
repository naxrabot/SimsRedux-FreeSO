using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using FSO.Content;
using FSO.LotView.Utils;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using FSO.Common.Utils;
using FSO.LotView.Components.Model;

namespace FSO.LotView.Components
{
    public class ObjectComponent : EntityComponent, IDisposable
    {
        private static Vector2[] PosCenterOffsets = new Vector2[]{
            new Vector2(2+16, 79+8),
            new Vector2(3+32, 158+16),
            new Vector2(5+64, 316+32)
        };

        public GameObject Obj;
        // the modes that this object renders with. most objects are 2d only (with 3d drop ins) but objects can also force 3d components in 2d
        public ComponentRenderMode Mode = ComponentRenderMode._2D;

        protected DGRP DrawGroup;
        protected DGRPRenderer dgrp;
        public WorldObjectRenderInfo RenderInfo;
        public int DynamicCounter; //REMOVE
        public List<SLOTItem> ContainerSlots;
        public List<ParticleComponent> Particles = new List<ParticleComponent>();
        public _2DStandaloneSprite HeadlineSprite;
        public MultitileObjectGroup MultitileGroup;
        public bool Dead;
        protected float ZOrder;

        public bool HideForCutaway;
        public WallSegments AdjacentWall;

        //for ultra lighting in 2d. objects can have a shadow component which handles drawing in 3d.
        private ObjectComponent ShadowComponent;
        public static Func<GameObject, ObjectComponent> MakeShadowComponent;

        protected bool _BoundsDirty = true;
        private Vector3[] BoundsCalcScratch = new Vector3[8];

        public new bool Visible {
            get { return _Visible; }
            set {
                if (_Visible != value)
                {
                    _Visible = value;
                    if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
                }
            }
        }

        #region 3D Members

        private BoundingBox _Bounds;
        private Matrix _World3D;
        private bool _World3DDirty;

        protected override bool _WorldDirty {
            get => base._WorldDirty;
            set {
                base._WorldDirty = value;
                if (value) _World3DDirty = true;
            }
        }

        public Matrix World3D
        {
            get
            {
                if (_World3DDirty || (Container != null))
                {
                    _BoundsDirty = true;
                    var worldPosition = WorldSpace.GetWorldFromTile(Position);
                    _World3D = Matrix.CreateTranslation(new Vector3(1.5f, 0.1f, 1.5f)) * Matrix.CreateTranslation(worldPosition);
                    if (GroundAlign != null) _World = GroundAlign.Value * _World;
                    _World3D = Matrix.CreateScale(3f) * Matrix.CreateRotationY(-RadianDirection) * _World3D;
                    _World3DDirty = false;
                    if (_ReduxScale != 1f) _World3D = ReduxScaleWorld(_World3D); // SimsRedux: resized
                }
                return _World3D;
            }
        }

        private BoundingBox GetTransformedBounds(BoundingBox bounds)
        {
            Matrix mat = World3D;
            Vector3[] points = BoundsCalcScratch;

            bounds.GetCorners(points);

            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

            for (int i = 0; i < 8; i++)
            {
                Vector3 point = Vector3.Transform(points[i], mat);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            return new BoundingBox(min, max);
        }

        public BoundingBox? GetRawBounds()
        {
            var bounds = dgrp.GetBounds();
            if (bounds != null)
            {
                Debug.BoundingBoxCache.AddToCacheIfMissing(Obj.OBJ.GUID, bounds.Value);
            }
            return bounds;
        }

        public BoundingBox GetBounds()
        {
            if (_BoundsDirty || _World3DDirty || Container != null)
            {
                var bounds = dgrp.GetBounds();

                if (bounds == null)
                {
                    if (!dgrp.CanHaveBounds)
                    {
                        // No need to keep checking this.
                        _BoundsDirty = false;
                    }

                    return new BoundingBox(); //don't cache, but also check again next time.
                }
                else
                {
                    _Bounds = GetTransformedBounds(bounds.Value);
                    _BoundsDirty = false;
                }
            }
            return _Bounds;
        }

        public float? IntersectsBounds(Ray ray)
        {
            return GetBounds().Intersects(ray);
        }

        #endregion

        public Rectangle Bounding { get { return dgrp.Bounding ?? new Rectangle(); } }

        public override sbyte Level
        {
            get { return _Level; }
            set { _Level = value; dgrp.Level = value; }
        }

        public override ushort Room
        {
            get
            {
                return dgrp.Room;
            }
            set
            {
                if (blueprint == null) return;
                dgrp.Room = (value> blueprint.Rooms.Count|| value == 0)?value:blueprint.Rooms[value].Base;
                dgrp.Level = Level;
            }
        }

        public override short ObjectID {
            get => base.ObjectID;
            set {
                dgrp.ObjectID = value;
                base.ObjectID = value;
            }
        }

        public override Vector3 GetSLOTPosition(int slot, bool avatar)
        {
            var item = (ContainerSlots != null && ContainerSlots.Count > slot) ? ContainerSlots[slot] : null;
            if (item != null)
            {
                var off = item.Offset;
                var centerRelative = new Vector3(off.X * (1 / 16.0f), off.Y * (1 / 16.0f), ((item.Height != 5 && item.Height != 0) ? SLOT.HeightOffsets[item.Height - 1] : off.Z) * (1 / 5.0f));
                centerRelative = Vector3.Transform(centerRelative, Matrix.CreateRotationZ(RadianDirection));
                if (avatar) centerRelative.Z = 0;
                return this.Position + centerRelative;
            } else return this.Position;
        }

        public ObjectComponent(GameObject obj) {
            this.Obj = obj;
            RenderInfo = new WorldObjectRenderInfo();
            if (obj.OBJ.BaseGraphicID > 0)
            {
                var gid = obj.OBJ.BaseGraphicID;
                this.DrawGroup = obj.Resource.Get<DGRP>(gid);
            }
            dgrp = new DGRPRenderer(this.DrawGroup, obj.OBJ);
            dgrp.DynamicSpriteBaseID = obj.OBJ.DynamicSpriteBaseId;
            dgrp.NumDynamicSprites = obj.OBJ.NumDynamicSprites;
            InterpolationOwner = this;
        }

        public DGRP DGRP
        {
            get
            {
                return DrawGroup;
            }
            set
            {
                if (DrawGroup != value)
                {
                    _BoundsDirty = true;
                    DrawGroup = value;
                }

                if (blueprint != null && dgrp.DGRP != value)
                {
                    blueprint.Changes.RegisterObjectChange(this);
                }
                dgrp.DGRP = value;
            }
        }

        private bool _CutawayHidden;

        public bool CutawayHidden
        {
            get
            {
                return _CutawayHidden;
            }
            set
            {
                if (blueprint != null && _CutawayHidden != value && RenderInfo.Layer == WorldObjectRenderLayer.STATIC)
                {
                    blueprint.Changes.RegisterObjectChange(this);
                }
                _CutawayHidden = value;
            }

        }

        private bool _ForceDynamic;

        public bool ForceDynamic
        {
            get
            {
                var bottomMost = GetBottomContainer();
                return _ForceDynamic || Headline != null || Mode.IsSet(ComponentRenderMode._3D) || bottomMost is AvatarComponent;
            }
            set
            {
                if (blueprint != null && _ForceDynamic != value)
                {
                    if (value) blueprint.Changes.RegisterObjectChange(this);
                    else blueprint.Changes.RegisterObjectChange(this);
                }
                _ForceDynamic = value;
            }
        }

        private ulong _DynamicSpriteFlags = 0x00000000;
        private ulong _DynamicSpriteFlags2 = 0x00000000;
        public ulong DynamicSpriteFlags
        {
            get {
                return _DynamicSpriteFlags;
            } set {

                if (dgrp != null && _DynamicSpriteFlags != value) {
                    dgrp.DynamicSpriteFlags = value;
                    if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
                }
                _DynamicSpriteFlags = value;
            }
        }

        public ulong DynamicSpriteFlags2
        {
            get
            {
                return _DynamicSpriteFlags2;
            }
            set
            {

                if (dgrp != null && _DynamicSpriteFlags2 != value)
                {
                    dgrp.DynamicSpriteFlags2 = value;
                    if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
                }
                _DynamicSpriteFlags2 = value;
            }
        }

        public float RadianDirection
        {
            get
            {
                switch (_Direction)
                {
                    case Direction.NORTH:
                        return 0;
                    case Direction.EAST:
                        return (float)Math.PI / 2;
                    case Direction.SOUTH:
                        return (float)Math.PI;
                    case Direction.WEST:
                        return (float)Math.PI * 1.5f;
                    // SimsRedux: diagonal placement (3D only)
                    case Direction.NORTHEAST:
                        return (float)Math.PI * 0.25f;
                    case Direction.SOUTHEAST:
                        return (float)Math.PI * 0.75f;
                    case Direction.SOUTHWEST:
                        return (float)Math.PI * 1.25f;
                    case Direction.NORTHWEST:
                        return (float)Math.PI * 1.75f;
                    default:
                        return 0;
                }
            }
        }

        // SimsRedux: a screenshot on the object's screen (a computer playing Twisted Metal 2, see ReduxScreens).
        private int _ReduxScreen = -1;
        public int ReduxScreen
        {
            get { return _ReduxScreen; }
            set
            {
                if (_ReduxScreen == value) return;
                _ReduxScreen = value;
                if (dgrp != null)
                {
                    dgrp.ReduxScreen = value;
                    dgrp.InvalidateRotation();
                }
                if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
            }
        }

        // SimsRedux: colour tint (see ReduxTint). Setting it redraws the object in its new colours.
        private uint _Tint;
        public uint Tint
        {
            get { return _Tint; }
            set
            {
                if (_Tint == value) return;
                _Tint = value;
                TintRetries = 0;
                if (dgrp != null)
                {
                    if (value != 0 && (value & 3) == 3 && dgrp.TintProfile == null) dgrp.TintProfile = ReduxTint.ObjectProfile(Obj);
                    dgrp.Tint = value;
                    dgrp.InvalidateRotation();
                }
                if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
            }
        }

        // SimsRedux: objects whose tinted picture was still loading; they get redrawn shortly.
        private static HashSet<ObjectComponent> TintWaiting = new HashSet<ObjectComponent>();
        private static int TintTick;
        private int TintRetries;

        /// <summary>Called once a frame by the lot drawing code.</summary>
        public static void RetryPendingTints()
        {
            if (TintWaiting.Count == 0) return;
            if (++TintTick % 10 != 0) return;
            var list = TintWaiting.ToList();
            TintWaiting.Clear();
            foreach (var c in list)
            {
                if (c.dgrp == null || c.dgrp.Tint == 0 || c.TintRetries++ > 60) continue;
                c.dgrp.InvalidateRotation();
                if (c.blueprint != null) c.blueprint.Changes.RegisterObjectChange(c);
            }
        }

        private Direction _Direction;
        public override Direction Direction
        {
            get
            {
                return _Direction;
            }
            set
            {
                _Direction = value;
                _WorldDirty = true;
                if (dgrp != null) {
                    dgrp.Direction = value;
                    dgrp.InvalidateRotation();
                }
            }
        }

        public override void OnRotationChanged(WorldState world)
        {
            base.OnRotationChanged(world);
            if (dgrp != null)
            {
                dgrp.InvalidateRotation();
            }
        }

        public override void OnZoomChanged(WorldState world)
        {
            base.OnZoomChanged(world);
            if (dgrp != null) {
                dgrp.InvalidateZoom();
            }
        }

        public override void OnPositionChanged()
        {
            dgrp.Position = Position;
        }

        public void UpdateDrawOrder(WorldState world)
        {
            if (world.CameraMode > CameraRenderMode._2DRotate)
            {
                if (!Visible) DrawOrder = 0;
                var w = World3D;
                var ctr = w.Translation;
                var forward = world.ViewProjection.Forward;
                forward.Z *= 1;
                if (forward.Z < 0) forward.Y = -forward.Y;
                DrawOrder = Vector3.Dot(ctr, forward);
            }
            else
            {
                DrawOrder = world.WorldSpace.GetDepthFromTile(Position);
            }
        }

        public bool DoDraw(WorldState world)
        {
            if (!Visible || (Room == 0 && !world.DrawOOB)) return false;
            bool result = false;
            if (world.CameraMode == CameraRenderMode._2D && Mode.IsSet(ComponentRenderMode._2D))
            {
                ValidateSprite(world);
                if (dgrp.Bounding != null) result |= world.WorldRectangle.Intersects(dgrp.Bounding.Value);
                if (Mode.IsSet(ComponentRenderMode._3D))
                {
                    result |= world.Frustum.Intersects(GetBounds());
                }
            } else 
            {
                result |= world.Frustum.Intersects(GetBounds());
            }
            return result;
        }

        public void ValidateSprite(WorldState world)
        {
            ReduxSyncScaleAnchor();
            dgrp.ValidateSprite(world);
        }

        // SimsRedux: drawn bigger or smaller (1 = normal size). Only the picture / model: the object keeps its tiles.
        private float _ReduxScale = 1f;
        public float ReduxScale
        {
            get { return _ReduxScale; }
            set
            {
                if (_ReduxScale == value) return;
                _ReduxScale = value;
                if (dgrp != null) dgrp.ReduxScale = value;
                _WorldDirty = true;
                _BoundsDirty = true;
                if (blueprint != null) blueprint.Changes.RegisterObjectChange(this);
                blueprint?.SM64?.UpdateObject(this);
            }
        }

        private bool _ReduxScaleOnWall;
        /// <summary>SimsRedux: hangs on a wall - resized on the wall around its middle (it stays on the wall).</summary>
        public bool ReduxScaleOnWall
        {
            get { return _ReduxScaleOnWall; }
            set
            {
                if (_ReduxScaleOnWall == value) return;
                _ReduxScaleOnWall = value;
                if (dgrp != null) dgrp.ReduxScaleOnWall = value;
                _WorldDirty = true;
            }
        }

        /// <summary>
        /// SimsRedux: from this tile to the middle of the whole object, in tiles (flat). A multi-tile object (a sofa,
        /// a bed) grows from its middle, so its parts stay together.
        /// </summary>
        private Vector3 ReduxGroupAnchor()
        {
            var grp = MultitileGroup;
            if (grp == null || grp.Objects.Count < 2) return Vector3.Zero;
            var me = Position;
            float sx = 0, sy = 0;
            int n = 0;
            foreach (var o in grp.Objects)
            {
                if (o == null) continue;
                var p = o.Position;
                if (Math.Abs(p.X - me.X) > 8 || Math.Abs(p.Y - me.Y) > 8) continue; // (a part off the lot)
                sx += p.X; sy += p.Y; n++;
            }
            if (n == 0) return Vector3.Zero;
            return new Vector3(sx / n - me.X, sy / n - me.Y, 0);
        }

        private Vector3 _ReduxAnchorUsed;
        private void ReduxSyncScaleAnchor()
        {
            if (_ReduxScale == 1f || dgrp == null) return;
            var a = ReduxGroupAnchor();
            if (Vector3.DistanceSquared(a, dgrp.ReduxScaleAnchor) > 0.0001f) dgrp.ReduxScaleAnchor = a;
            if (Vector3.DistanceSquared(a, _ReduxAnchorUsed) > 0.0001f) _World3DDirty = true;
        }

        /// <summary>
        /// SimsRedux: the 3D model resized around the middle of its floor (of the whole object, for a multi-tile one),
        /// or for a wall item around the middle of its back, on the wall - so it stays on the wall.
        /// </summary>
        private Matrix ReduxScaleWorld(Matrix world)
        {
            var anchor = ReduxGroupAnchor();
            _ReduxAnchorUsed = anchor;
            var local = Vector3.Zero; // (the middle of the tile, on the floor)
            if (_ReduxScaleOnWall)
            {
                var b = dgrp?.GetBounds();
                if (b == null) _World3DDirty = true; // (the model isn't ready yet: try again next time)
                else
                {
                    var bb = b.Value;
                    var c = (bb.Min + bb.Max) / 2f;
                    // the wall is behind the item: the side its model sits furthest towards
                    if (Math.Abs(c.Z) >= Math.Abs(c.X)) local = new Vector3(0, c.Y, (c.Z < 0) ? bb.Min.Z : bb.Max.Z);
                    else local = new Vector3((c.X < 0) ? bb.Min.X : bb.Max.X, c.Y, 0);
                }
            }
            var pivot = Vector3.Transform(local, world) + WorldSpace.GetWorldFromTile(anchor);
            return world * Matrix.CreateTranslation(-pivot) * Matrix.CreateScale(_ReduxScale) * Matrix.CreateTranslation(pivot);
        }

        public override Vector2 GetScreenPos(WorldState world)
        {
            var projected = Vector4.Transform(new Vector4(1.5f, 0, 1.5f, 1f), this.World * world.View * world.Projection);
            if (world.CameraMode < CameraRenderMode._3D) projected.Z = 1;
            var res1 = new Vector2(projected.X / projected.Z, -projected.Y / projected.Z);
            var size = PPXDepthEngine.GetWidthHeight();
            return new Vector2((size.X / PPXDepthEngine.SSAA) * 0.5f * (res1.X + 1f), (size.Y / PPXDepthEngine.SSAA) * 0.5f * (res1.Y + 1f));
        }

        public static Dictionary<WallSegments, Point> CutawayTests = new Dictionary<WallSegments, Point>
        {
            { WallSegments.BottomLeft, new Point(0,1) },
            { WallSegments.TopLeft, new Point(-1,0) },
            { WallSegments.TopRight, new Point(0,-1)},
            { WallSegments.BottomRight, new Point(1,0) }
        };

        public EntityComponent GetBottomContainer()
        {
            EntityComponent current = this;
            while (current.Container != null)
            {
                current = current.Container;
            }
            return current;
        }

        public override void Update(GraphicsDevice device, WorldState world)
        {
            if (Headline != null)
            {
                //todo: this and hideForCutaway should be part of the ForceDynamic bool (accessed by the static layer manager)
                if (blueprint != null && RenderInfo.Layer == WorldObjectRenderLayer.STATIC) blueprint.Changes.RegisterObjectChange(this);
            }

            var idleFrames = InterpolationOwner.IdleFrames;
            if (idleFrames > 0)
            {
                if (_IdleFramesPct > -3)
                {
                    _IdleFramesPct -= world.FramePerDraw / idleFrames;
                    _WorldDirty = true;
                    dgrp.Position = Position;
                }
            }
            else _IdleFramesPct = 0;

            if ((HideForCutaway || ReduxWallHung) && Level > 0)
            {
                if (HideForCutaway && !(world.BuildMode > 1) && world.DynamicCutaway && Level == world.Level)
                {
                    if (blueprint != null && RenderInfo.Layer == WorldObjectRenderLayer.STATIC) blueprint.Changes.RegisterObjectChange(this);
                }

                // SimsRedux 18.10: things hung on a wall (paintings, mirrors, wall lamps...) show on both sides of
                // their wall in Buy and Build mode; only Live mode hides them with a cut-away wall.
                bool showAll = world.BuildMode > 1 || (ReduxWallHung && world.BuildMode > 0);
                if (Level != world.Level || showAll || blueprint == null) CutawayHidden = false;
                else
                {
                    var tilePos = new Point((int)Math.Round(Position.X), (int)Math.Round(Position.Y));

                    if (tilePos.X >= 0 && tilePos.X < blueprint.Width && tilePos.Y >= 0 && tilePos.Y < blueprint.Height)
                    {
                        var wall = blueprint.Walls[Level - 1][tilePos.Y * blueprint.Width + tilePos.X];
                        var seg = AdjacentWall & wall.Segments;
                        var cutTest = ReduxCutTest(seg);
                        if (cutTest == Point.Zero) cutTest = ReduxCutTest(wall.OccupiedWalls & wall.Segments);
                        var other = tilePos + cutTest;
                        var cutaway = blueprint.Cutaway;

                        // (the tiles on both sides of the wall cut away - checked without per-frame allocations)
                        var canContinue = cutaway != null
                            && cutaway[tilePos.Y * blueprint.Width + tilePos.X]
                            && other.X >= 0 && other.X < blueprint.Width && other.Y >= 0 && other.Y < blueprint.Height
                            && cutaway[other.Y * blueprint.Width + other.X];

                        // SimsRedux: also hidden when the wall it hangs on is drawn cut down (fully or partly),
                        // so a painting doesn't float in the air (its back showing) over a cut wall
                        if (seg == 0) seg = wall.OccupiedWalls & wall.Segments;
                        if (!canContinue && seg != 0)
                        {
                            int wx = tilePos.X, wy = tilePos.Y;
                            bool topLeft = true, straight = true;
                            if ((seg & WallSegments.TopLeft) > 0) topLeft = true;
                            else if ((seg & WallSegments.TopRight) > 0) topLeft = false;
                            else if ((seg & WallSegments.BottomRight) > 0) { wx++; topLeft = true; }
                            else if ((seg & WallSegments.BottomLeft) > 0) { wy++; topLeft = false; }
                            else straight = false; // (a diagonal wall: left as it was)

                            if (!straight) { }
                            else if (world.CameraMode == CameraRenderMode._2D)
                            {
                                if (blueprint.WallComp != null) canContinue = blueprint.WallComp.ReduxWallCutAt(wx, wy, topLeft);
                            }
                            else if (ReduxWallHung) canContinue = ReduxWallCut3D(world, wx, wy, topLeft);
                        }
                        CutawayHidden = canContinue;
                    }
                }
            }
        }

        /// <summary>
        /// SimsRedux 18.10: hangs on a wall (a painting, mirror, wall lamp...). Set by the game (drawing only):
        /// in Live mode it's hidden while its wall is cut away or down, so it doesn't float over the room.
        /// </summary>
        public bool ReduxWallHung;

        /// <summary>The tile on the other side of this wall side (as CutawayTests, without a dictionary lookup).</summary>
        private static Point ReduxCutTest(WallSegments seg)
        {
            switch (seg)
            {
                case WallSegments.BottomLeft: return new Point(0, 1);
                case WallSegments.TopLeft: return new Point(-1, 0);
                case WallSegments.TopRight: return new Point(0, -1);
                case WallSegments.BottomRight: return new Point(1, 0);
            }
            return Point.Zero;
        }

        /// <summary>
        /// SimsRedux 18.10: is this wall (the top-left or top-right edge of tile x,y) drawn cut down in 3D? The 3D
        /// walls look up their height at each end, a little towards the camera (as WallComponentRC does).
        /// </summary>
        private bool ReduxWallCut3D(WorldState world, int x, int y, bool topLeft)
        {
            var cut = blueprint.Cutaway;
            if (cut == null) return false;
            var off = RC.WallComponentRC.CutCheckDir[(int)world.CutRotation] * 0.66f;
            for (int i = 0; i < 2; i++)
            {
                float px = x + off.X, py = y + off.Y;
                if (i == 1) { if (topLeft) py += 1; else px += 1; }
                var index = (int)py * blueprint.Width + (int)px;
                if (index >= 0 && index < cut.Length && cut[index]) return true;
            }
            return false;
        }

        public override float GetHeadlineScale()
        {
            return 0.5f;
        }

        public override Vector3 GetHeadlinePos()
        {
            return new Vector3(0, 0, -0.33f);
        }

        public override Vector3 GetLookTarget()
        {
            return new Vector3(Position.X, Position.Z + GetParticleBounds().Max.Y - 0.5f, Position.Y);
        }

        public override void Draw(GraphicsDevice device, WorldState world)
        {
            if (!Visible || (!world.DrawOOB && (Position.X < -2043 && Position.Y < -2043))) return;
            //#endif
            if (CutawayHidden && !world.RenderingThumbnail) return; // (SimsRedux 18.10: a lot picture has its walls up)
            var pos = Position;

            if (world.CameraMode > CameraRenderMode._2D || Mode.IsSet(ComponentRenderMode._3D)) //)
            {
                ReduxSyncScaleAnchor();
                var mworld = World3D;
                dgrp.World = mworld;
                if (this.DrawGroup != null) dgrp.Draw3D(world);
            }
            if (world.CameraMode == CameraRenderMode._2D && Mode.IsSet(ComponentRenderMode._2D))
            {
                if (this.DrawGroup != null)
                {
                    ReduxSyncScaleAnchor();
                    if (Container != null) dgrp.Position = pos;
                    dgrp.DrawImmediate(world);
                    if (dgrp.TintPending && dgrp.Tint != 0) TintWaiting.Add(this);
                }
            }

            if (Headline != null && !Headline.IsDisposed && world.CameraMode == CameraRenderMode._2D)
            {
                if (HeadlineSprite == null) HeadlineSprite = new _2DStandaloneSprite();
                var headOff = new Vector3(0, 0, 0.66f);
                var headPx = world.WorldSpace.GetScreenFromTile(headOff);

                var item = HeadlineSprite;
                item.Pixel = Headline;
                item.Depth = TextureGenerator.GetWallZBuffer(device)[30];

                item.SrcRect = new Rectangle(0, 0, Headline.Width, Headline.Height);
                item.WorldPosition = headOff;
                var off = PosCenterOffsets[(int)world.Zoom - 1];
                item.DestRect = new Rectangle(
                    ((int)headPx.X - Headline.Width / 2) + (int)off.X,
                    ((int)headPx.Y - Headline.Height / 2) + (int)off.Y, Headline.Width, Headline.Height);

                item.AbsoluteDestRect = item.DestRect;
                item.AbsoluteDestRect.Offset(world.WorldSpace.GetScreenFromTile(pos));
                item.AbsoluteWorldPosition = item.WorldPosition + WorldSpace.GetWorldFromTile(pos);
                HeadlineSprite.PrepareVertices(device);
                world._2D.DrawImmediate(item);
            }

            for (int i = 0; i < Particles.Count; i++)
            {
                var part = Particles[i];
                if (part.BoundsDirty && part.AutoBounds && dgrp != null)
                {
                    //this particle needs updated bounds.
                    BoundingBox bounds;
                    if (ShadowComponent != null)
                        bounds = ShadowComponent.GetParticleBounds();
                    else
                        bounds = GetParticleBounds();
                    part.Volume = bounds;
                    part.BoundsDirty = false;
                    part.Dispose();
                }
                part.Level = Level;
                part.OwnerWorld = Matrix.CreateScale(3) * World * Matrix.CreateTranslation(1.5f, 0, 1.5f) * Matrix.CreateScale(2);
                if (part.Dead) Particles.RemoveAt(i--);
            }
        }

        public void DrawLMap(GraphicsDevice device, sbyte level)
        {
            //#if !DEBUG 
            if (!Visible || (Position.X < -2043 && Position.Y < -2043) || Level < 1) return;
            //#endif
            dgrp.World = World3D;
            if (this.DrawGroup != null)
            {
                float yOff = 0;
                if (this.Container != null)
                {
                    yOff = this.Position.Z - this.Container.Position.Z;
                }
                dgrp.DrawLMap(device, level, yOff);
            }
        }

        public virtual BoundingBox GetParticleBounds()
        {
            //make an estimation based off of the sprite height
            var bounds = dgrp.GetBounds();
            if (bounds != null) return bounds.Value;

            if (Debug.BoundingBoxCache.TryGetValue(Obj.OBJ.GUID, out var bounds2)) return bounds2;

            //if (world.CameraMode == CameraRenderMode._2D || Mode.HasFlag(ComponentRenderMode._3D)) return  ?? new BoundingBox();
            if (DGRP == null) return new BoundingBox(new Vector3(-0.4f, 0.1f, -0.4f), new Vector3(0.4f, 0.9f, 0.4f));
            else
            {
                var image = DGRP.GetImage(1, 3, 1);
                var maxY = int.MinValue;
                var minY = int.MaxValue;
                var objOffset = 0f;

                if (image.Sprites.Length == 0) return new BoundingBox(new Vector3(-0.4f, 0.1f, -0.4f), new Vector3(0.4f, 0.9f, 0.4f));

                foreach (var spr in image.Sprites)
                {
                    var dim = spr.GetDimensions();

                    var top = spr.SpriteOffset.Y;
                    var btm = top + dim.Y;

                    if (top < minY) minY = (int)top;
                    if (btm > maxY) maxY = (int)btm;
                    objOffset += spr.ObjectOffset.Z * 1f / 5f;
                }
                objOffset /= image.Sprites.Length;
                //128 is a height of zero
                var topY = Math.Max(0, Math.Min(2.95f, (100 - minY) / 95f));
                var btmY = Math.Max(0, Math.Min(2.95f, (100 - maxY) / 95f));
                return new BoundingBox(new Vector3(-0.4f, btmY, -0.4f), new Vector3(0.4f, topY, 0.4f));
            }
        }

        public override void Preload(GraphicsDevice device, WorldState world)
        {
            if (this.DrawGroup != null) dgrp.Preload(world, Mode);
        }

        /*
        public virtual void DrawLMap(GraphicsDevice device, sbyte level)
        {
            if (ShadowComponent == null) ShadowComponent = new RC.ObjectComponentRC(Obj);
            if (!Visible) return;
            if (Container != null && Container is AvatarComponent) return;
            if (ShadowComponent.UnmoddedPosition != UnmoddedPosition) ShadowComponent.Position = UnmoddedPosition;
            if (ShadowComponent.DGRP != DGRP) ShadowComponent.DGRP = DGRP;
            if (ShadowComponent.Direction != Direction) ShadowComponent.Direction = Direction;
            if (ShadowComponent.Room != Room) ShadowComponent.Room = Room;
            if (ShadowComponent.DynamicSpriteFlags != DynamicSpriteFlags) ShadowComponent.DynamicSpriteFlags = DynamicSpriteFlags;
            if (ShadowComponent.DynamicSpriteFlags2 != DynamicSpriteFlags2) ShadowComponent.DynamicSpriteFlags2 = DynamicSpriteFlags2;
            if (ShadowComponent.Level != Level) ShadowComponent.Level = Level;
            ShadowComponent.DrawLMap(device, level);
        }
        */

        public void Dispose()
        {
            dgrp.Dispose();
            HeadlineSprite?.Dispose();
        }
    }
}
