using System.IO;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Engine.Scopes;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: a Sim changes into one of their own outfits (click your Sim: Change Into..., or a dresser:
    /// Change into Costume / Change Back). The outfits are the ones picked in Create-A-Sim (everyday, formal,
    /// swimwear, sleepwear, outdoors, high fashion, costume). Only ever the Sim sending it (on an online lot: a
    /// player's own Sim - the lot sets who sent it).
    /// </summary>
    public class VMNetReduxOutfitCmd : VMNetCommandBodyAbstract
    {
        /// <summary>Which outfit (VMPersonSuits): 0 everyday, then the occasion ones.</summary>
        public ushort Suit;

        public static readonly ushort[] Allowed =
        {
            (ushort)VMPersonSuits.DefaultDaywear, (ushort)VMPersonSuits.TS1Formal, (ushort)VMPersonSuits.DefaultSwimwear,
            (ushort)VMPersonSuits.DefaultSleepwear, (ushort)VMPersonSuits.TS1Winter, (ushort)VMPersonSuits.TS1HighFashion,
            (ushort)VMPersonSuits.ReduxCostume
        };

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (caller == null || !vm.TS1) return false;
            var suit = VMSuitProvider.GetPersonSuitTS1(caller, Suit);
            if (suit == null) return false;
            caller.SetPersonData(VMPersonDataVariable.CurrentOutfit, (short)Suit);
            caller.BodyOutfit = suit;
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (caller == null || caller.IsPet || caller.Dead) return false;
            return System.Array.IndexOf(Allowed, Suit) >= 0;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Suit);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Suit = reader.ReadUInt16();
        }
    }
}
