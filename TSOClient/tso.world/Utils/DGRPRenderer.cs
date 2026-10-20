using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.Common.Rendering;
using Microsoft.Xna.Framework.Graphics;
using FSO.Files.RC;
using FSO.LotView.Effects;
using FSO.Common.Utils;

namespace FSO.LotView.Utils
{
    public class DGRPRendererItem 
    {
        public _2DStandaloneSprite Sprite;
        public DGRPSprite DGRPSprite;
    }

    /// <summary>
    /// Used for rendering DGRPs in the world.
    /// </summary>
    public class DGRPRenderer : IDisposable
    {
        protected DGRP DrawGroup;
        protected OBJD Source;
        public Rectangle? Bounding;
        public ulong DynamicSpriteFlags = 0x00000000;
        public ulong DynamicSpriteFlags2 = 0x00000000;
        public ushort DynamicSpriteBaseID;
        public ushort NumDynamicSprites;

        public ushort Room;
        public sbyte Level = 1;
        public short ObjectID;

        //2d cache
        private List<DGRPRendererItem> Items = new List<DGRPRendererItem>();

        //3d cache
        private DGRP3DMesh Mesh;
        public Matrix World;
        
        public DGRPRenderer(DGRP group, OBJD source)
        {
            this.DrawGroup = group;
            this.Source = source;
        }

        /// <summary>
        /// Gets or sets the DGRP used by this DGRPRenderer instance.
        /// </summary>
        public DGRP DGRP
        {
            get
            {
                return DrawGroup;
            }
            set
            {
                DrawGroup = value;
                _TextureDirty = true;
                _Dirty = ComponentRenderMode.Both;
            }
        }

        protected ComponentRenderMode _Dirty = ComponentRenderMode.Both;
        private bool _TextureDirty = true;

        private Direction _Direction;

        /// <summary>
        /// Gets or sets the direction that the DGRP is facing.
        /// </summary>
        public Direction Direction
        {
            get{
                return _Direction;
            }
            set{
                _Direction = value;
                _TextureDirty = true;
                _Dirty = ComponentRenderMode.Both;
            }
        }

        private Vector3 _Position;
        public Vector3 Position
        {
            get
            {
                return _Position;
            }
            set
            {
                if (_Position != value) _Dirty = ComponentRenderMode.Both;
                _Position = value;
            }
        }

        /// <summary>SimsRedux: packed colour tint (see ReduxTint). 0 = original colours.</summary>
        public uint Tint;
        /// <summary>SimsRedux: which screenshot the object's screen shows (-1: its own screen).</summary>
        public int ReduxScreen = -1;
        /// <summary>SimsRedux: the whole object's colour profile (null = each picture decides alone).</summary>
        public ReduxTint.Profile TintProfile;
        /// <summary>SimsRedux: the tinted picture couldn't be made yet (still loading).</summary>
        public bool TintPending;

        private float RadianDirection
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

        public bool CanHaveBounds => DGRP != null && Mesh != null && Mesh.Geoms.Count > 0;

        public void InvalidateRotation()
        {
            _TextureDirty = true;
            _Dirty = ComponentRenderMode.Both;
        }

        public void InvalidateZoom()
        {
            _TextureDirty = true;
            _Dirty = ComponentRenderMode.Both;
        }

        public void InvalidateScroll()
        {
            //_Dirty = true;
        }

        public BoundingBox? GetBounds()
        {
            if (_Dirty.IsSet(ComponentRenderMode._3D) && DrawGroup != null)
            {
                Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                _Dirty &= ~ComponentRenderMode._3D;
            }
            return Mesh?.Bounds;
        }

        public virtual void ValidateSprite(WorldState world)
        {
            if (DrawGroup == null) return;
            if (_Dirty.IsSet(ComponentRenderMode._2D))
            {
                if (_TextureDirty)
                {
                    Dispose();
                    Items.Clear();
                    TintPending = false;
                    var direction = (uint)_Direction;

                    /** Compute the direction **/
                    var image = DrawGroup.GetImage(direction, (uint)world.Zoom, (uint)world.Rotation);
                    int maxX = int.MinValue, maxY = int.MinValue;
                    int minX = int.MaxValue, minY = int.MaxValue;
                    if (image != null)
                    {
                        foreach (var dgrpSprite in image.Sprites)
                        {
                            if (dgrpSprite == null) continue;
                            var texture = world._2D.GetWorldTexture(dgrpSprite);
                            if (texture.Pixel == null) { continue; }

                            var isDynamic = dgrpSprite.SpriteID >= DynamicSpriteBaseID && dgrpSprite.SpriteID < (DynamicSpriteBaseID + NumDynamicSprites);
                            if (isDynamic)
                            {
                                var dynamicIndex = (ushort)(dgrpSprite.SpriteID - DynamicSpriteBaseID);
                                
                                var isVisible = (dynamicIndex > 63) ? ((DynamicSpriteFlags2 & ((ulong)0x1 << (dynamicIndex-64))) > 0):
                                    ((DynamicSpriteFlags & ((ulong)0x1 << dynamicIndex)) > 0);
                                if (!isVisible)
                                {
                                    continue;
                                }
                            }
                            var sprite = new _2DStandaloneSprite(); //do not use sprite pool for DGRP, since we can reliably remember our own sprites.
                            sprite.Pixel = texture.Pixel;
                            // SimsRedux: colour tint
                            if (Tint != 0)
                            {
                                bool pending;
                                sprite.Pixel = ReduxTint.GetTinted(world.Device, dgrpSprite, texture.Pixel, Tint, TintProfile, out pending);
                                if (pending) TintPending = true;
                            }
                            // SimsRedux: a picture on the computer's screen (see ReduxScreens)
                            if (ReduxScreen >= 0)
                            {
                                var screen = ReduxScreens.Get(world.Device, dgrpSprite, sprite.Pixel, ReduxScreen);
                                if (screen != null) sprite.Pixel = screen;
                            }
                            sprite.Depth = texture.ZBuffer;
                            if (texture.ZBuffer != null)
                            {
                                sprite.RenderMode = _2DBatchRenderMode.Z_BUFFER;
                                sprite.WorldPosition = dgrpSprite.ObjectOffset;
                            }
                            else
                            {
                                sprite.RenderMode = _2DBatchRenderMode.NO_DEPTH;
                            }

                            var pt = ((TextureInfo)sprite.Pixel.Tag).Size;
                            sprite.SrcRect = new Rectangle(0, 0, pt.X, pt.Y);
                            sprite.DestRect = new Rectangle(0, 0, pt.X, pt.Y);
                            sprite.FlipHorizontally = dgrpSprite.Flip;
                            sprite.ObjectID = ObjectID;

                            Items.Add(new DGRPRendererItem { Sprite = sprite, DGRPSprite = dgrpSprite });

                            var pxX = (world.WorldSpace.CadgeWidth / 2.0f) + dgrpSprite.SpriteOffset.X;
                            var pxY = (world.WorldSpace.CadgeBaseLine - sprite.DestRect.Height) + dgrpSprite.SpriteOffset.Y;

                            if (dgrpSprite.ObjectOffset != Vector3.Zero) { }
                            var centerRelative = dgrpSprite.ObjectOffset * new Vector3(1f / 16f, 1f / 16f, 1f / 5f);
                            centerRelative = Vector3.Transform(centerRelative, Matrix.CreateRotationZ(RadianDirection));

                            var pxOff = world.WorldSpace.GetScreenFromTile(centerRelative);

                            sprite.DestRect.X = (int)(pxX + pxOff.X);
                            sprite.DestRect.Y = (int)(pxY + pxOff.Y);

                            if (sprite.DestRect.X < minX) minX = sprite.DestRect.X;
                            if (sprite.DestRect.Y < minY) minY = sprite.DestRect.Y;
                            if (sprite.DestRect.X + sprite.Pixel.Width > maxX) maxX = sprite.DestRect.X + sprite.Pixel.Width;
                            if (sprite.DestRect.Y + sprite.Pixel.Height > maxY) maxY = sprite.DestRect.Y + sprite.Pixel.Height;

                            sprite.WorldPosition = centerRelative * 3f;
                            var y = sprite.WorldPosition.Z;
                            sprite.WorldPosition.Z = sprite.WorldPosition.Y;
                            sprite.WorldPosition.Y = y;
                            sprite.Room = ((dgrpSprite.Flags & DGRPSpriteFlags.Luminous) > 0 && Room != 65534 && Room != 65533) ? (ushort)65535 : Room;
                            sprite.Floor = Level;
                        }
                    }

                    ReduxFlatDepth(world);
                    _TextureDirty = false;
                    Bounding = new Rectangle(minX, minY, maxX - minX, maxY - minY);
                }

                Rectangle? bounding = null;
                foreach (var item in Items) {
                    item.Sprite.AbsoluteDestRect = item.Sprite.DestRect;
                    item.Sprite.AbsoluteDestRect.Offset(world.WorldSpace.GetScreenFromTile(_Position));
                    if (bounding == null) bounding = item.Sprite.AbsoluteDestRect;
                    else bounding = Rectangle.Union(bounding.Value, item.Sprite.AbsoluteDestRect);
                    item.Sprite.AbsoluteWorldPosition = item.Sprite.WorldPosition + WorldSpace.GetWorldFromTile(_Position);
                    item.Sprite.PrepareVertices(world.Device);
                }
                Bounding = bounding ?? new Rectangle();

                _Dirty &= ~ComponentRenderMode._2D;
            }
        }

        /// <summary>
        /// SimsRedux: a flat object lying on the floor (a rug, a mat) whose depth picture puts it deeper than the
        /// game's own rugs is drawn with a depth made for it instead: the floor's own depth at each row, as far in
        /// front of the floor as the game's rugs are (about 7 steps). The Sims Online's braided rag rug sits right
        /// on the floor line, 7 steps deeper than every other rug, and the floor then hid parts of it - horizontal
        /// streaks and missing pieces. Only objects that are clearly flat on the floor are touched: no picture rises
        /// more than a few pixels above the tile's back corner, the pictures reach into the tile's front half, and
        /// each picture's depth runs along the floor (it slopes like the floor, row by row, close to the floor line).
        /// Everything else - and every rug that already sits where the game's rugs do - keeps its own depth.
        /// </summary>
        private void ReduxFlatDepth(WorldState world)
        {
            try
            {
                if (Items.Count == 0) return;
                int div = 1 << Math.Max(0, Math.Min(2, (int)WorldZoom.Near - (int)world.Zoom)); // (near pixels per pixel)
                int minTop = int.MaxValue, maxBottom = int.MinValue;
                foreach (var item in Items)
                {
                    var spr = item.Sprite;
                    if (spr.RenderMode != _2DBatchRenderMode.Z_BUFFER || spr.Depth == null || spr.Pixel == null) return;
                    if (item.DGRPSprite == null || item.DGRPSprite.ObjectOffset.Z != 0) return;
                    // the picture's top, relative to the object's own ground point (the tile's back corner is 32 near pixels up)
                    int top = (int)item.DGRPSprite.SpriteOffset.Y - spr.DestRect.Height;
                    minTop = Math.Min(minTop, top * div);
                    maxBottom = Math.Max(maxBottom, (top + spr.DestRect.Height) * div);
                }
                if (minTop < -40 || maxBottom < 16) return;

                bool tooDeep = false;
                foreach (var item in Items)
                {
                    var frame = item.DGRPSprite.GetSPR2Frame();
                    if (frame == null) return;
                    if (frame.ReduxZCount < 0) frame.DecodeIfRequired(true); // (worked out when it's decoded)
                    if (frame.ReduxZCount <= 0) return;
                    int h = item.Sprite.DestRect.Height;
                    int top = (int)item.DGRPSprite.SpriteOffset.Y - h;
                    float floorSlope = -TextureGenerator.FLAT_Z_INC * div;
                    if (Math.Abs(frame.ReduxZSlope - floorSlope) > 0.25f * -floorSlope) return; // not sloping like the floor
                    if (frame.ReduxZRms > 3 + div) return; // bumpy: a real 3D shape
                    float mid = h / 2f;
                    float dev = (frame.ReduxZAt0 + frame.ReduxZSlope * mid) - ReduxFloorZ(top + mid, div);
                    if (dev < -16 || dev > 8) return; // not at floor level (on a counter, in the air)
                    if (dev > -3) tooDeep = true; // (rugs sit 4-11 steps in front of the floor line; this one is on it)
                }
                if (!tooDeep) return;

                foreach (var item in Items)
                {
                    var spr = item.Sprite;
                    var depth = ReduxFlatDepthTexture(world.Device, spr.Pixel.Height, (int)item.DGRPSprite.SpriteOffset.Y - spr.DestRect.Height, div);
                    if (depth != null) spr.Depth = depth;
                }
            }
            catch (Exception) { } // (the object keeps its own depth)
        }

        /// <summary>SimsRedux: the floor's depth step at a row (pixels from the object's ground point, at this zoom).</summary>
        private static float ReduxFloorZ(float row, int div)
        {
            return 255 - TextureGenerator.FLAT_Z_INC * (div * row + 32);
        }

        private static Dictionary<long, Texture2D> ReduxFlatDepthCache = new Dictionary<long, Texture2D>();

        /// <summary>
        /// A depth picture (one pixel wide - every column is the same) for a picture <paramref name="height"/> rows
        /// tall whose top row is <paramref name="top"/> pixels from the object's ground point: the floor's depth at
        /// each row, 7 steps nearer - where the game's own rugs sit.
        /// </summary>
        private static Texture2D ReduxFlatDepthTexture(GraphicsDevice gd, int height, int top, int div)
        {
            if (gd == null || height <= 0 || height > 4096) return null;
            long key = ((long)height << 32) | ((long)(top & 0xFFFFFF) << 4) | (long)div;
            lock (ReduxFlatDepthCache)
            {
                Texture2D tex;
                if (ReduxFlatDepthCache.TryGetValue(key, out tex) && tex != null && !tex.IsDisposed) return tex;
                var data = new Color[height];
                for (int y = 0; y < height; y++)
                {
                    var z = (byte)Math.Max(0, Math.Min(254, Math.Round(ReduxFloorZ(top + y, div) - 7)));
                    data[y] = new Color(z, z, z, z);
                }
                tex = new Texture2D(gd, 1, height);
                tex.SetData(data);
                ReduxFlatDepthCache[key] = tex;
                return tex;
            }
        }

        public virtual void Draw(WorldState world)
        {
            ValidateSprite(world);

            foreach (var item in Items)
            {
                world._2D.Draw(item.Sprite);
            }
        }

        public virtual void DrawImmediate(WorldState world)
        {
            ValidateSprite(world);

            foreach (var item in Items)
            {
                world._2D.DrawImmediate(item.Sprite);
            }
        }

        public void Draw3D(WorldState world)
        {
            if (DrawGroup == null) return;
            if (_Dirty.IsSet(ComponentRenderMode._3D) || Mesh == null)
            {
                Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                _Dirty &= ~ComponentRenderMode._3D;
            }

            //immedately draw the mesh.
            var device = world.Device;
            var effect = WorldContent.RCObject;

            effect.World = World;
            effect.Level = (float)(Level - 0.999f);
            var advDir = WorldConfig.Current.Directional && WorldConfig.Current.AdvancedLighting;

            if (Mesh.DepthMask != null)
            {
                var geom = Mesh.DepthMask;
                //depth mask for drawing into a surface or wall
                if (geom.Verts != null)
                {
                    effect.SetTechnique(RCObjectTechniques.DepthClear);
                    effect.CurrentTechnique.Passes[0].Apply();

                    device.DepthStencilState = DepthClear1;
                    device.Indices = geom.Indices;
                    device.SetVertexBuffer(geom.Verts);

                    device.BlendState = NoColor;
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);

                    device.DepthStencilState = (Mesh.MaskType == DGRP3DMaskType.Portal) ? DepthClear2Strict : DepthClear2;
                    effect.CurrentTechnique.Passes[1].Apply();
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);

                    device.DepthStencilState = DepthStencilState.Default;
                    device.BlendState = BlendState.NonPremultiplied;
                    effect.SetTechnique(RCObjectTechniques.Draw);
                }
            }

            if (Room == 65533) effect.SetTechnique(RCObjectTechniques.DisabledDraw);

            int i = 0;
            foreach (var spr in Mesh.Geoms)
            {
                if (i == 0 || (((i - 1) > 63) ? ((DynamicSpriteFlags2 & ((ulong)0x1 << ((i - 1) - 64))) > 0) :
                    ((DynamicSpriteFlags & ((ulong)0x1 << (i - 1))) > 0)) || (Mesh.MaskType == DGRP3DMaskType.Portal && i == Mesh.Geoms.Count - 1))
                {
                    foreach (var geom in spr.Values)
                    {
                        if (geom.PrimCount == 0) continue;
                        if (Mesh.MaskType == DGRP3DMaskType.Portal && i == Mesh.Geoms.Count - 1)
                            device.DepthStencilState = Portal;
                        effect.MeshTex = geom.Pixel;
                        var info = geom.Pixel?.Tag as TextureInfo;
                        effect.UVScale = info?.UVScale ?? Vector2.One;
                        var pass = effect.CurrentTechnique.Passes[(advDir && Room < 65533) ? 1 : 0];
                        pass.Apply();
                        if (geom.Rendered)
                        {
                            device.Indices = geom.Indices;
                            device.SetVertexBuffer(geom.Verts);

                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);
                        }
                        if (Mesh.MaskType == DGRP3DMaskType.Portal && i == Mesh.Geoms.Count - 1)
                            device.DepthStencilState = DepthStencilState.Default;
                    }
                }
                i++;
            }

            if (Mesh.MaskType == DGRP3DMaskType.Portal)
            {
                var geom = Mesh.DepthMask;
                //clear the stencil, so it doesn't interfere with future portals.
                if (geom.Verts != null)
                {
                    effect.SetTechnique(RCObjectTechniques.DepthClear);
                    effect.CurrentTechnique.Passes[1].Apply();

                    device.DepthStencilState = StencilClearOnly;
                    device.Indices = geom.Indices;
                    device.SetVertexBuffer(geom.Verts);

                    device.BlendState = NoColor;
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);
                    device.BlendState = BlendState.NonPremultiplied;
                }
                device.DepthStencilState = DepthStencilState.Default;
                effect.SetTechnique(RCObjectTechniques.Draw);
            }
            if (Room == 65533) effect.SetTechnique(RCObjectTechniques.Draw);
        }

        public void DrawLMap(GraphicsDevice device, sbyte level, float yOff)
        {
            if (DrawGroup == null) return;
            if (_Dirty.IsSet(ComponentRenderMode._3D))
            {
                Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                _Dirty &= ~ComponentRenderMode._3D;
            }

            if (Mesh.MaskType == DGRP3DMaskType.Portal) return;
            //immedately draw the mesh.
            var effect = WorldContent.RCObject;

            var mat = World;
            mat.M42 = ((Level - level) - 1) * 2.95f + yOff; //set y translation to 0
            effect.World = mat;

            int i = 0;
            foreach (var spr in Mesh.Geoms)
            {
                if (i == 0 || (((i - 1) > 63) ? ((DynamicSpriteFlags2 & ((ulong)0x1 << ((i - 1) - 64))) > 0) :
                    ((DynamicSpriteFlags & ((ulong)0x1 << (i - 1))) > 0)))
                {
                    foreach (var geom in spr.Values)
                    {
                        if (geom.PrimCount == 0) continue;
                        foreach (var pass in effect.CurrentTechnique.Passes)
                        {
                            pass.Apply();
                            if (!geom.Rendered) continue;
                            device.Indices = geom.Indices;
                            device.SetVertexBuffer(geom.Verts);

                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geom.PrimCount);
                        }
                    }
                }
                i++;
            }
        }

        public virtual void Preload(WorldState world, ComponentRenderMode mode)
        {
            if (mode.IsSet(ComponentRenderMode._2D) && world.CameraMode != CameraRenderMode._3D)
                ValidateSprite(world);

            if (mode.IsSet(ComponentRenderMode._3D) || world.CameraMode != CameraRenderMode._2D || WorldConfig.Current.UltraLighting)
            {
                if (_Dirty.IsSet(ComponentRenderMode._3D))
                {
                    Mesh = Content.Content.Get().RCMeshes.Get(DrawGroup, Source);
                    _Dirty &= ~ComponentRenderMode._3D;
                }
            }
        }

        public void Dispose()
        {
            foreach (var item in Items)
            {
                item.Sprite.Dispose();
            }
        }

        #region 3D GPU States

        //depth clear mask
        //how it works:
        //pass 1: draw mask to stencil 1 with normal depth rules. no depth write.
        //pass 2: draw mask where stencil 1 exists with max far depth. depth write override, stencil clear.
        //pass 3: draw object normally

        public static DepthStencilState DepthClear1 = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Always,

            TwoSidedStencilMode = true,

            CounterClockwiseStencilFail = StencilOperation.Keep,
            CounterClockwiseStencilPass = StencilOperation.Replace,
            CounterClockwiseStencilDepthBufferFail = StencilOperation.Keep,

            StencilDepthBufferFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Zero,
            StencilFail = StencilOperation.Keep,

            ReferenceStencil = 1,
            DepthBufferWriteEnable = true
        };

        public static DepthStencilState DepthClear2 = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Zero,
            StencilDepthBufferFail = StencilOperation.Zero,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = true,
            DepthBufferFunction = CompareFunction.Always
        };

        public static DepthStencilState DepthClear2Strict = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Keep,
            StencilDepthBufferFail = StencilOperation.Keep,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = true,
            DepthBufferFunction = CompareFunction.Always
        };


        public static DepthStencilState Portal = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Keep,
            StencilDepthBufferFail = StencilOperation.Keep,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = true,
        };

        public static DepthStencilState StencilClearOnly = new DepthStencilState()
        {
            StencilEnable = true,
            StencilFunction = CompareFunction.Equal,
            StencilFail = StencilOperation.Keep,
            StencilPass = StencilOperation.Zero,
            StencilDepthBufferFail = StencilOperation.Keep,
            ReferenceStencil = 1,
            DepthBufferWriteEnable = false,
            DepthBufferFunction = CompareFunction.Always
        };

        public static BlendState NoColor = new BlendState()
        {
            ColorWriteChannels = ColorWriteChannels.None
        };

        #endregion
    }
}
