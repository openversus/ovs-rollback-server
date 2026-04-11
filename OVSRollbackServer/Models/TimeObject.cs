using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OVS.Rollback.Interfaces;

namespace OVS.Rollback.Models
{
    [Serializable]
    public class TimeObject : ITimeObject
    {
        private DateTimeOffset _timestamp;
        public DateTime RawTimestamp
        {
            get {
                if (null == _timestamp)
                {
                    _timestamp = DateTimeOffset.Now;
                }
                return _timestamp.DateTime;
            }
            private set { _timestamp = value; }
        }

        public int Year => Int32.Parse(RawTimestamp.ToString("yyyy"));

        public int Month => Int32.Parse(RawTimestamp.ToString("MM"));

        public int Day => Int32.Parse(RawTimestamp.ToString("dd"));

        public int Hour => Int32.Parse(RawTimestamp.ToString("HH"));

        public int Minute => Int32.Parse(RawTimestamp.ToString("mm"));

        public int Second => Int32.Parse(RawTimestamp.ToString("ss"));

        public int Millisecond => Int32.Parse(RawTimestamp.ToString("fff"));

        public DateTime UTCTimestamp => RawTimestamp.ToUniversalTime();
        public string UTCTimestampAsString => UTCTimestamp.ToString();

        public long UnixTimestamp => _timestamp.ToUnixTimeMilliseconds();
        public string UnixTimestampAsString => UnixTimestamp.ToString();

        public TimeObject()
        {
            _timestamp = DateTimeOffset.Now;
        }

        public TimeObject(DateTimeOffset timeStamp)
        {
            _timestamp = timeStamp;
        }

        public TimeObject(DateTime timeStamp)
        {
            _timestamp = new DateTimeOffset(timeStamp);
        }

        public TimeObject(string timeStamp)
        {
            try
            {
                DateTime.TryParse(timeStamp, out DateTime parsedTimestamp);
                _timestamp = parsedTimestamp;
            }
            catch
            {
                _timestamp = DateTimeOffset.MinValue;
            }
        }

        public string ToISO8601()
        {
            return RawTimestamp.ToString("o");
        }

        public string ToISO8601(DateTimeOffset? dateTimeOffset)
        {
            return dateTimeOffset?.DateTime.ToString("o") ?? string.Empty;
        }

        public string ToISO8601(DateTime? dateTime)
        {
            return dateTime?.ToString("o") ?? string.Empty;
        }

        public string ToJSON()
        {
            JsonSerializerOptions options = new() {
                WriteIndented = true,
                IncludeFields = true,
                IndentSize = 4,
                MaxDepth = 10,
                NewLine = "\n",
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            return Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(this, options));
        }

        new public string ToString()
        {
            return RawTimestamp.ToString() ?? string.Empty;
        }

        public string ToFormat(string format)
        {
            return RawTimestamp.ToString(format);
        }
    }
}
