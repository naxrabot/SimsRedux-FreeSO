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

            if (RecordStream != null) RecordTick(tick);
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
                }
                Executing = null;
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
                vm.InternalTick(tick.TickID);
                if (DesyncCooldown > 0) DesyncCooldown--;
            }
            LastTick = tick.TickID;
        }

        //functions for recording command streams. 
        //Should work on client or server, assuming there is a sync point at the start.

        public void Record(Stream output)
        {
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
