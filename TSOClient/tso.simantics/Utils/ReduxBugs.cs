using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: bugs on the floor (Sims 1's roaches, and the flies around trash). Sims 1 has no
    /// exterminator - roaches only went when a Sim stomped them or the maid cleaned - so click the bugs:
    /// Call Exterminator clears every bug on the lot for a small fee (also on the phone), and "Spray" has
    /// the Sim spray them with the bug spray can - the roach object's own spray animations and spray can
    /// (A2O/C2O-roach-spray, suit-roach-can), which its interaction table never used.
    /// </summary>
    public static class ReduxBugs
    {
        public const int ExterminatorPrice = 50;

        /// <summary>Is this object bugs?</summary>
        public static bool Is(VMEntity e)
        {
            if (!(e is VMGameObject)) return false;
            var label = e.Object?.OBJ?.ChunkLabel ?? "";
            return label.IndexOf("roach", StringComparison.OrdinalIgnoreCase) >= 0
                || label.Equals("Flies", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Bugs", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Every bug on the lot.</summary>
        public static List<VMEntity> All(VM vm)
        {
            return vm.Entities.Where(x => Is(x) && x.Position != LotTilePos.OUT_OF_WORLD).ToList();
        }

        public const string SprayLabel = "Spray";
        /// <summary>The script "Spray" runs (an id well past the object's own).</summary>
        private const ushort SprayTreeID = 0x1FE0;
        private static readonly ReduxWeakMap<TTAB, object> Patched = new ReduxWeakMap<TTAB, object>();
        private static readonly object Lock = new object();

        /// <summary>The "Spray" entry on these bugs (its interaction number), or -1.</summary>
        public static int SprayIndex(VMEntity bugs)
        {
            var ttab = bugs?.TreeTable;
            var ttas = bugs?.TreeTableStrings;
            if (ttab == null || ttas == null) return -1;
            foreach (var ia in ttab.Interactions)
                if (ia.ActionFunction == SprayTreeID && (ttas.GetString((int)ia.TTAIndex) ?? "") == SprayLabel) return (int)ia.TTAIndex;
            return -1;
        }

        /// <summary>The bugs nearest to this spot within that many tiles (same floor), or null.</summary>
        public static VMEntity Near(VM vm, LotTilePos pos, float tiles)
        {
            if (vm == null || pos == LotTilePos.OUT_OF_WORLD) return null;
            VMEntity best = null; float bestD = tiles * 16f;
            foreach (var b in All(vm))
            {
                if (b.Position.Level != pos.Level) continue;
                double ddx = b.Position.x - pos.x, ddy = b.Position.y - pos.y;
                var d = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                if (d <= bestD) { bestD = d; best = b; }
            }
            return best;
        }

        /// <summary>
        /// Called as each object is made: bugs get "Spray" (once per kind). Built the same on every player's game
        /// from the same files, so online everyone has the same entry. The script, for the Sim (stack object = the bugs):
        /// walk up to them, take out the spray can, spray (start, two loops, end), put it away, and the bugs are gone.
        /// </summary>
        public static void Patch(VMEntity ent)
        {
            try
            {
                if (!Is(ent)) return;
                var ttab = ent.TreeTable;
                var ttas = ent.TreeTableStrings;
                var res = ent.Object?.Resource;
                if (ttab == null || ttas == null || res?.Iff == null) return;
                lock (Lock)
                {
                    if (Patched.ContainsKey(ttab)) return;
                    Patched[ttab] = null;
                    if (res.RoutineCache == null) return;
                    if (!res.RoutineCache.ContainsKey(SprayTreeID))
                    {
                        if (res.Iff.Get<BHAV>(SprayTreeID) != null) return; // (the id is taken by something else)
                        var bhav = new BHAV
                        {
                            ChunkID = SprayTreeID,
                            ChunkLabel = "SimsRedux - Spray Bugs",
                            ChunkType = "BHAV",
                            ChunkProcessed = true,
                            AddedByPatch = true,
                            Version = 0,
                            Type = 0,
                            Args = 4,
                            Locals = 0,
                            Instructions = new BHAVInstruction[]
                            {
                                // 0: go up to the bugs (anywhere near, facing them; no "can't get there" balloon) - either way, spray
                                I(27, 1, 1, 0, 0, 0xFF, 0xFE, 0, 0, 2, 0),
                                // 1: the spray can (this object's suit 0, "suit-roach-can")
                                I(6, 2, 2, 0, 2, 0, 0, 0, 0, 0, 0),
                                // 2-5: spray start, loop, loop, end (this object's animations 6-9; each waits for its end)
                                I(44, 3, 2, 6, 0, 0, 0, 0, 0, 0, 0),
                                I(44, 4, 3, 7, 0, 0, 0, 0, 0, 0, 0),
                                I(44, 5, 4, 8, 0, 0, 0, 0, 0, 0, 0),
                                I(44, 6, 5, 9, 0, 0, 0, 0, 0, 0, 0),
                                // 6: put the can away
                                I(6, 7, 7, 0, 2, 1, 0, 0, 0, 0, 0),
                                // 7: back to standing
                                I(44, 8, 8, 0, 0, 0, 0, 0, 0, 0, 0),
                                // 8: the bugs are gone (remove the stack object)
                                I(18, 254, 254, 1, 0, 0, 0, 0, 0, 0, 0),
                            }
                        };
                        var routine = FSO.SimAntics.Engine.VMTranslator.ReduxAssemble(bhav, res);
                        if (routine == null) return;
                        res.Iff.AddChunk(bhav);
                        res.RoutineCache[SprayTreeID] = routine;
                    }
                    if (SprayIndex(ent) >= 0) return;

                    // the pie entry: humans only (no pet flags), no autonomy (no motive ads), always offered
                    int index = ttas.Length;
                    if (index > 255 || ttab.InteractionByIndex.ContainsKey((uint)index)) return;
                    foreach (var set in ttas.LanguageSets)
                    {
                        if (set == null || set.Strings.Length == 0) continue;
                        var list = set.Strings.ToList();
                        while (list.Count < index) list.Add(new STRItem { Value = "", Comment = "" });
                        list.Insert(index, new STRItem { Value = SprayLabel, Comment = "SimsRedux", LanguageCode = (list.Count > 0 ? list[0].LanguageCode : (byte)1) });
                        set.Strings = list.ToArray();
                    }
                    int motives = ttab.Interactions.Length > 0 ? (ttab.Interactions[0].MotiveEntries?.Length ?? 0) : 0;
                    var ia = new TTABInteraction
                    {
                        ActionFunction = SprayTreeID,
                        TestFunction = 0,
                        MotiveEntries = new TTABMotiveEntry[motives],
                        Flags = 0,
                        TTAIndex = (uint)index,
                        JoiningIndex = -1,
                    };
                    for (int i = 0; i < motives; i++) ia.MotiveEntries[i] = new TTABMotiveEntry();
                    ttab.InsertInteraction(ia, ttab.Interactions.Length);
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] bugs: " + e.Message); }
        }

        /// <summary>
        /// As any object goes (VMEntity.Delete, on every player's game alike): when it's bugs, the spray can goes from
        /// every Sim who has it out but isn't still spraying other bugs that are there. "Spray" takes the can out and puts
        /// it away itself, but if the bugs go first (the exterminator, another Sim's spray, the maid) the interaction
        /// stops part way and the can stayed bound to the Sim - and was saved with them. (This also clears a can left
        /// from before, the next time any bugs go.) Only game state is read, so every player does the same.
        /// </summary>
        public static void OnDelete(VMEntity obj, VMContext context)
        {
            try
            {
                if (!Is(obj) || context?.ObjectQueries?.Avatars == null) return;
                foreach (var e in context.ObjectQueries.Avatars)
                {
                    var ava = e as VMAvatar;
                    if (ava?.BoundAppearances == null || ava.BoundAppearances.Count == 0) continue;
                    var cans = ava.BoundAppearances.Where(IsCan).ToList();
                    if (cans.Count == 0 || StillSpraying(ava, obj)) continue;
                    foreach (var can in cans)
                    {
                        ava.BoundAppearances.Remove(can);
                        if (VM.UseWorld)
                        {
                            var apr = FSO.Content.Content.Get().AvatarAppearances.Get(can);
                            if (apr != null) ava.Avatar?.RemoveAccessory(apr);
                        }
                    }
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] bugs: " + e.Message); }
        }

        private static bool IsCan(string apr)
        {
            return apr != null && apr.IndexOf("roach-can", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Is this Sim in the middle of "Spray" on other bugs that are still there?</summary>
        private static bool StillSpraying(VMAvatar ava, VMEntity going)
        {
            var stack = ava.Thread?.Stack;
            if (stack == null) return false;
            foreach (var f in stack)
            {
                if (f?.Routine == null || f.Routine.ID != SprayTreeID) continue;
                var target = f.StackObject;
                if (target != null && target != going && !target.Dead && Is(target)) return true;
            }
            return false;
        }

        private static BHAVInstruction I(ushort opcode, byte t, byte f, params byte[] operand)
        {
            var op = new byte[8];
            Array.Copy(operand, op, Math.Min(operand.Length, op.Length));
            return new BHAVInstruction { Opcode = opcode, TruePointer = t, FalsePointer = f, Operand = op };
        }
    }
}
