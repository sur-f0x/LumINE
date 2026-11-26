using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LumINE.Utils;
using LumINE.Models;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// TalkService port (vyline stack/base/service/talk/mod.ts).
    /// protocolType=4, requestPath=/S4, sync=/SYNC4.
    /// </summary>
    public class TalkService
    {
        public const string RequestPath = "/S4";
        public const string SyncPath = "/SYNC4";

        private readonly RequestClient _request;
        private readonly Func<int> _getReqseq;
        private string _obsAccessToken;

        public TalkService(RequestClient request, Func<int> getReqseq)
        {
            _request = request;
            _getReqseq = getReqseq;
        }

        /// <summary>sync — long-poll operations (default timeout 60s).</summary>
        public async Task<Dictionary<int, object>> SyncAsync(
            long revision, long globalRev, long individualRev,
            int limit = 100, int timeoutMs = 60000,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var args = ThriftStructs.SyncArgs(revision, limit, globalRev, individualRev);
            var result = await _request.RequestCoreAsync(SyncPath, args, "sync", null, false, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<Dictionary<int, object>> GetProfileAsync(int syncReason = 0)
        {
            var result = await _request.RequestCoreAsync(RequestPath, ThriftStructs.GetProfileArgs(syncReason), "getProfile", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<Dictionary<int, object>> SendMessageAsync(
            string to, string text, int contentType,
            Dictionary<string, string> contentMetadata,
            string relatedMessageId, long createdTime,
            List<byte[]> chunks = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            int seq = _getReqseq();
            var message = ThriftStructs.Message(
                to: to,
                createdTime: createdTime, deliveredTime: 0,
                // E2EE text exists only inside the encrypted chunk payload.
                text: chunks != null && chunks.Count > 0 ? null : text, contentType: contentType,
                contentMetadata: contentMetadata,
                chunks: chunks,
                relatedMessageId: relatedMessageId, sessionId: 0);
            var args = ThriftStructs.SendMessageArgs(seq, message);
            var result = await _request.RequestCoreAsync(RequestPath, args, "sendMessage", null, false, 15000, cancellationToken)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task SendChatCheckedAsync(string chatMid, string lastMessageId, CancellationToken cancellationToken = default(CancellationToken))
        {
            int seq = _getReqseq();
            var args = ThriftStructs.SendChatCheckedArgs(seq, chatMid, lastMessageId, 0);
            await _request.RequestCoreAsync(RequestPath, args, "sendChatChecked", null, false, 15000, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<Dictionary<int, object>> GetMessageBoxesAsync(
            string minChatId = "", string maxChatId = "", bool activeOnly = false,
            int messageBoxCountLimit = 50, bool withUnreadCount = true,
            int lastMessagesPerMessageBoxCount = 1, bool unreadOnly = false,
            long lastOpRevision = 0)
        {
            var args = ThriftStructs.GetMessageBoxesArgs(
                minChatId, maxChatId, activeOnly,
                messageBoxCountLimit, withUnreadCount,
                lastMessagesPerMessageBoxCount, unreadOnly, lastOpRevision);
            var result = await _request.RequestCoreAsync(RequestPath, args, "getMessageBoxes", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        /// <summary>Lists group/room MIDs; direct chats are represented by contact MIDs.</summary>
        public async Task<List<string>> GetChatMidsAsync(bool includePeers = true)
        {
            var request = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Bool, 1, true),
                new ThriftField(ThriftTypes.Bool, 2, true)
            };
            var args = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Struct, 1, request)
            };
            var result = await _request.RequestCoreAsync(RequestPath, args, "getAllChatMids", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            var response = result.Success as Dictionary<int, object>;
            var mids = new List<string>();
            if (response != null)
            {
                AddStringMids(response, 1, mids);
                AddStringMids(response, 2, mids);
            }

            if (includePeers)
            {
                // getAllContactIds returns a bare list, represented by field 0.
                var contacts = await _request.RequestCoreAsync(RequestPath, new List<ThriftField>(), "getAllContactIds", null, false, 15000, CancellationToken.None)
                    .ConfigureAwait(false);
                var contactMids = contacts.Success as List<object>;
                if (contactMids != null)
                    foreach (var item in contactMids)
                        if (item != null) mids.Add(item.ToString());
            }

            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mid in mids)
                if (!string.IsNullOrEmpty(mid) && seen.Add(mid)) unique.Add(mid);
            return unique;
        }

        public async Task<List<string>> GetAllContactIdsAsync()
        {
            var result = await _request.RequestCoreAsync(RequestPath,
                new List<ThriftField>(), "getAllContactIds", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            var raw = result.Success as List<object>;
            var mids = new List<string>();
            if (raw != null)
                foreach (var item in raw)
                    if (item != null) mids.Add(item.ToString());
            return mids;
        }

        /// <summary>Fetches chats in bounded batches, as LINE rejects oversized MID lists.</summary>
        public async Task<List<Chat>> GetChatsAsync(bool includePeers = true, int batchSize = 200, List<string> targetMids = null)
        {
            var mids = targetMids ?? await GetChatMidsAsync(includePeers).ConfigureAwait(false);
            var chats = new List<Chat>();
            if (mids.Count == 0) return chats;
            if (batchSize < 1) batchSize = 200;

            for (int offset = 0; offset < mids.Count; offset += batchSize)
            {
                var batch = mids.GetRange(offset, Math.Min(batchSize, mids.Count - offset));
                var list = new List<object>();
                foreach (var mid in batch) list.Add(mid);
                var request = new List<ThriftField>
                {
                    new ThriftField(ThriftTypes.List, 1, new ThriftListValue(ThriftTypes.String, list)),
                    new ThriftField(ThriftTypes.Bool, 2, true)
                };
                var args = new List<ThriftField>
                {
                    new ThriftField(ThriftTypes.Struct, 1, request)
                };
                var result = await _request.RequestCoreAsync(RequestPath, args, "getChats", null, false, 15000, CancellationToken.None)
                    .ConfigureAwait(false);
                var response = result.Success as Dictionary<int, object>;
                var rawChats = response != null && response.ContainsKey(1)
                    ? response[1] as List<object>
                    : null;
                if (rawChats == null) continue;

                foreach (var raw in rawChats)
                {
                    var fields = raw as Dictionary<int, object>;
                    if (fields == null) continue;
                    object value;
                    var chat = new Chat();
                    if (fields.TryGetValue(2, out value) && value != null) chat.ChatMid = value.ToString();
                    if (fields.TryGetValue(1, out value) && value != null)
                    {
                        int type;
                        if (int.TryParse(value.ToString(), out type)) chat.ChatType = type;
                    }
                    if (fields.TryGetValue(6, out value) && value != null) chat.ChatName = value.ToString();
                    if (fields.TryGetValue(7, out value) && value != null) chat.PicturePath = value.ToString();
                    object extraValue, groupValue, memberValue;
                    if (fields.TryGetValue(8, out extraValue) && extraValue is Dictionary<int, object> extra &&
                        extra.TryGetValue(1, out groupValue) && groupValue is Dictionary<int, object> group && group.TryGetValue(4, out memberValue))
                    {
                        var memberMap = memberValue as Dictionary<object, object>;
                        var memberList = memberValue as List<object>;
                        if (memberMap != null) chat.MemberCount = memberMap.Count;
                        else if (memberList != null) chat.MemberCount = memberList.Count;
                    }
                    if (fields.TryGetValue(4, out value) && value != null)
                    {
                        bool disabled;
                        if (bool.TryParse(value.ToString(), out disabled)) chat.NotificationDisabled = disabled;
                    }
                    if (!string.IsNullOrEmpty(chat.ChatMid)) chats.Add(chat);
                }
            }
            return chats;
        }

        /// <summary>Fetches group member lists to support member counts in the chat roster.</summary>
        public async Task<List<Dictionary<int, object>>> GetGroupsAsync(List<string> groupMids)
        {
            var groups = new List<Dictionary<int, object>>();
            if (groupMids == null || groupMids.Count == 0) return groups;
            const int batchSize = 100;
            for (int offset = 0; offset < groupMids.Count; offset += batchSize)
            {
                var values = new List<object>();
                foreach (var mid in groupMids.GetRange(offset, Math.Min(batchSize, groupMids.Count - offset))) values.Add(mid);
                var args = new List<ThriftField>
                {
                    new ThriftField(ThriftTypes.List, 2, new ThriftListValue(ThriftTypes.String, values))
                };
                var result = await _request.RequestCoreAsync(RequestPath, args, "getGroups", null, false, 15000, CancellationToken.None)
                    .ConfigureAwait(false);
                var list = result.Success as List<object>;
                if (list == null) continue;
                foreach (var item in list)
                {
                    var group = item as Dictionary<int, object>;
                    if (group != null) groups.Add(group);
                }
            }
            return groups;
        }

        private static void AddStringMids(Dictionary<int, object> response, int fieldId, List<string> mids)
        {
            object value;
            if (!response.TryGetValue(fieldId, out value)) return;
            var list = value as List<object>;
            if (list == null) return;
            foreach (var item in list)
                if (item != null) mids.Add(item.ToString());
        }

        public async Task<List<object>> GetPreviousMessagesV2Async(
            string messageBoxId, string endMessageId, int messagesCount,
            bool withReadCount = false, bool receivedOnly = false,
            long lastOpRevision = 0, int syncReason = 0)
        {
            var args = ThriftStructs.GetPreviousMessagesV2Args(
                messageBoxId, endMessageId, messagesCount,
                withReadCount, receivedOnly, lastOpRevision, syncReason);
            var result = await _request.RequestCoreAsync(RequestPath, args, "getPreviousMessagesV2WithRequest", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            var list = result.Success as List<object>;
            return list ?? new List<object>();
        }

        /// <summary>getRecentMessagesV2 — the supported recent-history endpoint.</summary>
        public async Task<List<object>> GetRecentMessagesV2Async(string chatMid, int count = 50)
        {
            var args = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 2, chatMid),
                new ThriftField(ThriftTypes.I32, 3, count)
            };
            var result = await _request.RequestCoreAsync(RequestPath, args, "getRecentMessagesV2", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as List<object> ?? new List<object>();
        }

        public async Task<List<object>> GetMessageReadRangeAsync(List<string> chatIds, int syncReason = 0)
        {
            var args = ThriftStructs.GetMessageReadRangeArgs(chatIds, syncReason);
            var result = await _request.RequestCoreAsync(RequestPath, args, "getMessageReadRange", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as List<object> ?? new List<object>();
        }

        public async Task<string> GetObsAccessTokenAsync()
        {
            if (!string.IsNullOrEmpty(_obsAccessToken)) return _obsAccessToken;
            var args = new List<ThriftField> { new ThriftField(ThriftTypes.I32, 2, 2) };
            var result = await _request.RequestCoreAsync(RequestPath, args, "acquireEncryptedAccessToken", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            var token = result.Success as string;
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("画像用アクセスキーを取得できませんでした。");
            var parts = token.Split('\u001e');
            _obsAccessToken = parts.Length > 1 ? parts[1] : token;
            return _obsAccessToken;
        }

        public async Task UnsendMessageAsync(string messageId)
        {
            int seq = _getReqseq();
            var args = ThriftStructs.UnsendMessageArgs(seq, messageId);
            await _request.RequestCoreAsync(RequestPath, args, "unsendMessage", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public async Task ReactAsync(string messageId, int type)
        {
            await ReactAsync(messageId, type == 0 ? null : new MessageReaction { Type = type }).ConfigureAwait(false);
        }

        public async Task ReactAsync(string messageId, MessageReaction reaction)
        {
            var args = ReactionProtocol.BuildArgs(_getReqseq(), messageId, reaction);
            await _request.RequestCoreAsync(RequestPath, args, reaction == null ? "cancelReaction" : "react", null, false, 15000, CancellationToken.None).ConfigureAwait(false);
        }

        // ── E2EE key RPCs ─────────────────────────────────────

        public async Task<Dictionary<int, object>> GetE2eePublicKeyAsync(string mid, int keyVersion, int keyId)
        {
            var args = ThriftStructs.GetE2eePublicKeyArgs(mid, keyVersion, keyId);
            var result = await _request.RequestCoreAsync(RequestPath, args, "getE2EEPublicKey", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<Dictionary<int, object>> NegotiateE2eePublicKeyAsync(string mid)
        {
            var args = ThriftStructs.NegotiateE2eePublicKeyArgs(mid);
            var result = await _request.RequestCoreAsync(RequestPath, args, "negotiateE2EEPublicKey", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<Dictionary<int, object>> RegisterE2eePublicKeyAsync(int version, int keyId, byte[] keyData, long createdTime)
        {
            int seq = _getReqseq();
            var args = ThriftStructs.RegisterE2eePublicKeyArgs(version, keyId, keyData, createdTime);
            // registerE2EEPublicKey_args has reqSeq in some layouts; vyline uses publicKey only at field 2.
            var result = await _request.RequestCoreAsync(RequestPath, args, "registerE2EEPublicKey", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<Dictionary<int, object>> GetLastE2eeGroupSharedKeyAsync(int keyVersion, string chatMid)
        {
            var args = ThriftStructs.GetLastE2eeGroupSharedKeyArgs(keyVersion, chatMid);
            var result = await _request.RequestCoreAsync(RequestPath, args, "getLastE2EEGroupSharedKey", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<Dictionary<int, object>> GetE2eeGroupSharedKeyAsync(int keyVersion, string chatMid, int groupKeyId)
        {
            var args = ThriftStructs.GetLastE2eeGroupSharedKeyArgs(keyVersion, chatMid);
            args.Add(new ThriftField(ThriftTypes.I32, 4, groupKeyId));
            var result = await _request.RequestCoreAsync(RequestPath, args, "getE2EEGroupSharedKey", null, false, 15000, CancellationToken.None).ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<Dictionary<object, object>> GetLastE2eePublicKeysAsync(string chatMid)
        {
            var args = new List<ThriftField> { new ThriftField(ThriftTypes.String, 2, chatMid) };
            var result = await _request.RequestCoreAsync(RequestPath, args, "getLastE2EEPublicKeys", null, false, 15000, CancellationToken.None).ConfigureAwait(false);
            return result.Success as Dictionary<object, object> ?? new Dictionary<object, object>();
        }

        public async Task<Dictionary<int, object>> RegisterE2eeGroupKeyAsync(
            int keyVersion, string chatMid, List<string> members,
            List<int> keyIds, List<byte[]> encryptedSharedKeys)
        {
            var args = ThriftStructs.RegisterE2eeGroupKeyArgs(keyVersion, chatMid, members, keyIds, encryptedSharedKeys);
            var result = await _request.RequestCoreAsync(RequestPath, args, "registerE2EEGroupKey", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }

        public async Task<List<object>> GetContactsAsync(List<string> mids)
        {
            var contacts = new List<object>();
            if (mids == null || mids.Count == 0) return contacts;
            const int batchSize = 200;
            for (int offset = 0; offset < mids.Count; offset += batchSize)
            {
                var batch = mids.GetRange(offset, Math.Min(batchSize, mids.Count - offset));
                var values = new List<object>();
                foreach (var mid in batch) values.Add(mid);
                var request = new List<ThriftField>
                {
                    new ThriftField(ThriftTypes.List, 1, new ThriftListValue(ThriftTypes.String, values))
                };
                var args = new List<ThriftField>
                {
                    new ThriftField(ThriftTypes.Struct, 1, request)
                };
                var result = await _request.RequestCoreAsync(RequestPath, args, "getContactsV2", null, false, 15000, CancellationToken.None)
                    .ConfigureAwait(false);
                var response = result.Success as Dictionary<int, object>;
                object rawMap;
                if (response == null || !response.TryGetValue(1, out rawMap)) continue;
                var map = rawMap as Dictionary<object, object>;
                if (map == null) continue;
                foreach (var entryValue in map.Values)
                {
                    var entry = entryValue as Dictionary<int, object>;
                    object contactValue;
                    if (entry != null && entry.TryGetValue(3, out contactValue) && contactValue != null)
                        contacts.Add(contactValue);
                }
            }
            return contacts;
        }

        public async Task<Dictionary<int, object>> GetContactAsync(string mid)
        {
            var args = ThriftStructs.GetContactArgs(mid);
            var result = await _request.RequestCoreAsync(RequestPath, args, "getContact", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Success as Dictionary<int, object> ?? new Dictionary<int, object>();
        }
    }
}
