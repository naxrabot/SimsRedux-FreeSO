using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: keeps Makin' Magic's Mystery Man (the wand salesman in the dark coat) off the lots.
    ///
    /// In Sims 1 he turns up at a family's house when they move in, with a box of magic starter things,
    /// and fines Sims caught casting spells outside Magic Town. Simitone doesn't keep the "they've had
    /// their visit" note the way Sims 1 does, so he kept turning up. He's his own character, made by the
    /// expansion's scripts with "Create Object Instance" - so that is where he's stopped: the game is told
    /// he couldn't be made (the same answer it gets when there's no room for someone), and it carries on.
    ///
    /// He's recognised by his own character file's name - never by chance or by a setting - so every game
    /// on an online lot makes the same decision at the same moment and the lot stays in step. The maid,
    /// repairman, burglar, social worker, Grim Reaper and every other character are separate files and
    /// aren't touched.
    /// </summary>
    public static class ReduxMysteryMan
    {
        private static readonly string[] Names = { "mysteryman", "mysteryguy", "mysteriousman" };
        private static readonly Dictionary<uint, bool> Known = new Dictionary<uint, bool>();
        private static readonly HashSet<uint> Logged = new HashSet<uint>();

        /// <summary>True when this GUID is the Mystery Man's character (Sims 1 only).</summary>
        public static bool Is(uint guid)
        {
            lock (Known)
            {
                bool result;
                if (Known.TryGetValue(guid, out result)) return result;
                result = Check(guid);
                Known[guid] = result;
                return result;
            }
        }

        /// <summary>The Mystery Man about to be made by a lot script: true = don't make him.</summary>
        public static bool Block(VM vm, uint guid)
        {
            if (vm == null || !vm.TS1) return false;
            if (!Is(guid)) return false;
            lock (Logged) if (Logged.Add(guid)) Console.WriteLine("[SimsRedux] the Mystery Man was kept away (" + guid.ToString("X8") + ")");
            return true;
        }

        private static bool Check(uint guid)
        {
            try
            {
                var obj = Content.Content.Get().WorldObjects.Get(guid);
                if (obj?.OBJ == null || obj.OBJ.ObjectType != OBJDType.Person) return false;
                if (Matches(obj.OBJ.ChunkLabel)) return true;
                var ctss = obj.Resource?.Get<CTSS>(obj.OBJ.CatalogStringsID);
                if (ctss != null && Matches(ctss.GetString(0))) return true;
                return false;
            }
            catch { return false; }
        }

        private static bool Matches(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var letters = new System.Text.StringBuilder(name.Length);
            foreach (var c in name) if (char.IsLetter(c)) letters.Append(char.ToLowerInvariant(c));
            var s = letters.ToString();
            foreach (var n in Names) if (s.Contains(n)) return true;
            return false;
        }
    }
}
