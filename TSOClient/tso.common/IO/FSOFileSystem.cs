using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FSO.Common.IO
{
    /// <summary>
    /// SimsRedux: every file the game reads or writes goes through here instead of straight to
    /// System.IO. On Windows/Mac/Linux it's the normal disk (DiskFileSystem). In the web browser
    /// version the same calls will be answered from the player's own Sims 1 files stored in the
    /// browser, so the rest of the game doesn't need to know where it's running.
    ///
    /// A backend only has to implement this small interface; FSOFile and FSODirectory build the
    /// familiar File.* / Directory.* helpers (ReadAllBytes, WriteAllText, ...) on top of it.
    /// </summary>
    public interface IFSOFileSystem
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        Stream Open(string path, FileMode mode, FileAccess access, FileShare share);
        void DeleteFile(string path);
        void CreateDirectory(string path);
        void DeleteDirectory(string path, bool recursive);
        string[] GetFiles(string path, string searchPattern, SearchOption option);
        string[] GetDirectories(string path, string searchPattern, SearchOption option);
        void MoveFile(string source, string dest, bool overwrite);
    }

    /// <summary>The normal disk. Used on desktop (and anywhere a real file system exists).</summary>
    public class DiskFileSystem : IFSOFileSystem
    {
        public bool FileExists(string path) { return File.Exists(path); }
        public bool DirectoryExists(string path) { return Directory.Exists(path); }
        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share) { return new FileStream(path, mode, access, share); }
        public void DeleteFile(string path) { File.Delete(path); }
        public void CreateDirectory(string path) { Directory.CreateDirectory(path); }
        public void DeleteDirectory(string path, bool recursive) { Directory.Delete(path, recursive); }
        public string[] GetFiles(string path, string searchPattern, SearchOption option) { return Directory.GetFiles(path, searchPattern, option); }
        public string[] GetDirectories(string path, string searchPattern, SearchOption option) { return Directory.GetDirectories(path, searchPattern, option); }
        public void MoveFile(string source, string dest, bool overwrite) { File.Move(source, dest, overwrite); }
    }

    /// <summary>The file system the game is using right now (the disk unless the platform swaps it).</summary>
    public static class FSOFileSystem
    {
        public static IFSOFileSystem Current = new DiskFileSystem();
    }

    /// <summary>Drop-in replacement for System.IO.File that goes through FSOFileSystem.Current.</summary>
    public static class FSOFile
    {
        private static IFSOFileSystem FS { get { return FSOFileSystem.Current; } }

        public static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return FS.FileExists(path);
        }

        public static Stream Open(string path, FileMode mode)
        {
            return FS.Open(path, mode, (mode == FileMode.Append) ? FileAccess.Write : FileAccess.ReadWrite, FileShare.None);
        }
        public static Stream Open(string path, FileMode mode, FileAccess access) { return FS.Open(path, mode, access, FileShare.None); }
        public static Stream Open(string path, FileMode mode, FileAccess access, FileShare share) { return FS.Open(path, mode, access, share); }
        public static Stream Open(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) { return FS.Open(path, mode, access, share); }

        public static Stream OpenRead(string path) { return FS.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        public static Stream OpenWrite(string path) { return FS.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
        public static Stream Create(string path) { return FS.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None); }
        public static StreamReader OpenText(string path) { return new StreamReader(OpenRead(path)); }

        public static byte[] ReadAllBytes(string path)
        {
            using (var stream = OpenRead(path))
            {
                if (stream is MemoryStream mem) return mem.ToArray();
                var result = new MemoryStream();
                stream.CopyTo(result);
                return result.ToArray();
            }
        }

        public static string ReadAllText(string path)
        {
            using (var reader = OpenText(path)) return reader.ReadToEnd();
        }

        public static string ReadAllText(string path, Encoding encoding)
        {
            using (var reader = new StreamReader(OpenRead(path), encoding)) return reader.ReadToEnd();
        }

        public static string[] ReadAllLines(string path)
        {
            var lines = new List<string>();
            using (var reader = OpenText(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }
            return lines.ToArray();
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            using (var stream = FS.Open(path, FileMode.Create, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
        }

        public static void WriteAllText(string path, string text)
        {
            using (var writer = new StreamWriter(FS.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))) writer.Write(text);
        }

        public static void WriteAllText(string path, string text, Encoding encoding)
        {
            using (var writer = new StreamWriter(FS.Open(path, FileMode.Create, FileAccess.Write, FileShare.None), encoding)) writer.Write(text);
        }

        public static void WriteAllLines(string path, IEnumerable<string> lines)
        {
            using (var writer = new StreamWriter(FS.Open(path, FileMode.Create, FileAccess.Write, FileShare.None)))
                foreach (var line in lines) writer.WriteLine(line);
        }

        public static void AppendAllText(string path, string text)
        {
            using (var writer = new StreamWriter(FS.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))) writer.Write(text);
        }

        public static void AppendAllLines(string path, IEnumerable<string> lines)
        {
            using (var writer = new StreamWriter(FS.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)))
                foreach (var line in lines) writer.WriteLine(line);
        }

        public static void Delete(string path)
        {
            if (Exists(path)) FS.DeleteFile(path);
        }

        public static void Copy(string source, string dest) { Copy(source, dest, false); }

        public static void Copy(string source, string dest, bool overwrite)
        {
            if (!overwrite && Exists(dest)) throw new IOException("The file '" + dest + "' already exists.");
            using (var from = OpenRead(source))
            using (var to = FS.Open(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                from.CopyTo(to);
        }

        public static void Move(string source, string dest) { FS.MoveFile(source, dest, false); }
        public static void Move(string source, string dest, bool overwrite) { FS.MoveFile(source, dest, overwrite); }

        /// <summary>Like File.Replace: dest is replaced by source (optionally keeping a backup of dest).</summary>
        public static void Replace(string source, string dest, string backup)
        {
            if (backup != null && Exists(dest)) Copy(dest, backup, true);
            FS.MoveFile(source, dest, true);
        }
    }

    /// <summary>Drop-in replacement for System.IO.Directory that goes through FSOFileSystem.Current.</summary>
    public static class FSODirectory
    {
        private static IFSOFileSystem FS { get { return FSOFileSystem.Current; } }

        public static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return FS.DirectoryExists(path);
        }

        public static void CreateDirectory(string path) { FS.CreateDirectory(path); }

        public static void Delete(string path) { FS.DeleteDirectory(path, false); }
        public static void Delete(string path, bool recursive) { FS.DeleteDirectory(path, recursive); }

        public static string[] GetFiles(string path) { return FS.GetFiles(path, "*", SearchOption.TopDirectoryOnly); }
        public static string[] GetFiles(string path, string pattern) { return FS.GetFiles(path, pattern, SearchOption.TopDirectoryOnly); }
        public static string[] GetFiles(string path, string pattern, SearchOption option) { return FS.GetFiles(path, pattern, option); }

        public static IEnumerable<string> EnumerateFiles(string path) { return GetFiles(path); }
        public static IEnumerable<string> EnumerateFiles(string path, string pattern) { return GetFiles(path, pattern); }
        public static IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) { return GetFiles(path, pattern, option); }

        public static string[] GetDirectories(string path) { return FS.GetDirectories(path, "*", SearchOption.TopDirectoryOnly); }
        public static string[] GetDirectories(string path, string pattern) { return FS.GetDirectories(path, pattern, SearchOption.TopDirectoryOnly); }
        public static string[] GetDirectories(string path, string pattern, SearchOption option) { return FS.GetDirectories(path, pattern, option); }
    }
}
