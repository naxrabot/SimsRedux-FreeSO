using FSO.Common;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FSO.Common.IO;
namespace FSO.Content.TS1
{
    /// <summary>
    /// Provides families, neighbors, neighborhood structure and more given a current userdata folder. Should also allow the game to save these things.
    /// </summary>
    public class TS1NeighborhoodProvider
    {
        //injected from game. requires instantiating a vm and getting the person data after Init runs.
        public Func<uint, short[]> PreparePersonDataFromObject;

        public IffFile MainResource;
        public IffFile LotLocations;
        public IffFile StreetNames;
        public IffFile NeighbourhoodDesc;
        public IffFile STDesc;
        public IffFile MTDesc;
        public Dictionary<short, short> ZoningDictionary = new Dictionary<short, short>();
        public NBRS Neighbors;
        public NGBH Neighborhood;
        public TATT TypeAttributes;
        public Dictionary<short, FAMI> FamilyForHouse = new Dictionary<short, FAMI>();
        public Content ContentManager;
        public TS1GameState GameState = new TS1GameState();
        public string UserPath;
        public int NextSim;

        public HashSet<uint> DirtyAvatars = new HashSet<uint>();

        public TS1NeighborhoodProvider(Content contentManager)
        {
            ContentManager = contentManager;
            InitSpecific(0);
        }

        /// <summary>
        /// Intializes a specific neighbourhood. Also counts as a save discard, since it unloads the current neighbourhood.
        /// </summary>
        /// <param name="id"></param>
        public void InitSpecific(int id)
        {
            DirtyAvatars.Clear();
            ZoningDictionary.Clear();
            FamilyForHouse.Clear();

            var udName = "UserData" + ((id == 0) ? "" : (id + 1).ToString());
            //simitone shouldn't modify existing ts1 data, since our house saves are incompatible.
            //therefore we should copy to the simitone user data.

            var userPath = Path.Combine(FSOEnvironment.UserDir, udName + "/");

            if (!FSODirectory.Exists(userPath))
            {
                var source = Path.Combine(ContentManager.TS1BasePath, udName + "/");
                // SimsRedux: start from the game's untouched starting neighbourhood (TemplateUserData)
                // rather than the player's own Sims 1 save, so a new game is always a fresh game.
                var template = Path.Combine(ContentManager.TS1BasePath, "TemplateUserData/");
                bool usedTemplate = false;
                // SimsRedux: Neighbourhood 1 starts with the game's own starting families (the Goths,
                // Newbies, Pleasants...) when the game's UserData has them; otherwise the blank template
                if (TemplateIsUsable(template) && !(id == 0 && HasFamiliesInHouses(source))) { source = template; usedTemplate = true; }
                var destination = userPath;

                //quick and dirty copy.

                foreach (string dirPath in FSODirectory.GetDirectories(source, "*",
                    SearchOption.AllDirectories))
                    FSODirectory.CreateDirectory(dirPath.Replace('\\', '/').Replace(source, destination));

                foreach (string newPath in FSODirectory.GetFiles(source, "*.*",
                    SearchOption.AllDirectories))
                    FSOFile.Copy(newPath, newPath.Replace('\\', '/').Replace(source, destination), true);

                // the template can be missing the expansion packs' lot description files; borrow
                // just those from the normal UserData (they only hold lot names and descriptions)
                if (usedTemplate)
                {
                    var normal = Path.Combine(ContentManager.TS1BasePath, udName + "/");
                    foreach (var name in new[] { "Houses/NeighborhoodDesc.iff", "Houses/STDesc.iff", "Houses/MTDesc.iff" })
                    {
                        var to = Path.Combine(destination, name);
                        var from = Path.Combine(normal, name);
                        if (!FSOFile.Exists(to) && FSOFile.Exists(from))
                        {
                            FSODirectory.CreateDirectory(Path.GetDirectoryName(to));
                            FSOFile.Copy(from, to, true);
                        }
                    }
                }
            }

            LoadFolder(userPath);
        }

        /// <summary>
        /// SimsRedux: loads the neighbourhood (families, neighbours, zoning, Sims) from a folder.
        /// Normally the player's own UserData; while visiting a friend online it's a copy of the
        /// host's neighbourhood, so every player's game reads the same Sims and families.
        /// </summary>
        public void LoadFolder(string userPath)
        {
            DirtyAvatars.Clear();
            ZoningDictionary.Clear();
            FamilyForHouse.Clear();
            UserPath = userPath;

            MainResource = new IffFile(Path.Combine(UserPath, "Neighborhood.iff"));
            LotLocations = new IffFile(Path.Combine(UserPath, "LotLocations.iff"));
            var lotZoning = new IffFile(Path.Combine(UserPath, "LotZoning.iff"));
            StreetNames = new IffFile(Path.Combine(UserPath, "StreetNames.iff"));
            NeighbourhoodDesc = new IffFile(Path.Combine(UserPath, "Houses/NeighborhoodDesc.iff"));
            STDesc = new IffFile(Path.Combine(UserPath, "Houses/STDesc.iff"));
            MTDesc = new IffFile(Path.Combine(UserPath, "Houses/MTDesc.iff"));

            var zones = lotZoning.Get<STR>(1);
            for (int i = 0; i < zones.Length; i++)
            {
                var split = zones.GetString(i).Split(',');
                ZoningDictionary[short.Parse(split[0])] = (short)((split[1] == " community") ? 1 : 0);
            }
            Neighbors = MainResource.List<NBRS>().FirstOrDefault();
            Neighborhood = MainResource.List<NGBH>().FirstOrDefault();
            TypeAttributes = MainResource.List<TATT>().FirstOrDefault();

            FamilyForHouse = new Dictionary<short, FAMI>();
            var families = MainResource.List<FAMI>();
            foreach (var fam in families)
            {
                FamilyForHouse[(short)fam.HouseNumber] = fam;
            }

            LoadCharacters(true);
            //todo: manage avatar iffs here
        }

        /// <summary>
        /// SimsRedux: the game's untouched starting neighbourhood can only be used if it has the
        /// neighbour list and neighbourhood data in it (otherwise fall back to the normal UserData).
        /// </summary>
        private static bool TemplateIsUsable(string template)
        {
            try
            {
                var nbhd = Path.Combine(template, "Neighborhood.iff");
                if (!FSODirectory.Exists(template) || !FSOFile.Exists(nbhd)) return false;
                if (!FSOFile.Exists(Path.Combine(template, "LotLocations.iff")) || !FSOFile.Exists(Path.Combine(template, "LotZoning.iff"))) return false;
                var iff = new IffFile(nbhd);
                return iff.List<NBRS>()?.Count > 0 && iff.List<NGBH>()?.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>SimsRedux: does this neighbourhood folder have any family living in a house?</summary>
        public static bool HasFamiliesInHouses(string folder)
        {
            try
            {
                var nbhd = Path.Combine(folder, "Neighborhood.iff");
                if (!FSOFile.Exists(nbhd)) return false;
                var iff = new IffFile(nbhd);
                return (iff.List<FAMI>() ?? new List<FAMI>()).Any(f => f.HouseNumber > 0);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>SimsRedux: the folder a brand-new Neighbourhood 1 is copied from.</summary>
        public string FreshSource()
        {
            var game = Path.Combine(ContentManager.TS1BasePath, "UserData/");
            if (HasFamiliesInHouses(game)) return game;
            var template = Path.Combine(ContentManager.TS1BasePath, "TemplateUserData/");
            return TemplateIsUsable(template) ? template : game;
        }

        public void AddMissingNeighbors()
        {
            var objs = (TS1ObjectProvider)ContentManager.WorldObjects;
            var missing = objs.PersonGUIDs.Where(x => !Neighbors.Entries.Any(y => y.GUID == x)).Select(x => objs.Get(x)).ToList();
            foreach (var obj in missing)
            {
                if (obj == null) continue; // SimsRedux: a Sim whose character file isn't loaded (e.g. a friend's, after visiting online)
                var id = Neighbors.GetFreeID();
                Neighbors.AddNeighbor(new Neighbour()
                {
                    NeighbourID = id,
                    GUID = (uint)obj.GUID,
                    Name = Path.GetFileName(obj.Resource.Name).ToLowerInvariant().Replace(".iff", ""),
                    PersonData = PreparePersonDataFromObject((uint)obj.GUID),
                    PersonMode = 9,
                    Relationships = new Dictionary<int, List<short>>()
                });
            }
        }

        public void LoadCharacters(bool clearLast)
        {
            var objs = (TS1ObjectProvider)ContentManager.WorldObjects;
            if (objs.Entries == null) return;
            if (clearLast)
            {
                foreach (var obj in objs.Entries.Where(x => x.Value.Source == GameObjectSource.User).ToList())
                {
                    objs.RemoveObject((uint)obj.Key);
                    objs.PersonGUIDs.Remove((uint)obj.Key); // SimsRedux: was Add, which left unloaded Sims in the list
                }
            }

            NextSim = 0;
            var path = Path.Combine(UserPath, "Characters/");
            var files = FSODirectory.EnumerateFiles(path);
            foreach (var filename in files)
            {
                if (!string.Equals(Path.GetExtension(filename), ".iff", StringComparison.OrdinalIgnoreCase)) continue; // SimsRedux: skip other files (was: stop reading the folder)

                int userID;
                var name = Path.GetFileName(filename);
                if (name.Length > 8 && int.TryParse(name.Substring(4, 5), out userID) && userID >= NextSim)
                {
                    NextSim = userID + 1;
                }

                RegisterCharacterFile(filename);
            }
        }

        /// <summary>
        /// SimsRedux: makes the Sims in one character file (UserXXXXX.iff) available to the game.
        /// Returns the GUIDs it found. Replaces any Sim already loaded with the same GUID.
        /// </summary>
        public List<uint> RegisterCharacterFile(string filename)
        {
            var found = new List<uint>();
            var objs = (TS1ObjectProvider)ContentManager.WorldObjects;
            var file = new IffFile(filename);
            file.MarkThrowaway();

            var objects = file.List<OBJD>();
            if (objects != null)
            {
                foreach (var obj in objects)
                {
                    if (objs.Entries.ContainsKey(obj.GUID)) objs.RemoveObject(obj.GUID);
                    objs.Entries[obj.GUID] = new GameObjectReference(objs)
                    {
                        FileName = filename,
                        ID = obj.GUID,
                        Name = obj.ChunkLabel,
                        Source = GameObjectSource.User,
                        Group = (short)obj.MasterID,
                        SubIndex = obj.SubIndex
                    };
                    if (obj.ObjectType == OBJDType.Person) objs.PersonGUIDs.Add(obj.GUID);
                    found.Add(obj.GUID);
                }
            }
            return found;
        }

        public void SaveNewNeighbour(GameObject obj)
        {
            var objs = (TS1ObjectProvider)ContentManager.WorldObjects;
            //save to a new user iff
            var path = Path.Combine(UserPath, "Characters/");
            var filename = Path.Combine(path, "User" + (NextSim++).ToString().PadLeft(5, '0') + ".iff");
            using (var stream = FSOFile.Open(filename, FileMode.Create))
                obj.Resource.MainIff.Write(stream);

            objs.Entries[obj.OBJ.GUID] = new GameObjectReference(objs)
            {
                FileName = filename,
                ID = obj.GUID,
                Name = obj.OBJ.ChunkLabel,
                Source = GameObjectSource.User,
                Group = (short)obj.OBJ.MasterID,
                SubIndex = obj.OBJ.SubIndex
            };
        }

        public Neighbour GetNeighborByID(short ID)
        {
            Neighbour result = null;
            Neighbors.NeighbourByID.TryGetValue(ID, out result);
            return result;
        }

        public FAMI GetFamilyForHouse(short ID)
        {
            FAMI result = null;
            FamilyForHouse.TryGetValue(ID, out result);
            return result;
        }

        public int GetMagicoinsForNeighbor(short ID)
        {
            return GetInventoryByNID(ID)?.FirstOrDefault(x => x.GUID == 0x99E81BEC)?.Count ?? 0;
        }

        public int GetMagicoinsForFamily(FAMI family)
        {
            if (family == null) return 0;
            return family.FamilyGUIDs.Select(x => GetMagicoinsForNeighbor(GetNeighborIDForGUID(x) ?? -1)).Sum();
        }

        public void MoveOut(short houseID)
        {
            var old = GetFamilyForHouse(houseID);
            old.HouseNumber = 0;
            FamilyForHouse.Remove(houseID);
        }

        public void SetFamilyForHouse(short houseID, FAMI family, bool buy)
        {
            family.HouseNumber = houseID;
            if (buy)
            {
                family.Budget -= GetHouse(houseID)?.Get<SIMI>(1)?.PurchaseValue ?? 0;
            }
            FamilyForHouse[houseID] = family;
        }

        public FAMI GetFamily(ushort ID)
        {
            return MainResource.Get<FAMI>(ID);
        }

        public FAMs GetFamilyString(ushort ID)
        {
            return MainResource.Get<FAMs>(ID);
        }

        public short? GetNeighborIDForGUID(uint GUID)
        {
            short result = 0;
            if (Neighbors.DefaultNeighbourByGUID.TryGetValue(GUID, out result))
                return result;
            return null;
        }

        public List<InventoryItem> GetInventoryByNID(short ID)
        {
            List<InventoryItem> result = null;
            Neighborhood.InventoryByID.TryGetValue(ID, out result);
            return result;
        }

        public void SetInventoryForNID(short ID, List<InventoryItem> list)
        {
            Neighborhood.InventoryByID[ID] = list;
        }

        public short SetToNext(short current)
        {
            //short form: if we can find current, then we can get its entry index and add 1.
            var currentN = GetNeighborByID(current);
            if (currentN != null)
            {
                var id = currentN.RuntimeIndex + 1;
                return (short)((id == Neighbors.Entries.Count) ? -1 : Neighbors.Entries[id]?.NeighbourID ?? -1);
            }
            return (short)(Neighbors.Entries.FirstOrDefault(x => x.NeighbourID > current)?.NeighbourID ?? -1);
        }

        public short SetToNext(short current, uint guid)
        {
            IEnumerable<Neighbour> search = Neighbors.Entries;
            var firstNID = GetNeighborIDForGUID(guid);
            if (firstNID != null)
            {
                var firstN = GetNeighborByID(firstNID.Value);
                if (firstN != null)
                    search = search.Skip(firstN.RuntimeIndex);
            }
            return (short)(search.FirstOrDefault(x => x.NeighbourID > current && x.GUID == guid)?.NeighbourID ?? -1);
        }

        public void AvatarChanged(uint guid)
        {
            DirtyAvatars.Add(guid);
        }

        /// <summary>
        /// SimsRedux online town: set by a lot on the dedicated server. Several lots (each its own
        /// program) share the town's Neighborhood.iff, so a lot may only write what's its own: this
        /// says which neighbours (Sims) and families those are right now. Everything else is taken
        /// from the file as it is on disk at the moment of saving, so lots never undo each other.
        /// </summary>
        public Func<Tuple<HashSet<short>, HashSet<int>>> SharedTownOwnership;

        public bool SaveNeighbourhood(bool withSims)
        {
            var shared = SharedTownOwnership;
            if (shared == null) return WriteNeighbourhood(withSims);

            Tuple<HashSet<short>, HashSet<int>> mine;
            try { mine = shared(); }
            catch (Exception e) { Console.WriteLine("[SimsRedux] couldn't tell which Sims are this lot's: " + e.Message); mine = null; }
            using (var townLock = LockTownFile())
            {
                if (townLock == null) Console.WriteLine("[SimsRedux] the town file stayed busy - saving anyway");
                if (mine != null)
                {
                    try { PullOthersFromDisk(mine.Item1, mine.Item2); }
                    catch (Exception e) { Console.WriteLine("[SimsRedux] couldn't read the town's latest neighbourhood: " + e.Message); }
                }
                // written next to it first, then swapped in, so nobody ever reads half a file
                var path = Path.Combine(UserPath, "Neighborhood.iff");
                var temp = path + ".saving";
                if (withSims) WriteDirtyAvatars();
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    MainResource.Write(stream);
                File.Copy(temp, path, true);
                try { File.Delete(temp); } catch { }
                return true;
            }
        }

        /// <summary>Only one lot at a time reads-and-writes the town's neighbourhood (a lock file next to it).</summary>
        private IDisposable LockTownFile()
        {
            var path = Path.Combine(UserPath, "Neighborhood.lock");
            var until = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (DateTime.UtcNow > until) return null;
                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        /// <summary>
        /// Everything that isn't this lot's (other families, other Sims, their things) is brought up to
        /// date from the town file on disk - another lot may have changed it since this one opened.
        /// </summary>
        private void PullOthersFromDisk(HashSet<short> myNeighbours, HashSet<int> myFamilies)
        {
            var path = Path.Combine(UserPath, "Neighborhood.iff");
            if (!File.Exists(path)) return;
            var disk = new IffFile(path);

            foreach (var df in disk.List<FAMI>() ?? new List<FAMI>())
            {
                if (myFamilies.Contains(df.ChunkID)) continue;
                var fam = MainResource.Get<FAMI>(df.ChunkID);
                if (fam == null)
                {
                    // a family that arrived in the town on another lot
                    MainResource.AddChunk(df);
                    var dname = disk.Get<FAMs>(df.ChunkID);
                    if (dname != null && MainResource.Get<FAMs>(df.ChunkID) == null) MainResource.AddChunk(dname);
                    continue;
                }
                fam.HouseNumber = df.HouseNumber;
                fam.FamilyNumber = df.FamilyNumber;
                fam.Budget = df.Budget;
                fam.ValueInArch = df.ValueInArch;
                fam.FamilyFriends = df.FamilyFriends;
                fam.Unknown = df.Unknown;
                fam.FamilyGUIDs = df.FamilyGUIDs;
            }
            FamilyForHouse = new Dictionary<short, FAMI>();
            foreach (var fam in MainResource.List<FAMI>() ?? new List<FAMI>())
                FamilyForHouse[(short)fam.HouseNumber] = fam;

            var dn = disk.List<NBRS>()?.FirstOrDefault();
            if (dn != null && Neighbors != null)
            {
                foreach (var d in dn.Entries)
                {
                    if (myNeighbours.Contains(d.NeighbourID)) continue;
                    if (Neighbors.NeighbourByID.TryGetValue(d.NeighbourID, out var n))
                    {
                        // (what a visiting Sim thinks of this lot's Sims was made here: that part stays)
                        var keep = n.Relationships?.Where(kv => myNeighbours.Contains((short)kv.Key)).ToList();
                        n.Name = d.Name;
                        n.PersonMode = d.PersonMode;
                        n.PersonData = d.PersonData;
                        n.Relationships = (d.Relationships == null) ? null : new Dictionary<int, List<short>>(d.Relationships);
                        if (keep != null && n.Relationships != null) foreach (var kv in keep) n.Relationships[kv.Key] = kv.Value;
                    }
                    else Neighbors.AddNeighbor(d);
                }
            }

            var dg = disk.List<NGBH>()?.FirstOrDefault();
            if (dg != null && Neighborhood != null)
            {
                foreach (var kv in dg.InventoryByID)
                    if (!myNeighbours.Contains(kv.Key)) Neighborhood.InventoryByID[kv.Key] = kv.Value;
            }
        }

        private void WriteDirtyAvatars()
        {
            foreach (var ava in DirtyAvatars)
            {
                var obj = ContentManager.WorldObjects.Get(ava);
                if (obj != null)
                {
                    using (var stream = FSOFile.Open(obj.Resource.Name, FileMode.Create))
                        obj.Resource.MainIff.Write(stream);
                }
            }
            DirtyAvatars.Clear();
        }

        private bool WriteNeighbourhood(bool withSims)
        {
            if (withSims) WriteDirtyAvatars();

            // SimsRedux: written next to it first, then swapped in - a save cut short (the game closed,
            // the lot stopped) leaves the last good neighbourhood instead of half a file
            var path = Path.Combine(UserPath, "Neighborhood.iff");
            var temp = path + ".saving";
            using (var stream = FSOFile.Open(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                MainResource.Write(stream);
            FSOFile.Move(temp, path, true);

            return true;
        }

        /// <summary>
        /// SimsRedux: is this lot a community lot? (Lots missing from LotZoning.iff count as
        /// community lots, except Downtown's, same as the rest of the game.)
        /// </summary>
        public bool IsCommunity(short houseID)
        {
            short zone;
            if (!ZoningDictionary.TryGetValue(houseID, out zone))
                zone = (short)((houseID >= 81 && houseID <= 89) ? 2 : 1);
            return zone > 0;
        }

        /// <summary>
        /// SimsRedux: rezones a lot (residential or community) and saves LotZoning.iff.
        /// </summary>
        public bool SetZone(short houseID, bool community)
        {
            ZoningDictionary[houseID] = (short)(community ? 1 : 0);
            try
            {
                var path = Path.Combine(UserPath, "LotZoning.iff");
                var iff = new IffFile(path);
                var str = iff.Get<STR>(1);
                if (str == null) return false;
                var entry = houseID.ToString() + (community ? ", community" : ", residential");
                bool found = false;
                for (int i = 0; i < str.Length; i++)
                {
                    var split = str.GetString(i).Split(',');
                    short id;
                    if (split.Length > 0 && short.TryParse(split[0].Trim(), out id) && id == houseID)
                    {
                        str.SetString(i, entry);
                        found = true;
                        break;
                    }
                }
                if (!found) str.InsertString(str.Length, new STRItem(entry));
                using (var stream = FSOFile.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    iff.Write(stream);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] Couldn't save the lot zoning: " + e.Message);
                return false;
            }
        }

        public bool SaveHouse(int houseID, IffFile file)
        {
            using (var stream = FSOFile.Open(GetHousePath(houseID), FileMode.Create, FileAccess.Write, FileShare.None))
                file.Write(stream);

            return true;
        }

        public IffFile GetHouse(int id)
        {
            return new IffFile(Path.Combine(UserPath, "Houses/House"+id.ToString().PadLeft(2, '0')+".iff"));
        }

        public string GetHousePath(int id)
        {
            return Path.Combine(UserPath, "Houses/House" + id.ToString().PadLeft(2, '0') + ".iff");
        }

        public BMP GetHouseThumb(int id)
        {
            return GetHouse(id)?.Get<BMP>(512); //roof on
        }

        public short GetTATT(uint guid, int index)
        {
            short[] dat = null;
            if (TypeAttributes.TypeAttributesByGUID.TryGetValue(guid, out dat))
            {
                if (index >= dat.Length) return 0;
                else return dat[index];
            }
            return 0;
        }

        public Tuple<string, string> GetHouseNameDesc(int houseID)
        {
            STR res;
            if (houseID < 80) res = NeighbourhoodDesc.Get<STR>((ushort)(houseID + 2000));
            else if (houseID < 90) res = STDesc.Get<STR>((ushort)(houseID + 2000));
            else res = MTDesc.Get<STR>((ushort)(houseID + 2000));

            if (res == null) return new Tuple<string, string>("", "");
            else return new Tuple<string, string>(res.GetString(0), res.GetString(1));
        }

        public void SetTATT(uint guid, int index, short value)
        {
            short[] dat = null;
            if (!TypeAttributes.TypeAttributesByGUID.TryGetValue(guid, out dat))
            {
                var obj = ContentManager.WorldObjects.Get(guid);
                if (obj == null) return;
                dat = new short[32];
                TypeAttributes.TypeAttributesByGUID[guid] = dat;
            }
            if (index >= dat.Length) return;
            else dat[index] = value;
        }
    }

    public class TS1GameState
    {
        public FAMI ActiveFamily;
        public uint DowntownSimGUID;
        public short LotTransitInfo;
    }
}
