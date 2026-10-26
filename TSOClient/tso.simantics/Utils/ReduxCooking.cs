using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.7 (roadmap A11): cooking menus, The Sims 3/4 way. "Have Breakfast / Lunch / Dinner", "Have Quick ..."
    /// and "Serve ..." on a fridge ask which dish to make (the game's dish picker). Dishes unlock with Cooking skill and each
    /// has its own cost, cooking time, how filling it is, quality and - for a hard dish a Sim has only just learnt - a fire
    /// risk on the stove. Feature switch "cookmenu" (the picker is only offered when it's on; off = the game's own cooking).
    ///
    /// How it hooks into The Sims 1's own cooking (no game file is changed; Fridges.iff, Food.iff, Stoves.iff, CounterGlobals):
    ///  - The chosen dish rides on the fridge interaction itself: VMNetReduxCookCmd (type 73) queues the fridge's own
    ///    "Have A Meal" / "Have Lite Meal" / "Serve Meal" with the dish in its 4th argument (DishArg). Arguments are saved
    ///    with the Sim's queue and running script, so a save, or a guest joining part way, has it too.
    ///  - Cost: the fridge's grocery charge (Transfer Funds, Fridges BCON 4096: §10 meal, §20 group meal) is the dish's price
    ///    instead (VMTS1Budget asks Cost) - a group meal is twice the dish's price, as in the game.
    ///  - The plate the fridge hands the Sim (Food - Regular Meal / Light Meal / Regular Group Meal) is marked with the dish
    ///    in attribute DishAttr (Food.iff uses attributes 0-12), so it's saved with the lot and every player agrees.
    ///  - Cooking time: the counter's "prepare loop" and the stove's "cook loop" count down the Sim's TreeUseDataField2 (one
    ///    step per 30 / 25 animation ticks, about a game minute). When each loop starts for a marked plate the dish's extra
    ///    steps are added once (never below one step).
    ///  - Fire: when the stove's cook loop starts, the lot's own random numbers decide (FireChance) whether this dish burns.
    ///    If so the food is marked burnt (attribute 10, as the stove's own fire does - the Sim throws it away after) and
    ///    the moment the game calls it cooked a fire starts on that stove. The game's own fire chance for a beginner
    ///    (Cooking under 1.5) still applies too. Microwaves and toaster ovens never catch fire.
    ///  - How filling and quality: once it's cooked and no cooking script is still running for the Sim (the stove adds its
    ///    food points just after the game calls it cooked), the plate's food points - attribute 0 (what it fills Hunger by,
    ///    bite by bite), 8 (the picture) and 11 (the most it could have had) - are set from the dish: the most = the game's
    ///    own most x the dish's Fill %; the points = that x how well it was made. How well = the game's own result (Cooking
    ///    skill, fridge, counter and stove) plus a little for a Sim who knows the dish well (+3% a level past it, up to 10%).
    ///    The game's own "calculate meal quality" then gives the bad / OK / great meal from those, as always.
    ///  - A quick meal ("Have Quick ...", the game's light meal) skips the counter and goes straight to the stove or
    ///    microwave; its dishes' time is all cooking time and they never catch fire.
    /// Everything runs in the lot's own ticks from what's saved on the lot (VMAvatar.Tick), so visits stay in step.
    /// </summary>
    public static class ReduxCooking
    {
        public enum Kind : byte { Meal = 0, Quick = 1, Group = 2 }
        [Flags] public enum MealTime : byte { Breakfast = 1, Lunch = 2, Dinner = 4, Any = 7 }

        public class Dish
        {
            public byte Id;
            public string Name;
            public MealTime Times;
            /// <summary>A quick meal (Have Quick ...: the game's light meal - no counter step) - otherwise a full meal (Have ... / Serve ...).</summary>
            public bool Quick;
            /// <summary>The Cooking skill level (0-10) it unlocks at.</summary>
            public int Level;
            /// <summary>The price for one (a group meal costs twice this).</summary>
            public int Cost;
            /// <summary>How filling, % of the game's own meal.</summary>
            public int Fill;
            /// <summary>Extra steps at the counter and the stove (about a game minute each; negative = quicker).</summary>
            public int Prep, Cook;
            /// <summary>Fire chance (%) for a Sim who has only just learnt it (falls to 0 three levels on).</summary>
            public int Fire;
            /// <summary>The game's food picture for one plate and for a group meal (Food.iff object GUIDs).</summary>
            public uint Picture, GroupPicture;
        }

        // the game's food (Food.iff) - their catalog pictures are the dish pictures
        public const uint RegularMeal = 0xbf62f653, LightMeal = 0x500dc12b, GroupMeal = 0x15866694,
            Hamburger = 0x8816c7c2, Hamburgers = 0xa9fe9873, PizzaSlice = 0xd4eab5f6, PizzaBox = 0x543be3fe,
            FruitcakeSlice = 0xd6905815, Fruitcake = 0x96ecf350;

        private static Dish D(byte id, string name, MealTime t, bool quick, int level, int cost, int fill, int prep, int cook, int fire, uint pic = 0, uint group = 0)
        {
            return new Dish
            {
                Id = id, Name = name, Times = t, Quick = quick, Level = level, Cost = cost, Fill = fill, Prep = prep, Cook = cook, Fire = fire,
                Picture = pic != 0 ? pic : (quick ? LightMeal : RegularMeal),
                GroupPicture = group != 0 ? group : (quick ? LightMeal : GroupMeal),
            };
        }

        const MealTime B = MealTime.Breakfast, L = MealTime.Lunch, N = MealTime.Dinner, LN = MealTime.Lunch | MealTime.Dinner, All = MealTime.Any;

        /// <summary>
        /// The dishes. Ids are saved on plates and in queued actions: never reuse or renumber one (add new ones at the end).
        /// The game's own meal: §10 (group §20), about 80-100 food points; a quick meal §10.
        /// </summary>
        public static readonly Dish[] Dishes =
        {
            //  id  name                     when  quick  lvl  §   fill prep cook fire  picture
            D(1,  "Pancakes",               B,   false, 0,   8,  90,  0,  0,  0),
            D(2,  "Mac and Cheese",         LN,  false, 0,   8,  95,  0,  0,  0),
            D(3,  "Scrambled Eggs",         B,   false, 1,  10, 100, -1,  0,  0),
            D(4,  "Grilled Cheese",         LN,  false, 1,   9,  90, -1, -1,  0, Hamburger, Hamburgers),
            D(5,  "Waffles",                B,   false, 2,  11, 100,  0,  1,  0),
            D(6,  "Spaghetti",              LN,  false, 2,  12, 105,  1,  1,  0),
            D(7,  "French Toast",           B,   false, 3,  12, 105,  1,  1,  2),
            D(8,  "Chili",                  LN,  false, 3,  14, 110,  1,  3,  2),
            D(9,  "Omelette",               B,   false, 4,  14, 110,  1,  1,  3),
            D(10, "Burgers",                LN,  false, 4,  15, 110,  1,  2,  3, Hamburger, Hamburgers),
            D(11, "Homemade Pizza",         LN,  false, 5,  16, 105,  3,  2,  3, PizzaSlice, PizzaBox),
            D(12, "Stir Fry",               LN,  false, 5,  18, 110,  2,  1,  5),
            D(13, "Eggs Benedict",          B,   false, 6,  20, 115,  2,  2,  4),
            D(14, "Roast Chicken",          N,   false, 6,  22, 120,  2,  5,  4),
            D(15, "Lasagna",                LN,  false, 7,  24, 125,  4,  4,  5),
            D(16, "Fish and Chips",         LN,  false, 7,  22, 115,  2,  3,  8),
            D(17, "Crepes",                 B,   false, 8,  22, 110,  2,  2,  6),
            D(18, "Steak and Potatoes",     N,   false, 8,  30, 130,  2,  4,  6),
            D(19, "Beef Wellington",        N,   false, 9,  38, 135,  5,  5,  8),
            D(20, "Seafood Paella",         LN,  false, 9,  34, 130,  4,  5,  7),
            D(21, "Turkey Dinner",          N,   false, 10, 50, 140,  4,  7, 10),
            D(22, "Baked Alaska",           All, false, 10, 45, 115,  4,  3, 12, FruitcakeSlice, Fruitcake),
            // quick meals ("Have Quick ...": no counter, straight to the stove or microwave - their time is all cooking; never a fire)
            D(40, "Cereal",                 B,   true,  0,   5,  70,  0, -3,  0),
            D(41, "Sandwich",               LN,  true,  0,   6,  80,  0, -2,  0),
            D(42, "Fruit Bowl",             B,   true,  1,   7,  75,  0, -1,  0),
            D(43, "Garden Salad",           LN,  true,  2,   8,  85,  0,  0,  0),
            D(44, "Yogurt Parfait",         B,   true,  3,   8,  85,  0,  0,  0),
            D(45, "Wraps",                  LN,  true,  4,  10,  95,  0,  1,  0),
            D(46, "Bagels and Lox",         B,   true,  5,  12, 100,  0,  1,  0),
            D(47, "Sushi Rolls",            LN,  true,  6,  16, 100,  0,  3,  0),
            D(48, "Chef's Salad",           LN,  true,  8,  14, 110,  0,  2,  0),
        };

        private static readonly Dictionary<int, Dish> ById = new Dictionary<int, Dish>();
        static ReduxCooking() { foreach (var d in Dishes) ById[d.Id] = d; }

        public static Dish Find(int id) { Dish d; return ById.TryGetValue(id, out d) ? d : null; }

        /// <summary>The dishes for a fridge choice (Have / Have Quick / Serve) - every Cooking level, easiest first.</summary>
        public static List<Dish> For(Kind kind)
        {
            var list = new List<Dish>();
            foreach (var d in Dishes) if (d.Quick == (kind == Kind.Quick)) list.Add(d);
            list.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : a.Id.CompareTo(b.Id));
            return list;
        }

        public static int Price(Dish d, Kind kind) { return kind == Kind.Group ? d.Cost * 2 : d.Cost; }

        public static int SkillLevel(VMAvatar ava) { return Math.Max(0, Math.Min(10, ava.GetPersonData(VMPersonDataVariable.CookingSkill) / 100)); }

        public static bool Unlocked(Dish d, VMAvatar ava) { return d != null && ava != null && SkillLevel(ava) >= d.Level; }

        /// <summary>Fire chance (%) on the stove: the dish's for a Sim who has just learnt it, less with each level after (0 at three levels on).</summary>
        public static int FireChance(Dish d, int level)
        {
            if (d == null || d.Quick || d.Fire <= 0) return 0;
            int margin = Math.Max(0, level - d.Level);
            if (margin >= 3) return 0;
            return d.Fire * (3 - margin) / 3;
        }

        /// <summary>"Short", "Medium", "Long", "Very long" - how long it takes compared with the game's own meal.</summary>
        public static string TimeText(Dish d)
        {
            int extra = d.Prep + d.Cook;
            if (d.Quick) return extra <= -2 ? "Very quick" : extra <= 0 ? "Quick" : "Medium";
            return extra <= 0 ? "Short" : extra <= 3 ? "Medium" : extra <= 7 ? "Long" : "Very long";
        }

        /// <summary>"Light", "Filling", "Very filling", "Feast".</summary>
        public static string FillText(Dish d)
        {
            return d.Fill < 85 ? "Light" : d.Fill < 105 ? "Filling" : d.Fill < 125 ? "Very filling" : "A feast";
        }

        // ---------- the fridge's choices ----------

        /// <summary>Which fridge choice this interaction is (by the fridge's own name for it), or null.</summary>
        public static Kind? KindOf(VMEntity fridge, int interaction)
        {
            try
            {
                if (fridge == null || interaction < 0 || !ReduxLeftovers.IsFridge(fridge)) return null;
                var ttab = fridge.TreeTable;
                var ttas = fridge.TreeTableStrings;
                if (ttab?.InteractionByIndex == null || ttas == null) return null;
                FSO.Files.Formats.IFF.Chunks.TTABInteraction ia;
                if (!ttab.InteractionByIndex.TryGetValue((uint)interaction, out ia)) return null;
                var s = (ttas.GetString((int)ia.TTAIndex) ?? "").Trim();
                if (s.Equals("Have A Meal", StringComparison.OrdinalIgnoreCase)) return Kind.Meal;
                if (s.Equals("Have Lite Meal", StringComparison.OrdinalIgnoreCase)) return Kind.Quick;
                if (s.Equals("Serve Meal", StringComparison.OrdinalIgnoreCase)) return Kind.Group;
            }
            catch { }
            return null;
        }

        /// <summary>The fridge interaction's 4th argument carries the dish: DishArg + its id.</summary>
        public const short DishArg = 0x6300;

        public static Dish DishOf(VMQueuedAction action)
        {
            var args = action?.Args;
            if (args == null || args.Length < 4) return null;
            int v = args[3] - DishArg;
            if (v <= 0 || v > 255) return null;
            return Find(v);
        }

        /// <summary>The fridge's "Have Dinner" (its own name for the time of day) keeps the dish: "Have Dinner: Lasagna".</summary>
        public static string Rename(VMQueuedAction action, string name)
        {
            try
            {
                var d = DishOf(action);
                if (d == null || name == null || !ReduxLeftovers.IsFridge(action.Callee)) return name;
                return name + ": " + d.Name;
            }
            catch { return name; }
        }

        /// <summary>The fridge charges for groceries: the dish's price instead, while a Sim's dish choice runs.</summary>
        public static int Cost(VMStackFrame frame, int amount)
        {
            try
            {
                var ava = frame?.Caller as VMAvatar;
                var action = ava?.Thread?.ActiveAction;
                var d = DishOf(action);
                if (d == null || !ReduxLeftovers.IsFridge(action.Callee)) return amount;
                if (frame.Callee != action.Callee && !ReduxLeftovers.IsFridge(frame.Callee)) return amount;
                var kind = KindOf(action.Callee, action.InteractionNumber & 0xFFFF);
                if (kind == null) return amount;
                return Price(d, kind.Value);
            }
            catch { return amount; }
        }

        // ---------- the plate ----------

        /// <summary>On the plate: the dish (its id), what's been done (Stage bits), the stove it was cooked on.</summary>
        public const int DishAttr = 44, StageAttr = 45, StoveAttr = 46;
        private const int PrepDone = 1, CookDone = 2, Applied = 4, WillBurn = 8, Burned = 16;
        private const int FoodState = 1, FoodPoints = 0, FoodPicturePoints = 8, FoodMost = 11, FoodBurnt = 10;
        private const short Cooked = 4;
        private const int StepsField = (int)VMPersonDataVariable.TreeUseDataField2;

        public static bool IsPlate(VMEntity e)
        {
            var guid = e?.Object?.OBJ?.GUID ?? 0;
            return !(e is VMAvatar) && (guid == RegularMeal || guid == LightMeal || guid == GroupMeal);
        }

        /// <summary>Every tick, for every Sim (VMAvatar.Tick) - only from what's saved on the lot.</summary>
        public static void Tick(VMAvatar ava)
        {
            var action = ava?.Thread?.ActiveAction;
            var callee = action?.Callee;
            if (callee == null || ava.IsPet) return;

            if (ReduxLeftovers.IsFridge(callee))
            {
                // the plate the fridge has just handed over: it's this dish
                var d = DishOf(action);
                if (d == null) return;
                var held = ava.GetSlot(0);
                if (IsPlate(held) && held.GetAttribute(DishAttr) == 0)
                {
                    held.SetAttribute(DishAttr, d.Id);
                    held.SetAttribute(StageAttr, 0);
                    held.SetAttribute(StoveAttr, 0);
                }
                return;
            }

            if (!IsPlate(callee)) return;
            var dish = Find(callee.GetAttribute(DishAttr));
            if (dish == null) return;
            int stage = callee.GetAttribute(StageAttr);
            int was = stage;

            // the counter and the stove: the dish's own time (and the stove's fire roll), once each
            if ((stage & (PrepDone | CookDone)) != (PrepDone | CookDone))
            {
                var stack = ava.Thread.Stack;
                for (int i = 0; i < stack.Count; i++)
                {
                    var f = stack[i];
                    var label = f?.Routine?.Chunk?.ChunkLabel;
                    if (label == null) continue;
                    if ((stage & PrepDone) == 0 && label.Equals("prepare loop", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSteps(ava, dish.Prep);
                        stage |= PrepDone;
                    }
                    else if ((stage & CookDone) == 0 && label.StartsWith("cook loop", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSteps(ava, dish.Cook);
                        stage |= CookDone;
                        var stove = f.Callee;
                        // (only a real stove - "cook loop ( Cheap / Expensive )"; microwaves and toaster ovens don't catch fire)
                        if (stove != null && !(stove is VMAvatar) && label.IndexOf("Cheap", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int chance = FireChance(dish, SkillLevel(ava));
                            if (chance > 0 && (int)ava.Thread.Context.NextRandom(100) < chance)
                            {
                                stage |= WillBurn;
                                callee.SetAttribute(StoveAttr, stove.ObjectID);
                                // burnt (as the stove's own fire marks it): once it comes off the stove the Sim throws it away
                                callee.SetAttribute(FoodBurnt, 1);
                            }
                        }
                    }
                }
            }

            short state = callee.GetAttribute(FoodState);
            // the fire starts as it finishes cooking (the stove's own fire does too)
            if ((stage & (WillBurn | Burned)) == WillBurn && state >= Cooked)
            {
                stage |= Burned;
                Burn(ava, callee, dish);
            }

            // done: how filling and how good - once it's cooked and off the stove (the stove adds its food points
            // after the game already calls it cooked, so not while any cooking script is still running)
            if ((stage & Applied) == 0 && state >= Cooked && !Cooking(ava))
            {
                stage |= Applied;
                ApplyDish(callee, dish, SkillLevel(ava));
            }

            if (stage != was) callee.SetAttribute(StageAttr, (short)stage);
        }

        /// <summary>Is a cooking or preparing script still running for this Sim ("cook food", the stove's "cook core"...)?</summary>
        private static bool Cooking(VMAvatar ava)
        {
            var stack = ava.Thread.Stack;
            for (int i = 0; i < stack.Count; i++)
            {
                var label = stack[i]?.Routine?.Chunk?.ChunkLabel;
                if (label == null) continue;
                if (label.IndexOf("cook", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("prepar", StringComparison.OrdinalIgnoreCase) >= 0
                    || label.IndexOf("microwave", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("toastoven", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void AddSteps(VMAvatar ava, int steps)
        {
            if (steps == 0) return;
            int now = ava.GetPersonData((VMPersonDataVariable)StepsField);
            ava.SetPersonData((VMPersonDataVariable)StepsField, (short)Math.Max(1, Math.Min(200, now + steps)));
        }

        private static void ApplyDish(VMEntity plate, Dish dish, int level)
        {
            int most = plate.GetAttribute(FoodMost), points = plate.GetAttribute(FoodPoints);
            if (most <= 0) most = Math.Max(1, (int)points);
            if (points <= 0) return; // (nothing to scale - leave the game's plate alone)
            // how well it was made (per thousand): the game's own result, plus up to 10% for a Sim who knows the dish well
            int well = points * 1000 / most;
            well += Math.Min(100, Math.Max(0, level - dish.Level) * 30);
            well = Math.Max(200, Math.Min(1000, well));
            int newMost = Math.Max(1, Math.Min(short.MaxValue, most * dish.Fill / 100));
            int newPoints = Math.Max(1, newMost * well / 1000);
            plate.SetAttribute(FoodMost, (short)newMost);
            plate.SetAttribute(FoodPoints, (short)newPoints);
            plate.SetAttribute(FoodPicturePoints, (short)newPoints);
        }

        private static void Burn(VMAvatar ava, VMEntity plate, Dish dish)
        {
            try
            {
                plate.SetAttribute(FoodBurnt, 1);
                var vm = ava.Thread.Context.VM;
                var stove = vm.GetObjectById(plate.GetAttribute(StoveAttr));
                if (stove == null || stove.Dead || stove.Position == LotTilePos.OUT_OF_WORLD) return;
                var pos = stove.Position;
                var at = vm.Context.ObjectQueries.GetObjectsAt(pos);
                bool already = false;
                if (at != null) foreach (var o in at) if (o.Object?.OBJ?.GUID == Primitives.VMBurn.FIRE_GUID) { already = true; break; }
                if (!already) vm.Context.CreateObjectInstance(Primitives.VMBurn.FIRE_GUID, LotTilePos.FromBigTile(pos.TileX, pos.TileY, pos.Level), Direction.NORTH);
                if ((((VMEntityFlags2)stove.GetValue(VMStackObjectVariable.FlagField2) & VMEntityFlags2.Burns) > 0) && !stove.GetFlag(VMEntityFlags.FireProof))
                    stove.SetFlag(VMEntityFlags.Burning, true);
                ReduxComputer.Notice(ava, "Fire!", ava.Name + " set the " + dish.Name + " on fire! " + dish.Name + " is tricky for a new cook - it gets safer with more Cooking skill.");
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] cooking: " + e.Message); }
        }
    }
}
