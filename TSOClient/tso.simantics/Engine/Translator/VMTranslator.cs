using System;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics.Engine
{
    public class VMTranslator
    {
        private static VMTranslator _INSTANCE;
        public static VMTranslator INSTANCE
        {
            get
            {
                if (_INSTANCE == null) _INSTANCE = new VMTranslator();
                return _INSTANCE;
            }
            set
            {
                _INSTANCE = value;
            }
        }

        public virtual VMRoutine Assemble(BHAV bhav, GameIffResource res){
            var routine = new VMRoutine();
            PopulateRoutineFields(bhav, routine, res != null && res.ReduxTSO);
            return routine;
        }

        /// <summary>
        /// SimsRedux: where a Sims Online script's primitives go in Sims 1 mode (see GameIffResource.ReduxTSO).
        /// Primitive 1 is TSO's "generic Sims Online call" (the Sims 1's is "generic Sims call") and 25 is TSO's
        /// "transfer funds" (the Sims 1's is "budget"); 3, 19 and 51 do nothing in TSO (the Sims 1 uses them for
        /// find best interaction, make new character and manage inventory) and 30 is TSO's "syslog" (the Sims 1's
        /// is "gosub found action"). They're moved to spare slots that do what TSO did (VMContext.InitVMConfig).
        /// </summary>
        public const ushort ReduxTSOGenericCall = 250, ReduxTSOTransferFunds = 251, ReduxTSONothing = 252, ReduxTSOSysLog = 253;

        public static ushort ReduxTSOOpcode(ushort opcode)
        {
            switch (opcode)
            {
                case 1: return ReduxTSOGenericCall;
                case 25: return ReduxTSOTransferFunds;
                case 3: case 19: case 51: return ReduxTSONothing;
                case 30: return ReduxTSOSysLog;
                default: return opcode;
            }
        }

        protected void PopulateRoutineFields(BHAV bhav, VMRoutine routine)
        {
            PopulateRoutineFields(bhav, routine, false);
        }

        protected void PopulateRoutineFields(BHAV bhav, VMRoutine routine, bool reduxTSO)
        {
            routine.Locals = bhav.Locals;
            routine.Arguments = bhav.Args;
            routine.Type = bhav.Type;
            routine.ID = bhav.ChunkID;
            routine.Chunk = bhav;
            routine.Rti = new VMFunctionRTI
            {
                Name = bhav.ChunkLabel
            };

            VMInstruction[] instructions = new VMInstruction[bhav.Instructions.Length];
            for (var i = 0; i < bhav.Instructions.Length; i++)
            {
                var bhavInstruction = bhav.Instructions[i];
                var instruction = new VMInstruction();

                instruction.Index = (byte)i;
                instruction.Opcode = bhavInstruction.Opcode;
                if (reduxTSO && VMContext.ReduxTS1) instruction.Opcode = ReduxTSOOpcode(instruction.Opcode); // (SimsRedux)
                instruction.Operand = null;
                instruction.FalsePointer = bhavInstruction.FalsePointer;
                instruction.TruePointer = bhavInstruction.TruePointer;
                instruction.Breakpoint = bhavInstruction.Breakpoint;
                instruction.Function = routine;

                /** Routine call **/
                if (instruction.Opcode >= 256)
                {
                    var operand = new VMSubRoutineOperand();
                    operand.Read(bhavInstruction.Operand);
                    instruction.Operand = operand;
                }
                else
                {
                    var primitive = VMContext.Primitives[instruction.Opcode];
                    if (primitive != null)
                    {
                        if (primitive.OperandModel != null)
                        {
                            VMPrimitiveOperand operand = (VMPrimitiveOperand)Activator.CreateInstance(primitive.OperandModel);
                            operand.Read(bhavInstruction.Operand);
                            instruction.Operand = operand;
                        }
                    }
                }
                instructions[i] = instruction;

            }
            routine.Instructions = instructions;
        }
    }
}
