using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.Primitives;
using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// For asynchronous events, such as transactions, inventory access and plugins. Updates the
    /// state for a thread waiting on an asynchronous response, usually waking it up.
    /// CANNOT be sent by clients. Must be sent by server.
    /// 
    /// The intention is for the async state updates being sent to the SimAntics VM are run at 
    /// the same time on both the client and server VMs.
    /// </summary>
    public class VMNetAsyncResponseCmd : VMNetCommandBodyAbstract
    {
        public short ID;
        public VMAsyncState State;

        public override bool AcceptFromClient { get { return false; } }

        public VMNetAsyncResponseCmd() { }

        public VMNetAsyncResponseCmd(short id, VMAsyncState state)
        {
            ID = id;
            State = state;
        }

        public override bool Execute(VM vm)
        {
            var type = State.GetType();
            //if ID is 0, there is no thread to unblock, and we just have to do the extra functionality below.
            if (ID != 0)
            {
                VMEntity obj = vm.GetObjectById(ID);
                //we can only update an object's blocking state if it exists and is of the same type exactly.
                if (obj == null || obj.Thread.BlockingState == null || obj.Thread.BlockingState.GetType() != type) return false;
                obj.Thread.BlockingState = State;
            }

            if (type == typeof(VMTransferFundsState))
            {
                //special handling. update visual budgets of involved elements.
                //note: if we are the server and the reference budget IS the visual, do not update.
                if (vm.GlobalLink == null || !(vm.GlobalLink is VMTSOGlobalLinkStub))
                {
                    var state = (VMTransferFundsState)State;
                    var obj1 = vm.GetObjectByPersist(state.UID1);
                    if (obj1 != null)
                    {
                        foreach (var obj in obj1.MultitileGroup.Objects)
                        {
                            if (!vm.TS1) obj.TSOState.Budget.Value = state.Budget1;
                        }
                    }
                    var obj2 = vm.GetObjectByPersist(state.UID2);
                    if (obj2 != null)
                    {
                        foreach (var obj in obj2.MultitileGroup.Objects)
                        {
                            if (!vm.TS1) obj.TSOState.Budget.Value = state.Budget2;
                        }
                    }

                    // SimsRedux multiplayer: in TS1 only the server charges the family (players' games
                    // have no money link), so a player's game takes the family's new money from here -
                    // straight away, instead of waiting for the server's next full refresh of the lot
                    if (vm.TS1 && vm.GlobalLink == null && vm.TS1State?.CurrentFamily != null)
                    {
                        if (obj1 is VMAvatar && state.Budget1 != uint.MaxValue) vm.TS1State.CurrentFamily.Budget = (int)state.Budget1;
                        else if (obj2 is VMAvatar && state.Budget2 != uint.MaxValue) vm.TS1State.CurrentFamily.Budget = (int)state.Budget2;
                    }
                }
            }

            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return !FromNet;
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ID);
            VMAsyncState.SerializeGeneric(writer, State);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ID = reader.ReadInt16();
            State = VMAsyncState.DeserializeGeneric(reader, VMMarshal.LATEST_VERSION);
        }
        #endregion
    }
}
