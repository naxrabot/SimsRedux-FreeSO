using FSO.SimAntics.Engine;
using FSO.SimAntics.Engine.Utils;

namespace FSO.SimAntics.Primitives
{
    //like tso transfer funds, but really scaled back (only deduct from maxis)
    public class VMTS1Budget : VMPrimitiveHandler
    {
        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            var operand = (VMTransferFundsOperand)args;
            var amount = VMMemory.GetBigVariable(context, operand.GetAmountOwner(), (short)operand.AmountData);

            if ((operand.Flags & VMTransferFundsFlags.Subtract) > 0) amount = -amount; //instead of subtracting, we're adding
                                                                                       //weird terms for the flags here but ts1 is inverted
                                                                                       //amount contains the amount we are subtracting from the budget.

            // SimsRedux: a serving of leftovers from the fridge is already paid for (Utils/ReduxLeftovers.cs)
            if (amount > 0 && FSO.SimAntics.Utils.ReduxLeftovers.IsFreeServing(context)) return VMPrimitiveExitCode.GOTO_TRUE;
            // SimsRedux 16.7: a chosen dish costs its own price instead of the fridge's (Utils/ReduxCooking.cs)
            if (amount > 0) amount = FSO.SimAntics.Utils.ReduxCooking.Cost(context, amount);
            // SimsRedux Level Rewards: cheaper bills, free maid / gardener days (Utils/ReduxPerks.cs)
            if (amount > 0) amount = FSO.SimAntics.Utils.ReduxPerkRules.Budget(context, amount, (operand.Flags & VMTransferFundsFlags.JustTest) != 0);

            var oldBudget = context.VM.TS1State.CurrentFamily?.Budget ?? 0;
            var newBudget = oldBudget - amount;
            if (oldBudget < 0) return VMPrimitiveExitCode.GOTO_FALSE;
            if ((operand.Flags & VMTransferFundsFlags.JustTest) == 0 && context.VM.TS1State.CurrentFamily != null) context.VM.TS1State.CurrentFamily.Budget = newBudget;
            return VMPrimitiveExitCode.GOTO_TRUE;

            //ts1 does have expense types, which could be used for expenses monitoring (i do not think ts1 had this)
        }
    }
}
