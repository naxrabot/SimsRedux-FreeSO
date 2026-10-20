using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Common.Rendering.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using FSO.Content.Model;
using FSO.Vitaboy.Model;
using FSO.Files.RC;

namespace FSO.Vitaboy
{
    /// <summary>
    /// The base class for all avatars in the game.
    /// </summary>
    public abstract class Avatar : _3DComponent 
    {
        public List<AvatarBindingInstance> Bindings = new List<AvatarBindingInstance>();
        public static Effect Effect;
        public Skeleton Skeleton { get; set; }
        public Skeleton BaseSkeleton { get; set; }
        public List<Vector2> LightPositions;
        public DGRP3DMesh HeadObject;
        public float HeadObjectRotation;
        public float HeadObjectSpeedyVel;
        public bool HideHead;
        protected Matrix[] SkelBones;

        public static void setVitaboyEffect(Effect e) {
            Effect = e;
        }

        /// <summary>
        /// Creates a new Avatar instance.
        /// </summary>
        /// <param name="skel">A Skeleton instance.</param>
        public Avatar(Skeleton skel)
        {
            this.Skeleton = skel?.Clone();
            this.BaseSkeleton = skel?.Clone(); //keep a copy we can revert back to
        }

        public Avatar(Avatar old)
        {
            this.BaseSkeleton = old.BaseSkeleton.Clone();
            this.Skeleton = old.BaseSkeleton.Clone();
            for (int i = 0; i < old.Bindings.Count(); i++)
            {
                AvatarBindingInstance oldb = old.Bindings[i];
                Bindings.Add(new AvatarBindingInstance()
                {
                    Mesh = oldb.Mesh,
                    Texture = oldb.Texture,
                    TexName = oldb.TexName,
                    MeshName = oldb.MeshName
                });
            }
            for (int i = 0; i < old.Accessories.Count(); i++)
            {
                this.Accessories.Add(old.Accessories.Keys.ElementAt(i), old.Accessories.Values.ElementAt(i));
            } //just shallow copy the binding and accessory list, as the data inside isn't going to change any time soon...
        }

        private Dictionary<Appearance, AvatarAppearanceInstance> Accessories = new Dictionary<Appearance, AvatarAppearanceInstance>();
        
        /// <summary>
        /// Adds an accessory to this avatar.
        /// </summary>
        /// <param name="apr">The Appearance instance of the accessory.</param>
        public void AddAccessory(Appearance apr)
        {
            if (Accessories.ContainsKey(apr))
            {
                return;
            }

            var add = AddAppearance(apr, null);
            Accessories.Add(apr, add);
        }

        /// <summary>
        /// Remove an accessory from this avatar.
        /// </summary>
        /// <param name="apr">The Appearance of the accessory to remove.</param>
        public void RemoveAccessory(Appearance apr)
        {
            if (apr == null) return;
            if (Accessories.ContainsKey(apr))
            {
                RemoveAppearance(Accessories[apr], true);
                Accessories.Remove(apr);
            }
        }

        private bool GPUMode;
        private GraphicsDevice GPUDevice;
        public void StoreOnGPU(GraphicsDevice device)
        {
            GPUMode = true;
            GPUDevice = device;
            lock (Bindings)
            {
                foreach (var binding in Bindings)
                {
                    binding.Mesh.StoreOnGPU(device);
                    binding.SafeTexture(device);
                }
            }
        }

        private string UniformName(string name)
        {
            return name.ToLowerInvariant().Replace("lgt", "").Replace("med", "").Replace("drk", "");
        }

        /// <summary>
        /// Adds an Appearance instance to this avatar.
        /// </summary>
        /// <param name="appearance">The Appearance instance to add.</param>
        /// <returns>An AvatarAppearanceInstance instance.</returns>
        protected AvatarAppearanceInstance AddAppearance(Appearance appearance, string texOverride)
        {
            var result = new AvatarAppearanceInstance();
            result.Original = appearance;
            result.Bindings = new List<AvatarBindingInstance>();
            // SimsRedux: an outfit whose files are missing (a custom outfit's mesh or texture) shows
            // nothing for that part instead of stopping the game
            if (appearance?.Bindings == null) return result;
            // SimsRedux: a Sims Online outfit comes in its own version for each skin tone (its texture name says
            // which) - that version already has every part's texture, so nothing is swapped in below
            if (Content.Content.Get().TS1)
            {
                Appearance skinned = null;
                try { skinned = FSO.Content.TS1.ReduxTSOAvatars.ForSkin(appearance, texOverride); } catch { }
                if (skinned?.Bindings != null) { appearance = skinned; texOverride = null; }
            }

            int i = 0;
            int replaced = 0;
            // SimsRedux: a custom outfit can have an empty or broken part list - those parts are skipped
            var realBindings = appearance.Bindings.Select(bindingReference =>
            {
                if (bindingReference == null) return null;
                try { return bindingReference.RealBinding ?? FSO.Content.Content.Get().AvatarBindings?.Get(bindingReference.TypeID, bindingReference.FileID); }
                catch { return null; }
            }).ToList();

            if (Content.Content.Get().TS1)
            {
                foreach (var binding in realBindings)
                {
                    if (binding == null || binding.MeshName == null) { i++; continue; }
                    Mesh mesh;
                    try { mesh = Content.Content.Get().AvatarMeshes.Get(binding.MeshName); } catch { mesh = null; }
                    if (mesh?.TextureName == null) { i++; continue; } // (SimsRedux: its mesh is missing)
                    if (texOverride != null &&
                            (UniformName(mesh.TextureName.ToLowerInvariant()).EndsWith(UniformName(texOverride.ToLowerInvariant()))
                            || mesh.TextureName.ToLowerInvariant() == "x"))
                    {
                        replaced = i;
                    }
                    i++;
                }

                if (texOverride != null && replaced < realBindings.Count && realBindings[replaced] != null)
                {
                    realBindings[replaced] = realBindings[replaced].TS1Copy();
                    realBindings[replaced].TextureName = texOverride;
                }
            }

            foreach (var binding in realBindings)
            {
                if (binding == null) { continue; }
                // SimsRedux: a part whose mesh file is missing is left off (it used to stop the game)
                try { if (binding.MeshName != null && binding.TextureName == null && FSO.Content.Content.Get().AvatarMeshes.Get(binding.MeshName) == null) continue; }
                catch { continue; }
                AvatarBindingInstance inst;
                try { inst = AddBinding(binding); }
                catch (System.Exception e) { System.Console.WriteLine("[SimsRedux] outfit part skipped (" + binding.MeshName + "): " + e.Message); continue; }
                result.Bindings.Add(inst);
            }

            return result;
        }

        /// <summary>
        /// Removes an Appearance instance from this avatar.
        /// </summary>
        /// <param name="appearance">The Appearance instance to remove.</param>
        /// <param name="dispose">Should the appearance be disposed?</param>
        public void RemoveAppearance(AvatarAppearanceInstance appearance, bool dispose)
        {
            lock (Bindings)
            {
                if (appearance == null) return;
                foreach (var binding in appearance.Bindings)
                {
                    RemoveBinding(binding, dispose);
                }
            }
        }

        /// <summary>
        /// Adds a Binding instance to this avatar.
        /// </summary>
        /// <param name="binding">The Binding instance to add.</param>
        /// <returns>An AvatarBindingInstance instance.</returns>
        protected AvatarBindingInstance AddBinding(Binding binding)
        {
            var content = FSO.Content.Content.Get();
            var instance = new AvatarBindingInstance();
            if (binding.MeshName != null)
            {
                instance.Mesh = content.AvatarMeshes.Get(binding.MeshName);
                instance.TexName = binding.TextureName ?? instance.Mesh?.TextureName;
                instance.MeshName = binding.MeshName;
                instance.Texture = content.AvatarTextures.Get(binding.TextureName ?? instance.Mesh.TextureName);
                // SimsRedux: a texture that's gone (a removed download) - another for the same outfit,
                // not a pitch-black Sim
                if (instance.Texture == null && content.TS1 && instance.Mesh != null)
                {
                    var alt = ReduxAvatarFallback.Texture(binding.TextureName ?? instance.Mesh.TextureName, binding.MeshName, instance.Mesh.TextureName);
                    if (alt != null) instance.Texture = content.AvatarTextures.Get(alt);
                }
            }
            else
            {
                instance.Mesh = content.AvatarMeshes.Get(binding.MeshTypeID, binding.MeshFileID);
                instance.Texture = content.AvatarTextures.Get(binding.TextureTypeID, binding.TextureFileID);
            }

            /*if (instance.Mesh != null)
            {
                //We make a copy so we can modify it, most of the variables
                //are kept as pointers because we only change a few locals
                //per sim, the rest are global
                instance.Mesh = instance.Mesh.Clone();
            }*/

            instance.Mesh.Prepare(Skeleton.RootBone);

            
            /*if (GPUMode)
            {
                instance.Mesh.StoreOnGPU(GPUDevice);
                instance.Texture.Get(GPUDevice);
            }*/
            
            lock (Bindings)
            {
                Bindings.Add(instance);
            }
            return instance;
        }

        /// <summary>
        /// Removes a Binding instance from this avatar.
        /// </summary>
        /// <param name="instance">The Binding instance to remove.</param>
        /// <param name="dispose">Should the binding be disposed?</param>
        protected void RemoveBinding(AvatarBindingInstance instance, bool dispose)
        {
            lock (Bindings)
            {
                Bindings.Remove(instance);
            }
        }

        /// <summary>
        /// Initializes this Avatar instance.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();
        }

        /// <summary>
        /// When the skeleton changes (for example due to an animation) this
        /// method will recompute the meshes to adhere to the new skeleton positions.
        /// </summary>
        public void ReloadSkeleton()
        {
            if (Skeleton == null) return;
            Skeleton.ComputeBonePositions(Skeleton.RootBone, Matrix.Identity);
            SkelBones = new Matrix[Skeleton.Bones.Length];
            for (int i = 0; i < Skeleton.Bones.Length; i++)
            {
                SkelBones[i] = Skeleton.Bones[i].AbsoluteMatrix;
            }
        }

        /// <summary>
        /// Updates this Avatar instance.
        /// </summary>
        /// <param name="state">An UpdateState instance.</param>
        public override void Update(FSO.Common.Rendering.Framework.Model.UpdateState state)
        {
        }

        public static int DefaultTechnique = 0;
        public Vector4 AmbientLight = Vector4.One;

        /// <summary>
        /// Draws the meshes making up this Avatar instance.
        /// </summary>
        /// <param name="device">A GraphicsDevice instance.</param>
        public override void Draw(Microsoft.Xna.Framework.Graphics.GraphicsDevice device)
        {
            Effect.CurrentTechnique = Effect.Techniques[DefaultTechnique];
            var p = ParamsFor(Effect);
            p.View?.SetValue(View);
            p.Projection?.SetValue(Projection);
            p.World?.SetValue(World);
            p.AmbientLight?.SetValue(AmbientLight);

            DrawGeometry(device, Effect);
        }

        // SimsRedux: the effect's parameters, looked up by name once per effect instead of for every mesh of every
        // Sim every frame (the lookup goes through the parameter list comparing names)
        private class EffectParams
        {
            public Effect Effect;
            public EffectParameter View, Projection, World, AmbientLight, SkelBindings, MeshTex, LightPosition;
        }
        private static EffectParams LastParams;
        private static EffectParams ParamsFor(Effect effect)
        {
            var p = LastParams;
            if (p != null && p.Effect == effect) return p;
            var ps = effect.Parameters;
            p = new EffectParams
            {
                Effect = effect,
                View = ps["View"], Projection = ps["Projection"], World = ps["World"], AmbientLight = ps["AmbientLight"],
                SkelBindings = ps["SkelBindings"], MeshTex = ps["MeshTex"], LightPosition = ps["LightPosition"]
            };
            LastParams = p;
            return p;
        }

        public void DrawHeadOnly(Microsoft.Xna.Framework.Graphics.GraphicsDevice device, Effect effect, Matrix headMatrix)
        {
            //Effect.CurrentTechnique = Effect.Techniques[0];
            if (SkelBones == null) ReloadSkeleton();
            var matrixCopy = new Matrix[SkelBones.Length];
            // Locate the head bone and inject the custom matrix.

            for (int i = 0; i < Skeleton.Bones.Length; i++)
            {
                var bone = Skeleton.Bones[i];

                if (bone.Name == "HEAD") matrixCopy[i] = headMatrix;
            }

            effect.Parameters["SkelBindings"]?.SetValue(matrixCopy);

            lock (Bindings)
            {
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    foreach (var binding in Bindings)
                    {
                        effect.Parameters["MeshTex"]?.SetValue(binding.SafeTexture(device));
                        pass.Apply();
                        binding.Mesh.Draw(device);
                    }
                }
            }
        }

        public void DrawGeometry(Microsoft.Xna.Framework.Graphics.GraphicsDevice device, Effect effect)
        {
            //Effect.CurrentTechnique = Effect.Techniques[0];
            if (SkelBones == null) ReloadSkeleton();
            var prm = ParamsFor(effect);
            prm.SkelBindings?.SetValue(SkelBones);

            lock (Bindings)
            {
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    foreach (var binding in Bindings)
                    {
                        if (HideHead && binding.Mesh.BoneBindings.Any(meshBind => meshBind.BoneName == "HEAD"))
                        {
                            continue;
                        }

                        prm.MeshTex?.SetValue(binding.SafeTexture(device));
                        pass.Apply();
                        binding.Mesh.Draw(device);
                    }
                }
            }

            //skip drawing shadows if we're drawing id
            if (effect.CurrentTechnique == effect.Techniques[1]) return;
            // SimsRedux: with Lighting off there are no shadows to draw, but the head object (the plumbob above
            // the selected Sim) still is - it used to be skipped along with the shadows
            if (LightPositions == null) { DrawHeadObject(device, effect); return; }

            if (ShadBuf == null)
            {
                var shadVerts = new ShadowVertex[]
                {
                new ShadowVertex(new Vector3(-1, 0, -1), 25),
                new ShadowVertex(new Vector3(-1, 0, 1), 25),
                new ShadowVertex(new Vector3(1, 0, 1), 25),
                new ShadowVertex(new Vector3(1, 0, -1), 25),

                new ShadowVertex(new Vector3(-1, 0, -1), 19),
                new ShadowVertex(new Vector3(-1, 0, 1), 19),
                new ShadowVertex(new Vector3(1, 0, 1), 19),
                new ShadowVertex(new Vector3(1, 0, -1), 19)
                };
                for (int i = 0; i < shadVerts.Length; i++) shadVerts[i].Position *= 1f;
                int[] shadInd = new int[] { 2, 1, 0, 2, 0, 3, 6, 5, 4, 6, 4, 7 };

                ShadBuf = new VertexBuffer(device, typeof(ShadowVertex), shadVerts.Length, BufferUsage.None);
                ShadBuf.SetData(shadVerts);
                ShadIBuf = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, shadInd.Length, BufferUsage.None);
                ShadIBuf.SetData(shadInd);
            }

            foreach (var light in LightPositions)
            {
                //effect.Parameters["FloorHeight"]?.SetValue((float)(Math.Floor(Position.Y/2.95)*2.95 + 0.05));
                prm.LightPosition?.SetValue(light);
                var oldTech = effect.CurrentTechnique;
                effect.CurrentTechnique = effect.Techniques[4];
                effect.CurrentTechnique.Passes[0].Apply();
                device.DepthStencilState = DepthStencilState.DepthRead;
                device.SetVertexBuffer(ShadBuf);
                device.Indices = ShadIBuf;
                device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, 4);
                effect.CurrentTechnique = oldTech;
                device.DepthStencilState = DepthStencilState.Default;
            }

            DrawHeadObject(device, effect);
        }

        public void DrawHeadObject(GraphicsDevice device, Effect effect)
        {
            //0: high reflective
            //1: light reflective
            //2: outline
            if (effect.Techniques[1] == effect.CurrentTechnique) return;
            var headObj = HeadObject;
            if (headObj == null) return;
            var oldTech = effect.CurrentTechnique;
            effect.CurrentTechnique = effect.Techniques[6];
            device.RasterizerState = RasterizerState.CullClockwise;

            var trans = Matrix.Invert(effect.Parameters["View"].GetValueMatrix()).Translation;
            effect.Parameters["HOCameraPosition"]?.SetValue(trans);
            effect.Parameters["World"]?.SetValue(Matrix.CreateScale(1.33f) * Matrix.CreateRotationY(HeadObjectRotation) * Matrix.CreateTranslation(Skeleton.GetBone("HEAD").AbsoluteMatrix.Translation + new Vector3(0, 3.25f, 0)) * effect.Parameters["World"].GetValueMatrix());

            for (int i=0; i<headObj.Geoms.Count; i++)
            {
                var multi = 0.5f / (headObj.Geoms.Count - 1);
                var geom = headObj.Geoms[i];
                foreach (var item in geom)
                {
                    effect.Parameters["MeshTex"]?.SetValue(item.Key);
                    effect.Parameters["HOToonSpecThresh"]?.SetValue(0.5f);
                    effect.Parameters["HOToonSpecColor"]?.SetValue(new Vector3(multi * ((headObj.Geoms.Count-1) - i)));

                    effect.CurrentTechnique.Passes[0].Apply();
                    //if (i != 1) device.RasterizerState = RasterizerState.CullClockwise;
                    device.SetVertexBuffer(item.Value.Verts);
                    device.Indices = item.Value.Indices;
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, item.Value.PrimCount);
                    //if (i != 1) device.RasterizerState = RasterizerState.CullCounterClockwise;
                }
            }
            device.RasterizerState = RasterizerState.CullCounterClockwise;
            effect.CurrentTechnique = oldTech;
        }

        // TODO: memory leak
        private static VertexBuffer ShadBuf;
        private static IndexBuffer ShadIBuf;

        public override void DeviceReset(GraphicsDevice Device)
        {
        }
    }

    /// <summary>
    /// Holds bindings for an avatar.
    /// </summary>
    public class AvatarAppearanceInstance
    {
        public Appearance Original;
        public List<AvatarBindingInstance> Bindings;
    }

    /// <summary>
    /// Holds a mesh and texture for an avatar.
    /// </summary>
    public class AvatarBindingInstance 
    {
        public Mesh Mesh;
        public ITextureRef Texture;

        // SimsRedux: the texture's name, so a texture that fails to load can be found again
        public string TexName;
        public string MeshName;
        private int Fails;
        private int RetryIn;
        private bool CheckedBlack, UsePlain;

        /// <summary>
        /// SimsRedux: this binding's texture, ready to draw. A texture that failed to load (a file read
        /// that didn't work that moment) or was thrown away by the graphics card is loaded again
        /// instead of the Sim staying black (a few tries, then the outfit's fallback texture).
        /// </summary>
        public Texture2D SafeTexture(GraphicsDevice device)
        {
            if (Texture == null && (TexName == null || TexName == "x")) return null;
            if (RetryIn > 0) { RetryIn--; return ReduxAvatarFallback.Plain(device, TexName, MeshName); }
            if (UsePlain) return ReduxAvatarFallback.Plain(device, TexName, MeshName);
            Texture2D tex = null;
            string why = null;
            try { tex = Texture?.Get(device); }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            if (tex != null && !tex.IsDisposed)
            {
                // (once: a picture that came out completely black is as good as missing)
                // (SimsRedux: only when the picture can be read back without wiping the target being drawn into -
                //  otherwise it's checked on a later draw)
                if (!CheckedBlack && FSO.Common.Utils.TextureUtils.CanReadBackNow(device))
                {
                    CheckedBlack = true;
                    if (ReduxAvatarFallback.AllBlack(tex)) { why = "came out black"; tex = null; Texture = null; Fails = Math.Max(Fails, 3); }
                    else return tex;
                }
                else return tex;
            }
            if (why == null) why = Texture == null ? "not found" : (tex == null ? "empty" : "thrown away");

            Fails++;
            ReduxAvatarFallback.TextureProblem(TexName, why, Fails);
            try
            {
                var content = FSO.Content.Content.Get();
                if (Fails <= 3 && TexName != null) Texture = content.AvatarTextures.Get(TexName);
                else
                {
                    var alt = ReduxAvatarFallback.Texture(TexName, MeshName, Mesh?.TextureName);
                    if (alt != null) Texture = content.AvatarTextures.Get(alt);
                }
                tex = Texture?.Get(device);
                if (tex != null && !tex.IsDisposed) return tex;
            }
            catch (Exception) { }
            RetryIn = Math.Min(600, 30 * Fails); // (don't try every frame)
            // the last resort: a plain picture in the right colours, never a black Sim
            if (Fails >= 5) { UsePlain = true; return ReduxAvatarFallback.Plain(device, TexName, MeshName); }
            return ReduxAvatarFallback.Plain(device, TexName, MeshName);
        }
    }
}
