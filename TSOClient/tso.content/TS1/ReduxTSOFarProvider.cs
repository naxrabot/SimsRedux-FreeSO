using System;
using System.Collections.Generic;
using System.IO;
using FSO.Common.IO;
using FSO.Common.Utils;
using FSO.Content.Codecs;
using FSO.Content.Framework;
using FSO.Files.FAR1;
using FSO.Files.Formats.IFF;

namespace FSO.Content.TS1
{
    /// <summary>
    /// SimsRedux: The Sims Online's walls or floors, straight from its archives
    /// (housedata/walls/walls.far, walls2..4, and the same for floors) in the TSO folder
    /// (Content.TSOBasePath). Each file is offered as "tso_" + its name ("tso_asian01.wll"), so it
    /// never shares a name with a Sims 1 or custom one - and that name is what lots save it under.
    ///
    /// Archives are opened through the game's file layer (FSOFile), so the browser reads a big one a
    /// piece at a time. Files read are kept for a while (a timed cache, like the other providers).
    /// A broken or missing archive is left out (and reported), the rest still load.
    /// </summary>
    public class ReduxTSOFarProvider : FAR1Provider<IffFile>
    {
        public const string Prefix = "tso_";
        private readonly string Kind;
        private readonly string Ext;

        /// <summary>How many archives were loaded, and their size (for the online content stamp).</summary>
        public int ArchiveCount;
        public long ArchiveBytes;
        public int EntryCount;

        /// <param name="kind">"walls" or "floors" (the housedata folder and archive names)</param>
        /// <param name="ext">".wll" or ".flr"</param>
        public ReduxTSOFarProvider(Content contentManager, string kind, string ext) : base(contentManager, new IffCodec(), new string[0])
        {
            Kind = kind;
            Ext = ext;
        }

        public void InitTSO()
        {
            Cache = new TimedReferenceCache<string, IffFile>();
            EntriesByName = new Dictionary<string, Far1ProviderEntry<IffFile>>();
            EntriesOfType = new Dictionary<string, List<Far1ProviderEntry<IffFile>>>();

            foreach (var n in new[] { "", "2", "3", "4" })
            {
                var rel = "housedata/" + Kind + n + "/" + Kind + n + ".far";
                var path = Content.TSOPath(rel);
                FAR1Archive archive;
                List<FarEntry> entries;
                try
                {
                    if (!FSOFile.Exists(path)) { Console.WriteLine("[SimsRedux] Sims Online: no " + rel); continue; }
                    archive = new FAR1Archive(path, true);
                    entries = archive.GetAllFarEntries();
                }
                catch (Exception e)
                {
                    Console.WriteLine("[SimsRedux] skipped a broken Sims Online archive: " + rel + ": " + e.Message);
                    try { ReportProblem?.Invoke("broken Sims Online file", rel + ": " + e.Message); } catch { }
                    continue;
                }

                int added = 0;
                foreach (var entry in entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Filename)) continue;
                    if (entry.DataOffset < 0 || entry.DataLength <= 0) continue;
                    var file = entry.Filename.Replace('\\', '/');
                    file = file.Substring(file.LastIndexOf('/') + 1).ToLowerInvariant();
                    if (!file.EndsWith(Ext)) continue;
                    var name = Prefix + file;
                    if (EntriesByName.ContainsKey(name)) continue; // (the first archive's copy wins)

                    // the same data under its new name (the archive reads an entry by its offset and size only)
                    var renamed = new FarEntry
                    {
                        DataLength = entry.DataLength,
                        DataLength2 = entry.DataLength2,
                        DataOffset = entry.DataOffset,
                        Filename = name,
                        FilenameLength = (short)name.Length
                    };
                    var item = new Far1ProviderEntry<IffFile>(this) { Archive = archive, FarEntry = renamed };
                    EntriesByName[name] = item;
                    List<Far1ProviderEntry<IffFile>> group;
                    if (!EntriesOfType.TryGetValue(Ext, out group))
                    {
                        group = new List<Far1ProviderEntry<IffFile>>();
                        EntriesOfType[Ext] = group;
                    }
                    group.Add(item);
                    added++;
                }

                long size = 0;
                try { size = archive.ArchiveLength; } catch { }
                ArchiveCount++;
                ArchiveBytes += size;
                EntryCount += added;
            }
            Console.WriteLine("[SimsRedux] Sims Online " + Kind + ": " + EntryCount + " from " + ArchiveCount + " archives");
        }
    }
}
