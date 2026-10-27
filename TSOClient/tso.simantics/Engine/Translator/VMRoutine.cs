using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics
{
    public class VMRoutine
    {
        public VMRoutine(){
        }

        public byte Type;
        public VMInstruction[] Instructions;
        public ushort Locals;
        public ushort Arguments;
        public ushort ID;

        /** Run time info **/
        public VMFunctionRTI Rti;

        public BHAV Chunk;
        public uint RuntimeVer;

        /// <summary>
        /// SimsRedux: what this script is to the Redux hooks (the computer's "happy enough", the work and school
        /// checks, "Exit Skill Object"), worked out once from its name: -1 = not yet. Every script call used to take
        /// a lock and a table lookup per hook to find this out; now it's a field read. (Only depends on the
        /// script's name, so every player works out the same thing.)
        /// </summary>
        public sbyte ReduxKind = -1;
        public const sbyte ReduxKindNone = 0, ReduxKindHappyEnough = 1, ReduxKindDueAtWork = 2, ReduxKindCarPool = 3,
            ReduxKindSchoolBus = 4, ReduxKindExitSkill = 5, ReduxKindCarForSim = 6, ReduxKindExitFun = 7, ReduxKindWooHoo = 8,
            ReduxKindAttraction = 9, ReduxKindRelationships = 17, ReduxKindMail = 18;
        /// <summary>SimsRedux 16.9: what this script is as a choice's test to Utils/ReduxAttraction.cs (-1 = not yet).</summary>
        public sbyte ReduxAttractionKind = -1;

        public sbyte GetReduxKind()
        {
            var kind = ReduxKind;
            if (kind < 0)
            {
                var label = Chunk?.ChunkLabel ?? "";
                if (label.Equals("Exit Skill Object", System.StringComparison.OrdinalIgnoreCase)) kind = ReduxKindExitSkill;
                else if (label.Equals("Exit fun object", System.StringComparison.OrdinalIgnoreCase)) kind = ReduxKindExitFun;
                else if (label.IndexOf("happy enough", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindHappyEnough;
                else if (label.IndexOf("due at work", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindDueAtWork;
                else if (label.IndexOf("warnings car pool", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindCarPool;
                else if (label.IndexOf("school bus schedule", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindSchoolBus;
                else if (label.StartsWith("is this car for", System.StringComparison.OrdinalIgnoreCase)) kind = ReduxKindCarForSim;
                else if (FSO.SimAntics.Utils.ReduxWooHoo.IsHookLabel(label)) kind = ReduxKindWooHoo; // (16.7: WooHoo - the bed, the baby question)
                else if (FSO.SimAntics.Utils.ReduxAttraction.IsHookLabel(label)) kind = ReduxKindAttraction; // (16.9: a romantic social's answer)
                else if (FSO.SimAntics.Utils.VMReduxRelationships.IsHookLabel(label)) kind = ReduxKindRelationships; // (17.0: the game's own wedding)
                else if (FSO.SimAntics.Utils.VMReduxPhone.IsHookLabel(label)) kind = ReduxKindMail; // (17.2: the mail carrier delivered - packages)
                else kind = ReduxKindNone;
                ReduxKind = kind;
            }
            return kind;
        }

        public virtual VMPrimitiveExitCode Execute(VMStackFrame frame, out VMInstruction instruction)
        {
            instruction = frame.GetCurrentInstruction();
            var opcode = instruction.Opcode;

            if (opcode >= 256)
            {
                // SimsRedux: a Sim playing or writing on a computer keeps going (Utils/ReduxComputer.cs)
                if (opcode >= 4096 && opcode < 8192)
                {
                    // (only scripts with one of the hooks' names go any further - see ReduxKind)
                    var target = frame.ScopeResource?.GetRoutine(opcode) as VMRoutine;
                    var kind = (target == null) ? ReduxKindNone : target.GetReduxKind();
                    if (kind == ReduxKindHappyEnough)
                    {
                        var keep = FSO.SimAntics.Utils.ReduxComputer.Decide(frame.Thread, frame, opcode);
                        if (keep != 0 && frame.Thread != null && frame.Thread.ReduxHookShouldWait()) return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                        if (keep > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                        if (keep < 0) return VMPrimitiveExitCode.GOTO_FALSE; // (a need is low: they stop)
                    }
                    // SimsRedux: days off work (Utils/ReduxWeek.cs)
                    else if (kind == ReduxKindDueAtWork || kind == ReduxKindCarPool || kind == ReduxKindSchoolBus || kind == ReduxKindCarForSim)
                    {
                        var off = FSO.SimAntics.Utils.ReduxWeek.DayOff(frame, opcode);
                        // (a script asking again and again in one tick waits for the next tick - see VMThread.ReduxHookShouldWait)
                        if (off != 0 && frame.Thread != null && frame.Thread.ReduxHookShouldWait()) return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                        if (off < 0) return VMPrimitiveExitCode.GOTO_FALSE;
                        if (off > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                    }
                    // SimsRedux 16.7: WooHoo in bed, and no more "Should we have a baby?" (Utils/ReduxWooHoo.cs)
                    else if (kind == ReduxKindWooHoo)
                    {
                        var woo = FSO.SimAntics.Utils.ReduxWooHoo.Hook(frame, target);
                        // (the bed's sleep loop asking again and again in one tick waits for the next tick)
                        if (woo != 0 && frame.Thread != null && frame.Thread.ReduxHookShouldWait()) return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                        if (woo > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                        if (woo < 0) return VMPrimitiveExitCode.GOTO_FALSE;
                    }
                    // SimsRedux 16.9: a Sim who isn't attracted turns a romantic social down (Utils/ReduxAttraction.cs)
                    else if (kind == ReduxKindAttraction)
                    {
                        if (FSO.SimAntics.Utils.ReduxAttraction.Hook(frame, target) < 0) return VMPrimitiveExitCode.GOTO_FALSE;
                    }
                    // SimsRedux 17.0: the game's own Propose ended in a wedding - the couple is Married (Utils/ReduxRelationships.cs)
                    else if (kind == ReduxKindRelationships && frame.VM != null && frame.VM.TS1) FSO.SimAntics.Utils.VMReduxRelationships.Hook(frame);
                    // SimsRedux 17.2: the mail carrier put the mail in the mailbox - packages come out (Utils/ReduxPhone.cs; only tells)
                    else if (kind == ReduxKindMail && frame.VM != null && frame.VM.TS1) FSO.SimAntics.Utils.VMReduxPhone.Hook(frame);
                }
                else if (opcode >= 8192 && frame.VM != null && frame.VM.TS1)
                {
                    // SimsRedux 16.7: the social globals' own "should have baby?" (Utils/ReduxWooHoo.cs)
                    var target = frame.ScopeResource?.SemiGlobal?.GetRoutine(opcode) as VMRoutine;
                    if (target != null && target.GetReduxKind() == ReduxKindWooHoo)
                    {
                        var woo = FSO.SimAntics.Utils.ReduxWooHoo.Hook(frame, target);
                        // (the bed's sleep loop asking again and again in one tick waits for the next tick)
                        if (woo != 0 && frame.Thread != null && frame.Thread.ReduxHookShouldWait()) return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                        if (woo > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                        if (woo < 0) return VMPrimitiveExitCode.GOTO_FALSE;
                    }
                }
                // (global script calls: the two hooks below are only gone into when they can apply - a cheap
                //  check here, instead of two guarded calls and a script lookup on every global call)
                if (opcode < 4096 && frame.VM != null && frame.VM.TS1)
                {
                    // SimsRedux: no more than a couple of uninvited visitors a day (Utils/ReduxVisitCap.cs)
                    if (opcode == 369 && FSO.SimAntics.Utils.ReduxVisitCap.Skip(frame, opcode))
                        return (frame.Thread != null && frame.Thread.ReduxHookShouldWait()) ? VMPrimitiveExitCode.CONTINUE_NEXT_TICK : VMPrimitiveExitCode.GOTO_TRUE;
                    // SimsRedux: Smarter Sims get up from skill objects before a need runs out (Utils/ReduxExit.cs)
                    if (frame.Caller is VMAvatar && FSO.SimAntics.Utils.ReduxExit.StopSkill(frame, opcode))
                        return (frame.Thread != null && frame.Thread.ReduxHookShouldWait()) ? VMPrimitiveExitCode.CONTINUE_NEXT_TICK : VMPrimitiveExitCode.GOTO_TRUE;
                }
                frame.Thread.ExecuteSubRoutine(frame, opcode, (VMSubRoutineOperand)instruction.Operand);
                // SimsRedux: a Sim playing with a fun object keeps going until Fun is full (Utils/ReduxFunPlay.cs)
                if (opcode == FSO.SimAntics.Utils.ReduxFunPlay.ExitFunObject && frame.VM != null && frame.VM.TS1 && frame.Caller is VMAvatar)
                    FSO.SimAntics.Utils.ReduxFunPlay.KeepPlaying(frame);
                return VMPrimitiveExitCode.CONTINUE;
            }


            // SimsRedux: a script that loops "find the thief, walk to him" when the walk ends at once (the
            // police officer already there) never gives up its turn and was reset as an "infinite loop".
            // After a few walks in one tick, a walk waits for the next tick first (VMThread.ReduxWalkShouldWait;
            // the old "after 5000 primitives" limit still did ~1000 walk set-ups a tick: 1 frame a second).
            if ((opcode == 27 || opcode == 45) && frame.Thread != null && frame.Thread.ReduxWalkShouldWait(frame))
                return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
            // SimsRedux: a visitor/NPC walking in his own main loop stops for a top-priority interaction - the
            // police officer chasing the burglar takes the NPC Controller's "Arrest" (VMThread.ReduxMainLoopInterrupted)
            if ((opcode == 27 || opcode == 45) && frame.Thread != null && frame.Thread.ReduxMainLoopInterrupted(frame.ActionTree))
                return VMPrimitiveExitCode.GOTO_FALSE;

            var primitive = VMContext.Primitives[opcode];
            if (primitive == null)
            {
                return VMPrimitiveExitCode.GOTO_TRUE;
            }

            VMPrimitiveHandler handler = primitive.GetHandler();
            return handler.Execute(frame, instruction.Operand);
        }
    }


    public class VMFunctionRTI
    {
        public string Name;
    }
}
