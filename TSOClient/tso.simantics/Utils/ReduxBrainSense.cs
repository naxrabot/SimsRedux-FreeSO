using System;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 18.5 (Part B #9, B5a): sensible behaviour - brain factors (see VMReduxBrainFactors and VMReduxBrain's FACTOR
    /// API notes) that make an Adaptive Sim act the way a sensible person would:
    ///   sense-money       no pizza or paid services when the family is broke, or when there are bills waiting and money's tight;
    ///   sense-fire        a grown-up keeps their head in a fire: much less panicking at it, no fun and chats while it burns
    ///                     (calling the fire department and putting it out themselves are ReduxBrain.FindChore's essentials);
    ///   sense-cook-tired  no cooking on the stove while exhausted with low Cooking skill (Sims 1 kitchen fires) - a snack or a
    ///                     quick meal instead;
    ///   sense-tidy        tidying without being told: trash, puddles, dishes, a full trash can... before the room goes red - a
    ///                     little more for neat Sims (their Neat points, the Neat Freak trait).
    /// The essentials that need no factor (they're chosen outright, not weighed - and since 18.5 found by English name, the
    /// same on every game of a visit) stay in VMFindBestAction's ReduxBrain.FindChore: paying the bills when they're waiting,
    /// calling the fire department, putting a fire out, fixing broken things with the Mechanical skill for it, otherwise calling
    /// the repairman. Lights: Sims 1 has no lights choice a Sim makes on their own (lamps switch with the room), so none here.
    ///
    /// Every effect is moderate (about x0.3 - x2.5) so a real emergency - a need in the red - still wins. Only lot state is read
    /// (the family's money, objects by GUID, the Sim's needs/skills/personality/traits, the choice's ads and English name), with
    /// no allocations: the same answer on every game, for every choice of every Sim.
    /// </summary>
    public static class VMReduxBrainSense
    {
        /// <summary>The Sims 1 bills (Bills.iff, "Bills" - its "Pay Bills"): one is on the lot while bills are waiting.</summary>
        public const uint BillsGUID = 0xD840F488;

        public static void Register()
        {
            VMReduxBrain.AddFactorPerMille("sense-money", Money);
            VMReduxBrain.AddFactorPerMille("sense-fire", Fire);
            VMReduxBrain.AddFactorPerMille("sense-cook-tired", CookTired);
            VMReduxBrain.AddFactorPerMille("sense-tidy", Tidy);
        }

        // ------------------------------------------------------------------ money sense

        /// <summary>The family's money under these: broke / short / tight. Bills waiting count as tight under BillsTightBelow.</summary>
        public const int BrokeBelow = 100, ShortBelow = 300, TightBelow = 700, BillsTightBelow = 1500;

        // (English names that cost the family money: the phone's "Services.../Pizza", an expansion's "Order ..." / "Buy ...")
        private static readonly string[] PaidStarts = { "services", "order ", "buy ", "hire " };

        /// <summary>
        /// sense-money: a choice that costs money - phoning for pizza, a paid service, buying or ordering - when the family's
        /// money is low: x0.3 under $100, x0.5 under $300, x0.75 under $700; bills waiting with under $1500 makes it x0.8 more.
        /// </summary>
        private static int Money(VMReduxBrainContext ctx)
        {
            var fam = ctx.VM?.TS1State?.CurrentFamily;
            if (fam == null || ctx.Target is VMAvatar) return 1000;
            int budget = fam.Budget;
            if (budget >= BillsTightBelow) return 1000; // (quick out - plenty of money)
            bool bills = BillsWaiting(ctx.VM);
            if (budget >= TightBelow && !bills) return 1000;
            if (!Paid(VMReduxBrain.LotName(ctx.Target, ctx.Entry))) return 1000;
            int m = budget < BrokeBelow ? 300 : budget < ShortBelow ? 500 : budget < TightBelow ? 750 : 1000;
            if (bills) m = m * 800 / 1000;
            return m; // (no Note: a lowered choice that still wins isn't one to explain by money)
        }

        private static bool Paid(string english)
        {
            if (string.IsNullOrEmpty(english)) return false;
            var n = english.TrimStart();
            for (int i = 0; i < PaidStarts.Length; i++)
                if (n.StartsWith(PaidStarts[i], StringComparison.OrdinalIgnoreCase)) return true;
            return n.IndexOf("Pizza", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Slice", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>Bills are waiting to be paid (a Bills object is on the lot - by GUID, no list made).</summary>
        public static bool BillsWaiting(VM vm)
        {
            var list = vm?.Context?.ObjectQueries?.GetObjectsByGUID(BillsGUID);
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && !list[i].Dead) return true;
            return false;
        }

        // ------------------------------------------------------------------ safety: fire

        /// <summary>A fire is burning on the lot (the Sims 1 fire object, by GUID - no list made).</summary>
        public static bool FireOnLot(VM vm)
        {
            var list = vm?.Context?.ObjectQueries?.GetObjectsByGUID(Primitives.VMBurn.FIRE_GUID);
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && !list[i].Dead) return true;
            return false;
        }

        /// <summary>
        /// sense-fire: while a fire burns, a grown-up's choices at the fire itself (the game's "Fire!" panic) x0.5, and anything
        /// only for fun or company (TV, games, a chat) x0.6. Children are left to the game (they can't call or put it out).
        /// Calling the fire department and putting the fire out are FindChore's (chosen outright, before any of this).
        /// </summary>
        private static int Fire(VMReduxBrainContext ctx)
        {
            if (ctx.Sim == null || ctx.Sim.IsChild || !FireOnLot(ctx.VM)) return 1000;
            if (ctx.Target?.Object?.OBJ != null && ctx.Target.Object.OBJ.GUID == Primitives.VMBurn.FIRE_GUID) return 500;
            if (FunOrCompanyOnly(ctx)) return 600;
            return 1000;
        }

        /// <summary>The choice only gives fun or company (no physical need, comfort or room).</summary>
        private static bool FunOrCompanyOnly(VMReduxBrainContext ctx)
        {
            if (ctx.Ad(VMMotive.Fun) <= 0 && ctx.Ad(VMMotive.Social) <= 0) return false;
            return ctx.Ad(VMMotive.Hunger) <= 0 && ctx.Ad(VMMotive.Bladder) <= 0 && ctx.Ad(VMMotive.Energy) <= 0
                && ctx.Ad(VMMotive.Hygiene) <= 0 && ctx.Ad(VMMotive.Comfort) <= 0 && ctx.Ad(VMMotive.Room) <= 0;
        }

        // ------------------------------------------------------------------ safety: cooking while exhausted

        /// <summary>Energy under this counts as exhausted for cooking; a Cooking level (with a Foodie's bonus) under SafeCookLevel is low.</summary>
        public const int TiredBelow = -40, VeryTiredBelow = -70, SafeCookLevel = 4;

        /// <summary>
        /// sense-cook-tired: an exhausted Sim (energy under -40) with a low Cooking level (under 4, a Foodie counting 3 better):
        /// a meal on the stove (the fridge's "Have A Meal" / "Serve Meal") x0.6, x0.4 under -70; a snack or quick meal x1.25.
        /// Not when starving (hunger under -70): eating anything wins then.
        /// </summary>
        private static int CookTired(VMReduxBrainContext ctx)
        {
            if (ctx.Sim == null || ctx.Ad(VMMotive.Hunger) <= 0) return 1000;
            int energy = ctx.Need(VMMotive.Energy);
            if (energy >= TiredBelow || ctx.Need(VMMotive.Hunger) < -70) return 1000;
            if (ReduxCooking.SkillLevel(ctx.Sim) + VMReduxTraits.CookBonus(ctx.Sim) >= SafeCookLevel) return 1000;
            var kind = ReduxCooking.KindOf(ctx.Target, ctx.Entry);
            if (kind == ReduxCooking.Kind.Meal || kind == ReduxCooking.Kind.Group) return energy < VeryTiredBelow ? 400 : 600;
            if (kind == ReduxCooking.Kind.Quick || IsSnack(ctx))
            {
                ctx.Note("Too tired to cook safely - something quick instead");
                return 1250;
            }
            return 1000;
        }

        /// <summary>The fridge's "Have A Snack" (by its English name).</summary>
        private static bool IsSnack(VMReduxBrainContext ctx)
        {
            if (!ReduxLeftovers.IsFridge(ctx.Target)) return false;
            var n = VMReduxBrain.LotName(ctx.Target, ctx.Entry);
            return n != null && n.IndexOf("Snack", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // ------------------------------------------------------------------ tidying without being told

        /// <summary>
        /// sense-tidy: a cleaning choice (a chore - VMReduxBrain.KindOf - that gives Room and no fun, company or food: "Clean Up" on trash, a puddle or a
        /// dirty plate, "Empty Trash", "Clean" on a toilet or tub, "Make Bed", "Water"...) as the room need falls: x1.15 under
        /// 60, x1.4 under 30, x1.7 under 0, x2 under -40 (red) - then up to +20% more by the Sim's Neat points, and x1.15 more
        /// for a Neat Freak (the trait's own x2 is VMReduxTraits'), at most x2.5. (A Slob's x0.35 stays VMReduxTraits'.)
        /// Each such choice only shows when its object needs it (its own check), so a boost means there's mess.
        /// </summary>
        private static int Tidy(VMReduxBrainContext ctx)
        {
            if (ctx.Sim == null || ctx.Target is VMAvatar || ctx.Kind != VMReduxBrain.Kind.Chores) return 1000; // (not a bar's "Have a Drink")
            if (ctx.Ad(VMMotive.Room) <= 0 || ctx.Ad(VMMotive.Fun) > 0 || ctx.Ad(VMMotive.Social) > 0 || ctx.Ad(VMMotive.Hunger) > 0) return 1000;
            int room = ctx.Need(VMMotive.Room);
            int m = room < -40 ? 2000 : room < 0 ? 1700 : room < 30 ? 1400 : room < 60 ? 1150 : 1000;
            if (m == 1000) return 1000;
            int neat = Math.Max(0, Math.Min(1000, (int)ctx.Sim.GetPersonData(VMPersonDataVariable.NeatPersonality)));
            m = m * (1000 + neat / 5) / 1000;
            if (VMReduxTraits.Has(ctx.Sim, VMReduxTraits.T.NeatFreak)) m = m * 1150 / 1000;
            m = Math.Min(2500, m);
            ctx.Note(room < 0 ? "The place is getting messy - tidying up" : "Tidying up before it gets messy");
            return m;
        }
    }
}
