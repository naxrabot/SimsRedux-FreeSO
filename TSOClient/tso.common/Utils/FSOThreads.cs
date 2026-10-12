using System;
using System.Threading;

namespace FSO.Common.Utils
{
    /// <summary>
    /// SimsRedux: starts background work. Normally that's a new thread. Where threads aren't
    /// available (the web browser version sets FSOEnvironment.NoThreads), the work runs on the
    /// game thread at the start of the next update instead, so a loading screen can draw first.
    /// </summary>
    public static class FSOThreads
    {
        public static void Start(Action work)
        {
            if (FSOEnvironment.NoThreads)
            {
                GameThread.NextUpdate((state) => work());
                return;
            }
            new Thread(() => work()).Start();
        }
    }
}
