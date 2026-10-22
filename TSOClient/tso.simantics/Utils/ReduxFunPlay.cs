using System;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.1: a Sim playing with something (pinball, a video game, the TV, the dollhouse, the toy box, a
    /// train set, the stereo...) keeps playing until Fun is full - or until the player stops them, they're given
    /// something more important, or a need gets low.
    ///
    /// Why they stopped after one round: every Sims 1 fun object raises Fun only up to its own level (its tuning:
    /// pinball 55-65, the toy box 50, the computers 65-89) and, after each round, asks the global "Exit fun object"
    /// with that same level: "Fun at or above it? Then stop." So a Sim whose Fun was already near or past that level
    /// - most of the time with the smarter Sims, who keep their needs up - played one round and walked off to
    /// something else.
    ///
    /// Now, for the household's own Sims (not visitors, NPCs or pets): when that check is called while the object
    /// is raising their Fun, the level it raises Fun to is lifted to full and the check is asked about "full"
    /// instead. Everything else in the check stays as it was: the player's cancel or a new order (the check's own
    /// "notified?" step), and getting up when hunger, bladder, energy or comfort runs low (with the smarter limits,
    /// VMMemory.GetTuningVariable). An object that doesn't raise Fun as it's used is left to its own rules (so a Sim
    /// can't be kept at it with nothing gained).
    ///
    /// Only the Sim's needs, person data and the script's own call are read, and only the check's argument (saved
    /// with the script's stack) and the running Fun change's ceiling (saved with the Sim) are written, so every
    /// game on a lot does the same thing. No save format change.
    /// </summary>
    public static class ReduxFunPlay
    {
        /// <summary>The global "Exit fun object" (Global.iff 0x195); its name is checked too (VMRoutine.ReduxKindExitFun).</summary>
        public const ushort ExitFunObject = 0x195;
        /// <summary>Fun a Sim plays on to ("full"; the bar shows nearly all of it), and the ceiling the object's Fun gain is lifted to.</summary>
        public const short FullFun = 95, FunTop = 100;

        /// <summary>Called just after "Exit fun object" was pushed for this frame (the called script is now on top of the stack).</summary>
        public static void KeepPlaying(VMStackFrame frame)
        {
            try
            {
                var sim = frame.Caller as VMAvatar;
                var thread = frame.Thread;
                if (sim == null || thread == null || sim.IsPet || !frame.ActionTree) return;
                if (sim.GetPersonData(VMPersonDataVariable.PersonType) != 0) return; // (visitors and NPCs: the game's own rules)
                var stack = thread.Stack;
                if (stack == null || stack.Count == 0) return;
                var child = stack[stack.Count - 1];
                if (child == null || child == frame || child.Routine == null || child.Routine.GetReduxKind() != VMRoutine.ReduxKindExitFun) return;
                if (child.Args == null || child.Args.Length < 1) return;

                var fun = sim.GetMotiveData(VMMotive.Fun);
                var level = child.Args[0];
                bool lifted = level < FullFun && fun < FullFun && sim.ReduxRaiseMotiveCap(VMMotive.Fun, FunTop);
                if (lifted) child.Args[0] = FullFun;

                // (debug, SIMSREDUX_QUEUE_LOG=1: why a Sim stops playing - "notified" means the player or a waiting
                //  order stopped them; otherwise it's Fun reaching the level, or a low need)
                if (VMThread.ReduxQueueLog)
                {
                    var act = thread.ActiveAction;
                    Console.WriteLine("[SimsRedux] fun check: " + sim.Name + " at " + (frame.Callee?.ToString() ?? "?")
                        + " (" + (act?.Name ?? "?") + ", priority " + sim.GetPersonData(VMPersonDataVariable.Priority) + ")"
                        + ": Fun " + fun + ", object stops at " + level + (lifted ? " -> plays on to " + FullFun : " (unchanged)")
                        + ", notified " + (act != null && act.NotifyIdle) + ", queue " + thread.Queue.Count
                        + ", hunger " + sim.GetMotiveData(VMMotive.Hunger) + " bladder " + sim.GetMotiveData(VMMotive.Bladder)
                        + " energy " + sim.GetMotiveData(VMMotive.Energy) + " comfort " + sim.GetMotiveData(VMMotive.Comfort));
                }
            }
            catch (Exception) { }
        }
    }
}
