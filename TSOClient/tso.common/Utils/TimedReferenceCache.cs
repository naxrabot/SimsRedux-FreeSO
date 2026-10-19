using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace FSO.Common.Utils
{
    public static class TimedReferenceController
    {
        private const int DerefThreshold = 100;

        private static int CurRingNum = 0;
        private static List<HashSet<object>> ReferenceRing;
        private static Dictionary<object, int> ObjectToRing;
        private static int CheckFreq = 0;
        private static int TicksToNextCheck;
        private static CacheType Type;
        private static object InternalLock = new object { };
        private static int DerefsSinceLastCollect = 0;
        public static CacheType CurrentType { get { return Type; } }

        static TimedReferenceController()
        {
            SetMode(CacheType.ACTIVE);
        }

        public static void Clear()
        {
            lock (InternalLock)
            {
                ObjectToRing = new Dictionary<object, int>();
                foreach (var item in ReferenceRing)
                {
                    foreach (var obj in item)
                    {
                        (obj as ITimedCachable)?.Rereferenced(false);
                    }
                    item.Clear();
                }
                GC.Collect(2);
            }
        }

        public static void SetMode(CacheType type)
        {
            lock (InternalLock) {
                ObjectToRing = new Dictionary<object, int>();
                switch (type)
                {
                    case CacheType.AGGRESSIVE:
                        ReferenceRing = new List<HashSet<object>>();
                        CheckFreq = 1 * 60;
                        for (int i = 0; i < 5; i++) ReferenceRing.Add(new HashSet<object>());
                        break;
                    case CacheType.ACTIVE:
                        ReferenceRing = new List<HashSet<object>>();
                        CheckFreq = 5 * 60;
                        for (int i = 0; i < 3; i++) ReferenceRing.Add(new HashSet<object>());
                        break;
                    case CacheType.PERMANENT:
                    case CacheType.PASSIVE:
                        ReferenceRing = new List<HashSet<object>>();
                        CheckFreq = int.MaxValue;
                        ReferenceRing.Add(new HashSet<object>());
                        break;
                }
                Type = type;
            }
        }

        public static void Tick()
        {
            if (Type == CacheType.PERMANENT) return;
            if (TicksToNextCheck-- <= 0)
            {
                lock (InternalLock)
                {
                    var toDereference = ReferenceRing[CurRingNum];
                    DerefsSinceLastCollect += toDereference.Count;
                    foreach (var obj in toDereference) ObjectToRing.Remove(obj);
                    toDereference.Clear();
                    CurRingNum = (CurRingNum + 1) % ReferenceRing.Count;
                }
                TicksToNextCheck = CheckFreq;
                //GC.Collect();
                if (CurRingNum == 0 && DerefsSinceLastCollect >= DerefThreshold)
                {
                    GC.Collect();
                    DerefsSinceLastCollect = 0;
                }
            }
        }

        public static void KeepAlive(object o, KeepAliveType type)
        {
            if (type == KeepAliveType.ACCESS && (o is ITimedCachable)) ((ITimedCachable)o).Rereferenced(true);
            //should be called whenever the object is referenced
            lock (InternalLock)
            {
                var offset = ReferenceRing.Count - 1;
                var becomes = (CurRingNum + offset) % ReferenceRing.Count;
                int oldring;
                if (ObjectToRing.TryGetValue(o, out oldring))
                {
                    if (becomes != oldring)
                    {
                        // SimsRedux: moved to the newest ring. (This removed the wrong key and never added it to the
                        // new ring, so anything used twice stayed in ObjectToRing - kept forever - until a lot change:
                        // the main reason the browser ran out of memory.)
                        ReferenceRing[oldring].Remove(o);
                        ReferenceRing[becomes].Add(o);
                        ObjectToRing[o] = becomes;
                    }
                }
                else
                {
                    ReferenceRing[becomes].Add(o);
                    ObjectToRing.Add(o, becomes);
                }
            }
        }
    }

    public enum KeepAliveType
    {
        ACCESS,
        DEREFERENCED
    }

    public enum CacheType
    {
        AGGRESSIVE,
        ACTIVE,
        PASSIVE,
        PERMANENT
    }


    public class TimedReferenceCache<KEY, VALUE>
    {
        private ConcurrentDictionary<KEY, WeakReference> Cache = new ConcurrentDictionary<KEY, WeakReference>();
        private bool PermaMode = false;
        private List<VALUE> PermaRef = new List<VALUE>();
        private VALUE GetOrAddInternal(KEY key, Func<KEY, VALUE> valueFactory)
        {
            bool didCreate = false;
            VALUE created = default(VALUE);
            var value = Cache.GetOrAdd(key, (k) =>
            {
                //ConcurrentDictionary does not ensure we don't accidentally create things twice. This lock will help us, but I don't think it ensures a perfect world.
                lock (this) {
                    WeakReference prev;
                    if (Cache.TryGetValue(key, out prev) && prev.IsAlive && !IsDead(prev.Target)) return prev; //already created this value.
                    created = valueFactory(k);
                    didCreate = true;
                    return new WeakReference(created, true);
                }
            });

            var refVal = value.Target;
            if (didCreate && refVal == (object)created)
            {
                //i made this. if we're perma cache ensure a permanent reference.
                if (TimedReferenceController.CurrentType == CacheType.PERMANENT)
                {
                    PermaRef.Add(created);
                    PermaMode = true;
                }
                return created;
            }
            else if ((refVal != null || value.IsAlive) && !IsDead(refVal))
                return (VALUE)refVal;
            else
            {
                //refrerence died. must recache.
                WeakReference resultRemove;
                var removed = Cache.TryRemove(key, out resultRemove);
                return GetOrAddInternal(key, valueFactory);
            }
        }

        // SimsRedux: cleaned up for good (see ITimedCachableDead) - made again instead of handed out
        private static bool IsDead(object o) { return o is ITimedCachableDead d && d.ReduxDead; }

        public VALUE GetOrAdd(KEY key, Func<KEY, VALUE> valueFactory)
        {
            var result = GetOrAddInternal(key, valueFactory);
            if (result != null && !PermaMode)
            {
                TimedReferenceController.KeepAlive(result, KeepAliveType.ACCESS);
            }
            return result;
        }

        public bool TryRemove(KEY key, out VALUE value)
        {
            WeakReference resultRef;
            if (Cache.TryRemove(key, out resultRef))
            {
                value = (VALUE)resultRef.Target;
                return true;
            } else
            {
                value = default(VALUE);
                return false;
            }
        }
    }
}
