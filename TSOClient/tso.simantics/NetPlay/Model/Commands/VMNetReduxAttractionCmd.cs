using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 16.9 (Part B #1b): attraction and relationship types (Utils/ReduxAttraction.cs). Everything that changes
    /// the lot for them goes through this command, so on a visit every player's game does the same at the same moment.
    ///   SetRules    - the lot's game says whether it's on (On) and the Game Rule "Jealousy" (Jealousy, 0-2).
    ///   SetOverride - sets a Sim's ReduxAttraction.OverrideAttr (A, Value) - in practice 0 once a talk's new traits are on
    ///                 the lot. Sent by the game that keeps the Sim's traits: the
    ///                 lot's game for its own Sims and townies, a visiting player's game for their own Sim only.
    ///   Ask         - "Talk about our relationship" to another player's Sim (Singleplayer+): changes nothing, that
    ///                 player's screen asks them (Asked). A guest only for their own Sim (A).
    ///   Answer      - that player's answer (Yes): changes nothing (Answered). A guest only for their own Sim (B).
    ///   Talk        - the talk itself (A asks B for Want; Yes: B's player already agreed, On: B's player said no). The
    ///                 lot otherwise works out whether B
    ///                 agrees (with the lot's own random numbers): yes - both Sims' types change (Want); no - it hurts
    ///                 their relationship a little. Only the game running the lot sends it.
    /// </summary>
    public class VMNetReduxAttractionCmd : VMNetCommandBodyAbstract
    {
        public enum AttrMode : byte { SetRules = 0, SetOverride = 1, Ask = 2, Answer = 3, Talk = 4 }

        /// <summary>What "Talk about our relationship" asks for.</summary>
        public enum Want : byte { None = 0, Exclusive = 1, Open = 2, Committed = 3 }

        public AttrMode Mode;
        public short A, B, Value;
        public byte Jealousy, Wanted;
        public bool On, Yes;

        /// <summary>Ask, Answer: on every game (the screen asks / tells the right player).</summary>
        public static event Action<VM, VMNetReduxAttractionCmd> Asked, Answered;
        /// <summary>Talk: how it went (agreed or not), on every game - each game keeps the change for its own Sims.</summary>
        public static event Action<VM, VMNetReduxAttractionCmd, VMAvatar, VMAvatar, bool> Talked;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            try
            {
                switch (Mode)
                {
                    case AttrMode.SetRules:
                        ReduxAttraction.SetRules(vm, On, (ReduxAttraction.Jealousy)Math.Min((int)Jealousy, 2));
                        return true;
                    case AttrMode.SetOverride:
                        {
                            var sim = vm.GetObjectById(A) as VMAvatar;
                            if (sim == null || sim.IsPet) return false;
                            sim.SetAttribute(ReduxAttraction.OverrideAttr, Value);
                            return true;
                        }
                    case AttrMode.Ask: Fire(Asked, vm); return true;
                    case AttrMode.Answer: Fire(Answered, vm); return true;
                    case AttrMode.Talk: return Talk(vm);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] attraction command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private void Fire(Action<VM, VMNetReduxAttractionCmd> ev, VM vm)
        {
            try { ev?.Invoke(vm, this); } catch (Exception e) { Console.WriteLine("[SimsRedux] attraction: " + e.Message); }
        }

        /// <summary>The type both Sims have after agreeing to Want.</summary>
        public static ReduxAttraction.RelType TypeFor(Want w)
        {
            switch (w)
            {
                case Want.Open: return ReduxAttraction.RelType.Open;
                case Want.Committed: return ReduxAttraction.RelType.Polyamorous;
            }
            return ReduxAttraction.RelType.Monogamous;
        }

        /// <summary>The chance (0-100) that b agrees to Want with a - from how they get on, b's type and b's traits.</summary>
        public static int Chance(VMAvatar a, VMAvatar b, Want w)
        {
            if (a == null || b == null || !ReduxAttraction.Mutual(a, b)) return 0;
            var r = ReduxWooHoo.Rel(b, a);
            int daily = (r != null && r.Count > 0) ? r[0] : 0, life = (r != null && r.Count > 2) ? r[2] : 0;
            var want = TypeFor(w);
            var has = ReduxAttraction.TypeOf(b);
            int c;
            if (want == has) c = 85;
            else if (want == ReduxAttraction.RelType.Monogamous) c = 30 + life / 2; // (asked to commit to one)
            else c = 15 + life / 4;                                               // (asked to open up)
            if (want != ReduxAttraction.RelType.Monogamous && want != has
                && (VMReduxTraits.Has(b, VMReduxTraits.T.Jealous) || VMReduxTraits.Has(b, VMReduxTraits.T.Loyal))) c /= 2;
            if (daily < 0) c /= 2;
            return Math.Max(5, Math.Min(95, c));
        }

        private bool Talk(VM vm)
        {
            var a = vm.GetObjectById(A) as VMAvatar;
            var b = vm.GetObjectById(B) as VMAvatar;
            var w = (Want)Wanted;
            if (a == null || b == null || a == b || a.IsPet || b.IsPet || w == Want.None || w > Want.Committed) return false;
            if (!ReduxWooHoo.Grownup(a) || !ReduxWooHoo.Grownup(b)) return false;
            bool agreed;
            if (!ReduxAttraction.Mutual(a, b)) agreed = false;
            else if (Yes) agreed = true; // (B's player said yes)
            else if (On) agreed = false; // (B's player said no)
            else agreed = (int)vm.Context.NextRandom(100) < Chance(a, b, w);

            // they talk it over: A goes over to B for a moment
            ReduxWooHoo.PushWait(vm, a, b, Math.Max(1, vm.Context.Clock.TicksPerMinute) * 2, "Talk About Our Relationship");
            if (agreed)
            {
                var type = TypeFor(w);
                foreach (var s in new[] { a, b })
                {
                    int orient = s.GetAttribute(ReduxAttraction.OverrideAttr) & 0xFF;
                    s.SetAttribute(ReduxAttraction.OverrideAttr, ReduxAttraction.Pack(orient, type));
                }
                Closer(a, b, 8, 3);
                Closer(b, a, 8, 3);
            }
            else
            {
                Closer(a, b, -15, -5);
                Closer(b, a, -15, -5);
            }
            try { Talked?.Invoke(vm, this, a, b, agreed); } catch (Exception e) { Console.WriteLine("[SimsRedux] attraction: " + e.Message); }
            return true;
        }

        /// <summary>Daily and lifetime relationship up or down (the game's own numbers, -100 to 100).</summary>
        private static void Closer(VMAvatar from, VMAvatar to, int daily, int life)
        {
            var r = ReduxWooHoo.Rel(from, to);
            if (r == null) return;
            while (r.Count < 3) r.Add(0);
            r[0] = (short)Math.Max(-100, Math.Min(100, r[0] + daily));
            r[2] = (short)Math.Max(-100, Math.Min(100, r[2] + life));
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!FromNet) return true; // (this game runs the lot)
            // a visiting player: only their own Sim
            if (caller == null || caller.IsPet) return false;
            if (Mode == AttrMode.SetOverride) return A == caller.ObjectID;
            if (Mode == AttrMode.Ask) return A == caller.ObjectID;
            if (Mode == AttrMode.Answer) return B == caller.ObjectID;
            return false;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(A);
            writer.Write(B);
            writer.Write(Value);
            writer.Write(Jealousy);
            writer.Write(Wanted);
            writer.Write(On);
            writer.Write(Yes);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (AttrMode)reader.ReadByte();
            A = reader.ReadInt16();
            B = reader.ReadInt16();
            Value = reader.ReadInt16();
            Jealousy = reader.ReadByte();
            Wanted = reader.ReadByte();
            On = reader.ReadBoolean();
            Yes = reader.ReadBoolean();
        }
    }
}
