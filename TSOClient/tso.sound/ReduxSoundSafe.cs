using Microsoft.Xna.Framework.Audio;

namespace FSO.HIT
{
    /// <summary>
    /// SimsRedux: in the browser a sound can be cleaned up by the audio engine while the game still holds it;
    /// asking such a sound whether it's playing threw an error that reset the object using it (the stove, say).
    /// It now just counts as stopped.
    /// </summary>
    public static class ReduxSoundSafe
    {
        public static SoundState SafeState(this SoundEffectInstance inst)
        {
            if (inst == null) return SoundState.Stopped;
            try { return inst.IsDisposed ? SoundState.Stopped : inst.State; }
            catch { return SoundState.Stopped; }
        }
    }
}
