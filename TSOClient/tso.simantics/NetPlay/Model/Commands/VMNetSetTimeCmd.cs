using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetSetTimeCmd : VMNetCommandBodyAbstract
    {
        public int Hours;
        public int Minutes;
        public int Seconds;
        public long UTCStart;
        /// <summary>SimsRedux: the day too (0 = leave the day as it is).</summary>
        public int DayOfMonth;
        /// <summary>SimsRedux real-time mode: the month and year too (0 = leave them as they are).</summary>
        public int Month;
        public int Year;
        /// <summary>
        /// SimsRedux real-time mode: how many ticks make a game minute (0 = leave it as it is). Sims 1 normally has
        /// 30 (a game minute each second at speed 1); real-time mode uses 1800 (a game minute each real minute).
        /// </summary>
        public int TicksPerMinute;
        /// <summary>
        /// SimsRedux real-time mode: the clock jumped (to the real time). Each Sim's "last day at work" is kept
        /// right, so a car pool the jump went past isn't counted as a missed day (Utils/ReduxClockJump.cs).
        /// </summary>
        public bool ReduxJump;

        public override bool Execute(VM vm)
        {
            var clock = vm.Context.Clock;
            int oldAbs = FSO.SimAntics.Utils.ReduxClockJump.Abs(clock), oldMinute = clock.Hours * 60 + clock.Minutes;
            if (TicksPerMinute > 0) clock.TicksPerMinute = TicksPerMinute;
            clock.Hours = Hours;
            clock.Minutes = Minutes;
            clock.MinuteFractions = (Seconds * clock.TicksPerMinute) / 60;
            clock.UTCStart = UTCStart;
            clock.Ticks = 0;
            if (DayOfMonth > 0) clock.DayOfMonth = DayOfMonth;
            if (Month > 0) clock.Month = Month;
            if (Year > 0) clock.Year = Year;
            if (ReduxJump) FSO.SimAntics.Utils.ReduxClockJump.After(vm, oldAbs, oldMinute);
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            return !FromNet;
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Hours = reader.ReadInt32();
            Minutes = reader.ReadInt32();
            Seconds = reader.ReadInt32();
            UTCStart = reader.ReadInt64();
            DayOfMonth = reader.ReadInt32();
            Month = reader.ReadInt32();
            Year = reader.ReadInt32();
            TicksPerMinute = reader.ReadInt32();
            ReduxJump = reader.ReadBoolean();
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Hours);
            writer.Write(Minutes);
            writer.Write(Seconds);
            writer.Write(UTCStart);
            writer.Write(DayOfMonth);
            writer.Write(Month);
            writer.Write(Year);
            writer.Write(TicksPerMinute);
            writer.Write(ReduxJump);
        }
    }
}
