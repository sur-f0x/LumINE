using System;
using System.Collections.Generic;
using LumINE.Models;

namespace LumINE.Services.Vyline
{
    public static class MessageTimeline
    {
        public static int Compare(Message left, Message right)
        {
            return ChatActivityProtocol.Compare(left.CreatedTime ?? 0, left.Id, right.CreatedTime ?? 0, right.Id);
        }

        // Cache, history pages and UI all use this ordering. Never sort by
        // arrival/completion order, current culture, or a truncated numeric ID.
        public static List<Message> Normalize(IEnumerable<Message> messages)
        {
            var unique = new Dictionary<string, Message>(StringComparer.Ordinal);
            foreach (var message in messages)
                if (message != null && !message.IsDateSeparator && !string.IsNullOrEmpty(message.Id)) unique[message.Id] = message;
            var result = new List<Message>(unique.Values);
            result.Sort(Compare);
            return result;
        }
    }
}
