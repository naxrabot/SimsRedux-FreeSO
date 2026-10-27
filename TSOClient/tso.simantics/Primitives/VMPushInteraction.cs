using System;
using FSO.Files.Utils;
using FSO.Files.Formats.IFF.Chunks;
using System.IO;

namespace FSO.SimAntics.Engine.Primitives
{
    public class VMPushInteraction : VMPrimitiveHandler
    {


        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            var operand = (VMPushInteractionOperand)args;
            VMEntity interactionSource;

            if (operand.ObjectInLocal) interactionSource = context.VM.GetObjectById((short)context.Locals[operand.ObjectLocation]);
            else interactionSource = context.VM.GetObjectById((short)context.Args[operand.ObjectLocation]);

            short priority = 0;
            VMQueueMode mode = VMQueueMode.Normal;
            switch (operand.Priority)
            {
                case VMPushPriority.Inherited:
                    short oldPrio = 1;
                    if (context.ActionTree) oldPrio = (context.Caller as VMAvatar)?.GetPersonData(Model.VMPersonDataVariable.Priority) ?? 1;
                    if (context.ActionTree && context.VM.TS1) oldPrio = ReduxOrderPriority(context, oldPrio); // (SimsRedux 15.7: see there)
                    priority = Math.Max((short)1, oldPrio); break;
                case VMPushPriority.Maximum:
                    priority = (short)VMQueuePriority.Maximum; break;
                case VMPushPriority.Autonomous:
                    priority = (short)VMQueuePriority.Autonomous; break;
                case VMPushPriority.UserDriven:
                    priority = (short)VMQueuePriority.UserDriven; break;
                case VMPushPriority.ParentIdle:
                    priority = (short)VMQueuePriority.ParentIdle; mode = VMQueueMode.ParentIdle; break;
                case VMPushPriority.ParentExit:
                    priority = (short)VMQueuePriority.ParentExit; mode = VMQueueMode.ParentExit; break;
                case VMPushPriority.Idle:
                    priority = (short)VMQueuePriority.Idle; mode = VMQueueMode.Idle; break;
            }

            // SimsRedux: the interaction's object (or the Sim to push it on) can be gone - e.g. the Fancy Feet Cake's
            // entertainer left while "Watch stripper" pushes "watch" on the guests. Push nothing (false), as for a
            // missing interaction, instead of a SimAntics error.
            if (interactionSource == null || context.StackObject == null) return VMPrimitiveExitCode.GOTO_FALSE;
            // SimsRedux 16.9: no jealous reaction from a Sim the Game Rule "Jealousy" leaves out (Utils/ReduxAttraction.cs)
            if (context.VM.TS1 && FSO.SimAntics.Utils.ReduxAttraction.SkipJealousy(context)) return VMPrimitiveExitCode.GOTO_FALSE;
            var action = interactionSource.GetAction(operand.Interaction, context.StackObject, context.VM.Context, false);
            if (action == null) return VMPrimitiveExitCode.GOTO_FALSE;
            if (operand.UseCustomIcon) action.IconOwner = context.VM.GetObjectById((short)context.Locals[operand.IconLocation]);
            action.Mode = mode;
            action.Priority = priority;
            action.Flags |= TTABFlags.FSOSkipPermissions;
            if (operand.PushTailContinuation) action.Flags |= TTABFlags.FSOPushTail;
            if (operand.PushHeadContinuation) action.Flags |= TTABFlags.FSOPushHead;
            // SimsRedux 15.4: what a Sim's running interaction pushes onto that same Sim at its own ("inherited")
            // priority is the rest of that interaction - a social's own side of the conversation ("Joke - Person
            // A", "Talk - Person A"). It goes right after the running interaction (VMThread.EnqueueAction), not
            // behind the other orders the player has queued.
            // Never for an action that pushes itself again (the same object and menu entry) or for a push made by
            // something that was itself placed this way: a repeating chain would keep jumping ahead of the
            // player's queued orders, so they'd never run (those are placed by priority, as before).
            if (context.VM.TS1 && operand.Priority == VMPushPriority.Inherited && context.ActionTree
                && context.StackObject == context.Caller && !operand.PushHeadContinuation && !operand.PushTailContinuation)
            {
                var running = context.Thread?.ActiveAction;
                if (running != null && (running.Flags & VMThread.ReduxContinuation) == 0
                    && !(running.Callee == action.Callee && running.InteractionNumber == action.InteractionNumber))
                    action.Flags |= VMThread.ReduxContinuation;
            }

            context.StackObject.Thread.EnqueueAction(action);
            if (context.StackObject is VMAvatar && context.Caller is VMAvatar && context.StackObject != context.Caller)
            {
                //if this is an interaction between two sims, and this interaction is being pushed onto someone else,
                //show the interaction result chooser for that sim immediately, rather than force them to wait.
                //(erroneously shows up for "talk to" and "whisper to". there may be a better way to do this.
                action.InteractionResult = 0;
            }

            return VMPrimitiveExitCode.GOTO_TRUE;
        }

        /// <summary>
        /// SimsRedux 15.7: the priority an "inherited" push gets from a player's order that runs at once.
        /// A Sims 1 social picked from the menu ("Talk", "Joke", "Hug"...) is a "run immediately" interaction: it
        /// starts straight away, on top of whatever the Sim is doing, creates the conversation and pushes the two
        /// halves - "Talk" on this Sim, "Be Talked To" on the other - at its own ("inherited") priority, then ends.
        /// Running at once skipped the step that sets the Sim's current priority, so the halves inherited what the
        /// Sim had been doing before: 0 (standing idle, so 1) or an autonomous action's 2 - never the player's order.
        /// At that level the other Sim never stopped what they were doing to answer (anything they chose themselves
        /// outranks it), the next social queued cancelled the running one, and the Sim stood waiting for an answer
        /// that never came: no daily or lifetime relationship change and no Social gain.
        /// Now such a push takes the player's order's priority when that is higher - the other Sim stops to answer
        /// and queued socials wait their turn, as in the original game. Only the player's own orders (their usual
        /// priority); the Sims' own choices and scripts' pushes are as before. Worked out from the queue alone (the
        /// same on every machine); the priority is an existing saved field (no save change).
        /// </summary>
        private static short ReduxOrderPriority(VMStackFrame context, short prio)
        {
            var thread = context.Thread;
            if (thread == null || thread.IsCheck || context.Caller == null || context.Caller.Thread != thread) return prio;
            var running = thread.ActiveAction; // (the run-at-once order itself, while its own script runs)
            if (running == null || (running.Flags & TTABFlags.RunImmediately) == 0) return prio;
            if (running.Priority != (short)VMQueuePriority.UserDriven) return prio;
            return Math.Max(prio, running.Priority);
        }
    }

    public class VMPushInteractionOperand : VMPrimitiveOperand
    {
        public byte Interaction { get; set; }
        public byte ObjectLocation { get; set; }
        public VMPushPriority Priority { get; set; }
        public byte Flags;
        public byte IconLocation { get; set; }

        public bool UseCustomIcon
        {
            get { return (Flags & 1) > 0; }
            set
            {
                if (value) Flags |= 1;
                else Flags &= unchecked((byte)~1);
            }
        }

        public bool ObjectInLocal
        {
            get { return (Flags & 2) > 0; }
            set
            {
                if (value) Flags |= 2;
                else Flags &= unchecked((byte)~2);
            }
        }

        public bool PushHeadContinuation
        {
            get { return (Flags & 4) > 0; }
            set
            {
                if (value) Flags |= 4;
                else Flags &= unchecked((byte)~4);
            }
        }

        public bool PushTailContinuation
        {
            get { return (Flags & 128) > 0; }
            set
            {
                if (value) Flags |= 128;
                else Flags &= unchecked((byte)~128);
            }
        }

        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN))
            {
                Interaction = io.ReadByte();
                ObjectLocation = io.ReadByte();
                Priority = (VMPushPriority)io.ReadByte();
                Flags = io.ReadByte();
                IconLocation = io.ReadByte();
            }
        }

        public void Write(byte[] bytes) {
            using (var io = new BinaryWriter(new MemoryStream(bytes)))
            {
                io.Write(Interaction);
                io.Write(ObjectLocation);
                io.Write((byte)Priority);
                io.Write(Flags);
                io.Write(IconLocation);
            }
        }
        #endregion
    }

    public enum VMPushPriority : byte
    {
        Inherited = 0,
        Maximum = 1,
        Autonomous = 2,
        UserDriven = 3,
        ParentIdle = 4,
        ParentExit = 5,
        Idle = 6
    }
}
