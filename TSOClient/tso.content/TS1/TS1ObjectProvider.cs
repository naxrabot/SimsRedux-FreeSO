using FSO.Common.Content;
using FSO.Common.Utils;
using FSO.Content.Framework;
using FSO.Content.Interfaces;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.Content.TS1
{
    public class TS1ObjectProvider : AbstractObjectProvider, IObjectCatalog
    {
        private TS1SubProvider<IffFile> GameObjects;
        private static List<ObjectCatalogItem>[] ItemsByCategory;
        private static Dictionary<uint, ObjectCatalogItem> ItemsByGUID;
        public HashSet<uint> PersonGUIDs = new HashSet<uint>();

        public TS1ObjectProvider(Content contentManager, TS1Provider provider) : base(contentManager)
        {
            GameObjects = new TS1SubProvider<IffFile>(provider, ".iff");
        }

        /// <summary>SimsRedux: how much custom content loaded (shown once in game, and in the console).</summary>
        public static int ReduxModFiles, ReduxModCatalogItems, DownloadsCatalogItems;

        public void Init()
        {
            GameObjects.Init();

            Entries = new Dictionary<ulong, GameObjectReference>();
            Cache = new TimedReferenceCache<ulong, GameObject>();

            ItemsByGUID = new Dictionary<uint, ObjectCatalogItem>();
            ItemsByCategory = new List<ObjectCatalogItem>[30];
            for (int i = 0; i < 30; i++) ItemsByCategory[i] = new List<ObjectCatalogItem>();

            var allIffs = GameObjects.ListGeneric();
            // SimsRedux: when two files hold an object with the same GUID, which one wins (see GuidRank)
            var guidRank = new Dictionary<uint, int>();
            var guidKey = new Dictionary<uint, string>();
            var guidLogged = new HashSet<uint>();
            ReduxModFiles = 0; ReduxModCatalogItems = 0; DownloadsCatalogItems = 0;
            foreach (var iff in allIffs)
            {
                bool reduxMod = false;
                IffFile file;
                try { file = (IffFile)iff.GetThrowawayGeneric(); }
                catch (Exception e) { Console.WriteLine("[SimsRedux] skipped a damaged object file (" + iff + "): " + e.Message); continue; }
                if (file == null) continue;
                var source = GameObjectSource.Far;
                string filename = Path.GetFileName(iff.ToString().Replace('\\', '/'));
                if (iff is FileContentReference<object>)
                {
                    //if we're in downloads, remember the real filename and set as standalone
                    //for easy editing in volcanic (not patching)
                    var fileIff = iff as FileContentReference<object>;
                    if (fileIff.Filename.Contains("Downloads"))
                    {
                        filename = fileIff.Filename;
                        source = GameObjectSource.Standalone;
                        reduxMod = filename.Replace('\\', '/').IndexOf("redux/mods/", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (reduxMod) ReduxModFiles++;
                    }
                }
                file.MarkThrowaway();
                int rank = GuidRank(iff);
                string rankKey = GuidRankKey(iff);
                List<OBJD> objects;
                try { objects = file.List<OBJD>(); }
                catch (Exception e) { Console.WriteLine("[SimsRedux] skipped a damaged object file (" + iff + "): " + e.Message); continue; }
                if (objects != null)
                {
                    foreach (var obj in objects)
                    {
                        // SimsRedux: a GUID that's already taken. This used to silently swap the object for the later
                        // file but leave two catalog items, and which one won depended on the order the files were
                        // listed in - so two players (or the server) could get different objects. Now one fixed rule
                        // picks it: Overrides > shipped mods > the game's own archives > the player's Downloads, then
                        // the file name, whatever the order.
                        int prevRank;
                        if (guidRank.TryGetValue(obj.GUID, out prevRank))
                        {
                            var prevKey = guidKey[obj.GUID];
                            bool newWins = rank > prevRank || (rank == prevRank && string.CompareOrdinal(rankKey, prevKey) < 0);
                            if (guidLogged.Add(obj.GUID))
                                Console.WriteLine("[SimsRedux] two objects share GUID 0x" + obj.GUID.ToString("X8") + " (" + prevKey + ", " + rankKey + ") - using " + (newWins ? rankKey : prevKey));
                            if (!newWins) continue;
                            // the old one's catalog item goes, the new one's (if any) is added below
                            ObjectCatalogItem oldItem;
                            if (ItemsByGUID.TryGetValue(obj.GUID, out oldItem))
                            {
                                var guid = obj.GUID;
                                ItemsByCategory[oldItem.Category].RemoveAll(x => x.GUID == guid);
                                ItemsByGUID.Remove(guid);
                            }
                        }
                        guidRank[obj.GUID] = rank;
                        guidKey[obj.GUID] = rankKey;

                        Entries[obj.GUID] = new GameObjectReference(this)
                        {
                            FileName = filename,
                            ID = obj.GUID,
                            Name = obj.ChunkLabel,
                            Source = source,
                            Group = (short)obj.MasterID,
                            SubIndex = obj.SubIndex,
                            GlobalSimObject = obj.Global == 1 && obj.ObjectType != OBJDType.GiftToken
                        };
                        if (obj.ObjectType == OBJDType.Person) PersonGUIDs.Add(obj.GUID);

                        //does this object appear in the catalog?
                        if ((obj.FunctionFlags > 0 || obj.BuildModeType > 0) && obj.Disabled == 0 && 
                            (obj.IsMultiTile || obj.NumGraphics > 0) && (obj.MasterID == 0 || obj.SubIndex == -1))
                        {
                            //todo: more than one of these set? no normal game objects do this
                            //todo: room sort
                            var cat = (sbyte)Math.Log(obj.FunctionFlags, 2);
                            if (obj.FunctionFlags == 0) cat = (sbyte)(obj.BuildModeType+7);
                            if (cat < 0 || cat >= 30) continue; // (SimsRedux: a download with odd catalog flags - not in the catalog, but still usable)
                            var item = new ObjectCatalogItem()
                            {
                                Category = (sbyte)(cat), //0-7 buy categories. 8-15 build mode categories
                                RoomSort = (byte)obj.RoomFlags,
                                GUID = obj.GUID,
                                DisableLevel = 0,
                                Price = obj.Price,
                                Name = obj.ChunkLabel,

                                Subsort = (byte)obj.FunctionSubsort,
                                CommunitySort = (byte)obj.CommunitySubsort,
                                DowntownSort = (byte)obj.DTSubsort,
                                MagictownSort = (byte)obj.MTSubsort,
                                StudiotownSort = (byte)obj.STSubsort,
                                VacationSort = (byte)obj.VacationSubsort
                            };
                            ItemsByCategory[item.Category].Add(item);
                            ItemsByGUID[item.GUID] = item;
                            if (reduxMod) ReduxModCatalogItems++;
                            else if (source == GameObjectSource.Standalone) DownloadsCatalogItems++;
                        }
                    }
                }
            }

            Console.WriteLine("[SimsRedux] custom content: " + ReduxModFiles + " shipped object files, " + ReduxModCatalogItems +
                " shipped Buy/Build items, " + DownloadsCatalogItems + " from the Downloads folder");
            var globalSims = Entries.Values.Where(x => x.GlobalSimObject);
            ControllerObjects.Clear();
            ControllerObjects.AddRange(globalSims);

            ContentManager.Neighborhood.LoadCharacters(false);
        }

        /// <summary>
        /// SimsRedux: which file wins a GUID that two files share - higher wins. Overrides (3), shipped mods and
        /// creator content (2), the game's own archives (1), the player's Downloads folder (0).
        /// </summary>
        private static int GuidRank(IContentReference iff)
        {
            var fileIff = iff as FileContentReference<object>;
            if (fileIff == null) return 1; // inside one of the game's .far archives
            var path = (fileIff.Filename ?? "").Replace('\\', '/');
            if (path.IndexOf("redux/mods/Overrides/", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (path.IndexOf("redux/mods/", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("redux/creator/", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 0;
        }

        /// <summary>SimsRedux: the tie-break for files of the same rank - the file's name (the same on every machine).</summary>
        private static string GuidRankKey(IContentReference iff)
        {
            var fileIff = iff as FileContentReference<object>;
            var name = (fileIff != null) ? fileIff.Name : iff.ToString();
            return (name ?? "").Replace('\\', '/').ToLowerInvariant();
        }

        protected override Func<string, GameObjectResource> GenerateResource(GameObjectReference reference)
        {
            return (fname) =>
            {
                /** Better set this up! **/
                IffFile iff = null;

                if (reference.Source == GameObjectSource.Far)
                {
                    iff = GameObjects.Get(reference.FileName.ToLower());
                    iff.InitHash();
                    if (iff != null) iff.RuntimeInfo.Path = reference.FileName;
                }
                else
                {
                    //unused
                    iff = new IffFile(reference.FileName, reference.Source == GameObjectSource.User);
                    iff.InitHash();
                    iff.RuntimeInfo.Path = reference.FileName;
                    iff.RuntimeInfo.State = IffRuntimeState.Standalone;
                }

                if (iff != null)
                {
                    if (iff != null && iff.RuntimeInfo.State == IffRuntimeState.PIFFPatch)
                    {
                        //OBJDs may have changed due to patch. Remove all file references
                        ResetFile(iff);
                    }
                    iff.RuntimeInfo.UseCase = IffUseCase.Object;
                }

                return new GameObjectResource(iff, null, null, reference.FileName, ContentManager);
            };
        }

        public List<ObjectCatalogItem> All()
        {
            var result = new List<ObjectCatalogItem>();
            foreach (var cat in ItemsByCategory)
            {
                result.AddRange(cat);
            }
            return result;
        }

        public List<ObjectCatalogItem> GetItemsByCategory(sbyte category)
        {
            return ItemsByCategory[category];
        }

        public ObjectCatalogItem? GetItemByGUID(uint guid)
        {
            ObjectCatalogItem item;
            if (ItemsByGUID.TryGetValue(guid, out item))
                return item;
            else return null;
        }

        public List<uint> GetUntradableGUIDs()
        {
            return new List<uint>();
        }
    }
}
