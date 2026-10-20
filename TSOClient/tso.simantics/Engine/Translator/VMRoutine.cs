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
            ReduxKindSchoolBus = 4, ReduxKindExitSkill = 5, ReduxKindCarForSim = 6;

        public sbyte GetReduxKind()
        {
            var kind = ReduxKind;
            if (kind < 0)
            {
                var label = Chunk?.ChunkLabel ?? "";
                if (label.Equals("Exit Skill Object", System.StringComparison.OrdinalIgnoreCase)) kind = ReduxKindExitSkill;
                else if (label.IndexOf("happy enough", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindHappyEnough;
                else if (label.IndexOf("due at work", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindDueAtWork;
                else if (label.IndexOf("warnings car pool", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindCarPool;
                else if (label.IndexOf("school bus schedule", System.StringComparison.OrdinalIgnoreCase) >= 0) kind = ReduxKindSchoolBus;
                else if (label.StartsWith("is this car for", System.StringComparison.OrdinalIgnoreCase)) kind = ReduxKindCarForSim;
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
                        if (keep > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                        if (keep < 0) return VMPrimitiveExitCode.GOTO_FALSE; // (a need is low: they stop)
                    }
                    // SimsRedux: days off work (Utils/ReduxWeek.cs)
                    else if (kind == ReduxKindDueAtWork || kind == ReduxKindCarPool || kind == ReduxKindSchoolBus || kind == ReduxKindCarForSim)
                    {
                        var off = FSO.SimAntics.Utils.ReduxWeek.DayOff(frame, opcode);
                        if (off < 0) return VMPrimitiveExitCode.GOTO_FALSE;
                        if (off > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                    }
                }
                // SimsRedux: no more than a couple of uninvited visitors a day (Utils/ReduxVisitCap.cs)
                if (opcode < 4096 && FSO.SimAntics.Utils.ReduxVisitCap.Skip(frame, opcode)) return VMPrimitiveExitCode.GOTO_TRUE;
                // SimsRedux: Smarter Sims get up from skill objects before a need runs out (Utils/ReduxExit.cs)
                if (opcode < 4096 && FSO.SimAntics.Utils.ReduxExit.StopSkill(frame, opcode)) return VMPrimitiveExitCode.GOTO_TRUE;
                frame.Thread.ExecuteSubRoutine(frame, opcode, (VMSubRoutineOperand)instruction.Operand);
                return VMPrimitiveExitCode.CONTINUE;
            }


            // SimsRedux: a script that loops "find the thief, walk to him" when the walk ends at once (the
            // police officer already there) never gives up its turn and was reset as an "infinite loop".
            // After a few walks in one tick, a walk waits for the next tick first (VMThread.ReduxWalkShouldWait;
            // the old "after 5000 primitives" limit still did ~1000 walk set-ups a tick: 1 frame a second).
            if ((opcode == 27 || opcode == 45) && frame.Thread != null && frame.Thread.ReduxWalkShouldWait(frame))
                return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;

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
