namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 18.5 (Part B #9, B5a): the Sim brain's behaviour factors - one place that registers them all, called once from
    /// VMReduxBrain's static constructor (so before any lot weighs a choice or a visit reads the factor hash). Each group lives in
    /// its own file:
    ///   VMReduxBrainAhead  - thinking ahead (day planning, predicting needs, two needs at once) and the family working together
    ///                        (no rushing to the same object, shared knowledge of what's broken, sharing chores, the baby);
    ///   VMReduxBrainSense  - sensible behaviour (money sense, safety, tidying without being told);
    ///   VMReduxBrainGoals  - 19.5 (B5b): long-term goals (career sense, social memory, mood-aware);
    ///   VMReduxBrainHabits - 19.5 (B5b): habits (favourites, variety, finishing what they start, natural sleep);
    ///   VMReduxBrainCare   - 19.5 (B5b): caring for others (parenting, pet care, keeping friends, good host).
    /// Rules for every factor: VMReduxBrain's FACTOR API notes - lot state only, never change anything, quick, deterministic.
    /// </summary>
    public static class VMReduxBrainFactors
    {
        public static void RegisterAll()
        {
            VMReduxBrainAhead.Register();
            VMReduxBrainSense.Register();
            VMReduxBrainGoals.Register();  // 19.5 (B5b)
            VMReduxBrainHabits.Register();
            VMReduxBrainCare.Register();
        }
    }
}
