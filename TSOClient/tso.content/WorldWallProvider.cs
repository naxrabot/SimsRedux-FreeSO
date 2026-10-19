using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Common.Content;
using FSO.Content.Model;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.FAR1;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using FSO.Content.Framework;
using FSO.Content.Codecs;
using System.Text.RegularExpressions;
using FSO.Common.Utils;

namespace FSO.Content
{
    /// <summary>
    /// Provides access to wall (*.wll) data in FAR3 archives.
    /// </summary>
    public class WorldWallProvider : IContentProvider<Wall>
    {
        private Content ContentManager;
        public Wall Junctions;
        private List<WallStyle> WallStyles;
        private Dictionary<ushort, Wall> ById;
        private Dictionary<ushort, WallStyle> StyleById;
        public Dictionary<string, ushort> DynamicWallFromID;
        /// <summary>SimsRedux: where each wallpaper file came from (for making tinted copies of it).</summary>
        public Dictionary<ushort, IContentReference> ReduxSources = new Dictionary<ushort, IContentReference>();
        /// <summary>SimsRedux: the file the game's own wallpapers are in.</summary>
        public string ReduxGlobalsPath;
        private IffFile WallGlobals;
        private IffFile BuildGlobals;

        public Dictionary<ushort, WallReference> Entries;

        public IContentProvider<IffFile> Walls;

        public int NumWalls;

        public WorldWallProvider(Content contentManager)
        {
            this.ContentManager = contentManager;

            WallStyleToIndex = WallStyleIDs.ToDictionary(x => x, x => Array.IndexOf(WallStyleIDs, x));

            this.Entries = new Dictionary<ushort, WallReference>();
            this.ById = new Dictionary<ushort, Wall>();
            this.StyleById = new Dictionary<ushort, WallStyle>();
            this.WallStyles = new List<WallStyle>();
            DynamicWallFromID = new Dictionary<string, ushort>();
        }

        public ushort[] WallStyleIDs =
        {
            0x1, //wall
            0x2, //picket fence
            0xD, //iron fence
            0xC, //privacy fence
            0xE //banisters
        };

        public ushort[] WallStylePatterns =
        {
            0, //wall
            248, //picket fence
            250, //iron fence
            249, //privacy fence
            251, //banisters
        };

        public Dictionary<ushort, int> WallStyleToIndex;

        private void InitGlobals()
        {
            /** Get wall styles from globals file **/
            var styleStrs = BuildGlobals.Get<STR>(0x81);
            ushort wallID = 1;
            for (ushort i = 2; i < 512; i += 2)
            {
                var far = WallGlobals.Get<SPR>((ushort)(i));
                var medium = WallGlobals.Get<SPR>((ushort)(i + 512));
                var near = WallGlobals.Get<SPR>((ushort)(i + 1024));

                var fard = WallGlobals.Get<SPR>((ushort)(i + 1));
                var mediumd = WallGlobals.Get<SPR>((ushort)(i + 513));
                var neard = WallGlobals.Get<SPR>((ushort)(i + 1025));

                if (far != null)
                {
                    near.WallStyle = true; medium.WallStyle = true; far.WallStyle = true;
                }

                if (fard == null)
                { //no walls down, just render exactly the same
                    fard = far;
                    mediumd = medium;
                    neard = near;
                }
                else
                {
                    neard.WallStyle = true; mediumd.WallStyle = true; fard.WallStyle = true;
                }

                string name = null, description = null;
                int price = -1;
                int buyIndex = -1;
                WallStyleToIndex.TryGetValue(wallID, out buyIndex);

                if (buyIndex != -1)
                {
                    price = int.Parse(styleStrs.GetString(buyIndex * 3));
                    name = styleStrs.GetString(buyIndex * 3 + 1);
                    description = styleStrs.GetString(buyIndex * 3 + 2);
                }

                this.AddWallStyle(new WallStyle
                {
                    ID = wallID,
                    WallsUpFar = far,
                    WallsUpMedium = medium,
                    WallsUpNear = near,
                    WallsDownFar = fard,
                    WallsDownMedium = mediumd,
                    WallsDownNear = neard,

                    Price = price,
                    Name = name,
                    Description = description
                });

                wallID++;
            }

            DynamicStyleID = 256; //styles loaded from objects start at 256. The objd reference is dynamically altered to reference this new id, 
            //so only refresh wall cache at same time as obj cache! (do this on lot unload)

            /** Get wall patterns from globals file **/
            var wallStrs = BuildGlobals.Get<STR>(0x83);

            wallID = 0;
            for (ushort i = 0; i < 256; i++)
            {
                var far = WallGlobals.Get<SPR>((ushort)(i + 1536));
                var medium = WallGlobals.Get<SPR>((ushort)(i + 1536 + 256));
                var near = WallGlobals.Get<SPR>((ushort)(i + 1536 + 512));

                this.AddWall(new Wall
                {
                    ID = wallID,
                    Far = far,
                    Medium = medium,
                    Near = near,
                });

                if (i > 0 && i < (wallStrs.Length / 3) + 1)
                {
                    Entries.Add(wallID, new WallReference(this)
                    {
                        ID = wallID,
                        FileName = "global",

                        Name = wallStrs.GetString((i - 1) * 3 + 1),
                        Price = int.Parse(wallStrs.GetString((i - 1) * 3 + 0)),
                        Description = wallStrs.GetString((i - 1) * 3 + 2)
                    });
                }

                wallID++;
            }

            Junctions = new Wall
            {
                ID = wallID,
                Far = WallGlobals.Get<SPR>(4096),
                Medium = WallGlobals.Get<SPR>(4097),
                Near = WallGlobals.Get<SPR>(4098),
            };

            wallID = 256;
        }

        public void InitTS1()
        {
            var wallGlobalsPath = Path.Combine(ContentManager.TS1BasePath, "GameData/walls.iff");
            ReduxGlobalsPath = wallGlobalsPath;
            WallGlobals = new IffFile(wallGlobalsPath);

            var buildGlobalsPath = Path.Combine(ContentManager.TS1BasePath, "GameData/Build.iff");
            BuildGlobals = new IffFile(buildGlobalsPath);

            InitGlobals();

            //load *.wll iffs from both the TS1 provider and folder

            ushort wallID = 256;
            var files = new FileProvider<IffFile>(ContentManager, new IffCodec(), new Regex(".*/Walls.*\\.wll$") /* (SimsRedux: "$" - not "x.wll.bak") */);
            files.UseTS1 = true;
            var ts1 = new TS1SubProvider<IffFile>(ContentManager.TS1Global, ".wll");
            files.Init();
            ts1.Init();
            var sources = new List<IContentProvider<IffFile>>() {
                ts1,
                files
                };
            // SimsRedux: The Sims Online's wallpapers come after all the Sims 1 and custom ones (so those keep
            // the same numbers whether or not TSO is loaded). Lots save them by name ("tso_..."), not number.
            if (Content.HasTSO)
            {
                try
                {
                    var tso = new TS1.ReduxTSOFarProvider(ContentManager, "walls", ".wll");
                    tso.InitTSO();
                    sources.Add(tso);
                    Content.ReduxAddTSOStamp(tso.ArchiveCount, tso.ArchiveBytes);
                }
                catch (Exception e) { Console.WriteLine("[SimsRedux] Sims Online walls couldn't be loaded: " + e.Message); }
            }
            var compo =  new CompositeProvider<IffFile>(sources);

            Walls = compo;
            var all = compo.ListGeneric();
            foreach (var entry in all)
            {
                // SimsRedux: a damaged download is left out (it used to stop the game from starting)
                IffFile iff;
                STR catStrings;
                try
                {
                    iff = (IffFile)entry.GetThrowawayGeneric();
                    catStrings = iff?.Get<STR>(0);
                    if (catStrings == null) throw new Exception("no catalog text");
                }
                catch (Exception e)
                {
                    Console.WriteLine("[SimsRedux] skipped a damaged wall file (" + entry + "): " + e.Message);
                    continue;
                }
                int price;
                int.TryParse((catStrings.GetString(1) ?? "").Trim(), out price);
                DynamicWallFromID[Path.GetFileNameWithoutExtension(entry.ToString().Replace('\\', '/')).ToLowerInvariant()] = wallID;
                ReduxSources[wallID] = entry;

                Entries.Add(wallID, new WallReference(this)
                {
                    ID = wallID,
                    FileName = Path.GetFileName(entry.ToString().Replace('\\', '/')).ToLowerInvariant(),

                    Name = catStrings.GetString(0),
                    Price = price,
                    Description = catStrings.GetString(2)
                });

                wallID++;
            }
            NumWalls = wallID;
        }

        /// <summary>
        /// Initiates loading of walls.
        /// </summary>
        public void Init()
        {
            var wallGlobalsPath = ContentManager.GetPath("objectdata/globals/walls.iff");
            WallGlobals = new IffFile(wallGlobalsPath);

            var buildGlobalsPath = ContentManager.GetPath("objectdata/globals/build.iff");
            BuildGlobals = new IffFile(buildGlobalsPath); //todo: centralize?

            InitGlobals();
            ushort wallID = 256;

            var archives = new string[]
            {
                "housedata/walls/walls.far",
                "housedata/walls2/walls2.far",
                "housedata/walls3/walls3.far",
                "housedata/walls4/walls4.far"
            };

            for (var i = 0; i < archives.Length; i++)
            {
                var archivePath = ContentManager.GetPath(archives[i]);
                var archive = new FAR1Archive(archivePath, true);
                var entries = archive.GetAllEntries();

                foreach (var entry in entries)
                {
                    var iff = new IffFile();
                    DynamicWallFromID[new string(entry.Key.TakeWhile(x => x != '.').ToArray()).ToLowerInvariant()] = wallID;
                    var bytes = archive.GetEntry(entry);
                    using(var stream = new MemoryStream(bytes))
                    {
                        iff.Read(stream);
                    }

                    var catStrings = iff.Get<STR>(0);

                    Entries.Add(wallID, new WallReference(this)
                    {
                        ID = wallID,
                        FileName = entry.Key,

                        Name = catStrings.GetString(0),
                        Price = int.Parse(catStrings.GetString(1)),
                        Description = catStrings.GetString(2)
                    });

                    wallID++;
                }
                archive.Close();
            }

            var far1 = new FAR1Provider<IffFile>(ContentManager, new IffCodec(), new Regex(".*/walls.*\\.far"));
            far1.Init();
            Walls = far1;
            NumWalls = wallID;
        }

        private ushort DynamicStyleID;


        /// <summary>
        /// Adds a dynamic wall style to WorldWallProvider.
        /// </summary>
        /// <param name="input">Wallstyle to add.</param>
        /// <returns>The ID of the wallstyle.</returns>
        public ushort AddDynamicWallStyle(WallStyle input) //adds a new wall and returns its id
        {
            input.ID = DynamicStyleID++;
            AddWallStyle(input);
            return (ushort)(DynamicStyleID - 1);
        }

        private void AddWall(Wall wall)
        {
            //Walls.Add(wall);
            ById.Add(wall.ID, wall);
        }

        /// <summary>
        /// SimsRedux: adds a wallpaper made while playing (a tinted copy), from a wall file's contents
        /// (STR# 0 for its name, SPR 1 / 1793 / 2049 for its pictures). Returns its ID (the same one
        /// again if a wallpaper with this name is already there).
        /// </summary>
        public ushort ReduxAddRuntime(string name, IffFile iff)
        {
            name = name.ToLowerInvariant();
            ushort existing;
            if (DynamicWallFromID.TryGetValue(name, out existing)) return existing;
            var far = iff.Get<SPR>(1); var medium = iff.Get<SPR>(1793); var near = iff.Get<SPR>(2049);
            if (far == null || medium == null || near == null) throw new InvalidDataException("not a wallpaper");
            var id = (ushort)NumWalls;
            if (id >= 65000) throw new InvalidOperationException("too many wallpapers");
            NumWalls++;
            ById[id] = new Wall { ID = id, Far = far, Medium = medium, Near = near };
            var str = iff.Get<STR>(0);
            int price; int.TryParse((str?.GetString(1) ?? "").Trim(), out price);
            Entries[id] = new WallReference(this) { ID = id, FileName = name + ".wll", Name = str?.GetString(0) ?? name, Price = price, Description = str?.GetString(2) ?? "" };
            DynamicWallFromID[name] = id;
            ReduxRuntime.Add(id);
            return id;
        }
        private HashSet<ushort> ReduxRuntime = new HashSet<ushort>();

        /// <summary>
        /// SimsRedux: a lot names a wallpaper this game doesn't have (a missing download, or The Sims Online's
        /// files aren't loaded). It gets a plain grey stand-in registered under that same name, so the walls
        /// still look like walls and saving the lot keeps the name - the real one is back once its file is.
        /// Stand-ins are never offered in the catalog. Returns its ID (the existing one if the name is known).
        /// </summary>
        public ushort ReduxAddPlaceholder(string name)
        {
            name = (name ?? "").ToLowerInvariant();
            ushort existing;
            if (DynamicWallFromID.TryGetValue(name, out existing)) return existing;
            var look = PlaceholderLook();
            if (look == null) throw new InvalidDataException("no wallpaper to make a stand-in from");
            var id = (ushort)NumWalls;
            if (id >= 65000) throw new InvalidOperationException("too many wallpapers");
            NumWalls++;
            ById[id] = new Wall { ID = id, Far = look.Far, Medium = look.Medium, Near = look.Near };
            Entries[id] = new WallReference(this) { ID = id, FileName = name + ".wll", Name = "Missing wallpaper", Price = 0, Description = name };
            DynamicWallFromID[name] = id;
            ReduxRuntime.Add(id);
            ReduxPlaceholders.Add(id);
            Console.WriteLine("[SimsRedux] wallpaper '" + name + "' is missing - a grey stand-in keeps its place");
            return id;
        }
        private HashSet<ushort> ReduxPlaceholders = new HashSet<ushort>();
        private Wall ReduxPlaceholderWall;

        /// <summary>A grey copy of one of the game's own wallpapers (or, failing that, the wallpaper itself).</summary>
        private Wall PlaceholderLook()
        {
            if (ReduxPlaceholderWall != null) return ReduxPlaceholderWall;
            try
            {
                var src = FSO.Common.IO.FSOFile.ReadAllBytes(ReduxGlobalsPath);
                for (ushort p = 1; p < 16 && ReduxPlaceholderWall == null; p++)
                {
                    try
                    {
                        var bytes = ReduxPatternFile.MakeTinted(src, "SPR#", new List<Tuple<ushort, ushort>> {
                            Tuple.Create((ushort)(p + 1536), (ushort)1), Tuple.Create((ushort)(p + 1792), (ushort)1793), Tuple.Create((ushort)(p + 2048), (ushort)2049) },
                            ReduxGrey, "Missing wallpaper", 0, "");
                        var iff = new IffFile();
                        using (var ms = new MemoryStream(bytes)) iff.Read(ms);
                        var far = iff.Get<SPR>(1); var medium = iff.Get<SPR>(1793); var near = iff.Get<SPR>(2049);
                        if (far != null && medium != null && near != null) ReduxPlaceholderWall = new Wall { Far = far, Medium = medium, Near = near };
                    }
                    catch (InvalidDataException) { } // (that pattern isn't there: try the next)
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] grey stand-in wallpaper: " + e.Message); }
            if (ReduxPlaceholderWall == null)
            {
                // the game's own first wallpaper as it is
                foreach (var w in ById.Values)
                    if (w.ID > 0 && w.ID < 256 && w.Near != null && w.Medium != null && w.Far != null) { ReduxPlaceholderWall = w; break; }
            }
            return ReduxPlaceholderWall;
        }

        /// <summary>Any colour as a light grey of the same brightness (the stand-ins for missing walls and floors).</summary>
        public static Microsoft.Xna.Framework.Color ReduxGrey(Microsoft.Xna.Framework.Color c)
        {
            var l = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            var v = (byte)Math.Max(0, Math.Min(255, 150 + (l - 128) / 4));
            return new Microsoft.Xna.Framework.Color(v, v, v, c.A);
        }

        private void AddWallStyle(WallStyle wall)
        {
            WallStyles.Add(wall);
            StyleById.Add(wall.ID, wall);
        }

        /// <summary>
        /// Gets a wallstyle instance from WorldWallProvider.
        /// </summary>
        /// <param name="id">The ID of the wallstyle.</param>
        /// <returns>A WallStyle instance.</returns>
        public WallStyle GetWallStyle(ulong id)
        {
            if (StyleById.ContainsKey((ushort)id))
            {
                return StyleById[(ushort)id];
            }
            return null;
        }

        public Texture2D GetWallThumb(ushort id, GraphicsDevice device)
        {
            if (id < 256 || ReduxRuntime.Contains(id))
            {
                var spr = ById[id].Medium;
                return TextureUtils.Copy(device, (spr == null)?null:spr.Frames[2].GetTexture(device));
            }
            else
            {
                IffFile iff;
                if (this.Walls is FAR1Provider<IffFile>)
                    iff = ((FAR1Provider<IffFile>)this.Walls).ThrowawayGet(Entries[(ushort)id].FileName);
                else
                    iff = this.Walls.Get(Entries[(ushort)id].FileName);
                var spr = iff.Get<SPR>(1793);
                return (spr == null)?null:spr.Frames[2].GetTexture(device);
            }
        }


        public BMP GetWallStyleIcon(ushort id)
        {
            return WallGlobals.Get<BMP>(id);
        }

        #region IContentProvider<Wall> Members

        public Wall Get(ulong id)
        {
            if (ById.ContainsKey((ushort)id))
            {
                return ById[(ushort)id];
            }
            else
            {
                //get from iff
                WallReference entry = null;
                if (!Entries.TryGetValue((ushort)id, out entry)) entry = Entries.Values.FirstOrDefault(x => x.ID > 255);
                if (entry == null) return null;
                IffFile iff = this.Walls.Get(entry.FileName);
                if (iff == null) return null;

                var far = iff.Get<SPR>(1);
                var medium = iff.Get<SPR>(1793);
                var near = iff.Get<SPR>(2049);
                // (SimsRedux: a downloaded wall missing a zoom level uses another of its own instead of crashing)
                near = near ?? medium ?? far; medium = medium ?? near ?? far; far = far ?? medium ?? near;
                if (near == null) return null;

                ById[(ushort)id] = new Wall
                {
                    ID = (ushort)id,
                    Near = near,
                    Medium = medium,
                    Far = far
                };
                return ById[(ushort)id];
            }
        }

        public Wall Get(uint type, uint fileID)
        {
            return null;
        }

        public List<IContentReference<Wall>> List()
        {
            // (SimsRedux: stand-ins for missing wallpapers are never offered)
            if (ReduxPlaceholders.Count == 0) return new List<IContentReference<Wall>>(Entries.Values);
            return new List<IContentReference<Wall>>(Entries.Values.Where(x => !ReduxPlaceholders.Contains((ushort)x.ID)));
        }

        public Wall Get(string name)
        {
            throw new NotImplementedException();
        }

        public Wall Get(ContentID id)
        {
            throw new NotImplementedException();
        }

        #endregion
    }

    public class WallReference : IContentReference<Wall>
    {
        public ulong ID;
        public string FileName;

        public int Price; //remember these, just in place of a catalog
        public string Name;
        public string Description;

        private IContentProvider<Wall> Provider;

        public WallReference(IContentProvider<Wall> provider)
        {
            this.Provider = provider;
        }

        #region IContentReference<Wall> Members

        public Wall Get()
        {
            return Provider.Get(ID);
        }

        public object GetThrowawayGeneric()
        {
            throw new NotImplementedException();
        }

        public object GetGeneric()
        {
            return Get();
        }

        #endregion
    }
}
