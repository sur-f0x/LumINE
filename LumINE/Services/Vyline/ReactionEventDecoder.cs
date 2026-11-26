using System;
using Windows.Data.Json;

namespace LumINE.Services.Vyline
{
    public sealed class ReactionEventInfo
    {
        public string ChatMid { get; set; }
        public string ActorMid { get; set; }
        public int? Type { get; set; }
        public LumINE.Models.MessageReaction CustomReaction { get; set; }
    }

    public static class ReactionEventDecoder
    {
        public static ReactionEventInfo Parse(string operationType, string payload, string actor, string myMid)
        {
            var result = new ReactionEventInfo { ActorMid = operationType == "SEND_REACTION" || operationType == "139" ? myMid : actor };
            if (!string.IsNullOrEmpty(payload) && (payload.StartsWith("u") || payload.StartsWith("c") || payload.StartsWith("r"))) result.ChatMid = payload;
            else
            {
                JsonObject json;
                if (!JsonObject.TryParse(payload ?? "", out json)) return result;
                result.ChatMid = json.GetNamedString("chatMid", "");
                if (json.ContainsKey("curr"))
                {
                    var current = json.GetNamedValue("curr");
                    if (current.ValueType == JsonValueType.Null) result.Type = 0;
                    else if (current.ValueType == JsonValueType.Object)
                    {
                        IJsonValue raw;
                        int type;
                        if (current.GetObject().TryGetValue("predefinedReactionType", out raw) &&
                            int.TryParse(raw.ValueType == JsonValueType.String ? raw.GetString() : raw.Stringify(), out type) && type >= 2 && type <= 7) result.Type = type;
                        else if (current.GetObject().TryGetValue("paidReactionType", out raw) && raw.ValueType == JsonValueType.Object)
                        {
                            var paid = raw.GetObject();
                            var fields = new System.Collections.Generic.Dictionary<int, object>();
                            int field = 1;
                            foreach (string name in new[] { "productId", "emojiId", "resourceType", "version" })
                            {
                                IJsonValue value;
                                if (paid.TryGetValue(name, out value)) fields[field] = value.ValueType == JsonValueType.String ? value.GetString() : value.Stringify();
                                field++;
                            }
                            result.CustomReaction = ReactionProtocol.ReadType(new System.Collections.Generic.Dictionary<int, object> { { 2, fields } });
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(result.ActorMid) && !string.IsNullOrEmpty(result.ChatMid) && result.ChatMid.StartsWith("u") && result.ChatMid != myMid) result.ActorMid = result.ChatMid;
            return result;
        }
    }
}
