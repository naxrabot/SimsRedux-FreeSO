using FSO.Common.Content;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FSO.Content.Framework
{
    public class TS1Provider
    {
        private FAR1Provider<object> FarProvider;
        private FileProvider<object> FileProvider;
        private Content Manager;
        private Dictionary<string, string[]> BareFoldersByExtension = new Dictionary<string, string[]>()
        {
            // SimsRedux: Downloads also for heads/bodies (.bmp, .skn), floors (.flr) and object sounds (.wav),
            // like the original game
            { ".bmp", new string[] { "GameData/Skins/", "ExpansionShared/SkinsBuy/", "Downloads/" } },
            { ".tga", new string[] { "GameData/Skins/", "ExpansionShared/SkinsBuy/", "Downloads/" } }, // SimsRedux: some skins are .tga
            { ".cmx", new string[] { "GameData/Skins/", "ExpansionShared/SkinsBuy/", "Downloads/" } }, // downloads for animations
            { ".skn", new string[] { "GameData/Skins/", "ExpansionShared/SkinsBuy/", "Downloads/" } },
            { ".iff", new string[] { "Downloads/" } },
            { ".flr", new string[] { "Downloads/" } },
            { ".wll", new string[] { "Downloads/" } }, // SimsRedux: custom walls (the creator's too)
            { ".wav", new string[] { "Downloads/" } },

            { ".bcf", new string[] { "Downloads/" } }, //for animations
            { ".cfp", new string[] { "Downloads/" } }
        };

        private Dictionary<string, FileProvider<object>> FileProvidersByRegex = new Dictionary<string, FileProvider<object>>();

        public TS1Provider(Content contentManager)
        {
            // (SimsRedux: "$" so only real .far files count - "x.far.bak" used to be opened as an archive too)
            FarProvider = new FAR1Provider<object>(contentManager, null, new Regex(@"\.far$", RegexOptions.IgnoreCase), true);
            Manager = contentManager;
            // (still used: the neighbourhood map pictures are looked up by name through Get below)
            FileProvider = new FileProvider<object>(contentManager, null, new Regex(@".*"));
            //todo: files provider?
        }
        
        public void Init()
        {
            FarProvider.Init();
            FileProvider.UseTS1 = true;
            FileProvider.Init();
        }

        public Dictionary<string, IContentReference> BuildDictionary(string ext, string exclude)
        {
            var entries = FarProvider.GetEntriesForExtension(ext);
            var result = new Dictionary<string, IContentReference>();
            
            string[] folders = null;
            if (BareFoldersByExtension.TryGetValue(ext, out folders))
            {
                foreach (var folder in folders)
                {
                    var test = Manager.TS1AllFiles;
                    var regexStr = FolderPattern(folder, ext);
                    FileProvider<object> provider;
                    if (!FileProvidersByRegex.TryGetValue(regexStr, out provider))
                    {
                        // SimsRedux: any capitals (gamedata/skins, .BMP...) - a Sims folder copied from a Mac,
                        // or a download, isn't always spelled like the Windows game
                        var regex = new Regex(regexStr, RegexOptions.IgnoreCase);
                        provider = new FileProvider<object>(Manager, null, regex);
                        provider.UseTS1 = true;
                        provider.Init();
                        FileProvidersByRegex[regexStr] = provider;
                    }
                    var entries2 = provider.List();
                    foreach (var entry in entries2)
                    {
                        result[entry.ToString()] = entry;
                    }
                }
            }

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    var name = Path.GetFileName(entry.FarEntry.Filename.ToLowerInvariant().Replace('\\', '/'));
                    if (name.Contains(exclude)) continue;
                    result[name] = entry;
                }
            }

            // SimsRedux: shipped mods that replace the game's own files of the same name (they win over
            // the game's archives, like a loose file in the original game's expansion folders)
            {
                var regexStr = "^" + Regex.Escape(Content.ReduxModTag + "Overrides/") + ".*" + Regex.Escape(ext) + "$"; // (SimsRedux: whole path, so "x.iff.bak" isn't picked up)
                FileProvider<object> provider;
                if (!FileProvidersByRegex.TryGetValue(regexStr, out provider))
                {
                    provider = new FileProvider<object>(Manager, null, new Regex(regexStr, RegexOptions.IgnoreCase));
                    provider.UseTS1 = true;
                    provider.Init();
                    FileProvidersByRegex[regexStr] = provider;
                }
                foreach (var entry in provider.List()) result[entry.ToString()] = entry;
            }

            /*
            var entries3 = FileProvider.GetEntriesForExtension(ext);
            foreach (var entry in entries3)
            {
                var name = Path.GetFileName(entry.Name.ToLowerInvariant().Replace('\\', '/'));
                if (name.Contains(exclude)) continue;
                result[name] = entry;
            }*/

            return result;
        }

        /// <summary>
        /// SimsRedux: the pattern for files of one type in one folder. The folder has to start a path part (so
        /// "Downloads/" matches the game's Downloads folder and the shipped "~redux-mods/Downloads/", both relative
        /// paths with forward slashes), and the name has to end in the extension - "x.iff.bak" or "x.bmp.old"
        /// left in Downloads used to be loaded as if they were the real thing.
        /// </summary>
        private static string FolderPattern(string folder, string ext)
        {
            return "(^|/)" + Regex.Escape(folder) + ".*" + Regex.Escape(ext) + "$";
        }

        public object Get(string item) {
            return FarProvider.Get(item) ?? FileProvider.Get(item);
        }
    }

    public class TS1SubProvider<T> : IContentProvider<T>
    {
        private TS1Provider BaseProvider;
        private Dictionary<string, IContentReference> Entries;
        private string[] Extensions;
        private Func<object, T> Converter;

        public TS1SubProvider(TS1Provider baseProvider, string extension, Func<object, T> converter) : this(baseProvider, extension)
        {
            Converter = converter;
        }

        public TS1SubProvider(TS1Provider baseProvider, string extension)
        {
            BaseProvider = baseProvider;
            Extensions = new string[] { extension };
            Converter = x => (T)x;
        }

        public TS1SubProvider(TS1Provider baseProvider, string[] extensions)
        {
            BaseProvider = baseProvider;
            Extensions = extensions;
            Converter = x => (T)x;
        }

        public IEnumerable<string> GetAllNames()
        {
            return Entries.Keys;
        }

        public virtual void Init()
        {
            Entries = BaseProvider.BuildDictionary(Extensions[0], "globals");

            for (int i=1; i<Extensions.Length; i++)
            {
                var ents = BaseProvider.BuildDictionary(Extensions[i], "globals");
                foreach (var item in ents) Entries.Add(item.Key, item.Value);
            }
        }

        public T Get(ulong id)
        {
            throw new NotImplementedException();
        }

        public virtual T Get(string name)
        {
            IContentReference result = null;
            if (Entries.TryGetValue(name, out result))
            {
                return Converter(result.GetGeneric());
            }

            return default(T);
        }

        /// <summary>
        /// SimsRedux: the file read and decoded without going into the content cache (for data the caller keeps its
        /// own copy of - e.g. sounds, which the sound card keeps). Falls back to the cached Get if that fails.
        /// </summary>
        public virtual T ThrowawayGet(string name)
        {
            IContentReference result = null;
            if (Entries.TryGetValue(name, out result))
            {
                try
                {
                    var item = result.GetThrowawayGeneric();
                    if (item != null) return Converter(item);
                }
                catch { }
                return Converter(result.GetGeneric());
            }

            return default(T);
        }

        public T Get(uint type, uint fileID)
        {
            throw new NotImplementedException();
        }

        public List<IContentReference<T>> List()
        {
            throw new NotImplementedException();
        }

        public List<IContentReference> ListGeneric()
        {
            return new List<IContentReference>(Entries.Values);
        }

        public T Get(ContentID id)
        {
            if (id.FileName != null) return Get(id.FileName);
            throw new NotImplementedException();
        }
    }
}
