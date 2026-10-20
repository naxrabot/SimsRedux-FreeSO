using System;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Engine.Scopes;
using FSO.SimAntics.Engine.Primitives;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux: the baby's "Shame on you! The baby is being neglected" warning.
    ///
    /// The baby's main loop (Baby.iff, "main") checks every 5 game minutes. It starts a countdown at its tuning
    /// "20" and, while the baby is starving (hunger 0), takes 5 off each check: the warning was meant to come
    /// after 20 starving minutes. But while the baby is fed the script takes the 20 off again ("-=" where it
    /// meant to set it back to 20), so after the first few minutes the countdown is always used up and the
    /// warning came the moment hunger reached 0 - a couple of seconds of crying. Here that line sets the
    /// countdown back to 20 as intended. (The social worker still comes at the baby's next hourly check if
    /// it's still starving - 40 minutes after the warning.)
    ///
    /// The change is made in memory, to the script as it's loaded, the same on every player's game.
    /// </summary>
    public static class ReduxBaby
    {
        private const uint BabyGUID = 0x76F256C5;
        private const ushort MainTree = 0x1000;
        private const int ResetLine = 17;

        public static void Patch(VMEntity ent)
        {
            try
            {
                if (!(ent is VMGameObject) || ent.Object?.OBJ?.GUID != BabyGUID) return;
                var routine = ent.Object.Resource?.GetRoutine(MainTree) as VMRoutine;
                if (routine?.Instructions == null || routine.Instructions.Length <= ResetLine) return;
                var ins = routine.Instructions[ResetLine];
                var op = ins?.Operand as VMExpressionOperand;
                // (only the line as Maxis wrote it: local 1 -= tuning 128)
                if (ins.Opcode != 2 || op == null || op.Operator != VMExpressionOperator.MinusEquals
                    || op.LhsOwner != VMVariableScope.Local || op.LhsData != 1
                    || op.RhsOwner != VMVariableScope.Tuning || op.RhsData != 128) return;
                op.Operator = VMExpressionOperator.Assign;
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] baby: " + e.Message); }
        }
    }
}
