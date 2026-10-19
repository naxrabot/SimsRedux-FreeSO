using FSO.SimAntics.Engine;

namespace FSO.SimAntics.Primitives
{
    /// <summary>
    /// SimsRedux: The Sims Online's "transfer funds" primitive, in a Sims Online object's script in Sims 1 mode
    /// (VMTranslator.ReduxTSOOpcode). TSO's money moved through its servers; here nothing is paid or charged and
    /// the script is told the transfer didn't happen. The objects brought in so far (TS1/ReduxTSOObjects) never
    /// get here - it's the safe answer if one ever does.
    /// </summary>
    public class ReduxTSONoTransferFunds : VMPrimitiveHandler
    {
        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            return VMPrimitiveExitCode.GOTO_FALSE;
        }
    }
}
