using FSO.Common.Rendering.Framework.Model;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Common.Utils
{
    public enum AssetStreamingMode
    {
        None = 0,
        Lot = 1,
    }

    public static class AssetStreaming
    {
        private static object _StreamCallbacksLock = new object();
        private static Queue<Callback> _StreamUpdateCallbacks = new Queue<Callback>();
        private static Queue<Callback> _StreamUpdateCallbacksSwap = new Queue<Callback>();

        private static AssetStreamingMode _LoadingType = AssetStreamingMode.None;
        private static int _LoadingCount;

        private static int _LoadingRequests;
        private static int _LoadingComplete;

        public static AssetStreamingMode LoadingType => _LoadingType;

        public static void DigestStreamUpdate()
        {
            Queue<Callback> _callbacks;

            lock (_StreamCallbacksLock)
            {
                // Swap the active callbacks queue with the second one, so we can
                // process entries without fear of more being added.

                _callbacks = _StreamUpdateCallbacks;
                _StreamUpdateCallbacks = _StreamUpdateCallbacksSwap;
                _StreamUpdateCallbacksSwap = _callbacks;
            }

            // These callbacks have a frametime budget. If it's exceeded, the callbacks are pushed onto the next frame.
            float frameAllowance = 0.002f;
            float budgetSeconds = Math.Max(1f / FSOEnvironment.RefreshRate - frameAllowance, 0.005f);
            long budgetTicks = (long)(Stopwatch.Frequency * budgetSeconds);

            long startTime = Stopwatch.GetTimestamp();

            while (_callbacks.Count > 0)
            {
                _callbacks.Dequeue()();

                long now = Stopwatch.GetTimestamp();

                if ((now - startTime) > budgetTicks)
                {
                    break;
                }
            }

            if (_callbacks.Count > 0)
            {
                lock (_StreamCallbacksLock)
                {
                    // Push remaining callbacks onto the next frame.

                    while (_callbacks.Count > 0)
                    {
                        _StreamUpdateCallbacks.Enqueue(_callbacks.Dequeue());
                    }
                }
            }
        }

        public static void InStreamUpdate(Callback callback)
        {
            lock (_StreamCallbacksLock)
            {
                _StreamUpdateCallbacks.Enqueue(callback);
            }
        }

        /// <summary>
        /// Load a texture with support for multithreading during loading screens.
        /// The texture should already be created with the correct width and height.
        /// The data provider is called from a background thread when a loading state is active,
        /// otherwise it's done in the current thread.
        /// </summary>
        /// <typeparam name="T">Texture data type</typeparam>
        /// <param name="tex">Texture to put data into</param>
        /// <param name="type">The type of loading required for this texture load to multithread</param>
        /// <param name="dataProvider">Texture data generator</param>
        public static void LoadTexture<T>(Texture2D tex, AssetStreamingMode type, Func<TextureData<T>[]> dataProvider) where T : struct
        {
            if (_LoadingType >= type)
            {
                // Async load. Try source the data on a task, then set it on the game thread.
                AddLoadingResource();

                Task.Run(dataProvider).ContinueWith((taskResult) =>
                {
                    // SimsRedux: a picture whose data failed to come in on the side is made again on the
                    // game's own turn, instead of the texture staying empty (black) for good
                    var data = taskResult.IsFaulted ? null : taskResult.Result;
                    InStreamUpdate(() =>
                    {
                        try
                        {
                            if (data == null) data = dataProvider();
                            if (!tex.IsDisposed) TextureUtils.UploadTexData(tex, data);
                        }
                        catch (Exception e) { Console.WriteLine("[SimsRedux] texture load failed: " + e.Message); }

                        RemoveLoadingResource();
                    });
                });
            }
            else
            {
                TextureData<T>[] data = null;
                try { data = dataProvider(); }
                catch (Exception e) { Console.WriteLine("[SimsRedux] texture load failed, trying again: " + e.Message); }
                if (data == null)
                {
                    // (tried once more on the next frame)
                    InStreamUpdate(() =>
                    {
                        try { if (!tex.IsDisposed) TextureUtils.UploadTexData(tex, dataProvider()); }
                        catch (Exception e) { Console.WriteLine("[SimsRedux] texture load failed: " + e.Message); }
                    });
                    return;
                }

                TextureUtils.UploadTexData(tex, data);
            }
        }

        public static void BeginStreaming(AssetStreamingMode type)
        {
            _LoadingType = type;
        }

        /// <summary>
        /// Ends the multithreaded loading period.
        /// Returns true when there are no pending loads.
        /// </summary>
        /// <returns></returns>
        public static bool EndStreaming()
        {
            _LoadingType = AssetStreamingMode.None;

            return Volatile.Read(ref _LoadingCount) == 0;
        }

        public static void AddLoadingResource()
        {
            Interlocked.Increment(ref _LoadingRequests);
            Interlocked.Increment(ref _LoadingCount);
        }

        public static void RemoveLoadingResource()
        {
            Interlocked.Increment(ref _LoadingComplete);
            Interlocked.Decrement(ref _LoadingCount);
        }

        public static int GetCheckpoint()
        {
            // Note: doesn't entirely work as intended right now.
            // Mesh reading tasks can dispatch other texture reading tasks that aren't counted when the checkpoint was taken.

            return Volatile.Read(ref _LoadingRequests);
        }

        public static bool IsCheckpointMet(int checkpoint)
        {
            int diff = Volatile.Read(ref _LoadingComplete) - checkpoint;

            return diff >= 0;
        }
    }
}
