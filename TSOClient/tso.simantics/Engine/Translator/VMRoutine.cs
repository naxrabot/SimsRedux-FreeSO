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

        public virtual VMPrimitiveExitCode Execute(VMStackFrame frame, out VMInstruction instruction)
        {
            instruction = frame.GetCurrentInstruction();
            var opcode = instruction.Opcode;

            if (opcode >= 256)
            {
                // SimsRedux: a Sim playing or writing on a computer keeps going (Utils/ReduxComputer.cs)
                if (opcode >= 4096 && opcode < 8192)
                {
                    var keep = FSO.SimAntics.Utils.ReduxComputer.Decide(frame.Thread, frame, opcode);
                    if (keep > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                    if (keep < 0) return VMPrimitiveExitCode.GOTO_FALSE; // (a need is low: they stop)
                    // SimsRedux: days off work (Utils/ReduxWeek.cs)
                    var off = FSO.SimAntics.Utils.ReduxWeek.DayOff(frame, opcode);
                    if (off < 0) return VMPrimitiveExitCode.GOTO_FALSE;
                    if (off > 0) return VMPrimitiveExitCode.GOTO_TRUE;
                }
                // SimsRedux: Smarter Sims get up from skill objects before a need runs out (Utils/ReduxExit.cs)
                if (opcode < 4096 && FSO.SimAntics.Utils.ReduxExit.StopSkill(frame, opcode)) return VMPrimitiveExitCode.GOTO_TRUE;
                frame.Thread.ExecuteSubRoutine(frame, opcode, (VMSubRoutineOperand)instruction.Operand);
                return VMPrimitiveExitCode.CONTINUE;
            }


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
