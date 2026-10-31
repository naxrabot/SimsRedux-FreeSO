//#define THROW_SIMANTICS
#if !Server
    #define IDE_COMPAT
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Model;
using FSO.SimAntics.Marshals.Threads;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.NetPlay.EODs.Model;
using System.Threading;
using FSO.SimAntics.Engine.Primitives;
using FSO.SimAntics.Engine.Utils;
using FSO.SimAntics.Engine.Scopes;

namespace FSO.SimAntics.Engine
{
    /// <summary>
    /// Handles instruction execution
    /// </summary>
    public class VMThread
    {
        public static int MAX_USER_ACTIONS = 20;

        public VMContext Context;
        private VMEntity Entity;

        public VMThreadBreakMode ThreadBreak = VMThreadBreakMode.Active;
        public string ThreadBreakString;
        public int BreakFrame; //frame the last breakpoint was performed on
        public bool RoutineDirty;

        //check tree only vars
        public bool IsCheck;
        public List<VMPieMenuInteraction> ActionStrings;
        public Dictionary<int, short> MotiveAdChanges;

        public List<VMStackFrame> Stack;
        private bool ContinueExecution;

        public List<VMQueuedAction> Queue;
        public VMQueuedAction ActiveAction
        {
            get
            {
                return (ActiveQueueBlock>-1)?Queue[ActiveQueueBlock]:null;
            }
        }
        /// <summary>
        /// Set when a change to the queue or an item's priority is changed. Internal functions set this, but since you can modify the queue 
        /// from other classes MAKE SURE you set this when such a change is made. (eg. priority set from VMAvatar)
        /// </summary>
        public bool QueueDirty;

        public sbyte ActiveQueueBlock = -1; //cannot reorder items in the queue with index <= this.
        public short[] TempRegisters = new short[20];
        public int[] TempXL = new int[2];
        public VMPrimitiveExitCode LastStackExitCode = VMPrimitiveExitCode.GOTO_FALSE;

        public VMAsyncState BlockingState;
        public VMEODPluginThreadState EODConnection;
        public bool Interrupt;

        private ushort ActionUID;

        // Exception handling variables
        // Don't need to be serialized.
        public int DialogCooldown = 0;
        // the number of ticks that have executed so far this frame. If this exceeds the allowed max,
        // the thread resets, and a SimAntics Error pops up.
        public int TicksThisFrame = 0;
        // the maximum number of primitives a thread can execute in one frame. Tweak appropriately.

        // variables for internal scheduler
        public uint ScheduleIdleStart; // keep track of tick when we started idling for an object. must be synced!
        public uint ScheduleIdleEnd;

        public static readonly int MAX_LOOP_COUNT = 500000;

        /// <summary>
        /// SimsRedux 18.7: this thread was loaded busy with an object that isn't there and started over (see Load).
        /// Not saved: VM.Load reads it straight after loading the threads and clears it (ReduxFinishStartOver).
        /// </summary>
        public bool ReduxStartedOver;

        // SimsRedux: walks started by this thread in the current tick (see ReduxWalkShouldWait). Not saved -
        // it starts again from 0 on every tick, the same on every machine.
        private uint ReduxWalkTick = uint.MaxValue;
        private int ReduxWalksThisTick;
        public const int ReduxMaxWalksPerTick = 8;

        /// <summary>
        /// SimsRedux: a script that loops "find the thief, walk to him" (the police officer chasing a burglar)
        /// gets a walk that ends at once when he's already standing there, so the loop never gives up its turn.
        /// 12.4 let it go until 5000 primitives in the tick - that's over a thousand walk set-ups (slot search,
        /// route planning) every tick, which dropped the game to about 1 frame a second. Now, after a few walks
        /// in one tick, the next walk waits for the next tick.
        /// </summary>
        public bool ReduxWalkShouldWait(VMStackFrame frame)
        {
            var vm = Context?.VM;
            if (IsCheck || vm?.Scheduler == null) return false;
            var tick = vm.Scheduler.CurrentTickID;
            if (ReduxWalkTick != tick) { ReduxWalkTick = tick; ReduxWalksThisTick = 0; }
            return ++ReduxWalksThisTick > ReduxMaxWalksPerTick;
        }

        // SimsRedux: script calls the Redux hooks answered straight away in the current tick (see ReduxHookShouldWait)
        private uint ReduxHookTick = uint.MaxValue;
        private int ReduxHooksThisTick;
        public const int ReduxMaxHooksPerTick = 16;

        /// <summary>
        /// SimsRedux: the days-off hooks (Utils/ReduxWeek.cs) answer some of the car pool's scripts at once instead of
        /// running them ("warnings car pool", "due at work", "is this car for...?"). The game's own versions take time
        /// (they idle, show a message...), so a loop around one never spun; answered at once, a loop that keeps asking
        /// (the car pool's controller once someone in the family has a job, on their day off) ran until the "infinite
        /// loop" limit - half a million steps every tick, which froze the browser game after accepting a job. After a
        /// few answers in one tick, the next one waits for the next tick (the same on every machine).
        /// </summary>
        public bool ReduxHookShouldWait()
        {
            var vm = Context?.VM;
            if (IsCheck || vm?.Scheduler == null) return false;
            var tick = vm.Scheduler.CurrentTickID;
            if (ReduxHookTick != tick) { ReduxHookTick = tick; ReduxHooksThisTick = 0; }
            return ++ReduxHooksThisTick > ReduxMaxHooksPerTick;
        }

        /// <summary>
        /// SimsRedux: in Sims 1 a walk in a visitor's or NPC's own main loop (not in an interaction) stops -
        /// the walk primitive says false, "interrupted" - when a top-priority interaction has been pushed on
        /// him, so his loop gets back to idling and takes it. FreeSO only did that inside interactions, so
        /// the police officer kept chasing the burglar ("walk to him" again and again) and never started the
        /// "Arrest" the NPC Controller pushed on both of them: they stood by the road forever, the stolen
        /// money never came back and neither left. Family Sims are left as they were. Only the queue and
        /// person data are read (the same on every machine).
        /// </summary>
        public bool ReduxMainLoopInterrupted(bool actionTree)
        {
            if (actionTree || IsCheck || ActiveQueueBlock != -1) return false;
            var avatar = Entity as VMAvatar;
            if (avatar == null || Context?.VM == null || !Context.VM.TS1) return false;
            if (avatar.GetPersonData(VMPersonDataVariable.PersonType) == 0) return false; // (family Sims)
            if (avatar.GetPersonData(VMPersonDataVariable.NonInterruptable) != 0) return false;
            foreach (var item in Queue)
            {
                if (item.Mode == VMQueueMode.Normal && item.Priority >= (short)VMQueuePriority.Maximum
                    && item.Callee != null && !item.Callee.Dead) return true;
            }
            return false;
        }

        public static VMPrimitiveExitCode EvaluateCheck(VMContext context, VMEntity entity, VMStackFrame initFrame)
        {
            return EvaluateCheck(context, entity, initFrame, null, null);
        }

        public static VMPrimitiveExitCode EvaluateCheck(VMContext context, VMEntity entity, VMStackFrame initFrame, VMQueuedAction action)
        {
            return EvaluateCheck(context, entity, initFrame, action, null);
        }

        public static VMPrimitiveExitCode EvaluateCheck(VMContext context, VMEntity entity, VMStackFrame initFrame, VMQueuedAction action, List<VMPieMenuInteraction> actionStrings)
        {
            var temp = new VMThread(context, entity, 5);
            var forceClone = !context.VM.Scheduler.RunningNow;
            //temps should only persist on check trees running within the vm tick to avoid desyncs.
            if (entity.Thread != null)
            {
                temp.TempRegisters = forceClone?(short[])entity.Thread.TempRegisters.Clone() : entity.Thread.TempRegisters;
                temp.TempXL = forceClone ? (int[])entity.Thread.TempXL.Clone() : entity.Thread.TempXL;
            }
            temp.IsCheck = true;
            temp.ActionStrings = actionStrings; //generate and place action strings in here
            temp.Push(initFrame);
            if (action != null)
            {
                temp.Queue.Add(action); //this check runs an action. We may need its interaction number, etc.
                temp.ActiveQueueBlock = 0;
            }
            while (temp.Stack.Count > 0 && temp.DialogCooldown == 0 && !temp.Entity.Dead) //keep going till we're done! idling is for losers!
            {
                temp.Tick();
                temp.ThreadBreak = VMThreadBreakMode.Active; //cannot breakpoint in check trees
            }
            if (actionStrings != null && actionStrings.Count == 0)
            {
                //add an action string containing any modified ads
                actionStrings.Add(new VMPieMenuInteraction()
                {
                    MotiveAdChanges = temp.MotiveAdChanges
                });
            }
            if (context.VM.Aborting) return VMPrimitiveExitCode.ERROR;
            return (temp.DialogCooldown > 0) ? VMPrimitiveExitCode.RETURN_FALSE : temp.LastStackExitCode;
        }

        public bool RunInMyStack(VMRoutine routine, GameObject CodeOwner, short[] passVars, VMEntity stackObj)
        {
            //a little bit hacky. We may not need to do as serious a context switch as this.
            var OldStack = Stack;
            var OldQueue = Queue;
            var OldCheck = IsCheck;
            var OldQueueBlock = ActiveQueueBlock;

            VMStackFrame prevFrame = new VMStackFrame() { Caller = Entity, Callee = Entity };
            if (Stack.Count > 0)
            {
                prevFrame = Stack[Stack.Count - 1];
                Stack = new List<VMStackFrame>() { prevFrame };
            }
            else
            {
                Stack = new List<VMStackFrame>();
            }

            Queue = new List<VMQueuedAction>();
            if (Queue.Count > 0) Queue.Add(Queue[0]);
            IsCheck = true;

            ExecuteSubRoutine(prevFrame, routine, CodeOwner, new VMSubRoutineOperand(passVars));
            Stack.RemoveAt(0);
            if (Stack.Count == 0)
            {
                Stack = OldStack;
                Queue = OldQueue;
                return false;
                //bhav was invalid/empty
            }
            var frame = Stack[Stack.Count - 1];
            frame.Callee = stackObj;
            frame.StackObject = stackObj;

            try
            {
                while (Stack.Count > 0)
                {
                    NextInstruction();
                }
            }
            catch (Exception e)
            {
                if (e is ThreadAbortException) throw e;
                //we need to catch these so that the parent can be restored.
            }

            //copy child stack things to parent stack
            Stack = OldStack;
            Queue = OldQueue;
            IsCheck = OldCheck;
            ActiveQueueBlock = OldQueueBlock;

            return (LastStackExitCode == VMPrimitiveExitCode.RETURN_TRUE) ? true : false;
        }

        public VMThread(VMContext context, VMEntity entity, int stackSize)
        {
            this.Context = context;
            this.Entity = entity;

            this.Stack = new List<VMStackFrame>(stackSize);
            this.Queue = new List<VMQueuedAction>();
        }

        /// <summary>
        /// Checks to see if it can push an interaction, and pushes it.
        /// Returns true on success, false on failure.
        /// </summary>
        public bool AttemptPush()
        {
            int priorityCompare = ((VMAvatar)Entity).GetPersonData(VMPersonDataVariable.Priority);
            if (priorityCompare <= 2) priorityCompare -= 1; //autonomous interactions fall through to other ones
            QueueDirty = true;
            while (Queue.Count > ActiveQueueBlock+1)
            {
                var item = Queue[ActiveQueueBlock+1];
                if (item.Priority <= priorityCompare) return false;
                if (item.NotifyIdle) Entity.SetFlag(VMEntityFlags.InteractionCanceled, item.NotifyIdle);
                if (IsCheck || ((item.Mode != VMQueueMode.ParentIdle || !Entity.GetFlag(VMEntityFlags.InteractionCanceled)) && CheckAction(item) != null))
                {
                    Entity.SetFlag(VMEntityFlags.InteractionCanceled, false);
                    if (!ExecuteAction(item)) return false;
                    ActiveQueueBlock++;
                    return true;
                }
                else if (!IsCheck && ActiveQueueBlock > -1 && Context.VM.TS1 && item.Mode == VMQueueMode.Normal
                    && item.Priority >= (short)VMQueuePriority.UserDriven)
                {
                    // SimsRedux: a player's order looked at from INSIDE a running interaction (Sims 1 lets an
                    // interaction's "idle, allow push" run the next order on top of it) whose check says no right
                    // now stays in the queue, in its place, for when it really comes up - after the running
                    // interaction has finished (it's already been told to: a higher-priority order is waiting).
                    // It was dropped here: a Sim getting out of bed in the morning (still lying down / asleep to
                    // the checks) lost every order the player gave while the animation played. When it comes up
                    // from the Sim's own loop (no interaction running) a check that says no still drops it, below.
                    return false;
                }
                else
                {
                    ReduxQueueNote(this, item, (item.Mode == VMQueueMode.ParentIdle && Entity.GetFlag(VMEntityFlags.InteractionCanceled))
                        ? "it was waiting on an action that was cancelled" : "its check said no when it came to run (it was fine when picked)");
                    Queue.RemoveAt(ActiveQueueBlock + 1); //keep going.
                }
            }
            return false;
        }

        private static string ReduxLastQueueNote; // (the last drop noted, and how many times in a row since)
        private static int ReduxQueueNoteRepeats;
        /// <summary>
        /// SimsRedux: write dropped orders to queue_debug.log too (off unless turned on - set this from
        /// a debug option, or SIMSREDUX_QUEUE_LOG=1). At most ReduxQueueLogMax lines a session, in a fresh file each session.
        /// </summary>
        public static bool ReduxQueueLog = Environment.GetEnvironmentVariable("SIMSREDUX_QUEUE_LOG") == "1";
        public const int ReduxQueueLogMax = 500;
        private static int ReduxQueueLogLines;

        /// <summary>
        /// SimsRedux: a player's order that was dropped before it ran (it shows in the queue for a moment,
        /// then goes) - noted in the console and queue_debug.log, so we can see why.
        /// </summary>
        public static void ReduxQueueNote(VMThread t, VMQueuedAction a, string why)
        {
            try
            {
                if (t == null || a == null || t.IsCheck || !(t.Entity is VMAvatar) || a.Priority < (short)VMQueuePriority.UserDriven) return;
                var what = t.Entity.ToString() + ": '" + (a.Name ?? "?") + "' on " + (a.Callee?.ToString() ?? "?") + " was dropped - " + why;
                // the same drop over and over (a script pushing an action that keeps failing): noted a few times, not
                // every time - each note is a write to the log file
                if (what == ReduxLastQueueNote) { if (++ReduxQueueNoteRepeats > 3) return; }
                else { ReduxLastQueueNote = what; ReduxQueueNoteRepeats = 0; }
                var line = DateTime.Now.ToString("HH:mm:ss") + " " + what;
                Console.WriteLine("[SimsRedux] " + line);
                if (!ReduxQueueLog || ReduxQueueLogLines >= ReduxQueueLogMax) return;
                var path = System.IO.Path.Combine(FSO.Common.FSOEnvironment.UserDir, "queue_debug.log");
                if (ReduxQueueLogLines++ == 0) FSO.Common.IO.FSOFile.WriteAllText(path, line + "\r\n"); // (a fresh file each session - it never grows past the cap)
                else FSO.Common.IO.FSOFile.AppendAllText(path, line + "\r\n");
            }
            catch { }
        }

        public void TryRunImmediately()
        {
            //check if we have a run immediately interaction, and inject it if we do.#
            while (true)
            {
                var ind = Queue.FindIndex(x => (x.Flags & TTABFlags.RunImmediately) > 0);
                if ((ind > ActiveQueueBlock || (!(Stack.Count > 0 && Stack.LastOrDefault().ActionTree) && ind > -1)))
                {
                    //not already running (if no action we are still not running if we're queue[0], so go for it)
                    //swap current item with ind.
                    var temp = Queue[ind];
                    Queue.RemoveAt(ind);
                    if (CheckAction(temp) != null)
                    {
                        Queue.Insert(ActiveQueueBlock+1, temp);
                        var frame = temp.ToStackFrame(Entity);
                        frame.SpecialResult = VMSpecialResult.Interaction;
                        Push(frame);
                        ActiveQueueBlock++; //both the run immediately interaction and the active interaction must be protected.
                        break;
                    }
                } else
                {
                    break;
                }
            }
        }

        private void EndCurrentInteraction()
        {
            QueueDirty = true;
            var interaction = Queue[ActiveQueueBlock];
            //clear "interaction cancelled" since we are leaving the interaction
            if (interaction.Mode != VMQueueMode.ParentIdle) Entity.SetFlag(VMEntityFlags.InteractionCanceled, false);
            if (interaction.Callback != null) interaction.Callback.Run(Entity);
            if (Queue.Count > 0) Queue.RemoveAt(ActiveQueueBlock);
            if (Entity is VMAvatar && !IsCheck && ActiveQueueBlock == 0) ReduxStopLeftoverSounds();
            if (Entity is VMAvatar && !IsCheck) FSO.SimAntics.Utils.ReduxAway.InteractionEnded(Entity, interaction); // (19.6: At Work/School ended off the lot)
            if (Entity is VMAvatar && !IsCheck && ActiveQueueBlock == 0)
            {
                //some things are reset when an interaction ends
                //motive deltas reset between interactions
                ((VMAvatar)Entity).SetPersonData(VMPersonDataVariable.NonInterruptable, 0); //verified in ts1
                ((VMAvatar)Entity).ClearMotiveChanges();
            }
            ContinueExecution = true; //continue where the Allow Push idle left off
            ActiveQueueBlock--;
            //update priority with the priority of the interaction we are going back to (or 0)
            ((VMAvatar)Entity).SetPersonData(VMPersonDataVariable.Priority, (ActiveQueueBlock > -1) ? Queue[ActiveQueueBlock].Priority : (short)0);
            EvaluateQueuePriorities();
        }

        /// <summary>
        /// SimsRedux: when a Sim's interaction ends, the repeating sounds it started on that Sim stop
        /// with it (snoring after a nap, a chopping or munching sound that kept looping) - some
        /// interactions end in a way that skips their own "stop sounds" step. One-off sounds finish
        /// by themselves and are left alone.
        /// </summary>
        public void ReduxStopLeftoverSounds()
        {
            var threads = Entity.SoundThreads;
            if (threads == null || threads.Count == 0) return;
            for (int i = 0; i < threads.Count; i++)
            {
                var entry = threads[i];
                var hit = entry?.Sound as FSO.HIT.HITThread;
                if (entry == null || entry.Sound == null) continue;
                bool repeating = entry.Loop || (hit != null && (hit.Loop || !hit.SimpleMode));
                if (!repeating) continue;
                entry.Sound.RemoveOwner(Entity.ObjectID);
                threads.RemoveAt(i--);
            }
            if (threads.Count == 0) Context.VM.SoundEntities.Remove(Entity);
        }

        public void AbortCurrentInteraction()
        {
            //go all the way back to the stack frame that Allow Push'd us.
            var returnTo = Stack.FindLast(x => x.SpecialResult == VMSpecialResult.Interaction);
            if (returnTo != null)
            {
                var ind = Stack.IndexOf(returnTo);
                while (Stack.Count > ind)
                {
                    Stack.RemoveAt(Stack.Count-1);
                }
                EndCurrentInteraction();
            }
        }

        public void Tick(){
#if IDE_COMPAT
            if (ThreadBreak == VMThreadBreakMode.Pause) return;
            else if (ThreadBreak == VMThreadBreakMode.Reset)
            {
                Entity.Reset(Context);
                ThreadBreak = VMThreadBreakMode.Active;
            }
            else if (ThreadBreak == VMThreadBreakMode.Immediate)
            {
                Breakpoint(Stack.LastOrDefault(), "Paused."); return;
            }
#endif

            if (BlockingState != null) BlockingState.WaitTime++;
            if (DialogCooldown > 0) DialogCooldown--;
#if !THROW_SIMANTICS
            try
            {
#endif
                if (!Entity.Dead)
                {
                    if (QueueDirty)
                    {
                        EvaluateQueuePriorities();
                        TryRunImmediately();
                        QueueDirty = false;
                    }
                    if (Stack.Count == 0)
                    {
                        if (IsCheck) return; //running out of execution means check trees have ended.
                        Entity.ExecuteEntryPoint(1, Context, false);
                        if (Stack.Count == 0) return;
                    }
                    if ((!Stack.LastOrDefault().ActionTree) || (Queue[0].Callee != null && !Queue[0].Callee.Dead)) //main or our target is not dead (SimsRedux: a missing target counts as dead)
                    {
#if IDE_COMPAT
                        if (ThreadBreak == VMThreadBreakMode.ReturnTrue)
                        {
                            var bf = Stack[BreakFrame];
                            HandleResult(bf, bf.GetCurrentInstruction(), VMPrimitiveExitCode.RETURN_TRUE);
                            Breakpoint(Stack.LastOrDefault(), "Returned True.");
                            return;
                        }
                        if (ThreadBreak == VMThreadBreakMode.ReturnFalse)
                        {
                            var bf = Stack[BreakFrame];
                            HandleResult(bf, bf.GetCurrentInstruction(), VMPrimitiveExitCode.RETURN_TRUE);
                            Breakpoint(Stack.LastOrDefault(), "Returned False.");
                            return;
                        }
#endif
                        ContinueExecution = true;
                        while (ContinueExecution)
                        {
                            if (TicksThisFrame++ > MAX_LOOP_COUNT)
                            {
                                TicksThisFrame = 0;
                                throw new Exception("Thread entered infinite loop! ( >" + MAX_LOOP_COUNT + " primitives)");
                            }
                            ContinueExecution = false;
                            NextInstruction();
                        }
                    }
                    else //interaction owner is dead, rip
                    {
                        Entity.Reset(Context);
                    }
                }

#if !THROW_SIMANTICS
            }
            catch (Exception e)
            {
#if IDE_COMPAT
                if (!IsCheck && VM.SignalBreaks)
                {
                    Breakpoint(Stack.LastOrDefault(), "!"+e.Message+" "+StackTraceSimplify(e.StackTrace.Split('\n').FirstOrDefault(x => x.Contains(".cs")) ?? ""));
                    ContinueExecution = false;
                    return;
                }
#endif

                if (e is ThreadAbortException) throw e;
                // SimsRedux soak test: told about every exception (before the dialog cooldown, so none go uncounted).
                // Null outside the soak test; it only looks, never changes anything.
                // (once per exception: if the same exception object reaches a handler here again, it isn't told twice)
                var onException = ReduxOnException;
                if (onException != null && !ReferenceEquals(e, ReduxLastReported))
                {
                    ReduxLastReported = e;
                    try { onException(this, Entity, e, (Stack.Count > 0) ? Stack[Stack.Count - 1] : null); } catch { }
                }
                // full details for the console - once per error dialog: a script failing every tick wrote its whole
                // stack trace every tick (slow, especially in the browser), while the dialog was shown once (SimsRedux)
                if (DialogCooldown == 0) Console.WriteLine("[SimsRedux] SimAntics exception in " + Entity?.ToString() + ": " + e);
                if (Stack.Count == 0) return;
                var context = Stack[Stack.Count - 1];
                bool Delete = ((Entity is VMGameObject) && (DialogCooldown > 30 * 20 - 10));
                if (DialogCooldown == 0)
                {

                    var simExcept = new VMSimanticsException(e.Message + StackTraceSimplify((e.StackTrace ?? "").Split('\n').FirstOrDefault(x => x.Contains(".cs")) ?? ""), context);
                    string exceptionStr = "A SimAntics Exception has occurred, and has been suppressed: \r\n\r\n" + simExcept.ToString() + "\r\n\r\nThe object will be reset. Please report this!";
                    // SimsRedux web: show where in the code it happened, so one screenshot is enough
                    if (OperatingSystem.IsBrowser()) exceptionStr += "\r\n\r\n" + string.Join("\r\n", (e.StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim().Replace("at FSO.", "").Replace("at Simitone.", "")));
                    exceptionStr += DescribeFailure(context); // SimsRedux: which values the failing line was reading
                    VMDialogInfo info = new VMDialogInfo
                    {
                        Caller = null,
                        Icon = context.Callee,
                        Operand = new VMDialogOperand { },
                        Message = exceptionStr,
                        Title = "SimAntics Exception!"
                    };
                    Context.VM.SignalDialog(info);
                    DialogCooldown = 30 * 20;
                }

                if (!IsCheck)
                {
                    // SimsRedux: the object acted on can be gone (null) - e.g. a Go Here whose marker wasn't in the
                    // loaded save. Resetting it threw out of the whole VM tick ("couldn't update TS1GameScreen",
                    // then "Tick wrong! Got #, Missed #"); reset whoever is still there instead.
                    context.Callee?.Reset(context.VM.Context);
                    if (context.Caller != null) context.Caller.Reset(context.VM.Context);
                    else Entity?.Reset(context.VM.Context);
                    if (Delete) Entity.Delete(true, context.VM.Context);
                } else
                {
                    Stack.Clear();
                }
            }
#endif
        }

        /// <summary>
        /// SimsRedux soak test: called for every SimAntics exception a thread catches (thread, its entity, the
        /// exception, the frame that was running or null). Null unless the soak test is running.
        /// </summary>
        public static Action<VMThread, VMEntity, Exception, VMStackFrame> ReduxOnException;
        [ThreadStatic] private static Exception ReduxLastReported;

        /// <summary>SimsRedux soak test: which values the failing line was reading (as the exception dialog shows them).</summary>
        public string ReduxDescribeFailure(VMStackFrame context)
        {
            return (context == null) ? "" : DescribeFailure(context);
        }

        /// <summary>
        /// SimsRedux: the browser's fast build can't say where in the C# code an error happened, so
        /// for an expression line this works out which side failed and what it was reading
        /// (e.g. "left: StackObjectAttributes[3] - FAILED: object not found").
        /// </summary>
        private string DescribeFailure(VMStackFrame context)
        {
            try
            {
                var instr = context.GetCurrentInstruction();
                if (instr?.Operand is VMExpressionOperand op)
                {
                    string Side(VMVariableScope scope, short data)
                    {
                        try { return scope + "[" + data + "] = " + VMMemory.GetVariable(context, scope, data); }
                        catch (Exception ex) { return scope + "[" + data + "] FAILED (" + ex.GetType().Name + ")"; }
                    }
                    var extra = "";
                    if (op.LhsOwner == VMVariableScope.StackObjectAttributes || op.RhsOwner == VMVariableScope.StackObjectAttributes
                        || op.LhsOwner.ToString().StartsWith("StackObject") || op.RhsOwner.ToString().StartsWith("StackObject"))
                        extra = "\r\nstack object: " + (context.StackObject?.ToString() ?? "NONE (id " + context.StackObjectID + ")");
                    if (op.LhsOwner == VMVariableScope.JobData || op.RhsOwner == VMVariableScope.JobData)
                        extra += "\r\njob " + TempRegisters[0] + " level " + TempRegisters[1];
                    return "\r\n\r\nline: " + op.LhsOwner + " " + op.Operator + " " + op.RhsOwner
                        + "\r\nleft: " + Side(op.LhsOwner, op.LhsData)
                        + "\r\nright: " + Side(op.RhsOwner, op.RhsData) + extra;
                }
                return instr == null ? "" : "\r\n\r\nprimitive " + instr.Opcode + " (" + instr.Operand?.GetType().Name + ")";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private string StackTraceSimplify(string st)
        {
            var lastSlash = st.LastIndexOf('\\');
            if (lastSlash == -1) lastSlash = st.LastIndexOf('/');
            return (lastSlash == -1) ? st : st.Substring(lastSlash+1);
        }

        private void EvaluateQueuePriorities()
        {
            EnsureDirectControlAction();
            if (ActiveQueueBlock == -1 || ActiveQueueBlock >= Queue.Count) return;
            var active = Queue[ActiveQueueBlock];
            int CurrentPriority = (int)((VMAvatar)Entity).GetPersonData(VMPersonDataVariable.Priority); 
            if (CurrentPriority == (int)VMQueuePriority.Autonomous) CurrentPriority -= 1; // allow other auto actions to interrupt us

            // HACK: TS1 pushes a "Cancel Interaction" action onto the tree to interrupt itself, as well as setting current interaction to prio 0.
            // We're simulating that by notifiying idle if priority hits 0. (implied cancel interaction queued)
            // Make sure this interaction is not *meant* to be priority 0, like tso's idle.
            active.NotifyIdle = active.Priority != 0 && CurrentPriority == 0; 
            for (int i = ActiveQueueBlock + 1; i < Queue.Count; i++)
            {
                if (Queue[i].Callee == null || Queue[i].Callee.Dead)
                {
                    Queue.RemoveAt(i--); //remove interactions to dead objects (not within active queue block)
                    continue;
                }
                if ((int)Queue[i].Priority > CurrentPriority)// && mode != VMQueueMode.ParentIdle)
                {
                    active.NotifyIdle = true;
                    Entity.SetFlag(VMEntityFlags.InteractionCanceled, true);
                }
            }
        }

        private void NextInstruction()
        {
            /** Next instruction **/
            var currentFrame = Stack.LastOrDefault();
            if (currentFrame == null) return;

            if (currentFrame is VMRoutingFrame) HandleResult(currentFrame, null, ((VMRoutingFrame)currentFrame).Tick());
            else if (currentFrame is VMDirectControlFrame) HandleResult(currentFrame, null, ((VMDirectControlFrame)currentFrame).Tick());
            else
            {
                VMInstruction instruction;
                VMPrimitiveExitCode result = currentFrame.Routine.Execute(currentFrame, out instruction);
                HandleResult(currentFrame, instruction, result);
            }
        }

        public VMRoutingFrame PushNewRoutingFrame(VMStackFrame frame, bool failureTrees)
        {
            var childFrame = new VMRoutingFrame
            {
                Routine = frame.Routine,
                Caller = frame.Caller,
                Callee = frame.Callee,
                CodeOwner = frame.CodeOwner,
                StackObject = frame.StackObject,
                Thread = this,
                CallFailureTrees = failureTrees,
                ActionTree = frame.ActionTree
            };

            Stack.Add(childFrame);
            return childFrame;
        }

        public VMDirectControlFrame PushNewDirectControlFrame(VMStackFrame frame)
        {
            var childFrame = new VMDirectControlFrame
            {
                Routine = frame.Routine,
                Caller = frame.Caller,
                Callee = frame.Callee,
                CodeOwner = frame.CodeOwner,
                StackObject = frame.StackObject,
                Thread = this,
                ActionTree = frame.ActionTree
            };

            Stack.Add(childFrame);
            return childFrame;
        }

        public void ExecuteSubRoutine(VMStackFrame frame, VMRoutine routine, GameObject codeOwner, VMSubRoutineOperand args)
        {
            if (routine == null)
            {
                Pop(VMPrimitiveExitCode.ERROR);
                return;
            }

            var childFrame = new VMStackFrame
            {
                Routine = routine,
                Caller = frame.Caller,
                Callee = frame.Callee,
                CodeOwner = codeOwner,
                StackObject = frame.StackObject,
                _StackObjectID = frame.StackObjectID, //pass this without doing a lookup
                ActionTree = frame.ActionTree
            };
            childFrame.Args = new short[(routine.Arguments > 4) ? routine.Arguments : 4];
            for (var i = 0; i < childFrame.Args.Length; i++)
            {
                short argValue = (i > 3) ? (short)-1 : args.Arguments[i];
                if (argValue == -1 && args.UseTemp0)
                {
                    argValue = TempRegisters[i];
                }
                childFrame.Args[i] = argValue;
            }
            Push(childFrame);
        }

        public VMPrimitiveExitCode ExecuteSubRoutine(VMStackFrame frame, ushort opcode, VMSubRoutineOperand operand)
        {
            VMRoutine bhav = null;

            GameObject CodeOwner;
            if (opcode >= 8192)
            {
                // Semi-Global sub-routine call
                bhav = (VMRoutine)frame.ScopeResource.SemiGlobal.GetRoutine(opcode);
            }
            else if (opcode >= 4096)
            {
                // Private sub-routine call
                bhav = (VMRoutine)frame.ScopeResource.GetRoutine(opcode);
            }
            else
            {
                // Global sub-routine call
                //CodeOwner = frame.Global.Resource;
                bhav = (VMRoutine)frame.Global.Resource.GetRoutine(opcode);
            }

            CodeOwner = frame.CodeOwner;
            
            ExecuteSubRoutine(frame, bhav, CodeOwner, operand);
#if IDE_COMPAT
            if (Stack.LastOrDefault().GetCurrentInstruction().Breakpoint || ThreadBreak == VMThreadBreakMode.StepIn)
            {
                Breakpoint(frame, "Stepped in.");
                ContinueExecution = false;
            }
            else
#endif
            {
                ContinueExecution = true;
            }

            return VMPrimitiveExitCode.CONTINUE;
        }

        private void ExecuteInstruction(VMStackFrame frame)
        {
            var instruction = frame.GetCurrentInstruction();
            var opcode = instruction.Opcode;

            if (opcode >= 256)
            {
                ExecuteSubRoutine(frame, opcode, (VMSubRoutineOperand)instruction.Operand);
                return;
            }


            // SimsRedux: a walk loop that ends at once waits for the next tick (see ReduxWalkShouldWait)
            if ((opcode == 27 || opcode == 45) && ReduxWalkShouldWait(frame))
            {
                HandleResult(frame, instruction, VMPrimitiveExitCode.CONTINUE_NEXT_TICK);
                return;
            }
            // SimsRedux: a visitor/NPC walking in his own main loop stops for a top-priority interaction (see ReduxMainLoopInterrupted)
            if ((opcode == 27 || opcode == 45) && ReduxMainLoopInterrupted(frame.ActionTree))
            {
                HandleResult(frame, instruction, VMPrimitiveExitCode.GOTO_FALSE);
                return;
            }

            var primitive = VMContext.Primitives[opcode];
            if (primitive == null)
            {
                HandleResult(frame, instruction, VMPrimitiveExitCode.GOTO_TRUE);
                return;
            }

            VMPrimitiveHandler handler = primitive.GetHandler();
            var result = handler.Execute(frame, instruction.Operand);
            HandleResult(frame, instruction, result);
        }

        // SimsRedux: debug trace of one Sim's script steps (used to find scripts that loop forever)
        public static VMEntity TraceEntity;
        public static List<string> TraceLog = new List<string>();
        public static HashSet<VMRoutine> TraceRoutines = new HashSet<VMRoutine>();
        /// <summary>SimsRedux: also write each step's operand (its settings) into the trace.</summary>
        public static bool TraceOperands;

        public static string DescribeOperand(object operand)
        {
            if (operand == null) return "";
            try
            {
                var parts = new List<string>();
                foreach (var f in operand.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    parts.Add(f.Name + "=" + f.GetValue(operand));
                foreach (var p in operand.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    var v = p.GetValue(operand);
                    if (v is System.Collections.IEnumerable && !(v is string)) continue;
                    parts.Add(p.Name + "=" + v);
                }
                return operand.GetType().Name + "{" + string.Join(" ", parts) + "}";
            }
            catch { return operand.GetType().Name; }
        }

        private void HandleResult(VMStackFrame frame, VMInstruction instruction, VMPrimitiveExitCode result)
        {
            if (TraceEntity != null && Entity == TraceEntity && TraceLog.Count < 4000 && frame?.Routine != null)
            {
                try
                {
                    var t = TempRegisters;
                    TraceLog.Add((frame.Routine.Chunk?.ChunkLabel ?? "?") + "(" + frame.Routine.ID + ") line " + frame.InstructionPointer
                        + " op " + (instruction?.Opcode.ToString() ?? "-") + " -> " + result
                        + "  temps " + t[0] + "," + t[1] + "," + t[2] + "," + t[3]
                        + "  locals " + string.Join(",", frame.Locals ?? new short[0])
                        + "  args " + string.Join(",", frame.Args ?? new short[0])
                        + (TraceOperands ? "  " + DescribeOperand(instruction?.Operand) : ""));
                    TraceRoutines.Add(frame.Routine);
                }
                catch { }
            }
            switch (result)
            {
                // Don't advance the instruction pointer, this primitive isnt finished yet
                case VMPrimitiveExitCode.CONTINUE_NEXT_TICK:
                    ScheduleIdleStart = Context.VM.Scheduler.CurrentTickID;
                    Context.VM.Scheduler.ScheduleTickIn(Entity, 1);
                    ContinueExecution = false;
                    break;
                case VMPrimitiveExitCode.CONTINUE_FUTURE_TICK:
                    ContinueExecution = false;
                    break;
                case VMPrimitiveExitCode.ERROR:
                    ContinueExecution = false;
                    Pop(result);
                    break;
                case VMPrimitiveExitCode.RETURN_TRUE:
                case VMPrimitiveExitCode.RETURN_FALSE:
                    /** pop stack and return false **/
                    Pop(result);
                    break;
                case VMPrimitiveExitCode.GOTO_TRUE:
                    MoveToInstruction(frame, instruction.TruePointer, true);
                    break;
                case VMPrimitiveExitCode.GOTO_FALSE:
                    MoveToInstruction(frame, instruction.FalsePointer, true);
                    break;
                case VMPrimitiveExitCode.GOTO_TRUE_NEXT_TICK:
                    MoveToInstruction(frame, instruction.TruePointer, true);
                    if (ContinueExecution)
                    {
                        ScheduleIdleStart = Context.VM.Scheduler.CurrentTickID;
                        Context.VM.Scheduler.ScheduleTickIn(Entity, 1);
                        ContinueExecution = false;
                    }
                    break;
                case VMPrimitiveExitCode.GOTO_FALSE_NEXT_TICK:
                    MoveToInstruction(frame, instruction.FalsePointer, true);
                    if (ContinueExecution)
                    {
                        ScheduleIdleStart = Context.VM.Scheduler.CurrentTickID;
                        Context.VM.Scheduler.ScheduleTickIn(Entity, 1);
                        ContinueExecution = false;
                    }
                    break;
                case VMPrimitiveExitCode.CONTINUE:
                    ContinueExecution = true;
                    break;
                case VMPrimitiveExitCode.INTERRUPT:
                    Stack.Clear();
                    QueueDirty = true;
                    if (Queue.Count > 0) Queue.RemoveAt(0);
                    if (Entity is VMAvatar && !IsCheck) ReduxStopLeftoverSounds();
                    LastStackExitCode = result;
                    break;
            }
        }

        private void MoveToInstruction(VMStackFrame frame, byte instruction, bool continueExecution)
        {
            if (frame is VMRoutingFrame)
            {
                //TODO: Handle returning false into the pathfinder (indicates failure)
                return;
            }

            ContinueExecution = continueExecution;
            switch (instruction)
            {
                case 255:
                    Pop(VMPrimitiveExitCode.RETURN_FALSE);
                    break;
                case 254:
                    Pop(VMPrimitiveExitCode.RETURN_TRUE); break;
                case 253:
                    //attempt to continue along only path available. 
                    if (frame.GetCurrentInstruction().TruePointer != 253)
                    {
                        MoveToInstruction(frame, frame.GetCurrentInstruction().TruePointer, continueExecution); return;
                    }
                    else if (frame.GetCurrentInstruction().FalsePointer != 253)
                    {
                        MoveToInstruction(frame, frame.GetCurrentInstruction().FalsePointer, continueExecution); return;
                    }
                    Pop(VMPrimitiveExitCode.ERROR); break;
                default:
                    frame.InstructionPointer = instruction;
                    if (frame.GetCurrentInstruction().Breakpoint ||
                        (ThreadBreak != VMThreadBreakMode.Active && (
                            ThreadBreak == VMThreadBreakMode.StepIn ||
                            (ThreadBreak == VMThreadBreakMode.StepOver && Stack.Count - 1 <= BreakFrame) ||
                            (ThreadBreak == VMThreadBreakMode.StepOut && Stack.Count <= BreakFrame)
                        )))
                    {
                        string result = "Unknown Break";
                        switch (ThreadBreak) {
                            case VMThreadBreakMode.StepIn:
                                result = "Stepped In."; break;
                            case VMThreadBreakMode.StepOut:
                                result = "Stepped Out."; break;
                            case VMThreadBreakMode.StepOver:
                                result = "Stepped Over."; break;
                        }
                        Breakpoint(frame, result);
                    }
                    break;
            }

            ContinueExecution = (ThreadBreak != VMThreadBreakMode.Pause) && ContinueExecution;
        }

        public void Breakpoint(VMStackFrame frame, string description)
        {
            if (IsCheck) return; //can't breakpoint in check trees.
            ThreadBreak = VMThreadBreakMode.Pause;
            ThreadBreakString = description;
            BreakFrame = Stack.IndexOf(frame);
            Context.VM.BreakpointHit(Entity);
        }

        public void Pop(VMPrimitiveExitCode result)
        {
            var discardResult = Stack[Stack.Count - 1].SpecialResult;
            var contextSwitch = (Stack.Count > 1) && Stack.LastOrDefault().ActionTree != Stack[Stack.Count - 2].ActionTree;
            Stack.RemoveAt(Stack.Count - 1);
            LastStackExitCode = result;

            if (discardResult == VMSpecialResult.Interaction) //interaction switching back to main (it cannot be the other way...)
            {
                var interaction = Queue[ActiveQueueBlock];
                EndCurrentInteraction();
                result = (!interaction.Flags.HasFlag(TTABFlags.RunImmediately)) ? VMPrimitiveExitCode.CONTINUE_NEXT_TICK : VMPrimitiveExitCode.CONTINUE;
            }
            else if (discardResult == VMSpecialResult.Retry)
            {
                result = VMPrimitiveExitCode.CONTINUE;
            }

            if (Stack.Count > 0)
            {
                if (result == VMPrimitiveExitCode.RETURN_TRUE)
                    result = VMPrimitiveExitCode.GOTO_TRUE;
                else if (result == VMPrimitiveExitCode.RETURN_FALSE)
                    result = VMPrimitiveExitCode.GOTO_FALSE;
                var currentFrame = Stack.Last();
                HandleResult(currentFrame, currentFrame.GetCurrentInstruction(), result);
            }
            else // :(
            {
                ContinueExecution = false;
            }
        }

        public bool Push(VMStackFrame frame)
        {
            if (frame.Routine.Instructions.Length == 0) return false; //some bhavs are empty... do not execute these.
            Stack.Add(frame);

            /** Initialize the locals **/
            var numLocals = Math.Max(frame.Routine.Locals, frame.Routine.Arguments);
            frame.Locals = new short[numLocals];
            frame.Thread = this;

            frame.InstructionPointer = 0;
            return true;
        }

        /// <summary>
        /// Add an item to the action queue
        /// </summary>
        /// <param name="invocation"></param>
        public void EnqueueAction(VMQueuedAction invocation)
        {
            invocation.UID = ActionUID++;
            QueueDirty = true;
            if (!IsCheck && (invocation.Flags & TTABFlags.RunImmediately) > 0)
            {
                // shove this action in the queue to try run it next tick.
                // interaction can be run normally if we actually hit it using allow push. (unlikely)
                // otherwise next tick will detect the "run immediately" interaction's presence. 
                // and it will be pushed to the stack immediately.
                // TODO: check if any interactions of this kind clobber the temps.
                // this doesnt ""run immediately"", but is good enough.

                invocation.Mode = VMQueueMode.Idle; //hide
                //invocation.Priority = short.MinValue;
                this.Queue.Add(invocation);
                return;
            }
            var leapfrog = Context.VM.TS1 ? (TTABFlags)0: TTABFlags.Leapfrog;

            if (Queue.Count == 0) //if empty, just queue right at the front 
                this.Queue.Add(invocation);
            else if ((invocation.Flags & TTABFlags.FSOPushHead) > 0)
                //place right after active interaction, ignoring all priorities.
                this.Queue.Insert(ActiveQueueBlock + 1, invocation);
            else if (Context.VM.TS1 && (invocation.Flags & ReduxContinuation) > 0 && ReduxContinuationFits(invocation))
            {
                // SimsRedux 15.4: the rest of the running interaction (see ReduxContinuation) runs next, ahead of
                // the player's other queued orders (after any earlier part of it already waiting there, in order).
                int at = ActiveQueueBlock + 1;
                while (at < Queue.Count && (Queue[at].Flags & ReduxContinuation) > 0) at++;
                this.Queue.Insert(at, invocation);
            }
            else if ((invocation.Flags & (TTABFlags.FSOPushTail | leapfrog)) > 0 && invocation.Mode != VMQueueMode.ParentExit)
            {
                //this one's weird. start at the left til there's a lower priority. (eg. parent exit or idle)
                bool hitParentEnd = (invocation.Mode != VMQueueMode.ParentIdle);
                for (int i = ActiveQueueBlock+1; i < Queue.Count; i++)
                {
                    if (Queue[i].Priority < invocation.Priority) //we have higher priority than this item
                    {
                        this.Queue.Insert(i, invocation); //insert before this. will come before parent exit and pals.
                        EvaluateQueuePriorities();
                        return;
                    }
                }
                this.Queue.Add(invocation); //right at the end! (somehow)
            }
            else //we've got an even harder job! find a place for this interaction based on its priority
            {
                bool hitParentEnd = (invocation.Mode != VMQueueMode.ParentIdle);
                for (int i = Queue.Count - 1; i > ActiveQueueBlock; i--)
                {
                    if (hitParentEnd && (invocation.Priority <= Queue[i].Priority || Queue[i].Mode == VMQueueMode.ParentExit)) //skip until we find a parent exit or something with the same or higher priority.
                    {
                        this.Queue.Insert(i + 1, invocation);
                        if (Context.VM.TS1 && invocation.Priority <= Queue[i].Priority)
                        {
                            // queue skip all items with a lower priority (after this interaction)
                            // i've verified this happens in ts1, but I don't know if it does in TSO so i've locked it for now.
                            i += 2;
                            // SimsRedux: cancel each of them once. (This looped "while there are items after it" - but
                            // cancelling an item that has started doesn't take it out of the queue, so it cancelled the same
                            // one forever and froze the game: found by the headless test, the pedestrian portal pushing
                            // "leave" onto a busy neighbour.)
                            // Only items that can really be skipped (not started, normal, not must-run - the ones
                            // CancelAction takes out of the queue): cancelling any other kind goes down CancelAction's
                            // "cancel what's running" branch, which flags the Sim's CURRENT interaction as cancelled and
                            // drops its priority to 0 - so queueing an order cut short whatever the Sim was in the middle
                            // of (getting out of bed in the morning) instead of skipping the item behind it.
                            var later = new List<ushort>();
                            for (int j = i; j < this.Queue.Count; j++)
                            {
                                var q = this.Queue[j];
                                if (!q.Flags.HasFlag(TTABFlags.MustRun)
                                    && (q.Mode == VMQueueMode.Normal || q.Flags.HasFlag(TTABFlags.FSODirectControl)))
                                    later.Add(q.UID);
                            }
                            foreach (var uid in later) CancelAction(uid);
                        }
                        EvaluateQueuePriorities();
                        return;
                    }
                    if (Queue[i].Mode == VMQueueMode.ParentExit) hitParentEnd = true;
                }
                this.Queue.Insert(ActiveQueueBlock + 1, invocation); //this is more important than all other queued items that are not running, so stick this to run next.
            }
            EvaluateQueuePriorities();
        }

        /// <summary>
        /// SimsRedux 15.4: marks an interaction a Sim's running interaction pushed onto that same Sim at its own
        /// ("inherited") priority - the rest of that interaction (VMPushInteraction). A Sims 1 social is two steps: the
        /// pie-menu action ("Joke") makes the conversation, pushes "Joke - Person B" on the other Sim and "Joke - Person
        /// A" on this one, and ends. Placed by priority, "Person A" went BEHIND every order already queued at the same
        /// priority - so with Talk, Joke and Hug queued the Sim walked off to start the Joke while the other Sim stood
        /// waiting for the Talk, which timed out, and each conversation in the queue broke the next ("cancelled").
        /// Saved with the action's flags, so a loaded or joined game places it the same way.
        /// </summary>
        public const TTABFlags ReduxContinuation = (TTABFlags)(1 << 26);

        /// <summary>
        /// SimsRedux 15.4: a continuation goes next only past plain orders that don't outrank it - never ahead of a
        /// higher-priority order (the player's order still beats a Sim's own autonomous chat) or a parent's
        /// idle/exit step. Otherwise it's placed by priority as before.
        /// </summary>
        private bool ReduxContinuationFits(VMQueuedAction invocation)
        {
            if (invocation.Mode != VMQueueMode.Normal || ActiveQueueBlock < 0) return false;
            for (int i = ActiveQueueBlock + 1; i < Queue.Count; i++)
            {
                var q = Queue[i];
                if (q.Mode != VMQueueMode.Normal || q.Priority > invocation.Priority) return false;
            }
            return true;
        }

        public void CancelAction(ushort actionUID)
        {
            var interaction = Queue.FirstOrDefault(x => x.UID == actionUID);
            if (interaction != null)
            {
                if (Entity is VMAvatar && interaction == Queue[0] && Context.VM.EODHost != null) Context.VM.EODHost.ForceDisconnect((VMAvatar)Entity);
                QueueDirty = true;
                interaction.NotifyIdle = true;
                //cancel any idle parents after this interaction
                var index = Queue.IndexOf(interaction);

                if (interaction.Mode == Engine.VMQueueMode.ParentIdle)
                {
                    for (int i = index + 1; i < Queue.Count; i++)
                    {
                        if (Queue[i].Mode == Engine.VMQueueMode.ParentIdle)
                        {
                            if (interaction.Mode == Engine.VMQueueMode.ParentIdle) Queue.RemoveAt(i--);
                            else
                            {
                                Queue[i].NotifyIdle = true;
                                Queue[i].Priority = 0;
                            }
                        }
                        else if (Queue[i].Mode == Engine.VMQueueMode.ParentExit)
                        {
                            Queue[i].NotifyIdle = true;
                            Queue[i].Priority = 0;
                        }
                        //parent exit needs to "appear" like it is cancelled.
                    }
                }

                var canQueueSkip = !interaction.Flags.HasFlag(TTABFlags.MustRun);

                if (canQueueSkip && (index > ActiveQueueBlock || Stack.LastOrDefault()?.ActionTree == false) && (interaction.Mode == Engine.VMQueueMode.Normal || interaction.Flags.HasFlag(TTABFlags.FSODirectControl)))
                {
                    ReduxQueueNote(this, interaction, "it was cancelled before it started");
                    Queue.Remove(interaction);
                    if (Context.VM.TS1) interaction.Callee?.ExecuteEntryPoint(4, Context, true, Entity); //queue skipped (SimsRedux: its object can be gone)
                }
                else
                {
                    Entity.SetFlag(VMEntityFlags.InteractionCanceled, true);
                    ((VMAvatar)Entity).SetPersonData(VMPersonDataVariable.Priority, 0);
                }
            }
        }

        private bool ExecuteAction(VMQueuedAction action)
        {
            //set the new interaction's priority
            ((VMAvatar)Entity).SetPersonData(VMPersonDataVariable.Priority, action.Priority);
            var frame = action.ToStackFrame(Entity);
            frame.SpecialResult = VMSpecialResult.Interaction;
            return Push(frame);
        }

        public List<VMPieMenuInteraction> CheckTS1Action(VMQueuedAction action, bool auto)
        {
            var result = new List<VMPieMenuInteraction>();

            if (Entity is VMAvatar && !action.Flags.HasFlag(TTABFlags.FSOSkipPermissions)) //just let everyone use the CSR interactions
            {
                var avatar = (VMAvatar)Entity;

                if ((action.Flags & (TTABFlags.TS1AllowCats | TTABFlags.TS1AllowDogs)) > 0)
                {
                    //interaction can only be performed by cats or dogs
                    //if (!avatar.IsPet) return null;
                    //check we're the correct type
                    if (avatar.IsCat && (action.Flags & TTABFlags.TS1AllowCats) == 0) return null;
                    if (avatar.IsDog && (action.Flags & TTABFlags.TS1AllowDogs) == 0) return null;
                }
                else if (avatar.IsPet) return null; //not allowed

                var isVisitor = avatar.GetPersonData(VMPersonDataVariable.PersonType) == 1 && avatar.GetPersonData(VMPersonDataVariable.GreetStatus) < 2;
                //avatar.ObjectID != Context.VM.GetGlobalValue(3);
                var debugTrees = false;

                TTABFlags ts1State =
                      ((isVisitor) ? TTABFlags.AllowVisitors : 0)
                    | ((avatar.GetPersonData(VMPersonDataVariable.PersonsAge) < 18) ? TTABFlags.TS1NoChild : 0)
                    | ((avatar.GetPersonData(VMPersonDataVariable.PersonsAge) >= 18 && !avatar.IsPet) ? TTABFlags.TS1NoAdult : 0);

                //DEBUG: enable debug interction for all CSRs.
                if ((action.Flags & TTABFlags.Debug) > 0)
                {
                    if (!isVisitor && debugTrees)
                        return result; //do not bother running check
                    else
                        return null; //disable debug for everyone else.
                }

                //NEGATIVE EFFECTS:
                var pos = ts1State & (TTABFlags.TS1NoChild | TTABFlags.TS1NoAdult);
                var ts1Compare = action.Flags;
                if ((pos & ts1Compare) > 0) return null;

                var negMask = (TTABFlags.AllowVisitors);

                var negatedFlags = (~ts1Compare) & negMask;
                if ((negatedFlags & ts1State) > 0) return null; //we are disallowed
            }
            if (action.CheckRoutine != null)
            {
                var args = new short[4];
                if (auto) args[0] = 1;
                // SimsRedux 16.9: same-gender proposals (Utils/ReduxAttraction.cs)
                short reduxGender;
                var reduxFlipped = FSO.SimAntics.Utils.ReduxAttraction.BeginCheck(Entity, action, out reduxGender);
                VMPrimitiveExitCode checkResult;
                try
                {
                    checkResult = EvaluateCheck(Context, Entity, new VMStackFrame()
                    {
                        Caller = Entity,
                        Callee = action.Callee,
                        CodeOwner = action.CodeOwner,
                        StackObject = action.StackObject,
                        Routine = action.CheckRoutine,
                        Args = args
                    }, null, result);
                }
                finally { FSO.SimAntics.Utils.ReduxAttraction.EndCheck(reduxFlipped, reduxGender); }
                if (checkResult != VMPrimitiveExitCode.RETURN_TRUE)
                {
                    return null;
                }
                // SimsRedux 16.9: romantic choices only toward Sims they're attracted to (Utils/ReduxAttraction.cs)
                if (!FSO.SimAntics.Utils.ReduxAttraction.AllowCheck(Entity, action)) return null;
            }
            return result;
        }

        public List<VMPieMenuInteraction> CheckAction(VMQueuedAction action, bool auto = false)
        {
            // 1. check action flags for permissions (if we are avatar)
            // 2. run check tree

            // rules:
            // Dogs/Cats means people CANNOT use these interactions. (DogsFlag|CatsFlag & IsDog|IsCat)

            // When Allow Object Owner is OFF it disallows the object owner, otherwise no effect
            // Visitors, Roommates, Ghosts have this same negative effect.
            // Friends apprars to override Owner, Visitors, Roommates

            // Allow CSRs:positive effect.

            if (action == null) return null;
            if (Context.VM.TS1) return CheckTS1Action(action, auto);
            var result = new List<VMPieMenuInteraction>();
            
            if (!action.Flags.HasFlag(TTABFlags.FSOSkipPermissions) && Entity is VMAvatar) //just let everyone use the CSR interactions
            {
                var avatar = (VMAvatar)Entity;

                if (avatar.GetSlot(0) != null && (action.Flags & TTABFlags.TSOAvailableCarrying) == 0) return null;

                if ((action.Flags & (TTABFlags.AllowCats | TTABFlags.AllowDogs)) > 0)
                {
                    //interaction can only be performed by cats or dogs
                    if (!avatar.IsPet) return null;
                    //check we're the correct type
                    if (avatar.IsCat && (action.Flags & TTABFlags.AllowCats) == 0) return null;
                    if (avatar.IsDog && (action.Flags & TTABFlags.AllowDogs) == 0) return null;
                }
                else if (avatar.IsPet && (avatar.AvatarState.Permissions < VMTSOAvatarPermissions.Admin || auto)) return null; //not allowed

                bool isActionGlobal = action.ActionRoutine.ID < 4096; // Ignore global actions for disabling interactions due to repair.
                bool isRepair = (action.Flags & TTABFlags.TSOIsRepair) > 0;

                if ((!isActionGlobal || isRepair) && 
                    (isRepair != ((action.Callee.MultitileGroup.BaseObject?.TSOState as VMTSOObjectState)?.Broken ?? false))) return null;

                uint ownerID = 0;
                if (action.Callee is VMGameObject) {
                    var state = ((VMTSOObjectState)action.Callee.TSOState);
                    ownerID = state?.OwnerID ?? 0;
                    if (ownerID != 0 && state.ObjectFlags.HasFlag(VMTSOObjectFlags.FSODonated))
                    {
                        ownerID = Context.VM.TSOState.OwnerID; //owner rewrite to mayor
                    }
                }

                TSOFlags tsoState =
                    ((!(action.Callee is VMGameObject) || avatar.PersistID == ownerID)
                    ? TSOFlags.AllowObjectOwner : 0)
                    | ((avatar.AvatarState.Permissions == VMTSOAvatarPermissions.Visitor) ? TSOFlags.AllowVisitors : 0)
                    | ((avatar.AvatarState.Permissions >= VMTSOAvatarPermissions.Roommate) ? TSOFlags.AllowRoommates : 0)
                    | ((avatar.AvatarState.Permissions == VMTSOAvatarPermissions.Admin) ? TSOFlags.AllowCSRs : 0)
                    | ((avatar.GetPersonData(VMPersonDataVariable.IsGhost) > 0) ? TSOFlags.AllowGhost : 0)
                    | TSOFlags.AllowFriends;
                TSOFlags tsoCompare = action.Flags2;
                //if flags are empty apart from "Non-Empty", force everything but visitor. (a kind of default state)
                if (tsoCompare == TSOFlags.NonEmpty) tsoCompare |= TSOFlags.AllowFriends | TSOFlags.AllowRoommates | TSOFlags.AllowObjectOwner;

                //DEBUG: enable debug interction for all CSRs.
                if ((action.Flags & TTABFlags.Debug) > 0)
                {
                    if ((tsoState & TSOFlags.AllowCSRs) > 0)
                        return result; //do not bother running check
                    else
                        return null; //disable debug for everyone else.
                }

                if ((action.Flags & TTABFlags.TSOAvailableWhenDead) > 0) tsoCompare |= TSOFlags.AllowGhost;
                if ((action.Flags & TTABFlags.AllowVisitors) > 0) tsoCompare |= TSOFlags.AllowVisitors; //wrong???????

                var posMask = (TSOFlags.AllowObjectOwner);
                if (((tsoState & posMask) & (tsoCompare & posMask)) == 0)
                {
                    //NEGATIVE EFFECTS:
                    var negMask = (TSOFlags.AllowVisitors | TSOFlags.AllowRoommates | TSOFlags.AllowGhost);

                    var negatedFlags = (~tsoCompare) & negMask;
                    if ((negatedFlags & tsoState) > 0) return null; //we are disallowed
                    if ((tsoCompare & TSOFlags.AllowCSRs) > 0 && (tsoState & TSOFlags.AllowCSRs) == 0) return null; // only admins can run csr.
                }
            }
            if ((!action.Flags.HasFlag(TTABFlags.FSOSkipPermissions) || ((action.Flags & TTABFlags.TSORunCheckAlways) > 0))
                && action.CheckRoutine != null)
            {
                var args = new short[4];
                if (auto) args[0] = 1;
                if (EvaluateCheck(Context, Entity, new VMStackFrame()
                {
                    Caller = Entity,
                    Callee = action.Callee,
                    CodeOwner = action.CodeOwner,
                    StackObject = action.StackObject,
                    Routine = action.CheckRoutine,
                    Args = args
                }, null, result) != VMPrimitiveExitCode.RETURN_TRUE)
                {
                    return null;
                }
            }
            return result;
        }

        public void EnsureDirectControlAction()
        {
            if (!(Entity is VMAvatar ava) ||
                VM.GlobTS1 ||
                ava.GetPersonData(VMPersonDataVariable.UnusedAndDoNotUse2) != 32767 ||
                ava.GetPersonData(VMPersonDataVariable.Posture) != 0 ||
                Queue.Any(entry => entry.Flags.HasFlag(TTABFlags.FSODirectControl)))
            {
                return;
            }

            var routine = Entity.GetRoutineWithOwner(9001, Context);
            if (routine == null || routine.routine == null) return;

            EnqueueAction(
                new VMQueuedAction
                {
                    Callee = Entity,
                    CodeOwner = routine.owner,
                    ActionRoutine = routine.routine,
                    Name = "Direct Control",
                    StackObject = Entity,
                    Args = new short[4],
                    Priority = (short)VMQueuePriority.Idle + 1,
                    Flags = TTABFlags.FSOSkipPermissions | TTABFlags.FSODirectControl,
                    Mode = VMQueueMode.Idle
                }
            );
        }

        #region VM Marshalling Functions
        public virtual VMThreadMarshal Save()
        {
            var stack = new VMStackFrameMarshal[Stack.Count];
            int i = 0;
            foreach (var item in Stack) stack[i++] = item.Save();

            var queue = new VMQueuedActionMarshal[Queue.Count];
            i = 0;
            foreach (var item in Queue) queue[i++] = item.Save();

            return new VMThreadMarshal
            {
                Stack = stack,
                Queue = queue,
                ActiveQueueBlock = ActiveQueueBlock,
                TempRegisters = (short[])TempRegisters.Clone(),
                TempXL = (int[])TempXL.Clone(),
                LastStackExitCode = LastStackExitCode,

                BlockingState = BlockingState,
                EODConnection = EODConnection,

                Interrupt = Interrupt,

                ActionUID = ActionUID,
                DialogCooldown = DialogCooldown,
                ScheduleIdleStart = ScheduleIdleStart
            };
        }

        public virtual void Load(VMThreadMarshal input, VMContext context)
        {
            Stack = new List<VMStackFrame>();
            foreach (var item in input.Stack)
            {
                if (item is VMRoutingFrameMarshal)
                {
                    Stack.Add(new VMRoutingFrame(item, context, this));
                }
                else if (item is VMDirectControlFrameMarshal)
                {
                    Stack.Add(new VMDirectControlFrame(item, context, this));
                }
                else
                {
                    Stack.Add(new VMStackFrame(item, context, this));
                }
            }
            // SimsRedux: a save can point an object at a script that doesn't exist here (seen with
            // custom lamps: "CT - Get my Room Impact in Temp"). Running it would crash and pop up a
            // SimAntics error. For objects, start the script over instead (the same as a reset, without the popup).
            if (Entity is VMGameObject && Stack.Any(f => f.GetType() == typeof(VMStackFrame) && f.Routine == null))
            {
                System.Console.WriteLine("[SimsRedux] An object was saved in a script that no longer exists; restarting it.");
                Stack.Clear();
            }
            Queue = new List<VMQueuedAction>();
            QueueDirty = true;
            foreach (var item in input.Queue) Queue.Add(new VMQueuedAction(item, context));
            ActiveQueueBlock = input.ActiveQueueBlock;
            // SimsRedux: code or an interaction belonging to an object this game doesn't have (its files are
            // missing - e.g. a Sims Online object without The Sims Online's files) can't run: this thread forgets
            // what it was doing and starts over, instead of the whole lot failing to load
            // - and the same when it was busy with an object held back (Utils/ReduxHeldObjects: e.g. a Sims 1 table
            // with a missing sculpture on it) or no longer there, like a missing object dropped from a Sim's hands.
            // (The held-object checks only run on a lot with such objects; "acting on something that isn't in the
            // save" is checked on every lot loaded from disk - not on an online join/resync - see ReduxBusyWithMissing.)
            string reduxWhy = null;
            if (Stack.Any(f => f.CodeOwner == null) || Queue.Any(q => q.CodeOwner == null)
                || ReduxBusyWithMissing(input, context, Entity, out reduxWhy))
            {
                // (18.7: says who and why - the soak test reads it to find what deleted the object out from under them)
                System.Console.WriteLine("[SimsRedux] Something was saved busy with an object that isn't in this game; it starts over. ("
                    + (Entity?.ToString() ?? "?") + ": " + (reduxWhy ?? "its code's object isn't loaded") + ")");
                Stack.Clear();
                Queue.Clear();
                ActiveQueueBlock = -1;
                ReduxStartedOver = true; // (18.7: VM.Load finishes the reset once the whole lot is in - see ReduxFinishStartOver)
            }
            TempRegisters = input.TempRegisters;
            TempXL = input.TempXL;
            LastStackExitCode = input.LastStackExitCode;

            BlockingState = input.BlockingState;
            EODConnection = input.EODConnection;
            Interrupt = input.Interrupt;
            ActionUID = input.ActionUID;
            DialogCooldown = input.DialogCooldown;
            ScheduleIdleStart = input.ScheduleIdleStart;
        }

        /// <summary>
        /// SimsRedux: was this thread saved busy with an object held back for missing files (as the one acted on,
        /// the stack object or the interaction's icon), or acting on an object that isn't there (it would crash on
        /// its first tick)? The held-back checks only on a lot that has held-back or dropped objects; the "isn't
        /// there" check (the object acted on) on every lot.
        /// </summary>
        private static bool ReduxBusyWithMissing(VMThreadMarshal input, VMContext context, VMEntity entity, out string why)
        {
            why = null;
            if (context?.VM == null) return false;
            var held = context.VM.ReduxHeld;
            Func<short, bool> isGone = (id) => id > 0 && context.VM.GetObjectById(id) == null;
            // 18.7: a Sim's interaction (or an order waiting in their queue) whose stack object isn't in the save - the other
            // Sim in a chat, the plate in their hands, when that one was left out of the house's save (a visitor, a townie)
            // - starts over too: the script went on with no object and stopped with a NullReferenceException a moment after
            // the house opened (Headless Test #14: "Gossip A:43", "get food:3", "drop food:1" just after arriving by taxi).
            // (only a Sim's interactions: a Sim's own loop or an object's script often keeps an old stack object it no
            // longer uses, and starting an object's script over would run its setup again)
            bool sim = entity is VMAvatar;
            // (15.1: on every lot, something saved busy with an object that isn't in the save - a visitor left out
            // of the house's save, a "go here" marker - starts over: it used to load with nothing to act on and
            // stop with a SimAntics error on its first use of it, e.g. "Go Here" Standard entry, "Tease Scare")
            if (held == null || !held.AnyMissing)
            {
                // (only when the lot comes from its save on disk: an online join/resync takes the lot's running state
                // exactly as it is - cleaning it up here would make this game differ from the lot, a desync)
                if (context.VM.ReduxLoadingLiveState) return false;
                if (input.Stack != null)
                    foreach (var f in input.Stack)
                    {
                        if (f != null && isGone(f.Callee)) { why = "script " + f.RoutineID + " acting on object " + f.Callee + ", gone"; return true; }
                        if (f != null && sim && f.ActionTree && isGone(f.StackObject)) { why = "script " + f.RoutineID + " with stack object " + f.StackObject + ", gone"; return true; }
                    }
                if (input.Queue != null)
                    foreach (var q in input.Queue)
                    {
                        if (q != null && isGone(q.Callee)) { why = "queued '" + q.Name + "' on object " + q.Callee + ", gone"; return true; }
                        if (q != null && sim && isGone(q.StackObject)) { why = "queued '" + q.Name + "' with stack object " + q.StackObject + ", gone"; return true; }
                    }
                return false;
            }
            Func<short, bool> isHeld = (id) => id != 0 && (held.Holds(id) || held.Dropped.Contains(id));
            if (context.VM.ReduxLoadingLiveState) sim = false; // (the stack-object check: disk loads only, as above - never an online join)
            if (input.Stack != null)
                foreach (var f in input.Stack)
                    if (f != null && (isHeld(f.Callee) || isHeld(f.StackObject) || isHeld(f.Caller) || isGone(f.Callee) || (sim && f.ActionTree && isGone(f.StackObject)))) { why = "script " + f.RoutineID + " with held/gone object " + f.Callee + "/" + f.StackObject; return true; }
            if (input.Queue != null)
                foreach (var q in input.Queue)
                    if (q != null && (isHeld(q.Callee) || isHeld(q.StackObject) || isHeld(q.IconOwner) || isGone(q.Callee) || (sim && isGone(q.StackObject)))) { why = "queued '" + q.Name + "' with held/gone object " + q.Callee + "/" + q.StackObject; return true; }
            return false;
        }

        public VMThread(VMThreadMarshal input, VMContext context, VMEntity entity)
        {
            Context = context;
            Entity = entity;
            Load(input, context);
        }
        #endregion
    }

    public enum VMThreadBreakMode
    {
        Active = 0,
        Pause = 1,
        StepIn = 2,
        StepOut = 3,
        StepOver = 4,
        ReturnTrue = 5,
        ReturnFalse = 6,
        Immediate = 7,
        Reset = 8
    }
}