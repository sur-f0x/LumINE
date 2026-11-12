using System;
using System.Collections.Generic;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public static class ReactionProtocol
    {
        public static List<ThriftField> BuildArgs(int sequence, string messageId, int type)
        {
            long id;
            if (!long.TryParse(messageId, out id) || id <= 0) throw new ArgumentException("リアクションの対象メッセージIDが不正です。");
            if (type != 0 && (type < 2 || type > 7)) throw new ArgumentOutOfRangeException("type");
            var request = new List<ThriftField> { new ThriftField(ThriftTypes.I32, 1, sequence), new ThriftField(ThriftTypes.I64, 2, id) };
            if (type != 0) request.Add(new ThriftField(ThriftTypes.Struct, 3, new List<ThriftField> { new ThriftField(ThriftTypes.I32, 1, type) }));
            return new List<ThriftField> { new ThriftField(ThriftTypes.Struct, 1, request) };
        }

        public static List<MessageReaction> Read(object raw)
        {
            var bySender = new Dictionary<string, MessageReaction>(StringComparer.Ordinal);
            var list = raw as List<object>;
            if (list != null) foreach (var entry in list)
            {
                var fields = entry as Dictionary<int, object>;
                object sender, time, rawType, predefined;
                if (fields == null || !fields.TryGetValue(1, out sender) || sender == null || !fields.TryGetValue(3, out rawType)) continue;
                var typeFields = rawType as Dictionary<int, object>;
                int type;
                if (typeFields == null || !typeFields.TryGetValue(1, out predefined) || predefined == null || !int.TryParse(predefined.ToString(), out type) || type < 2 || type > 7) continue;
                long at = 0;
                if (fields.TryGetValue(2, out time) && time != null) long.TryParse(time.ToString(), out at);
                string mid = sender.ToString();
                if (string.IsNullOrEmpty(mid)) continue;
                MessageReaction previous;
                if (!bySender.TryGetValue(mid, out previous) || previous.AtMillis <= at) bySender[mid] = new MessageReaction { FromMid = mid, AtMillis = at, Type = type };
            }
            return new List<MessageReaction>(bySender.Values);
        }
    }
}
