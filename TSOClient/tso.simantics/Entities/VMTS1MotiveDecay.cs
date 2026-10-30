using FSO.SimAntics.Model;
using System;
using System.IO;

namespace FSO.SimAntics.Entities
{
    public class VMTS1MotiveDecay : VMIMotiveDecay
    {
        // SimsRedux: "Slower Motives" (Mod The Sims) built in - per hour, awake/asleep:
        //   Energy 11.25 -> 10 used, 22.5 -> 30 regained asleep (17, 18)  · Hygiene 5.1/2.4 -> 3/1.5
        //   Bladder 9 / 6 per hour, and eating fills it 10% of the food (was 30%)
        //   Comfort active/normal/lazy 12/15/18 -> 6/9/12
        // (the numbers below are per 2 game minutes: per hour / 30)
        public static float[] Constants = new float[]
        {
            180, //energy span 0
            16, //wake hours 1
            7, //wake hour 2
            0.01f, //energy drift 3
            0.1f, //hunger to bladder 4 (was 0.3)
            0.0021f, //hunger decrement ratio 5
            0.055f, //social decrement base 6
            0.000125f, //social decrement multiplier 7
            0.25f, //ent dec awake 8
            1f, //ent mult asleep 9
            0.1f, //hyg decrement awake 10 (was 0.17)
            0.05f, //hyg decrement asleep 11 (was 0.08)
            0.3f, //blad decrement awake 12
            0.2f, //blad decrement asleep 13 (was 0.15)
            0.2f, //comfort decrement active 14 (was 0.4)
            0.4f, //comfort decrement lazy 15 (was 0.6)
            0.3f, //comfort decrement 16 (was 0.5)
            160, //energy span used while awake 17 (was the 180 above: 10 an hour instead of 11.25)
            240, //energy span regained while asleep 18 (was the 180 above: 30 an hour instead of 22.5)
        };

        public static VMMotive[] DecrementMotives = new VMMotive[]
        {
            VMMotive.Hunger,
            VMMotive.Comfort,
            VMMotive.Hygiene,
            VMMotive.Bladder,
            VMMotive.Energy,
            VMMotive.Fun,
            VMMotive.Social
        };

        public short[] MotiveFractions = new short[7]; //in 1/1000ths
        public int LastMinute;

        public void Tick(VMAvatar avatar, VMContext context)
        {
            var roomScore = context.GetRoomScore(context.GetRoomAt(avatar.Position));
            avatar.SetMotiveData(VMMotive.Room, roomScore);
            var minutes = context.Clock.Minutes;
            if (minutes/2 == LastMinute || avatar.GetValue(VMStackObjectVariable.Hidden) > 0) return;
            var hours = context.Clock.Hours;
            LastMinute = minutes/2;
            var sleeping = (avatar.GetMotiveData(VMMotive.SleepState) != 0);
            int moodSum = 0;

            for (int i = 0; i < 7; i++)
            {
                int frac = 0;
                var dm = DecrementMotives[i];
                if (avatar.HasMotiveChange(dm) && dm != VMMotive.Energy) continue;
                var motive = avatar.GetMotiveData(dm);
                var r_Hunger = ToFixed1000(Constants[5]) * (100 + avatar.GetMotiveData(VMMotive.Hunger));
                switch (i)
                {
                    case 0:
                        frac = r_Hunger; break;
                    case 1:
                        var active = avatar.GetPersonData(VMPersonDataVariable.ActivePersonality);
                        if (active > 666)
                            frac = ToFixed1000(Constants[14]);
                        else if (active < 666)
                            frac = ToFixed1000(Constants[15]);
                        else
                            frac = ToFixed1000(Constants[16]);
                        break;
                    case 2:
                        frac = ToFixed1000(Constants[sleeping ? 11 : 10]); break;
                    case 3:
                        frac = ToFixed1000(Constants[sleeping ? 13 : 12]) + FracMul(r_Hunger, ToFixed1000(Constants[4])); break;
                    case 4:
                        //try to wake the sim up if they're asleep on the wake hour, and they're well rested.
                        if (sleeping && hours == Constants[2] && motive >= Constants[0] - 100)
                        {
                            avatar.SetMotiveData(VMMotive.SleepState, 0);
                        }

                        if (sleeping)
                        {
                            if (avatar.GetMotiveData(VMMotive.SleepState) == -1)
                            {
                                frac = -643 * 2; 
                            }
                            else
                            {
                                frac = (context.Clock.Hours >= Constants[2]) ? ToFixed1000(Constants[3]) : 0;
                                // SimsRedux: a sofa nap. Sims 1's sofas set the nap's energy change to "no rise, up to 80" and
                                // end the nap only once energy is above 0 - the rise itself came from being asleep. With
                                // nothing here, a Sim who lay down tired napped for ever with energy flat (the 19.2 soak
                                // test's "STUCK:family:Nap": 6+ hours at energy -40). Asleep with no energy rise of the
                                // object's own (a bed's or recliner's), energy comes back at the asleep rate for how deep the
                                // sleep is (a nap's -20 of a bed's -40: half a cheap bed, 15 an hour), up to that level.
                                var sleepDepth = Math.Min(40, -(int)avatar.GetMotiveData(VMMotive.SleepState));
                                short napMax;
                                var napRise = avatar.ReduxMotiveChangeRate(VMMotive.Energy, out napMax);
                                if (!avatar.IsPet && sleepDepth > 0 && napRise <= 0)
                                {
                                    int cap = (napMax > 0 && napMax < 100) ? napMax : 100;
                                    if (motive < cap)
                                        frac = -(ToFixed1000(Constants[18]) / (30 * (24 - (int)Constants[1]))) * sleepDepth / 40;
                                }
                            }
                        } else
                        {
                            frac = (ToFixed1000(Constants[17]) / (30 * (int)Constants[1]));
                        }
                        //energy span over wake hours. small energy drift applied if asleep during the day.
                       
                        break;
                    case 5:
                        frac = (sleeping)?0:ToFixed1000(Constants[8]);
                        break;
                    case 6:
                        frac = ToFixed1000(Constants[6]) +
                            ToFixed1000(Constants[7]) * avatar.GetPersonData(VMPersonDataVariable.OutgoingPersonality);
                        break;
                }

                frac = FSO.SimAntics.Utils.ReduxPerkRules.Decay(avatar, context, frac); // SimsRedux Level Rewards: needs drop slower with a perk
                MotiveFractions[i] += (short)frac;
                if (MotiveFractions[i] >= 1000)
                {
                    motive -= (short)(MotiveFractions[i] / 1000);
                    MotiveFractions[i] %= 1000;
                    if (motive < -100) motive = -100;
                    avatar.SetMotiveData(DecrementMotives[i], motive);
                }
                if (MotiveFractions[i] < 0)
                {
                    motive += (short)(1 - MotiveFractions[i] / 1000);
                    MotiveFractions[i] += (short)((1 - MotiveFractions[i]/1000) * 1000);
                    avatar.SetMotiveData(DecrementMotives[i], motive);
                }
                moodSum += motive;
            }
            moodSum += roomScore;

            avatar.SetMotiveData(VMMotive.Mood, (short)(moodSum / 8));
        }

        public int ToFixed1000(float input)
        {
            return (int)(input * 1000);
        }

        public int FracMul(int input, int frac)
        {
            return (int)((long)input * frac) / 1000;
        }

        public void SerializeInto(BinaryWriter writer)
        {
            writer.Write(LastMinute);
            for (int i = 0; i < 7; i++)
                writer.Write(MotiveFractions[i]);
        }

        public void Deserialize(BinaryReader reader)
        {
            LastMinute = reader.ReadInt32();
            for (int i = 0; i < 7; i++)
                MotiveFractions[i] = reader.ReadInt16();
        }
    }
}
