using System;
using System.Collections.Generic;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// NestedArray-compatible thrift builders (vyline stack/base/thrift/readwrite/struct.ts).
    /// Format: List&lt;ThriftField&gt; where each field is [type, fieldId, value].
    /// </summary>
    public static class ThriftStructs
    {
        // ── Auth ──────────────────────────────────────────────

        /// refresh_args → [[12, 1, RefreshAccessTokenRequest{1: refreshToken}]]</summary>
        public static List<ThriftField> RefreshArgs(string refreshToken)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Struct, 1, new List<ThriftField>
                {
                    new ThriftField(ThriftTypes.String, 1, refreshToken)
                })
            };
        }

        // ── Talk ──────────────────────────────────────────────

        /// sync_args → request SyncRequest</summary>
        public static List<ThriftField> SyncArgs(long lastRevision, int count, long lastGlobalRevision, long lastIndividualRevision)
        {
            var request = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I64, 1, lastRevision),
                new ThriftField(ThriftTypes.I32, 2, count),
                new ThriftField(ThriftTypes.I64, 3, lastGlobalRevision),
                new ThriftField(ThriftTypes.I64, 4, lastIndividualRevision)
            };
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Struct, 1, request)
            };
        }

        /// sendMessage_args → seq + Message</summary>
        public static List<ThriftField> SendMessageArgs(long seq, List<ThriftField> message)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 1, unchecked((int)seq)),
                new ThriftField(ThriftTypes.Struct, 2, message)
            };
        }

        /// Message struct (vyline Message builder field IDs).</summary>
        public static List<ThriftField> Message(
            string to,
            long createdTime, long deliveredTime,
            string text, int contentType,
            Dictionary<string, string> contentMetadata,
            List<byte[]> chunks,
            string relatedMessageId, byte sessionId)
        {
            var fields = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 2, to ?? ""),
                new ThriftField(ThriftTypes.I64, 5, createdTime),
                new ThriftField(ThriftTypes.I64, 6, deliveredTime)
            };
            if (!string.IsNullOrEmpty(text))
                fields.Add(new ThriftField(ThriftTypes.String, 10, text));
            fields.Add(new ThriftField(ThriftTypes.Bool, 14, false));
            fields.Add(new ThriftField(ThriftTypes.I32, 15, contentType));
            if (contentMetadata != null && contentMetadata.Count > 0)
            {
                var map = new Dictionary<object, object>();
                foreach (var kv in contentMetadata) map[kv.Key] = kv.Value;
                fields.Add(new ThriftField(ThriftTypes.Map, 18,
                    new ThriftMapValue(ThriftTypes.String, ThriftTypes.String, map)));
            }
            else
            {
                fields.Add(new ThriftField(ThriftTypes.Map, 18,
                    new ThriftMapValue(ThriftTypes.String, ThriftTypes.String, new Dictionary<object, object>())));
            }
            fields.Add(new ThriftField(ThriftTypes.Byte, 19, sessionId));
            if (chunks != null && chunks.Count > 0)
            {
                var list = new List<object>();
                foreach (var c in chunks) list.Add(c);
                fields.Add(new ThriftField(ThriftTypes.List, 20,
                    new ThriftListValue(ThriftTypes.String, list)));
            }
            if (!string.IsNullOrEmpty(relatedMessageId))
            {
                fields.Add(new ThriftField(ThriftTypes.String, 21, relatedMessageId));
                fields.Add(new ThriftField(ThriftTypes.I32, 22, 3));
                fields.Add(new ThriftField(ThriftTypes.I32, 24, 1));
            }
            return fields;
        }

        /// sendChatChecked_args</summary>
        public static List<ThriftField> SendChatCheckedArgs(int seq, string chatMid, string lastMessageId, byte sessionId)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 1, seq),
                new ThriftField(ThriftTypes.String, 2, chatMid),
                new ThriftField(ThriftTypes.String, 3, lastMessageId),
                new ThriftField(ThriftTypes.Byte, 4, sessionId)
            };
        }

        /// getMessageBoxes_args</summary>
        public static List<ThriftField> GetMessageBoxesArgs(
            string minChatId, string maxChatId, bool activeOnly,
            int messageBoxCountLimit, bool withUnreadCount,
            int lastMessagesPerMessageBoxCount, bool unreadOnly, long lastOpRevision)
        {
            var request = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 1, minChatId ?? ""),
                new ThriftField(ThriftTypes.String, 2, maxChatId ?? ""),
                new ThriftField(ThriftTypes.Bool, 3, activeOnly),
                new ThriftField(ThriftTypes.I32, 4, messageBoxCountLimit),
                new ThriftField(ThriftTypes.Bool, 5, withUnreadCount),
                new ThriftField(ThriftTypes.I32, 6, lastMessagesPerMessageBoxCount),
                new ThriftField(ThriftTypes.Bool, 7, unreadOnly),
                new ThriftField(ThriftTypes.I64, 8, lastOpRevision)
            };
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Struct, 2, request),
                new ThriftField(ThriftTypes.I32, 3, 0) // syncReason
            };
        }

        /// getProfile_args (syncReason only)</summary>
        public static List<ThriftField> GetProfileArgs(int syncReason)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 1, syncReason)
            };
        }

        /// getPreviousMessagesV2WithRequest_args</summary>
        public static List<ThriftField> GetPreviousMessagesV2Args(
            string messageBoxId, string endMessageId, int messagesCount,
            bool withReadCount, bool receivedOnly, long lastOpRevision, int syncReason)
        {
            var end = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 1, endMessageId ?? "")
            };
            var request = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 1, messageBoxId),
                new ThriftField(ThriftTypes.Struct, 2, end),
                new ThriftField(ThriftTypes.I32, 3, messagesCount),
                new ThriftField(ThriftTypes.Bool, 4, withReadCount),
                new ThriftField(ThriftTypes.Bool, 5, receivedOnly),
                new ThriftField(ThriftTypes.I64, 6, lastOpRevision)
            };
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Struct, 2, request),
                new ThriftField(ThriftTypes.I32, 3, syncReason)
            };
        }

        /// getMessageReadRange_args</summary>
        public static List<ThriftField> GetMessageReadRangeArgs(List<string> chatIds, int syncReason)
        {
            var list = new List<object>();
            if (chatIds != null) foreach (var id in chatIds) list.Add(id);
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.List, 2, new ThriftListValue(ThriftTypes.String, list)),
                new ThriftField(ThriftTypes.I32, 3, syncReason)
            };
        }

        /// getE2EEPublicKey_args</summary>
        public static List<ThriftField> GetE2eePublicKeyArgs(string mid, int keyVersion, int keyId)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 2, mid),
                new ThriftField(ThriftTypes.I32, 3, keyVersion),
                new ThriftField(ThriftTypes.I32, 4, keyId)
            };
        }

        /// negotiateE2EEPublicKey_args</summary>
        public static List<ThriftField> NegotiateE2eePublicKeyArgs(string mid)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 2, mid)
            };
        }

        /// registerE2EEPublicKey_args → publicKey struct</summary>
        public static List<ThriftField> RegisterE2eePublicKeyArgs(int version, int keyId, byte[] keyData, long createdTime)
        {
            var publicKey = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 1, version),
                new ThriftField(ThriftTypes.I32, 2, keyId),
                new ThriftField(ThriftTypes.String, 4, keyData ?? new byte[0]),
                new ThriftField(ThriftTypes.I64, 5, createdTime)
            };
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Struct, 2, publicKey)
            };
        }

        /// getLastE2EEGroupSharedKey_args</summary>
        public static List<ThriftField> GetLastE2eeGroupSharedKeyArgs(int keyVersion, string chatMid)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 2, keyVersion),
                new ThriftField(ThriftTypes.String, 3, chatMid)
            };
        }

        /// registerE2EEGroupKey_args</summary>
        public static List<ThriftField> RegisterE2eeGroupKeyArgs(
            int keyVersion, string chatMid, List<string> members,
            List<int> keyIds, List<byte[]> encryptedSharedKeys)
        {
            var memberList = new List<object>();
            if (members != null) foreach (var m in members) memberList.Add(m);
            var keyIdList = new List<object>();
            if (keyIds != null) foreach (var k in keyIds) keyIdList.Add(k);
            var encList = new List<object>();
            if (encryptedSharedKeys != null) foreach (var e in encryptedSharedKeys) encList.Add(e);
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 2, keyVersion),
                new ThriftField(ThriftTypes.String, 3, chatMid),
                new ThriftField(ThriftTypes.List, 4, new ThriftListValue(ThriftTypes.String, memberList)),
                new ThriftField(ThriftTypes.List, 5, new ThriftListValue(ThriftTypes.I32, keyIdList)),
                new ThriftField(ThriftTypes.List, 6, new ThriftListValue(ThriftTypes.String, encList))
            };
        }

        /// unsendMessage_args</summary>
        public static List<ThriftField> UnsendMessageArgs(int seq, string messageId)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 1, seq),
                new ThriftField(ThriftTypes.String, 2, messageId)
            };
        }

        /// getContacts: [[15, 2, [11, mids]]]</summary>
        public static List<ThriftField> GetContactsArgs(List<string> mids)
        {
            var list = new List<object>();
            if (mids != null) foreach (var m in mids) list.Add(m);
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.List, 2, new ThriftListValue(ThriftTypes.String, list))
            };
        }

        /// getContact: [[11, 2, mid]]</summary>
        public static List<ThriftField> GetContactArgs(string mid)
        {
            return new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 2, mid)
            };
        }

        /// noop</summary>
        public static List<ThriftField> NoopArgs()
        {
            return new List<ThriftField>();
        }
    }
}
