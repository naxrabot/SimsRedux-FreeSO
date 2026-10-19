using System.Runtime.CompilerServices;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: a lookup keyed by game data (an object's pie table, a script) that doesn't keep that data
    /// alive. The game's files are let go when nothing uses them and loaded again later; a plain dictionary kept
    /// every old copy (with all its pictures) for good.
    /// </summary>
    public class ReduxWeakMap<TKey, TValue> where TKey : class
    {
        private class Box { public TValue Value; }
        private readonly ConditionalWeakTable<TKey, Box> Table = new ConditionalWeakTable<TKey, Box>();

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (key != null && Table.TryGetValue(key, out var b)) { value = b.Value; return true; }
            value = default(TValue);
            return false;
        }

        public bool ContainsKey(TKey key) { return key != null && Table.TryGetValue(key, out _); }

        public TValue this[TKey key]
        {
            get { return TryGetValue(key, out var v) ? v : throw new System.Collections.Generic.KeyNotFoundException(); }
            set { if (key != null) Table.AddOrUpdate(key, new Box { Value = value }); }
        }
    }
}
