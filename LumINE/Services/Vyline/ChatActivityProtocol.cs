using System;
using System.Collections.Generic;

namespace LumINE.Services.Vyline
{
    public static class ChatActivityProtocol
    {
        public static long Timestamp(long value) { return value > 0 && value < 100000000000L ? value * 1000L : value; }
        public static int Compare(long leftTime, string leftId, long rightTime, string rightId)
        {
            int time = Timestamp(leftTime).CompareTo(Timestamp(rightTime));
            if (time != 0) return time;
            return CompareIds(leftId, rightId);
        }
        public static int CompareIds(string left, string right)
        {
            bool ln = IsDigits(left), rn = IsDigits(right);
            if (ln != rn) return ln ? -1 : 1;
            if (ln)
            {
                string a = left.TrimStart('0'), b = right.TrimStart('0');
                int size = a.Length.CompareTo(b.Length);
                if (size != 0) return size;
                int digits = string.CompareOrdinal(a, b);
                if (digits != 0) return digits;
            }
            return string.CompareOrdinal(left, right);
        }
        private static bool IsDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }
        public static Dictionary<int, object> LatestMessage(Dictionary<int, object> box)
        {
            Dictionary<int, object> latest = null;
            long latestTime = -1;
            // lastMessages has different field IDs in older MessageBox and
            // ExtendedMessageBox schemas. Match the Message structure itself.
            foreach (var value in box.Values)
            {
                var list = value as List<object>;
                if (list == null) continue;
                foreach (var raw in list)
                {
                    var fields = raw as Dictionary<int, object>;
                    object id, from, to, created;
                    if (fields == null || !fields.TryGetValue(4, out id) || id == null || !fields.TryGetValue(1, out from) ||
                        !(from is string) || !fields.TryGetValue(2, out to) || !(to is string)) continue;
                    long time = 0;
                    if (fields.TryGetValue(5, out created) && created != null) long.TryParse(created.ToString(), out time);
                    time = Timestamp(time);
                    if (latest == null || Compare(time, id.ToString(), latestTime, latest[4].ToString()) > 0) { latest = fields; latestTime = time; }
                }
            }
            return latest;
        }
    }
}
