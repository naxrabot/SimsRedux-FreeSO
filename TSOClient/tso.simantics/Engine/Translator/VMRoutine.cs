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
                if (opcode >= 4096 && opcode < 8192 && FSO.SimAntics.Utils.ReduxComputer.KeepGoing(frame.Thread, frame, opcode))
                    return VMPrimitiveExitCode.GOTO_TRUE;
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
