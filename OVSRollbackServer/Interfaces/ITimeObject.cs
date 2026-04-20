using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Interfaces
{
    public interface ITimeObject
    {
        public DateTime RawTimestamp { get; }
        public int Year { get; }
        public int Month { get; }
        public int Day { get; }
        public int Hour { get; }
        public int Minute { get; }
        public int Second { get; }
        public int Millisecond { get; }
        public DateTime UTCTimestamp { get; }
        public long UnixTimestamp { get; }
        public string ToISO8601();
        public string ToString();
        public string ToJSON();
        public string ToFormat(string format);
    }
}
