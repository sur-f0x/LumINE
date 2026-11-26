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
            return BuildArgs(sequence, messageId, type == 0 ? null : new MessageReaction { Type = type });
        }

        public static List<ThriftField> BuildArgs(int sequence, string messageId, MessageReaction reaction)
        {
            long id;
            if (!long.TryParse(messageId, out id) || id <= 0) throw new ArgumentException("リアクションの対象メッセージIDが不正です。");
            var request = new List<ThriftField> { new ThriftField(ThriftTypes.I32, 1, sequence), new ThriftField(ThriftTypes.I64, 2, id) };
            if (reaction != null)
            {
                var type = new List<ThriftField>();
                if (reaction.IsCustom)
                {
                    if (!ValidId(reaction.ProductId) || !ValidId(reaction.EmojiId) || reaction.ResourceType < 0 || reaction.Version < 0) throw new ArgumentException("絵文字のリアクション情報が不正です。");
                    type.Add(new ThriftField(ThriftTypes.Struct, 2, new List<ThriftField> {
                        new ThriftField(ThriftTypes.String, 1, reaction.ProductId), new ThriftField(ThriftTypes.String, 2, reaction.EmojiId),
                        new ThriftField(ThriftTypes.I32, 3, reaction.ResourceType), new ThriftField(ThriftTypes.I64, 4, reaction.Version)
                    }));
                }
                else
                {
                    if (reaction.Type < 2 || reaction.Type > 7) throw new ArgumentOutOfRangeException("reaction");
                    type.Add(new ThriftField(ThriftTypes.I32, 1, reaction.Type));
                }
                request.Add(new ThriftField(ThriftTypes.Struct, 3, type));
            }
            return new List<ThriftField> { new ThriftField(ThriftTypes.Struct, 1, request) };
        }

        private static bool ValidId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
            return true;
        }

        public static MessageReaction ReadType(Dictionary<int, object> fields)
        {
            if (fields == null) return null;
            object raw;
            if (fields.TryGetValue(2, out raw))
            {
                var paid = raw as Dictionary<int, object>;
                object product, emoji, resource, version;
                if (paid == null || !paid.TryGetValue(1, out product) || !paid.TryGetValue(2, out emoji) || product == null || emoji == null || !ValidId(product.ToString()) || !ValidId(emoji.ToString())) return null;
                int resourceType; long ver;
                var reaction = new MessageReaction { ProductId = product.ToString(), EmojiId = emoji.ToString() };
                if (paid.TryGetValue(3, out resource) && resource != null && int.TryParse(resource.ToString(), out resourceType)) reaction.ResourceType = resourceType;
                if (paid.TryGetValue(4, out version) && version != null && long.TryParse(version.ToString(), out ver)) reaction.Version = ver;
                return reaction;
            }
            int type;
            return fields.TryGetValue(1, out raw) && raw != null && int.TryParse(raw.ToString(), out type) && type >= 2 && type <= 7 ? new MessageReaction { Type = type } : null;
        }

        public static List<MessageReaction> Read(object raw)
        {
            var bySender = new Dictionary<string, MessageReaction>(StringComparer.Ordinal);
            var list = raw as List<object>;
            if (list != null) foreach (var entry in list)
            {
                var fields = entry as Dictionary<int, object>;
                object sender, time, rawType;
                if (fields == null || !fields.TryGetValue(1, out sender) || sender == null || !fields.TryGetValue(3, out rawType)) continue;
                var typeFields = rawType as Dictionary<int, object>;
                var reaction = ReadType(typeFields);
                if (reaction == null) continue;
                long at = 0;
                if (fields.TryGetValue(2, out time) && time != null) long.TryParse(time.ToString(), out at);
                string mid = sender.ToString();
                if (string.IsNullOrEmpty(mid)) continue;
                MessageReaction previous;
                reaction.FromMid = mid; reaction.AtMillis = at;
                if (!bySender.TryGetValue(mid, out previous) || previous.AtMillis <= at) bySender[mid] = reaction;
            }
            return new List<MessageReaction>(bySender.Values);
        }
    }
}
