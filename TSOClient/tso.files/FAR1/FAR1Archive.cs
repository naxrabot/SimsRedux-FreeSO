using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

using FSO.Common.IO;
namespace FSO.Files.FAR1
{
    /// <summary>
    /// A FAR1 (File Archive v1) archive.
    /// </summary>
    public class FAR1Archive
    {
        private string m_Path;
        private BinaryReader m_Reader;

        private uint m_ManifestOffset;
        private uint m_NumFiles;
        private List<FarEntry> m_Entries = new List<FarEntry>();
        private bool V1b = true;

        /// <summary>
        /// SimsRedux: the path this archive was opened from (used to tell apart two archives that hold a file with the same name).
        /// </summary>
        public string ArchivePath
        {
            get { return m_Path; }
        }

        /// <summary>
        /// The offset into the archive of the manifest.
        /// </summary>
        public uint ManifestOffset
        {
            get { return m_ManifestOffset; }
        }

        /// <summary>
        /// The number of files/entries in the archive.
        /// </summary>
        public uint NumFiles
        {
            get { return m_NumFiles; }
        }

        /// <summary>
        /// Creates a new FAR1Archive instance from a path.
        /// </summary>
        /// <param name="Path">The path to the archive.</param>
        public FAR1Archive(string Path, bool v1b)
        {
            m_Path = Path;
            m_Reader = new BinaryReader(FSOFile.Open(Path, FileMode.Open, FileAccess.Read, FileShare.Read));

            //Magic number - An 8-byte string (not null-terminated), consisting of the ASCII characters "FAR!byAZ"
            var headBytes = m_Reader.ReadBytes(8);
            string Header = Encoding.ASCII.GetString(headBytes);
            //Version - A 4-byte unsigned integer specifying the version; 1a and 1b each specify 1.
            uint Version = m_Reader.ReadUInt32();

            if ((Header != "FAR!byAZ") || (Version != 1))
            {
                // SimsRedux: say which file, and what it starts with (a broken download shows here)
                m_Reader.Dispose();
                throw (new Exception("Archive wasn't a valid FAR V.1 archive! (" + Path + ", starts " + BitConverter.ToString(headBytes) + ", version " + Version + ")"));
            }

            //File table offset - A 4-byte unsigned integer specifying the offset to the file table 
            //from the beginning of the archive.
            m_ManifestOffset = m_Reader.ReadUInt32();
            m_Reader.BaseStream.Seek(m_ManifestOffset, SeekOrigin.Begin);

            m_NumFiles = m_Reader.ReadUInt32();


            for (int i = 0; i < m_NumFiles; i++)
            {

                FarEntry Entry = new FarEntry();
                Entry.DataLength = m_Reader.ReadInt32();
                Entry.DataLength2 = m_Reader.ReadInt32();
                Entry.DataOffset = m_Reader.ReadInt32();
                Entry.FilenameLength = (v1b) ? m_Reader.ReadInt16() : (short)m_Reader.ReadInt32();
                Entry.Filename = Encoding.ASCII.GetString(m_Reader.ReadBytes(Entry.FilenameLength));

                m_Entries.Add(Entry);
            }                  
        }

        /// <summary>
        /// Gets an entry based on a KeyValuePair.
        /// </summary>
        /// <param name="Entry">A KeyValuePair (string, byte[]) representing the entry. The byte array can be null.</param>
        /// <returns>A FarEntry or null if the entry wasn't found.</returns>
        public byte[] GetEntry(KeyValuePair<string, byte[]> Entry)
        {
            foreach (FarEntry Ent in m_Entries)
            {
                if (Ent.Filename == Entry.Key)
                {
                    return ReadEntryData(Ent);
                }
            }

            return null;
        }

        /// <summary>
        /// Gets an entry's data from a FarEntry instance.
        /// </summary>
        /// <param name="Entry">A FarEntry instance.</param>
        /// <returns>The entry's data.</returns>
        public byte[] GetEntry(FarEntry Entry)
        {
            // SimsRedux: we were handed the entry itself, so jump straight to its data instead of
            // searching every entry by name (that made loading a big archive slow at startup).
            if (Entry == null) return null;
            return ReadEntryData(Entry);
        }

        /// <summary>
        /// Reads one entry's bytes from the archive.
        /// </summary>
        private byte[] ReadEntryData(FarEntry Entry)
        {
            // SimsRedux: textures can load on worker threads on Windows, and they all share this one reader,
            // so the seek and the read have to happen together without anyone else moving the position.
            lock (m_Reader)
            {
                var stream = m_Reader.BaseStream;
                long length = stream.Length;
                if (Entry.DataOffset < 0 || Entry.DataOffset > length) return new byte[0];
                stream.Seek(Entry.DataOffset, SeekOrigin.Begin);
                // SimsRedux: never ask for more than the file actually has left, so a damaged archive
                // can't make us allocate gigabytes for one entry.
                long count = Math.Min((long)Math.Max(Entry.DataLength, 0), length - Entry.DataOffset);
                return m_Reader.ReadBytes((int)count);
            }
        }

        /// <summary>
        /// Returns a list of all FarEntry instances in this archive.
        /// </summary>
        /// <returns></returns>
        public List<FarEntry> GetAllFarEntries()
        {
            return m_Entries;
        }

        /// <summary>
        /// Gets all entries in the archive.
        /// </summary>
        /// <returns>A List of KeyValuePair instances.</returns>
        public List<KeyValuePair<string, byte[]>> GetAllEntries()
        {
            List<KeyValuePair<string, byte[]>> Entries = new List<KeyValuePair<string,byte[]>>();

            foreach (FarEntry Entry in m_Entries)
            {
                byte[] Data = ReadEntryData(Entry); // SimsRedux: same locked, size-checked read as GetEntry

                KeyValuePair<string, byte[]> KvP = new KeyValuePair<string, byte[]>(Entry.Filename, Data);
                Entries.Add(KvP);
            }

            return Entries;
        }

        public void Close()
        {
            lock (m_Reader) m_Reader.Close(); // SimsRedux: don't close it out from under a read on another thread
        }
    }
}
