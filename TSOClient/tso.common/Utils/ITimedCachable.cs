namespace FSO.Common.Utils
{
    public interface ITimedCachable
    {
        void Rereferenced(bool save);
    }

    /// <summary>
    /// SimsRedux: a cached thing that has been cleaned up for good (its last finalizer ran) but can still be
    /// reached for a moment through the cache's weak reference. The cache must never hand it out again.
    /// </summary>
    public interface ITimedCachableDead
    {
        bool ReduxDead { get; }
    }
}
