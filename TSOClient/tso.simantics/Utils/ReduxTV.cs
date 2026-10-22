using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Engine;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 15.7: every TV has the full set of channels.
    ///
    /// How the Sims 1 TVs work (from the game's own TVs.iff): the Monochrome, Trottco and Soma TVs share one
    /// script and one menu - the TV's price only changes how much fun it is and its picture. Action, Horror,
    /// Romance and Cartoons are on every TV. The other four - Animal Show, News, Sports and Entertainment - are
    /// only offered when the lot has a satellite dish ("Is there a Satellite Dish?"), whatever the TV. And TVs
    /// from elsewhere (other packs, downloads made from an older copy of the TV) can be missing channels.
    ///
    /// So, when a TV is loaded (in memory only - no game file is changed or copied):
    ///  1. its "Is there a Satellite Dish?" check always says yes, so every channel is on every TV;
    ///  2. a TV missing any of the game's own TV's "Switch to ..." channels gets them, made from the game's own TV's
    ///     scripts for them (the player's own TVs.iff), pointed at the TV's own same-named scripts. A TV that doesn't
    ///     work the same way inside (one of those scripts missing) is left as it is.
    /// Built the same way from the same files on every game, so an online lot offers every player the same menu.
    /// </summary>
    public static class ReduxTV
    {
        /// <summary>The game's own TVs (TVs.iff: Soma Plasma, Trottco, Monochrome) - all one script.</summary>
        private static readonly uint[] ReferenceTVs = { 0xFC8D548B, 0x5FA381C1, 0xC192DACC };
        private const string DishLabel = "Is there a Satellite Dish?";
        private const string ChannelPrefix = "Switch to ";
        /// <summary>Where the added scripts go in a TV's file (ids well past the object's own).</summary>
        private const ushort FirstAddedID = 0x1E00, LastAddedID = 0x1EFF;

        private static readonly ReduxWeakMap<TTAB, object> Patched = new ReduxWeakMap<TTAB, object>();
        private static readonly ReduxWeakMap<GameIffResource, object> DishPatched = new ReduxWeakMap<GameIffResource, object>();
        private static readonly object Lock = new object();

        /// <summary>A TV: a menu with "Watch TV" and at least one "Switch to ..." channel.</summary>
        public static bool IsTV(TTAB ttab, TTAs ttas)
        {
            if (ttab == null || ttas == null) return false;
            bool watch = false, channel = false;
            foreach (var ia in ttab.Interactions)
            {
                var n = (ttas.GetString((int)ia.TTAIndex) ?? "").Trim();
                if (n.Equals("Watch TV", StringComparison.OrdinalIgnoreCase)) watch = true;
                else if (n.StartsWith(ChannelPrefix, StringComparison.OrdinalIgnoreCase)) channel = true;
            }
            return watch && channel;
        }

        /// <summary>Called as each object is made (VMEntity); only does anything to TVs, once per kind.</summary>
        public static void Patch(VMEntity ent)
        {
            try
            {
                if (!(ent is VMGameObject)) return;
                var content = FSO.Content.Content.Get();
                if (content == null || !content.TS1) return;
                var ttab = ent.TreeTable;
                var ttas = ent.TreeTableStrings;
                var res = ent.Object?.Resource;
                if (ttab == null || ttas == null || res?.Iff == null || res.RoutineCache == null) return;
                if (!IsTV(ttab, ttas)) return;
                lock (Lock)
                {
                    if (Patched.ContainsKey(ttab)) return;
                    Patched[ttab] = null;
                    NoDishNeeded(res);
                    PlainNames(ttas);
                    // (only a TV's own tree table - one kept in a shared file is left alone)
                    if (!ReferenceEquals(res.Iff.Get<TTAB>(ttab.ChunkID), ttab)) return;
                    AddMissingChannels(ttab, ttas, res);
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] TV channels: " + e.Message); }
        }

        /// <summary>The game's own menu says "Switch to EP6 News" (its working name) - players see "Switch to News".</summary>
        private static void PlainNames(TTAs ttas)
        {
            foreach (var set in ttas.LanguageSets)
            {
                if (set?.Strings == null) continue;
                foreach (var s in set.Strings)
                    if (s?.Value != null && s.Value.Trim().Equals("Switch to EP6 News", StringComparison.OrdinalIgnoreCase)) s.Value = "Switch to News";
            }
        }

        /// <summary>A menu name as players see it (trimmed, "EP6 News" as "News").</summary>
        private static string Plain(string name)
        {
            name = (name ?? "").Trim();
            return name.Equals("Switch to EP6 News", StringComparison.OrdinalIgnoreCase) ? "Switch to News" : name;
        }

        // ------------------------------------------------------------------
        // 1. no satellite dish needed
        // ------------------------------------------------------------------

        private static void NoDishNeeded(GameObjectResource res)
        {
            if (DishPatched.ContainsKey(res)) return;
            DishPatched[res] = null;
            foreach (var bhav in res.Iff.List<BHAV>() ?? new List<BHAV>())
            {
                if (bhav == null || !string.Equals((bhav.ChunkLabel ?? "").Trim(), DishLabel, StringComparison.OrdinalIgnoreCase)) continue;
                var yes = YesRoutine(bhav.ChunkID, bhav.ChunkLabel, bhav.Args, bhav.Locals);
                var routine = VMTranslator.ReduxAssemble(yes, res);
                if (routine != null) res.RoutineCache[bhav.ChunkID] = routine;
            }
        }

        /// <summary>A script that just says yes (temp 0 = 0, then true).</summary>
        private static BHAV YesRoutine(ushort id, string label, byte args, ushort locals)
        {
            return new BHAV
            {
                ChunkID = id,
                ChunkLabel = label ?? DishLabel,
                ChunkType = "BHAV",
                ChunkProcessed = true,
                AddedByPatch = true,
                Version = 0,
                Type = 0,
                Args = args,
                Locals = locals,
                Instructions = new BHAVInstruction[]
                {
                    // temp[0] := 0 -> true
                    Expr(0, 0, 5, 8, 7, 254, 254),
                }
            };
        }

        // ------------------------------------------------------------------
        // 2. the game's own TV's channels on TVs that are missing some
        // ------------------------------------------------------------------

        private static GameObject ReferenceTV()
        {
            var objs = FSO.Content.Content.Get()?.WorldObjects;
            if (objs == null) return null;
            foreach (var guid in ReferenceTVs)
            {
                try
                {
                    var o = objs.Get(guid);
                    if (o?.Resource?.Iff != null && o.OBJ != null) return o;
                }
                catch { }
            }
            return null;
        }

        private static void AddMissingChannels(TTAB ttab, TTAs ttas, GameObjectResource res)
        {
            var refObj = ReferenceTV();
            if (refObj == null || ReferenceEquals(refObj.Resource, res)) return; // (the game's own TVs have them all)
            var refRes = refObj.Resource;
            var refTtab = refRes.Get<TTAB>(refObj.OBJ.TreeTableID);
            var refTtas = refRes.Get<TTAs>(refObj.OBJ.TreeTableID);
            if (refTtab == null || refTtas == null) return;

            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ia in ttab.Interactions) have.Add(Plain(ttas.GetString((int)ia.TTAIndex)));

            var added = new Dictionary<ushort, ushort>(); // the game's TV's script -> the copy in this TV's file
            int count = 0;
            foreach (var ia in refTtab.Interactions)
            {
                var name = Plain(refTtas.GetString((int)ia.TTAIndex));
                if (!name.StartsWith(ChannelPrefix, StringComparison.OrdinalIgnoreCase) || have.Contains(name)) continue;

                ushort action = Copy(ia.ActionFunction, refRes, refTtas, res, ttab, ttas, added);
                if (action == 0) continue;
                ushort test = 0;
                if (ia.TestFunction != 0)
                {
                    test = Copy(ia.TestFunction, refRes, refTtas, res, ttab, ttas, added);
                    if (test == 0) continue;
                }

                int index = ttas.Length;
                if (index > 255 || ttab.InteractionByIndex.ContainsKey((uint)index)) break;
                foreach (var set in ttas.LanguageSets)
                {
                    if (set == null || set.Strings.Length == 0) continue;
                    var list = set.Strings.ToList();
                    while (list.Count < index) list.Add(new STRItem { Value = "", Comment = "" });
                    list.Insert(index, new STRItem { Value = name, Comment = "SimsRedux", LanguageCode = (list.Count > 0 ? list[0].LanguageCode : (byte)1) });
                    set.Strings = list.ToArray();
                }
                int motives = ttab.Interactions.Length > 0 ? (ttab.Interactions[0].MotiveEntries?.Length ?? 0) : 0;
                var entry = new TTABInteraction
                {
                    ActionFunction = action,
                    TestFunction = test,
                    MotiveEntries = new TTABMotiveEntry[motives],
                    Flags = ia.Flags,
                    TTAIndex = (uint)index,
                    AttenuationCode = ia.AttenuationCode,
                    AttenuationValue = ia.AttenuationValue,
                    AutonomyThreshold = ia.AutonomyThreshold,
                    JoiningIndex = -1,
                };
                for (int i = 0; i < motives; i++) entry.MotiveEntries[i] = new TTABMotiveEntry();
                ttab.InsertInteraction(entry, ttab.Interactions.Length);
                have.Add(name);
                count++;
            }
            if (count > 0) Console.WriteLine("[SimsRedux] TV channels: " + count + " channel(s) added to " + (res.Name ?? "a TV"));
        }

        /// <summary>
        /// Copies one of the game's TV's own scripts into this TV's file (or finds the copy already made), with its
        /// calls pointed at this TV's scripts of the same name, its tuning numbers written in, and its "push Watch TV"
        /// pointed at this TV's "Watch TV". 0 if it can't be done safely (then the channel isn't added).
        /// </summary>
        private static ushort Copy(ushort refID, GameIffResource refRes, TTAs refTtas, GameObjectResource res, TTAB ttab, TTAs ttas, Dictionary<ushort, ushort> added)
        {
            if (refID < 4096 || refID >= 8192) return 0;
            ushort done;
            if (added.TryGetValue(refID, out done)) return done;
            var src = refRes.Get<BHAV>(refID);
            if (src?.Instructions == null) return 0;

            var ins = new BHAVInstruction[src.Instructions.Length];
            for (int i = 0; i < ins.Length; i++)
            {
                var s = src.Instructions[i];
                var op = (byte[])s.Operand.Clone();
                ushort opcode = s.Opcode;
                if (opcode >= 8192) return 0; // (a semi-global call - not something to carry across)
                if (opcode >= 4096)
                {
                    var label = (refRes.Get<BHAV>(opcode)?.ChunkLabel ?? "").Trim();
                    var mine = FindByLabel(res, label);
                    if (mine == 0 && label.Equals(DishLabel, StringComparison.OrdinalIgnoreCase)) mine = AddYes(res, label);
                    if (mine == 0) return 0;
                    opcode = mine;
                }
                else if (opcode >= 256) { } // (a global call: the same for every object)
                else if (opcode == 2)
                {
                    // (operand: lhs data 0-1, rhs data 2-3, signed 4, operator 5, lhs scope 6, rhs scope 7)
                    if (!LiteralTuning(op, 6, 0, refRes) || !LiteralTuning(op, 7, 2, refRes)) return 0;
                }
                else if (opcode == 13)
                {
                    // push interaction: operand 0 is an entry of the game's TV's menu - the same-named one on this TV
                    var name = (refTtas.GetString(op[0]) ?? "").Trim();
                    int mineIndex = -1;
                    foreach (var ia in ttab.Interactions.OrderBy(x => x.TTAIndex))
                        if ((ttas.GetString((int)ia.TTAIndex) ?? "").Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) { mineIndex = (int)ia.TTAIndex; break; }
                    if (mineIndex < 0 || mineIndex > 255) return 0;
                    op[0] = (byte)mineIndex;
                }
                else return 0; // (anything else could point at the game's TV's own pictures, sounds or animations)
                ins[i] = new BHAVInstruction { Opcode = opcode, TruePointer = s.TruePointer, FalsePointer = s.FalsePointer, Operand = op };
            }

            ushort id = FreeID(res);
            if (id == 0) return 0;
            var bhav = new BHAV
            {
                ChunkID = id,
                ChunkLabel = "SimsRedux - " + (src.ChunkLabel ?? "channel"),
                ChunkType = "BHAV",
                ChunkProcessed = true,
                AddedByPatch = true,
                Version = src.Version,
                Type = src.Type,
                Args = src.Args,
                Locals = src.Locals,
                Instructions = ins
            };
            var routine = VMTranslator.ReduxAssemble(bhav, res);
            if (routine == null) return 0;
            res.Iff.AddChunk(bhav);
            res.RoutineCache[id] = routine;
            added[refID] = id;
            return id;
        }

        /// <summary>
        /// An expression's side that reads the object's own tuning (scope 26, a table of its own) becomes the number
        /// itself, as the game's TV has it. Global tuning is left as it is (the same for everyone).
        /// </summary>
        private static bool LiteralTuning(byte[] op, int scopeAt, int dataAt, GameIffResource refRes)
        {
            if (op[scopeAt] != 26) return true;
            ushort data = (ushort)(op[dataAt] | (op[dataAt + 1] << 8));
            int table = data >> 7, key = data & 0x7F;
            if (table >= 64) return true; // (semi-global or global tuning)
            var bcon = refRes.Get<BCON>((ushort)(4096 + table));
            if (bcon?.Constants == null || key >= bcon.Constants.Length) return false;
            ushort value = bcon.Constants[key];
            op[dataAt] = (byte)(value & 0xFF);
            op[dataAt + 1] = (byte)(value >> 8);
            op[scopeAt] = 7; // literal
            return true;
        }

        private static ushort FindByLabel(GameObjectResource res, string label)
        {
            if (string.IsNullOrEmpty(label)) return 0;
            foreach (var b in (res.Iff.List<BHAV>() ?? new List<BHAV>()).OrderBy(x => x.ChunkID))
                if (b.ChunkID >= 4096 && b.ChunkID < 8192 && string.Equals((b.ChunkLabel ?? "").Trim(), label, StringComparison.OrdinalIgnoreCase)
                    && res.RoutineCache.ContainsKey(b.ChunkID)) return b.ChunkID;
            return 0;
        }

        private static ushort AddYes(GameObjectResource res, string label)
        {
            ushort id = FreeID(res);
            if (id == 0) return 0;
            var bhav = YesRoutine(id, label, 0, 0);
            var routine = VMTranslator.ReduxAssemble(bhav, res);
            if (routine == null) return 0;
            res.Iff.AddChunk(bhav);
            res.RoutineCache[id] = routine;
            return id;
        }

        private static ushort FreeID(GameObjectResource res)
        {
            for (int id = FirstAddedID; id <= LastAddedID; id++)
                if (res.Iff.Get<BHAV>((ushort)id) == null && !res.RoutineCache.ContainsKey((ushort)id)) return (ushort)id;
            return 0;
        }

        private static BHAVInstruction Expr(short lhs, short rhs, byte oper, byte lhsOwner, byte rhsOwner, byte t, byte f)
        {
            var op = new byte[8];
            op[0] = (byte)(lhs & 0xFF); op[1] = (byte)((lhs >> 8) & 0xFF);
            op[2] = (byte)(rhs & 0xFF); op[3] = (byte)((rhs >> 8) & 0xFF);
            op[4] = 0; op[5] = oper; op[6] = lhsOwner; op[7] = rhsOwner;
            return new BHAVInstruction { Opcode = 2, TruePointer = t, FalsePointer = f, Operand = op };
        }
    }
}
