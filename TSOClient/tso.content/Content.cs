using System.Collections.Generic;
using System.IO;
using FSO.Files.FAR3;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Content;
using FSO.Files;
using FSO.Files.Formats.tsodata;
using FSO.Files.Formats.IFF;
using FSO.Common;
using FSO.Content.Model;
using FSO.Content.Interfaces;
using FSO.Content.TS1;
using FSO.Content.Framework;
using FSO.Vitaboy;
using FSO.Content.Upgrades;

using FSO.Common.IO;
namespace FSO.Content
{
    public enum ContentMode
    {
        SERVER,
        CLIENT
    }

    /// <summary>
    /// Content is a singleton responsible for loading data.
    /// </summary>
    public class Content
    {
        public static ContentLoadingProgress LoadProgress
        {
            get { return _LoadProgress; }
            set { _LoadProgress = value; try { ReduxProgressChanged?.Invoke(value); } catch { } }
        }
        private static ContentLoadingProgress _LoadProgress;
        /// <summary>
        /// SimsRedux 19.6 (web): told at each loading stage as it starts - the browser writes it down at once, because the
        /// whole content load is one long step there and Safari can close the page part way through it.
        /// </summary>
        public static System.Action<ContentLoadingProgress> ReduxProgressChanged;

        public static void Init(string basepath, GraphicsDevice device){
            if (INSTANCE != null) {
                if (!INSTANCE.Inited) INSTANCE.Init();
                return;
            }
            INSTANCE = new Content(basepath, ContentMode.CLIENT, device, true);
        }

        public static void Init(string basepath, ContentMode mode)
        {
            if (INSTANCE != null) return;
            INSTANCE = new Content(basepath, mode, null, true);
        }

        public static void InitBasic(string basepath, GraphicsDevice device)
        {
            if (INSTANCE != null) return;
            INSTANCE = new Content(basepath, ContentMode.CLIENT, device, false);
        }

        private static Content INSTANCE;
        public static Content Get()
        {
            return INSTANCE;
        }

        //for debugging TS1 content system
        public static bool TS1Hybrid
        {
            get
            {
                return (Target == FSOEngineMode.TS1 || Target == FSOEngineMode.TS1Hybrid);
            }
            set
            {
                Target = (value) ? FSOEngineMode.TS1Hybrid : FSOEngineMode.TSO;
            }
        }
        public static FSOEngineMode Target;
        public static string TS1HybridBasePath = "D:/Games/The Sims/";

        /**
         * Content Manager
         */
        public string BasePath;
        public string[] AllFiles;
        public string[] TS1AllFiles;

        /// <summary>
        /// SimsRedux: mods that ship with the game (Content/redux/mods/), so every player - and the
        /// server - has exactly the same objects. They join the game's own file list under this tag
        /// ("~redux-mods/Downloads/x.iff" acts like the game's Downloads folder; "Overrides/" files
        /// replace the game's own objects of the same name). The folder's manifest.txt lists them.
        /// </summary>
        public const string ReduxModTag = "~redux-mods/";
        public static string ReduxModsDir { get { return Path.Combine(FSOEnvironment.ContentDir, "redux/mods/"); } }

        /// <summary>
        /// SimsRedux: content made with the Sims Refresh Creator (Content/redux/creator/, listed in its
        /// manifest.txt). It's only loaded while it's being tried out on the Test server: the game sets
        /// this before the content loads (Test server, a Test lot worker, or the "creatorcontent" setting).
        /// </summary>
        public static bool ReduxCreatorContent;
        public const string ReduxCreatorTag = "~redux-creator/";
        public static string ReduxCreatorDir { get { return Path.Combine(FSOEnvironment.ContentDir, "redux/creator/"); } }

        /// <summary>
        /// SimsRedux: items made on the server from pictures (the panel's Item Editor > New item from picture), downloaded
        /// by the game into its own folder (Simitone.Client's ReduxCustomItems: UserDir/redux_custom/&lt;server&gt;/, listed in
        /// its manifest.txt). Set before the content loads; null or empty = none.
        /// </summary>
        public static string ReduxCustomDir;
        public const string ReduxCustomTag = "~redux-custom/";

        /// <summary>
        /// SimsRedux: The Sims Online's own folder (TSOClient - the one with housedata and objectdata in it),
        /// "" = none. In the browser it's "/tso/" (downloaded from the server by Admins and Testers).
        /// Its content is only used while ReduxTSOContent is on (set before the content loads, like
        /// ReduxCreatorContent). Stage 1: TSO's walls and floors join the build catalogs. Stage 2: TSO's adult
        /// clothes and heads join Create-a-Sim (TS1/ReduxTSOAvatars). Stage 3: TSO's buy and build objects
        /// without interactions join Buy and Build mode (TS1/ReduxTSOObjects).
        /// </summary>
        public static string TSOBasePath = "";
        public static bool ReduxTSOContent;
        /// <summary>How much of The Sims Online is brought in (part of the online content stamp).</summary>
        public const int ReduxTSOStage = 3;

        /// <summary>A file in The Sims Online's folder, e.g. TSOPath("housedata/walls/walls.far").</summary>
        public static string TSOPath(string rel)
        {
            return Path.Combine(TSOBasePath ?? "", (rel ?? "").TrimStart('/', '\\'));
        }

        /// <summary>The Sims Online's content is switched on and its folder has what stage 1 needs.</summary>
        public static bool HasTSO
        {
            get
            {
                if (!ReduxTSOContent || string.IsNullOrEmpty(TSOBasePath)) return false;
                try
                {
                    foreach (var f in new[] { "objectdata/globals/build.iff", "housedata/walls/walls.far", "housedata/floors/floors.far" })
                        if (FSOFile.Exists(TSOPath(f))) return true;
                }
                catch (System.Exception e) { System.Console.WriteLine("[SimsRedux] Sims Online folder check: " + e.Message); }
                return false;
            }
        }

        private static readonly object TSOStampLock = new object();
        private static int ReduxTSOFiles;
        private static long ReduxTSOBytes;

        /// <summary>Called by the TSO providers with the archives they loaded (for ReduxTSOStamp).</summary>
        public static void ReduxAddTSOStamp(int files, long bytes)
        {
            lock (TSOStampLock) { ReduxTSOFiles += files; ReduxTSOBytes += bytes; }
        }

        /// <summary>
        /// What Sims Online content this game loaded: "" = none, else stage, archive count and their bytes
        /// ("tso3:...": the walls, floors, the Sims' clothes and heads, and the objects' archives and globals).
        /// Two games playing one lot must have the same stamp.
        /// </summary>
        public static string ReduxTSOStamp
        {
            get
            {
                lock (TSOStampLock) return ReduxTSOFiles == 0 ? "" : "tso" + ReduxTSOStage + ":" + ReduxTSOFiles + ":" + ReduxTSOBytes;
            }
        }

        /// <summary>A file from the game's file list as a real path (the game's folder, or the shipped mods).</summary>
        public string TS1Path(string file)
        {
            if (file != null && file.StartsWith(ReduxModTag)) return Path.Combine(ReduxModsDir, file.Substring(ReduxModTag.Length));
            if (file != null && file.StartsWith(ReduxCreatorTag)) return Path.Combine(ReduxCreatorDir, file.Substring(ReduxCreatorTag.Length));
            if (file != null && file.StartsWith(ReduxCustomTag) && !string.IsNullOrEmpty(ReduxCustomDir)) return Path.Combine(ReduxCustomDir, file.Substring(ReduxCustomTag.Length));
            return Path.Combine(TS1BasePath, file);
        }

        private static void AddReduxMods(List<string> files)
        {
            AddManifest(files, ReduxModsDir, ReduxModTag, "mod");
            if (ReduxCreatorContent) AddManifest(files, ReduxCreatorDir, ReduxCreatorTag, "creator");
            if (!string.IsNullOrEmpty(ReduxCustomDir)) AddManifest(files, ReduxCustomDir, ReduxCustomTag, "server-made (from pictures)");
        }

        /// <summary>SimsRedux: adds the files a folder's manifest.txt lists (one per line, # = comment) to the game's file list.</summary>
        private static void AddManifest(List<string> files, string dir, string tag, string what)
        {
            try
            {
                var manifest = Path.Combine(dir, "manifest.txt");
                if (!FSO.Common.IO.FSOFile.Exists(manifest)) return;
                int n = 0;
                foreach (var line in FSO.Common.IO.FSOFile.ReadAllText(manifest).Split('\n'))
                {
                    var f = line.Trim().Replace('\\', '/');
                    if (f.Length == 0 || f.StartsWith("#")) continue;
                    if (f.Contains("..")) continue; // (stays inside its folder)
                    files.Add(tag + f);
                    n++;
                }
                System.Console.WriteLine("[SimsRedux] " + n + " " + what + " files added");
            }
            catch (System.Exception e) { System.Console.WriteLine("[SimsRedux] Couldn't add the " + what + " files: " + e.Message); }
        }
        public string[] ContentFiles;
        private GraphicsDevice Device;
        public ContentMode Mode;
        public bool TS1 = TS1Hybrid;
        public TS1Provider TS1Global;
        public TS1BCFProvider BCFGlobal;
        /// <summary>SimsRedux: The Sims Online's clothes and heads (null: not loaded - no TSO folder, or no avatardata in it).</summary>
        public ReduxTSOAvatars ReduxTSOAvatarSet;
        public string TS1BasePath = TS1HybridBasePath;
        public string VersionString = "unidentified";
        public bool Inited = false;

        public ChangeManager Changes;

        /// <summary>
        /// Creates a new instance of Content.
        /// </summary>
        /// <param name="basePath">Path to client directory.</param>
        /// <param name="device">A GraphicsDevice instance.</param>
        private Content(string basePath, ContentMode mode, GraphicsDevice device, bool init)
        {
            LoadProgress = ContentLoadingProgress.Started;
            this.BasePath = basePath;
            this.Device = device;
            this.Mode = mode;

            ImageLoader.PremultiplyPNG = 1;// (FSOEnvironment.DirectX)?0:1;

            if (device != null)
            {
                RCMeshes = new RCMeshProvider(device);
                UIGraphics = new UIGraphicsProvider(this);
                IffFile.TargetTS1 = TS1;
                if (TS1)
                {
                    TS1Global = new TS1Provider(this);
                    AvatarTextures = new TS1AvatarTextureProvider(TS1Global);
                    AvatarMeshes = new TS1BMFProvider(TS1Global);
                }
                else
                {
                    AvatarTextures = new AvatarTextureProvider(this);
                    AvatarMeshes = new AvatarMeshProvider(this, Device);
                }
                AvatarHandgroups = new HandgroupProvider(this);
                AbstractTextureRef.FetchDevice = device;
                AbstractTextureRef.ImageFetchFunction = AbstractTextureRef.ImageFetchWithDevice;
            }
            Changes = new ChangeManager();
            CustomUI = new CustomUIProvider(this);

            if (TS1)
            {
                var provider = new TS1ObjectProvider(this, TS1Global);
                WorldObjects = provider;
                WorldCatalog = provider;
                BCFGlobal = new TS1BCFProvider(this, TS1Global);
                AvatarAnimations = new TS1BCFAnimationProvider(BCFGlobal);
                AvatarSkeletons = new TS1BCFSkeletonProvider(BCFGlobal);
                AvatarAppearances = new TS1BCFAppearanceProvider(BCFGlobal);
                Audio = new TS1Audio(this);
            } else
            {
                AvatarBindings = new AvatarBindingProvider(this);
                AvatarAppearances = new AvatarAppearanceProvider(this);
                AvatarOutfits = new AvatarOutfitProvider(this);

                AvatarPurchasables = new AvatarPurchasables(this);
                AvatarCollections = new AvatarCollectionsProvider(this);
                AvatarThumbnails = new AvatarThumbnailProvider(this);

                AvatarAnimations = new AvatarAnimationProvider(this);
                AvatarSkeletons = new AvatarSkeletonProvider(this);
                WorldObjects = new WorldObjectProvider(this);
                WorldCatalog = new WorldObjectCatalog();
                Audio = new Audio(this);
                CityMaps = new CityMapsProvider(this);
                RackOutfits = new RackOutfitsProvider(this);
                Ini = new IniProvider(this);
                GlobalTuning = new Tuning(Path.Combine(basePath, "tuning.dat"));

                Upgrades = new ObjectUpgradeProvider(this);
            }
            WorldFloors = new WorldFloorProvider(this);
            WorldWalls = new WorldWallProvider(this);
            WorldObjectGlobals = new WorldGlobalProvider(this);
            WorldRoofs = new WorldRoofProvider(this);

            InitBasic();
            if (init) Init();
        }

        /// <summary>
        /// Initiates loading for world.
        /// </summary>
        public void InitWorld()
        {
            LoadProgress = ContentLoadingProgress.InitObjects;
            if (TS1)
            {
                WorldObjectGlobals.Init();
                ((TS1ObjectProvider)WorldObjects).Init();
                LoadProgress = ContentLoadingProgress.InitArch;

                lock (TSOStampLock) { ReduxTSOFiles = 0; ReduxTSOBytes = 0; } // (the walls and floors add theirs)
                if (ReduxTSOAvatarSet != null) ReduxAddTSOStamp(ReduxTSOAvatarSet.ArchiveCount, ReduxTSOAvatarSet.ArchiveBytes); // (and the Sims' clothes)
                var tsoObjects = ((TS1ObjectProvider)WorldObjects).ReduxTSOSet;
                if (tsoObjects != null) ReduxAddTSOStamp(tsoObjects.ArchiveCount, tsoObjects.ArchiveBytes); // (and the objects)
                WorldWalls.InitTS1();
                WorldFloors.InitTS1();
            }
            else
            {
                WorldObjectGlobals.Init();
                ((WorldObjectProvider)WorldObjects).Init((Device != null));
                ((WorldObjectCatalog)WorldCatalog).Init(this, WorldObjects.CatalogEnrich);
                LoadProgress = ContentLoadingProgress.InitArch;

                WorldWalls.Init();
                WorldFloors.Init();
                Upgrades.Init();
                if (Mode == ContentMode.SERVER) Upgrades.LoadJSONTuning();
            }
            WorldObjectGlobals.InitCurves();
            WorldRoofs.Init();
            LoadProgress = ContentLoadingProgress.Done;
        }

        private void InitBasic()
        {
            var contentFiles = new List<string>();
            _ScanFiles("Content/", contentFiles, "Content/");
            ContentFiles = contentFiles.ToArray();
            CustomUI.Init();
            if (!TS1)
            {
                var allFiles = new List<string>();
                _ScanFiles(BasePath, allFiles, BasePath);
                AllFiles = allFiles.ToArray();
                UIGraphics?.Init();
                DataDefinition = new TSODataDefinition();
                try
                {
                    using (var stream = FSOFile.Open("Content/FSODataDefinition.dat", FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        DataDefinition.Read(stream);
                    }
                }
                catch
                {
                    using (var stream = FSOFile.OpenRead(GetPath("TSOData_datadefinition.dat")))
                    {
                        DataDefinition.Read(stream);
                    }
                }

                try
                {
                    VersionString = FSOFile.ReadAllText(GetPath("version"));
                }
                catch { }
            } else
            {
                VersionString = "TS1";
            }
        }

        /// <summary>
        /// Setup the content manager so it knows where to find various files.
        /// </summary>
        private void Init()
        {
            Inited = true;
            if (!TS1) Audio.Init();
            /** Scan system for files **/
            if (AllFiles == null)
            {
                LoadProgress = ContentLoadingProgress.ScanningFiles;
                var allFiles = new List<string>();
                if (Target != FSOEngineMode.TS1)
                {
                    _ScanFiles(BasePath, allFiles, BasePath);
                    AllFiles = allFiles.ToArray();
                }
            }

            var ts1AllFiles = new List<string>();
            var oldBase = BasePath;
            if (TS1)
            {
                _ScanFiles(TS1BasePath, ts1AllFiles, TS1BasePath);
                AddReduxMods(ts1AllFiles); // SimsRedux: the mods that ship with the game
                TS1AllFiles = ts1AllFiles.ToArray();
            }

            LoadProgress = ContentLoadingProgress.InitGlobal;
            TS1Global?.Init();
            LoadProgress = ContentLoadingProgress.InitBCF;
            BCFGlobal?.Init();

            if (!TS1) PIFFRegistry.Init(Path.Combine(FSOEnvironment.ContentDir, "Patch/"));
            else PIFFRegistry.Init(Path.Combine(FSOEnvironment.ContentDir, "TS1Patch/"));

            LoadProgress = ContentLoadingProgress.InitAvatars;
            Archives = new Dictionary<string, FAR3Archive>();
            if (Target != FSOEngineMode.TS1 && Mode == ContentMode.CLIENT)
            {
                UIGraphics.Init();
            }

            if (TS1)
            {
                ((TS1AvatarTextureProvider)AvatarTextures)?.Init();
                ((TS1BMFProvider)AvatarMeshes)?.Init();
                // SimsRedux: The Sims Online's adult clothes and heads, next to the Sims 1 ones (only their file lists
                // are read now - see ReduxTSOAvatars). Loaded without graphics too, so a lot worker's content stamp
                // is the same as its players'.
                try { ReduxTSOAvatarSet = HasTSO ? ReduxTSOAvatars.Load() : ReduxTSOAvatars.Unload(); }
                catch (System.Exception e) { ReduxTSOAvatarSet = null; System.Console.WriteLine("[SimsRedux] Sims Online clothes: " + e.Message); }
                Jobs = new TS1JobProvider(TS1Global);
                Neighborhood = new TS1NeighborhoodProvider(this);
            } else
            {
                if (Mode == ContentMode.CLIENT) AvatarHandgroups.Init();
                AvatarBindings.Init();
                AvatarOutfits.Init();
                AvatarPurchasables.Init();
                AvatarCollections.Init();
                AvatarThumbnails.Init();
                ((AvatarTextureProvider)AvatarTextures)?.Init();
                ((AvatarAnimationProvider)AvatarAnimations).Init();
                ((AvatarSkeletonProvider)AvatarSkeletons).Init();
                ((AvatarAppearanceProvider)AvatarAppearances).Init();
                ((AvatarMeshProvider)AvatarMeshes)?.Init();
                CityMaps.Init();
                RackOutfits.Init();
                Ini.Init();
            }

            LoadProgress = ContentLoadingProgress.InitAudio;
            if (TS1) Audio.Init();

            InitWorld();
        }

        /// <summary>
        /// Scans a directory for a list of files.
        /// </summary>
        /// <param name="dir">The directory to scan.</param>
        /// <param name="fileList">The list of files to scan for.</param>
        private void _ScanFiles(string dir, List<string> fileList, string baseDir)
        {
            var dirName = Path.GetFileName(dir);

            // Hack... Don't scan archive data.
            if (dirName == "ArchiveCities")
            {
                return;
            }

            var fullPath = dir;
            var files = FSODirectory.GetFiles(fullPath);
            foreach (var file in files)
            {
                fileList.Add(file.Substring(baseDir.Length));
            }

            var dirs = FSODirectory.GetDirectories(fullPath);
            foreach (var subDir in dirs)
            {
                _ScanFiles(subDir, fileList, baseDir);
            }
        }

        /// <summary>
        /// Gets a path relative to the client's directory.
        /// </summary>
        /// <param name="path">The path to combine with the client's directory.</param>
        /// <returns>The path combined with the client's directory.</returns>
        public string GetPath(string path)
        {
            return Path.Combine(BasePath, path);
        }

        private Dictionary<string, FAR3Archive> Archives;

        /// <summary>
        /// Gets a resource using a path and ID.
        /// </summary>
        /// <param name="path">The path to the file. If this path is to an archive, assetID can be null.</param>
        /// <param name="assetID">The ID for the resource. Can be null if path doesn't point to an archive.</param>
        /// <returns></returns>
        public Stream GetResource(string path, ulong assetID)
        {
            if (path.EndsWith(".dat"))
            {
                /** Archive **/
                if (!Archives.ContainsKey(path))
                {
                    FAR3Archive newArchive = new FAR3Archive(GetPath(path));
                    Archives.Add(path, newArchive);
                }

                var archive = Archives[path];
                var bytes = archive.GetItemByID(assetID);
                return new MemoryStream(bytes, false);
            }

            if (path.EndsWith(".bmp") || path.EndsWith(".png") || path.EndsWith(".tga")) path = "uigraphics/" + path;

            return FSOFile.OpenRead(GetPath(path));
        }

        /** World **/
        public AbstractObjectProvider WorldObjects;
        public WorldGlobalProvider WorldObjectGlobals;
        public WorldFloorProvider WorldFloors;
        public WorldWallProvider WorldWalls;
        public IObjectCatalog WorldCatalog;
        public WorldRoofProvider WorldRoofs;

        public ObjectUpgradeProvider Upgrades;

        public UIGraphicsProvider UIGraphics;
        public CustomUIProvider CustomUI;
        
        /** Avatar **/
        public IContentProvider<Mesh> AvatarMeshes;
        public AvatarBindingProvider AvatarBindings;
        public IContentProvider<ITextureRef> AvatarTextures;
        public IContentProvider<Skeleton> AvatarSkeletons;
        public IContentProvider<Appearance> AvatarAppearances;
        public AvatarOutfitProvider AvatarOutfits;
        public IContentProvider<Animation> AvatarAnimations;
        public AvatarPurchasables AvatarPurchasables;
        public HandgroupProvider AvatarHandgroups;
        public AvatarCollectionsProvider AvatarCollections;
        public AvatarThumbnailProvider AvatarThumbnails;

        /** Audio **/
        public IAudioProvider Audio;

        /** GlobalTuning **/
        public Tuning GlobalTuning;

        /** Parsing **/
        public TSODataDefinition DataDefinition;

        /** Config **/
        public IniProvider Ini;

        /** Maps **/
        public CityMapsProvider CityMaps;

        /** Rack Outfits **/
        public RackOutfitsProvider RackOutfits;

        /** TS1 Job Data **/
        public TS1JobProvider Jobs;

        /** TS1 Neighbourhood Data **/
        public TS1NeighborhoodProvider Neighborhood;

        /** 3D Reconstructed Object Meshes **/
        public RCMeshProvider RCMeshes;
    }

    public enum ContentLoadingProgress : int
    {
        Started = 0,
        ScanningFiles = 1,
        InitGlobal = 2,
        InitBCF = 3,
        InitAvatars = 4,
        InitAudio = 5,
        InitObjects = 6,
        InitArch = 7,
        Done = 8,

        Invalid = -1
    }

    public enum FSOEngineMode
    {
        TSO,
        TS1Hybrid,
        TS1
    }
}
