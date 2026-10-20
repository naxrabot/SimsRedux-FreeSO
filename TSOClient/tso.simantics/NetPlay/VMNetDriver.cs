using FSO.SimAntics.NetPlay.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Engine.TSOTransaction;
using System.IO;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay
{
    public abstract class VMNetDriver
    {
        /// <summary>SimsRedux: the last few commands and what happened to them (shown by the web build's ?dbg=vm box).</summary>
        public static readonly System.Collections.Generic.List<string> DebugLog = new System.Collections.Generic.List<string>();
        public static void Note(string text)
        {
            lock (DebugLog)
            {
                DebugLog.Add(text);
                if (DebugLog.Count > 12) DebugLog.RemoveAt(0);
            }
        }

        // ---- SimsRedux soak test hooks: null unless the soak test is running; they only look, never change anything ----
        /// <summary>A command threw while running (driver, lot, command, exception).</summary>
        public static System.Action<VMNetDriver, VM, VMNetCommandBodyAbstract, System.Exception> ReduxOnCommandFailed;
        /// <summary>Every command that ran (driver, lot, command, whether Execute said it worked).</summary>
        public static System.Action<VMNetDriver, VM, VMNetCommandBodyAbstract, bool> ReduxOnCommandDone;
        /// <summary>The lot's random seed didn't match the tick's (driver, lot, tick, expected seed, actual seed).</summary>
        public static System.Action<VMNetDriver, VM, uint, ulong, ulong> ReduxOnDesync;
        /// <summary>A tick ran (driver, lot, tick id, how long it took in Stopwatch ticks).</summary>
        public static System.Action<VMNetDriver, VM, uint, long> ReduxOnTicked;

        public IVMTSOGlobalLink GlobalLink;
        public abstract void SendCommand(VMNetCommandBodyAbstract cmd);
        public abstract void SendDirectCommand(uint pid, VMNetCommandBodyAbstract cmd);
        public abstract bool Tick(VM vm);
        public abstract string GetUserIP(uint uid);
        public VMCloseNetReason CloseReason;
        public string CloseString;
        private BinaryWriter RecordStream;
        public VMNetCommand Executing;

        /// <summary>
        /// Indicates a VM inspired total connection shutdown. 
        /// </summary>
        public event VMNetClosedHandler OnShutdown;
        public delegate void VMNetClosedHandler(VMCloseNetReason reason);

        public uint DesyncTick = 0;
        private int DesyncCooldown = 30;
        public uint LastTick = 0;
        public uint GreatestTick = 0;
        public uint CurrentTick = 0;
        public bool InResync
        {
            get
            {
                //if the current tick is less than our greatest tick
                //then the client has already seen this tick.
                return GreatestTick > CurrentTick;
            }
        }

        public bool AsyncBreak; //if 

        protected void InternalTick(VM vm, VMNetTick tick)
        {
            if (tick.Commands == null) tick.Commands = new System.Collections.Generic.List<VMNetCommand>(); // (SimsRedux)
            CurrentTick = tick.TickID;
            if (CurrentTick > GreatestTick) GreatestTick = CurrentTick;
            if (!tick.ImmediateMode && (tick.Commands.Count == 0 || !(tick.Commands[0].Command is VMStateSyncCmd)) && vm.Context.RandomSeed != tick.RandomSeed)
            {
                var onDesync = ReduxOnDesync;
                if (onDesync != null) { try { onDesync(this, vm, CurrentTick, tick.RandomSeed, vm.Context.RandomSeed); } catch { } }
                if (DesyncCooldown == 0)
                {
                    System.Console.WriteLine("DESYNC - Requested state from host");
                    if (DesyncTick == 0) DesyncTick = CurrentTick - 1;
                    vm.SendCommand(new VMRequestResyncCmd());
                    DesyncCooldown = 30 * 30;
                }
                else
                {
                    System.Console.WriteLine("WARN - DESYNC - Expected " + tick.RandomSeed + ", was at " + vm.Context.RandomSeed);
                }
            }

            if (RecordStream != null) RecordTick(ReduxRecordSpeed ? ReduxWithSpeed(vm, tick) : tick);
            vm.Context.RandomSeed = tick.RandomSeed;
            AsyncBreak = false;
            bool doTick = !tick.ImmediateMode;
            foreach(var cmd in tick.Commands)
            {
                if (cmd.Command is VMStateSyncCmd && ((VMStateSyncCmd)cmd.Command).Run)
                {
                    if (LastTick + 1 != tick.TickID) System.Console.WriteLine("Jump to tick " + tick.TickID);
                    if (!(this is VMFSORDriver)) doTick = false; //something weird here. this can break loading from saves casually - but must not be active for resyncs.
                    //disable just for fsor playback
                }

                if (cmd?.Command == null) continue; // SimsRedux: a broken command is skipped, not a crash
                var caller = vm.GetAvatarByPersist(cmd.Command.ActorUID);
                Executing = cmd;
                bool ok;
                // SimsRedux: one command going wrong (a click on something that just vanished, say) used
                // to stop the whole game with "Something went wrong". Now it's logged and skipped.
                try { ok = cmd.Command.Execute(vm, caller); }
                catch (System.Exception e)
                {
                    ok = false;
                    System.Console.WriteLine("[SimsRedux] command " + cmd.Command.GetType().Name + " failed and was skipped: " + e);
                    var onFailed = ReduxOnCommandFailed;
                    if (onFailed != null) { try { onFailed(this, vm, cmd.Command, e); } catch { } }
                }
                Executing = null;
                var onDone = ReduxOnCommandDone;
                if (onDone != null) { try { onDone(this, vm, cmd.Command, ok); } catch { } }
                if (!(cmd.Command is VMStateSyncCmd)) Note(cmd.Command.GetType().Name.Replace("VMNet", "").Replace("Cmd", "") + (ok ? " done" : " FAILED") + (caller == null ? " (no Sim for uid " + cmd.Command.ActorUID + ")" : ""));
                if (vm.FSOVAsyncLoading)
                {
                    tick.Commands.Remove(cmd); //don't worry about modifying an enumerated collection - we are exiting anyways.
                    AsyncBreak = true;
                    return;
                }
            }

            if (tick.TickID < LastTick) System.Console.WriteLine("Tick wrong! Got " + tick.TickID + ", Missed " + ((int)tick.TickID - (LastTick + 1)));
            else if (doTick && vm.Context.Ready)
            {
                if (tick.TickID > LastTick + 1) System.Console.WriteLine("Tick wrong! Got " + tick.TickID + ", Missed " + ((int)tick.TickID - (LastTick + 1)));
                vm.Trace?.NewTick(tick.TickID);
                var onTicked = ReduxOnTicked;
                if (onTicked == null) vm.InternalTick(tick.TickID);
                else
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    vm.InternalTick(tick.TickID);
                    try { onTicked(this, vm, tick.TickID, System.Diagnostics.Stopwatch.GetTimestamp() - started); } catch { }
                }
                if (DesyncCooldown > 0) DesyncCooldown--;
            }
            LastTick = tick.TickID;
        }

        //functions for recording command streams. 
        //Should work on client or server, assuming there is a sync point at the start.

        /// <summary>
        /// SimsRedux soak test: the recording also notes the lot's speed whenever it changed (single player sets
        /// it directly, not through a command - a pause for a question, the speed buttons), so a replay pauses
        /// and runs exactly where the recorded game did. Only the recorded copy of the tick gets the note.
        /// </summary>
        public bool ReduxRecordSpeed;
        private int ReduxRecordedSpeed = int.MinValue, ReduxRecordedLastSpeed;

        private VMNetTick ReduxWithSpeed(VM vm, VMNetTick tick)
        {
            if (vm.SpeedMultiplier == ReduxRecordedSpeed && vm.LastSpeedMultiplier == ReduxRecordedLastSpeed) return tick;
            ReduxRecordedSpeed = vm.SpeedMultiplier;
            ReduxRecordedLastSpeed = vm.LastSpeedMultiplier;
            var copy = new VMNetTick
            {
                TickID = tick.TickID,
                RandomSeed = tick.RandomSeed,
                ImmediateMode = tick.ImmediateMode,
                Commands = new System.Collections.Generic.List<VMNetCommand>(tick.Commands),
            };
            copy.Commands.Insert(0, new VMNetCommand(new VMNetSetSpeedCmd { Full = true, Speed = vm.SpeedMultiplier, LastSpeed = vm.LastSpeedMultiplier }));
            return copy;
        }

        public void Record(Stream output)
        {
            ReduxRecordedSpeed = int.MinValue;
            RecordStream = new BinaryWriter(output);
            RecordStream.Write(new char[] { 'F', 'S', 'O', 'r' });
            RecordStream.Write(0); //version, for future extension
        }

        public void RecordTick(VMNetTick tick)
        {
            tick.SerializeInto(RecordStream);
        }

        public void EndRecord()
        {
            RecordStream?.BaseStream?.Close();
            RecordStream = null; // (SimsRedux: a closed recording is never written to again)
        }

        /// <summary>SimsRedux soak test: recording right now.</summary>
        public bool IsRecording { get { return RecordStream != null; } }

        /// <summary>SimsRedux soak test: everything recorded so far written out (so the file can be copied).</summary>
        public void FlushRecord()
        {
            try { RecordStream?.Flush(); } catch { }
        }

        public virtual void Shutdown()
        {
            if (OnShutdown != null) OnShutdown(CloseReason);
        }

        public delegate void VMNetMessageHandler(VMNetMessageType type, byte[] data);
    }

    public enum VMCloseNetReason
    {
        Unspecified = 0,
        LeaveLot = 1,
        ServerShutdown = 2,
        NetException = 3, //server sent a bad tick
        NetExceptionDirect = 4
    }
}
