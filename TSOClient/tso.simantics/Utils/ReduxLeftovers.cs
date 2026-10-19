using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: leftovers, like The Sims 2. A group meal (the dinner platter or the barbecue platter)
    /// with servings still on it gets "Put Away Leftovers": the Sim clears it away like Clean Up, and the
    /// servings that were left go into the fridge. The fridge then has "Have Leftovers (n)": the Sim takes
    /// out a ready meal and eats it, with no cooking (the fridge's own ready-meal routine).
    ///
    /// No game file is changed: both entries are added to the objects' pie menus in memory and run the
    /// objects' own routines. The servings in the fridge are kept on the fridge itself (attribute
    /// StoreAttr), so they're saved with the house and online every player sees the same.
    /// </summary>
    public static class ReduxLeftovers
    {
        public const string PutAwayLabel = "Put Away Leftovers";
        public const string HaveLabel = "Have Leftovers";
        /// <summary>The fridge attribute the servings are kept in (well past the ones the fridge uses).</summary>
        public const int StoreAttr = 40;
        public const int MaxStored = 16;
        // (Sims 1's plate states, attribute 1: 0-1 raw, 2 prepared, 4 cooked - eat it; picture 15 = a cooked plate)
        private const short CookedState = 4, CookedGraphic = 15;

        private static readonly HashSet<uint> GroupMeals = new HashSet<uint> { 0x15866694, 0xa9fe9873 }; // regular group meal, BBQ group meal

        private class Entry { public uint Source; public string Label; }
        private static readonly Dictionary<TTAB, Dictionary<uint, Entry>> Patched = new Dictionary<TTAB, Dictionary<uint, Entry>>();
        private static readonly object Lock = new object();

        public static bool IsGroupMeal(VMEntity e) { return e?.Object?.OBJ != null && GroupMeals.Contains(e.Object.OBJ.GUID); }
        public static bool IsFridge(VMEntity e)
        {
            var label = e?.Object?.OBJ?.ChunkLabel ?? "";
            return !(e is VMAvatar) && label.StartsWith("Fridge", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Called as each object is made: platters and fridges get their new entry (once per kind).</summary>
        public static void Patch(VMEntity ent)
        {
            try
            {
                var ttab = ent?.TreeTable;
                var ttas = ent?.TreeTableStrings;
                if (ttab == null || ttas == null || ent is VMAvatar) return;
                bool meal = IsGroupMeal(ent), fridge = IsFridge(ent);
                if (!meal && !fridge) return;
                lock (Lock)
                {
                    if (Patched.ContainsKey(ttab)) return;
                    var want = meal ? "Clean Up" : "Debug Reg Meal";
                    TTABInteraction src = null;
                    foreach (var ia in ttab.Interactions)
                    {
                        var s = ttas.GetString((int)ia.TTAIndex) ?? "";
                        if (s.Equals(want, StringComparison.OrdinalIgnoreCase)) { src = ia; break; }
                    }
                    if (src == null) { Patched[ttab] = null; return; }
                    var map = new Dictionary<uint, Entry>();
                    Add(ttab, ttas, src, map, meal ? PutAwayLabel : HaveLabel);
                    Patched[ttab] = map;
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] leftovers: " + e.Message); }
        }

        private static void Add(TTAB ttab, TTAs ttas, TTABInteraction src, Dictionary<uint, Entry> map, string label)
        {
            int index = ttas.Length;
            foreach (var set in ttas.LanguageSets)
            {
                if (set == null || set.Strings.Length == 0) continue;
                var list = set.Strings.ToList();
                while (list.Count < index) list.Add(new STRItem { Value = "", Comment = "" });
                list.Insert(index, new STRItem { Value = label, Comment = "SimsRedux", LanguageCode = (list.Count > 0 ? list[0].LanguageCode : (byte)1) });
                set.Strings = list.ToArray();
            }
            if (index < 0 || index > 255 || ttab.InteractionByIndex.ContainsKey((uint)index)) return;
            var ia = new TTABInteraction
            {
                ActionFunction = src.ActionFunction,
                TestFunction = 0, // (offered when there's something to do - see AdjustPie)
                MotiveEntries = new TTABMotiveEntry[src.MotiveEntries?.Length ?? 0], // (no autonomy: only when you ask)
                Flags = src.Flags & ~(TTABFlags.Debug),
                Flags2 = src.Flags2,
                TTAIndex = (uint)index,
                AttenuationCode = src.AttenuationCode,
                AttenuationValue = src.AttenuationValue,
                AutonomyThreshold = src.AutonomyThreshold,
                JoiningIndex = -1,
            };
            for (int i = 0; i < ia.MotiveEntries.Length; i++) ia.MotiveEntries[i] = new TTABMotiveEntry();
            ttab.InsertInteraction(ia, ttab.Interactions.Length);
            map[(uint)index] = new Entry { Source = src.TTAIndex, Label = label };
        }

        /// <summary>A chosen entry runs as the object's own interaction it copies, under its new name.</summary>
        public static void Adjust(VMEntity obj, int interaction, VMQueuedAction action)
        {
            if (obj?.TreeTable == null || action == null || interaction < 0) return;
            Dictionary<uint, Entry> map;
            lock (Lock) { if (!Patched.TryGetValue(obj.TreeTable, out map) || map == null) return; }
            Entry e;
            if (!map.TryGetValue((uint)interaction, out e)) return;
            action.InteractionNumber = (int)e.Source;
            action.Name = e.Label;
        }

        /// <summary>Only offered when there are leftovers: servings on the platter, or in the fridges.</summary>
        public static void AdjustPie(VMEntity obj, List<VMPieMenuInteraction> pie)
        {
            try
            {
                if (pie == null || obj == null) return;
                bool meal = IsGroupMeal(obj), fridge = IsFridge(obj);
                if (!meal && !fridge) return;
                if (meal)
                {
                    if (obj.GetAttribute(2) <= 0) pie.RemoveAll(p => p != null && p.Name == PutAwayLabel);
                    return;
                }
                int stored = Stored(obj.Thread?.Context?.VM);
                if (stored <= 0) pie.RemoveAll(p => p != null && p.Name != null && p.Name.StartsWith(HaveLabel));
                else foreach (var p in pie) if (p != null && p.Name == HaveLabel) p.Name = HaveLabel + " (" + stored + ")";
            }
            catch { }
        }

        private static IEnumerable<VMEntity> Fridges(VM vm)
        {
            if (vm == null) return Enumerable.Empty<VMEntity>();
            return vm.Entities.Where(x => IsFridge(x) && x.MultitileGroup?.BaseObject == x).OrderBy(x => x.ObjectID);
        }

        /// <summary>The servings put away in this lot's fridges.</summary>
        public static int Stored(VM vm)
        {
            int n = 0;
            foreach (var f in Fridges(vm)) n += Math.Max((short)0, f.GetAttribute(StoreAttr));
            return n;
        }

        /// <summary>A platter being removed: if a Sim is putting it away, its servings go in the fridge.</summary>
        public static void OnDelete(VMEntity obj, VMContext context)
        {
            try
            {
                if (!IsGroupMeal(obj) || context?.VM == null) return;
                // (the servings as they were when the Sim started: clearing up may empty the platter first)
                int servings = Math.Max(obj.GetAttribute(2), obj.GetAttribute(StoreAttr));
                if (servings <= 0) return;
                bool puttingAway = context.VM.Entities.Any(x => x is VMAvatar && x.Thread?.ActiveAction?.Callee == obj && x.Thread.ActiveAction.Name == PutAwayLabel);
                if (!puttingAway) return;
                var fridge = Fridges(context.VM).FirstOrDefault();
                if (fridge == null) return;
                int now = Math.Max((short)0, fridge.GetAttribute(StoreAttr));
                fridge.SetAttribute(StoreAttr, (short)Math.Min(MaxStored, now + servings));
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] leftovers: " + e.Message); }
        }

        /// <summary>Every tick, for every Sim: a Sim starting on "Have Leftovers" takes one serving out.</summary>
        public static void Tick(VMAvatar ava)
        {
            var action = ava?.Thread?.ActiveAction;
            if (action == null || action.Name == null) return;
            // the plate taken out for leftovers is already cooked: straight to eating it (at a table, like any
            // meal) - it used to count as raw, so the Sim went looking to prepare it and dropped it on the floor
            if (action.ReduxCounted && action.Name.StartsWith(HaveLabel))
            {
                var held = ava.GetSlot(0);
                if (held != null && (held.Object?.OBJ?.ChunkLabel ?? "").StartsWith("Food - Regular Meal", StringComparison.OrdinalIgnoreCase)
                    && held.GetAttribute(1) < CookedState)
                {
                    held.SetAttribute(1, CookedState);
                    held.SetValue(VMStackObjectVariable.Graphic, CookedGraphic);
                }
                return;
            }
            if (action.ReduxCounted) return;
            if (action.Name == PutAwayLabel && IsGroupMeal(action.Callee))
            {
                // remember how many servings it had
                action.ReduxCounted = true;
                action.Callee.SetAttribute(StoreAttr, action.Callee.GetAttribute(2));
                return;
            }
            if (!action.Name.StartsWith(HaveLabel)) return;
            if (!IsFridge(action.Callee)) return;
            action.ReduxCounted = true;
            var vm = ava.Thread.Context.VM;
            var fridge = Fridges(vm).FirstOrDefault(f => f.GetAttribute(StoreAttr) > 0);
            if (fridge == null) { ava.Thread.CancelAction(action.UID); return; } // (none left after all)
            fridge.SetAttribute(StoreAttr, (short)(fridge.GetAttribute(StoreAttr) - 1));
        }
    }
}
