using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// LineService port: sendMessage (optimistic + E2EE path), markAsRead, fetchChats, fetchMessagesSince.
    /// Serialized send queue (runSendRpc, 15s timeout).
    /// </summary>
    public class LineService
    {
        public const int SendTimeoutMs = 15000;

        private readonly LineSession _session;
        private readonly ChatStore _store;
        private readonly ReadEngine _readEngine;
        private readonly object _sendLock = new object();
        private Task _sendChain = Task.CompletedTask;
        public Task ReactAsync(string messageId, int type)
        {
            return ReactAsync(messageId, type == 0 ? null : new MessageReaction { Type = type });
        }
        public Task ReactAsync(string messageId, MessageReaction reaction)
        {
            lock (_sendLock)
            {
                var next = _sendChain.ContinueWith(_ => _session.Talk.ReactAsync(messageId, reaction)).Unwrap();
                _sendChain = next.ContinueWith(t => { var observed = t.Exception; });
                return next;
            }
        }

        public LineService(LineSession session, ChatStore store, ReadEngine readEngine)
        {
            _session = session;
            _store = store;
            _readEngine = readEngine;
        }

        /// <summary>Serialized send (vyline runSendRpc).</summary>
        public Task<Dictionary<int, object>> SendMessageAsync(
            string chatId, string text,
            string relatedMessageId = null,
            Dictionary<string, string> contentMetadata = null,
            CancellationTokenEx cancel = null)
        {
            lock (_sendLock)
            {
                var prev = _sendChain;
                var next = prev.ContinueWith(_ => SendMessageInnerAsync(chatId, text, relatedMessageId, contentMetadata))
                    .Unwrap();
                _sendChain = next.ContinueWith(t => { var _ = t.Exception; });
                return next;
            }
        }

        private async Task<Dictionary<int, object>> SendMessageInnerAsync(
            string chatId, string text,
            string relatedMessageId,
            Dictionary<string, string> contentMetadata)
        {
            using (var cts = new System.Threading.CancellationTokenSource(SendTimeoutMs))
            {
                var plainMeta = contentMetadata != null
                    ? new Dictionary<string, string>(contentMetadata)
                    : new Dictionary<string, string>();

                try
                {
                    // Match LINE's flow: send plain first, then retry sealed chats on error 82.
                    return await _session.Talk.SendMessageAsync(
                        chatId, text, 0, plainMeta, relatedMessageId,
                        0, null, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (ThriftRpcException ex)
                {
                    // 82 asks for E2EE retry; 81 means the plaintext request
                    // used an invalid E2EE protocol for this sealed group.
                    if (ex.Code != 82 && ex.Code != 81 && !(ex.Code == 99 && IsGroupMid(chatId))) throw;
                    var sealedMeta = contentMetadata != null
                        ? new Dictionary<string, string>(contentMetadata)
                        : new Dictionary<string, string>();
                    bool encrypted = await TryEncryptE2eeAsync(chatId, text, sealedMeta, false, ex.Code == 99).ConfigureAwait(false);
                    if (!encrypted || _pendingChunks == null)
                        throw new InvalidOperationException("This chat requires E2EE, but a usable peer key is not available.", ex);

                    var chunks = _pendingChunks;
                    _pendingChunks = null;
                    using (var retryCts = new System.Threading.CancellationTokenSource(SendTimeoutMs))
                    {
                    try
                    {
                        return await _session.Talk.SendMessageAsync(
                            chatId, text, 0, sealedMeta, relatedMessageId,
                            0, chunks, retryCts.Token).ConfigureAwait(false);
                    }
                    catch (ThriftRpcException encryptedError)
                    {
                        // A group key can rotate after membership changes. Refresh
                        // it once when the server rejects this E2EE envelope.
                        if ((encryptedError.Code != 81 && encryptedError.Code != 99) || !IsGroupMid(chatId)) throw;
                        sealedMeta.Clear();
                        foreach (var pair in plainMeta) sealedMeta[pair.Key] = pair.Value;
                        if (!await TryEncryptE2eeAsync(chatId, text, sealedMeta, true, encryptedError.Code == 99).ConfigureAwait(false) || _pendingChunks == null)
                            throw;
                        var refreshedChunks = _pendingChunks;
                        _pendingChunks = null;
                        return await _session.Talk.SendMessageAsync(
                            chatId, text, 0, sealedMeta, relatedMessageId,
                            0, refreshedChunks, retryCts.Token).ConfigureAwait(false);
                    }
                    }
                }
            }
        }

        /// <summary>
        /// Letter sealing encrypt for text. Returns true if meta/chunks prepared
        /// via contentMetadata e2ee fields + chunks embedded by TalkService path.
        /// Simplified: negotiate/get peer key, encrypt, put chunks into message.
        /// Returns null if not ready (caller sends plain).
        /// </summary>
        private async Task<bool> TryEncryptE2eeAsync(
            string peerMid, string text, Dictionary<string, string> meta, bool refreshGroupKey = false, bool recreateGroupKey = false, int contentType = 0, bool jsonPayload = false)
        {
            bool isGroup = IsGroupMid(peerMid);
            var selfKeys = await E2eeKeyStore.LoadSelfKeysAsync(_session.AccountId).ConfigureAwait(false);
            if (selfKeys == null || selfKeys.Count == 0) return false;

            if (isGroup)
            {
                var groupKey = recreateGroupKey
                    ? await RecreateGroupKeyAsync(peerMid).ConfigureAwait(false)
                    : await LoadGroupKeyAsync(peerMid, 0, refreshGroupKey).ConfigureAwait(false);
                if (groupKey == null) return false;
                var groupSelf = selfKeys[selfKeys.Count - 1];
                List<byte[]> groupChunks; int groupSpec;
                E2ee.EncryptTextMessage(peerMid, _session.Mid ?? "", text, contentType,
                    groupKey.PrivateKey, groupSelf.PublicKey, groupSelf.KeyId, groupKey.KeyId,
                    out groupChunks, out groupSpec, 2, jsonPayload);
                _pendingChunks = groupChunks;
                meta["e2eeVersion"] = "2"; meta["contentType"] = contentType.ToString(); meta["e2eeMark"] = "2";
                return true;
            }

            var peerKey = await E2eeKeyStore.LoadPeerKeyAsync(_session.AccountId, peerMid).ConfigureAwait(false);
            if (peerKey == null)
            {
                // try negotiate once, cache
                try
                {
                    var neg = await _session.Talk.NegotiateE2eePublicKeyAsync(peerMid).ConfigureAwait(false);
                    peerKey = E2eeKeyStore.ExtractPeerKey(neg);
                    if (peerKey != null)
                        await E2eeKeyStore.SavePeerKeyAsync(_session.AccountId, peerMid, peerKey).ConfigureAwait(false);
                }
                catch { return false; }
                if (peerKey == null) return false;
            }

            var self = selfKeys[selfKeys.Count - 1]; // latest
            List<byte[]> chunkList;
            int specVersion;
            // Use existing E2ee.EncryptTextMessage
            E2ee.EncryptTextMessage(
                peerMid, _session.Mid ?? "", text, contentType,
                self.PrivateKey, peerKey.PublicKey,
                self.KeyId, peerKey.KeyId,
                out chunkList, out specVersion, 2, jsonPayload);

            // Store chunks for TalkService.SendMessage to pick up
            _pendingChunks = chunkList;
            meta["e2eeVersion"] = "2";
            meta["contentType"] = contentType.ToString();
            meta["e2eeMark"] = "2";
            return true; // E2EE path will be used
        }

        private List<byte[]> _pendingChunks;

        public Task<Dictionary<int, object>> SendMediaMessageAsync(string chatId, string payload, Dictionary<string, string> metadata)
        {
            lock (_sendLock)
            {
                var next = _sendChain.ContinueWith(_ => SendMediaInnerAsync(chatId, payload, metadata)).Unwrap();
                _sendChain = next.ContinueWith(t => { var observed = t.Exception; });
                return next;
            }
        }

        private async Task<Dictionary<int, object>> SendMediaInnerAsync(string chatId, string payload, Dictionary<string, string> metadata)
        {
            bool recreate = false;
            for (int attempt = 0; ; attempt++)
            {
                var sealedMeta = new Dictionary<string, string>(metadata);
                if (!await TryEncryptE2eeAsync(chatId, payload, sealedMeta, attempt > 0, recreate, 1, true).ConfigureAwait(false) || _pendingChunks == null)
                    throw new InvalidOperationException("画像送信用のE2EE鍵を取得できませんでした。");
                var chunks = _pendingChunks; _pendingChunks = null;
                try
                {
                    using (var timeout = new System.Threading.CancellationTokenSource(SendTimeoutMs))
                    {
                        var result = await _session.Talk.SendMessageAsync(chatId, null, 1, sealedMeta, null, 0, chunks, timeout.Token).ConfigureAwait(false);
                        if (!result.ContainsKey(27))
                        {
                            var envelope = new List<object>(); foreach (var chunk in chunks) envelope.Add(chunk);
                            result[27] = envelope;
                        }
                        if (!result.ContainsKey(18))
                        {
                            var values = new Dictionary<object, object>(); foreach (var pair in sealedMeta) values[pair.Key] = pair.Value;
                            result[18] = values;
                        }
                        return result;
                    }
                }
                catch (ThriftRpcException ex)
                {
                    if (attempt >= 2 || !IsGroupMid(chatId) || (ex.Code != 81 && ex.Code != 99)) throw;
                    recreate = ex.Code == 99;
                }
            }
        }

        public async Task<string> MarkAsReadAsync(string chatMid, string lastMessageId = null)
        {
            await _store.ClearUnreadAsync(chatMid).ConfigureAwait(false);
            return await _readEngine.MarkAsReadAsync(chatMid, lastMessageId).ConfigureAwait(false);
        }

        public Task<List<Chat>> FetchChatsAsync(bool force = false, bool refresh = false)
        {
            return _store.GetChatsAsync(force, refresh);
        }

        public Task<List<Message>> FetchMessagesAsync(string chatMid, int count = 50)
        {
            return _store.GetMessagesAsync(chatMid, count);
        }

        public Task<List<Message>> FetchLatestMessagesAsync(string chatMid, int count = 50)
        {
            return _store.GetLatestMessagesAsync(chatMid, count);
        }

        public Task<List<Message>> FetchCachedLatestMessagesAsync(string chatMid, int count = 50)
        {
            return _store.GetCachedLatestMessagesAsync(chatMid, count);
        }

        public Task<List<Message>> RefreshRecentMessagesAsync(string chatMid, int count = 30)
        {
            return _store.RefreshRecentMessagesAsync(chatMid, count);
        }

        /// <summary>Best-effort decryption for cached or freshly fetched direct and group messages.</summary>
        public async Task DecryptMessagesAsync(List<Message> messages)
        {
            if (messages == null || string.IsNullOrEmpty(_session.Mid)) return;
            List<E2eeSelfKey> selfKeys;
            try { selfKeys = await E2eeKeyStore.LoadSelfKeysAsync(_session.AccountId).ConfigureAwait(false); }
            catch { return; }
            if (selfKeys == null || selfKeys.Count == 0) return;
            var batchPeerKeys = new Dictionary<string, E2eePeerKey>(StringComparer.Ordinal);

            foreach (var message in messages)
            {
                if (message == null || message.Chunks == null || message.Chunks.Count < 5 ||
                    (message.ContentMetadata != null && message.ContentMetadata.ContainsKey("messageState")))
                    continue;
                if ((message.ContentType == 0 && !string.IsNullOrEmpty(message.Text)) || !string.IsNullOrEmpty(message.MediaKeyMaterial)) continue;

                var chatMid = IsGroupMid(message.To) ? message.To : (IsGroupMid(message.From) ? message.From : null);
                if (chatMid != null)
                {
                    await DecryptGroupMessageAsync(message, chatMid).ConfigureAwait(false);
                    continue;
                }

                bool sentBySelf = string.Equals(message.From, _session.Mid, StringComparison.Ordinal);
                string peerMid = sentBySelf ? message.To : message.From;
                int selfKeyId = ReadKeyId(message.Chunks[sentBySelf ? 3 : 4]);
                int peerKeyId = ReadKeyId(message.Chunks[sentBySelf ? 4 : 3]);
                if (string.IsNullOrEmpty(peerMid) || selfKeyId <= 0 || peerKeyId <= 0)
                    continue;

                var selfKey = selfKeys.Find(k => k.KeyId == selfKeyId);
                if (selfKey == null) continue;

                E2eePeerKey peerKey;
                string cacheKey = peerMid + ":" + peerKeyId;
                if (!batchPeerKeys.TryGetValue(cacheKey, out peerKey))
                {
                    peerKey = await E2eeKeyStore.LoadPeerKeyAsync(_session.AccountId, peerMid, peerKeyId).ConfigureAwait(false);
                    if (peerKey == null)
                    {
                        try
                        {
                            var raw = await _session.Talk.GetE2eePublicKeyAsync(peerMid, 1, peerKeyId)
                                .ConfigureAwait(false);
                            peerKey = E2eeKeyStore.ExtractPeerKey(raw);
                            if (peerKey != null && peerKey.KeyId == peerKeyId)
                                await E2eeKeyStore.SavePeerKeyAsync(_session.AccountId, peerMid, peerKey)
                                    .ConfigureAwait(false);
                            else
                                peerKey = null;
                        }
                        catch { peerKey = null; }
                    }
                    batchPeerKeys[cacheKey] = peerKey;
                }
                if (peerKey == null || peerKey.PublicKey == null) continue;

                try
                {
                    var payload = E2ee.DecryptMessage(
                        message.Chunks, message.To, message.From, _session.Mid,
                        selfKey.PrivateKey, selfKeyId, peerKeyId, peerMid,
                        peerKey.PublicKey, message.ContentType, message.ContentMetadata);
                    object decryptedText;
                    if (payload.TryGetValue("text", out decryptedText) && decryptedText is string text)
                        message.Text = text;
                    object mediaKey;
                    if (payload.TryGetValue("keyMaterial", out mediaKey) && mediaKey is string mediaKeyText)
                        message.MediaKeyMaterial = mediaKeyText;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[E2EE] message decrypt skipped: " + ex.GetType().Name);
                }
            }
        }

        private async Task DecryptGroupMessageAsync(Message message, string chatMid)
        {
            try
            {
                bool mine = string.Equals(message.From, _session.Mid, StringComparison.Ordinal);
                int senderKid = ReadKeyId(message.Chunks[3]);
                int groupKid = ReadKeyId(message.Chunks[4]);
                var groupKey = await LoadGroupKeyAsync(chatMid, groupKid).ConfigureAwait(false);
                if (groupKey == null) return;
                string senderMid = mine ? _session.Mid : message.From;
                int senderKeyId = senderKid;
                E2eePeerKey senderKey = null;
                if (mine)
                    senderKey = await E2eeKeyStore.LoadPeerKeyAsync(_session.AccountId, _session.Mid, senderKid).ConfigureAwait(false);
                else
                    senderKey = await E2eeKeyStore.LoadPeerKeyAsync(_session.AccountId, senderMid, senderKeyId).ConfigureAwait(false);
                if (mine && (senderKey == null || senderKey.PublicKey == null))
                {
                    var selfKeys = await E2eeKeyStore.LoadSelfKeysAsync(_session.AccountId).ConfigureAwait(false);
                    var own = selfKeys == null ? null : selfKeys.Find(k => k.KeyId == senderKid);
                    if (own != null) senderKey = new E2eePeerKey { PublicKey = own.PublicKey };
                }
                if (senderKey == null)
                {
                    var raw = await _session.Talk.GetE2eePublicKeyAsync(senderMid, 1, senderKeyId).ConfigureAwait(false);
                    senderKey = E2eeKeyStore.ExtractPeerKey(raw);
                    if (senderKey != null) await E2eeKeyStore.SavePeerKeyAsync(_session.AccountId, senderMid, senderKey).ConfigureAwait(false);
                }
                if (senderKey == null || senderKey.PublicKey == null) return;
                var payload = E2ee.DecryptMessage(message.Chunks, message.To, message.From, _session.Mid,
                    groupKey.PrivateKey, groupKid, senderKid, senderMid, senderKey.PublicKey,
                    message.ContentType, message.ContentMetadata, true);
                object text;
                if (payload.TryGetValue("text", out text) && text is string value) message.Text = value;
                object mediaKey;
                if (payload.TryGetValue("keyMaterial", out mediaKey) && mediaKey is string mediaKeyText)
                    message.MediaKeyMaterial = mediaKeyText;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[E2EE] group decrypt skipped: " + ex.Message); }
        }

        private async Task<E2eeGroupKey> LoadGroupKeyAsync(string groupMid, int expectedKeyId, bool forceRefresh = false)
        {
            if (!forceRefresh)
            {
                var cached = expectedKeyId > 0 ? await E2eeKeyStore.LoadGroupKeyAsync(_session.AccountId, groupMid, expectedKeyId).ConfigureAwait(false) : null;
                if (cached != null && (expectedKeyId <= 0 || cached.KeyId == expectedKeyId)) return cached;
            }

            Dictionary<int, object> result;
            try
            {
                result = expectedKeyId > 0
                    ? await _session.Talk.GetE2eeGroupSharedKeyAsync(2, groupMid, expectedKeyId).ConfigureAwait(false)
                    : await _session.Talk.GetLastE2eeGroupSharedKeyAsync(2, groupMid).ConfigureAwait(false);
            }
            catch (ThriftRpcException ex)
            {
                if (ex.Code != 5) throw;
                // Old history cannot be recovered by rotating today's key.
                if (expectedKeyId > 0) return null;
                return await RecreateGroupKeyAsync(groupMid).ConfigureAwait(false);
            }
            return await UnwrapGroupKeyAsync(groupMid, expectedKeyId, result).ConfigureAwait(false);
        }

        private async Task<E2eeGroupKey> RecreateGroupKeyAsync(string groupMid)
        {
            // The server supplies the current eligible members and public keys;
            // never wrap to a stale locally cached roster after a device change.
            var recipients = await _session.Talk.GetLastE2eePublicKeysAsync(groupMid).ConfigureAwait(false);
            object ownValue;
            if (!recipients.TryGetValue(_session.Mid, out ownValue))
                throw new InvalidOperationException("E2EE group member keys do not include this account.");
            var own = E2eeKeyStore.ExtractPeerKey(ownValue as Dictionary<int, object>);
            var selfKeys = await E2eeKeyStore.LoadSelfKeysAsync(_session.AccountId).ConfigureAwait(false);
            var self = own == null || selfKeys == null ? null : selfKeys.Find(k => k.KeyId == own.KeyId && SameBytes(k.PublicKey, own.PublicKey));
            if (self == null)
                throw new InvalidOperationException("E2EE鍵がメイン端末の現在の鍵と一致しません。本人確認を伴う再ログインが必要です。");
            var secret = CryptoHelper.RandomBytes(32);
            var members = new List<string>(); var ids = new List<int>(); var wrapped = new List<byte[]>();
            foreach (var recipient in recipients)
            {
                var peer = E2eeKeyStore.ExtractPeerKey(recipient.Value as Dictionary<int, object>);
                if (peer == null || peer.KeyId <= 0 || peer.PublicKey == null || peer.PublicKey.Length != 32)
                    throw new InvalidOperationException("E2EE group member public key is missing or invalid.");
                var shared = CryptoHelper.X25519SharedSecret(self.PrivateKey, peer.PublicKey);
                var aes = CryptoHelper.Sha256(shared, System.Text.Encoding.ASCII.GetBytes("Key"));
                var iv = CryptoHelper.XorHalf(CryptoHelper.Sha256(shared, System.Text.Encoding.ASCII.GetBytes("IV")));
                members.Add(recipient.Key.ToString()); ids.Add(peer.KeyId);
                wrapped.Add(CryptoHelper.AesCbcEncrypt(aes, iv, secret));
            }
            var result = await _session.Talk.RegisterE2eeGroupKeyAsync(1, groupMid, members, ids, wrapped).ConfigureAwait(false);
            // Use the generation acknowledged by the server, including its
            // wrapped key if another member won a concurrent registration.
            var key = await UnwrapGroupKeyAsync(groupMid, 0, result).ConfigureAwait(false);
            if (key == null) throw new InvalidOperationException("登録したE2EEグループ鍵を読み取れませんでした。");
            return key;
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private async Task<E2eeGroupKey> UnwrapGroupKeyAsync(string groupMid, int expectedKeyId, Dictionary<int, object> result)
        {
            Dictionary<int, object> fields = result;
            object nested;
            if (fields.TryGetValue(1, out nested) && nested is Dictionary<int, object> d) fields = d;
            string creatorMid = GetFieldString(fields, 3);
            int creatorKid = GetFieldInt(fields, 4);
            int receiverKid = GetFieldInt(fields, 6);
            int groupKid = GetFieldInt(fields, 2);
            var encrypted = GetFieldBytes(fields, 7);
            if (string.IsNullOrEmpty(creatorMid) || groupKid <= 0 || receiverKid <= 0 || encrypted == null) return null;
            if (expectedKeyId > 0 && expectedKeyId != groupKid) return null;

            var selfKeys = await E2eeKeyStore.LoadSelfKeysAsync(_session.AccountId).ConfigureAwait(false);
            var self = selfKeys == null ? null : selfKeys.Find(k => k.KeyId == receiverKid);
            if (self == null) return null;
            var rawPub = await _session.Talk.GetE2eePublicKeyAsync(creatorMid, 1, creatorKid).ConfigureAwait(false);
            var creatorKey = E2eeKeyStore.ExtractPeerKey(rawPub);
            if (creatorKey == null) return null;
            var shared = CryptoHelper.X25519SharedSecret(self.PrivateKey, creatorKey.PublicKey);
            var aesKey = CryptoHelper.Sha256(shared, System.Text.Encoding.ASCII.GetBytes("Key"));
            var iv = CryptoHelper.XorHalf(CryptoHelper.Sha256(shared, System.Text.Encoding.ASCII.GetBytes("IV")));
            var plain = CryptoHelper.AesCbcDecryptNoPadding(aesKey, iv, encrypted);
            int pad = plain.Length == 0 ? 0 : plain[plain.Length - 1];
            if (pad > 0 && pad <= 16 && pad <= plain.Length)
            {
                bool valid = true;
                for (int i = plain.Length - pad; i < plain.Length; i++) if (plain[i] != pad) valid = false;
                if (valid) Array.Resize(ref plain, plain.Length - pad);
            }
            if (plain.Length != 32) return null;
            var key = new E2eeGroupKey { Mid = groupMid, KeyId = groupKid, PrivateKey = plain };
            await E2eeKeyStore.SaveGroupKeyAsync(_session.AccountId, key).ConfigureAwait(false);
            return key;
        }

        private static bool IsGroupMid(string mid) => !string.IsNullOrEmpty(mid) &&
            (mid.StartsWith("c", StringComparison.Ordinal) || mid.StartsWith("r", StringComparison.Ordinal));
        private static int GetFieldInt(Dictionary<int, object> d, int id) { object v; int n; return d != null && d.TryGetValue(id, out v) && v != null && int.TryParse(v.ToString(), out n) ? n : 0; }
        private static string GetFieldString(Dictionary<int, object> d, int id) { object v; return d != null && d.TryGetValue(id, out v) && v != null ? v.ToString() : null; }
        private static byte[] GetFieldBytes(Dictionary<int, object> d, int id) { object v; if (d == null || !d.TryGetValue(id, out v) || v == null) return null; if (v is byte[] b) return b; try { return Convert.FromBase64String(v.ToString()); } catch { return null; } }

        private static int ReadKeyId(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 4) return 0;
            return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        }

        /// <summary>afterMessageId より新しいメッセージのみ（delta）。</summary>
        public async Task<List<Message>> FetchMessagesSinceAsync(string chatMid, string afterMessageId, int limit = 25)
        {
            // local first
            var local = _store.GetCachedMessages(chatMid);
            if (!string.IsNullOrEmpty(afterMessageId))
            {
                long after;
                if (long.TryParse(afterMessageId, out after))
                {
                    var newer = new List<Message>();
                    foreach (var m in local)
                    {
                        long id;
                        if (m.Id != null && long.TryParse(m.Id, out id) && id > after)
                            newer.Add(m);
                    }
                    if (newer.Count > 0) return newer;
                }
            }

            try
            {
                var raw = await _session.Talk.GetPreviousMessagesV2Async(
                    chatMid, "", limit, false, false, 0, 0).ConfigureAwait(false);
                var fetched = _store.MapMessageList(raw, chatMid);
                if (fetched.Count > 0)
                {
                    await DecryptMessagesAsync(fetched).ConfigureAwait(false);
                    _store.MergeMessages(chatMid, fetched);
                    await _store.SaveMessagesAsync(chatMid).ConfigureAwait(false);
                    return fetched;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LineService] delta failed: " + ex.Message);
            }
            return new List<Message>();
        }
    }

    /// <summary>Placeholder for optional cancellation plumbed later.</summary>
    public class CancellationTokenEx
    {
    }
}
