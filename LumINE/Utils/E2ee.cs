using System;
using System.Collections.Generic;
using System.Text;
using LumINE.Utils;
using Windows.Data.Json;

namespace LumINE.Utils
{
    public sealed class E2eeKeyChainEntry
    {
        public int KeyId { get; set; }
        public byte[] PrivateKey { get; set; }
        public byte[] PublicKey { get; set; }
    }
    public static class E2ee
    {
        public static void DecryptKeyChain(
            byte[] serverPub, byte[] myPriv, byte[] encKeyChain,
            out byte[] privateKey, out byte[] publicKey, int expectedKeyId = 0)
        {
            var entries = DecryptKeyChainEntries(serverPub, myPriv, encKeyChain);
            var selected = expectedKeyId > 0 ? entries.Find(k => k.KeyId == expectedKeyId) : entries.Count == 1 ? entries[0] : null;
            if (selected == null) throw new InvalidOperationException("E2EE key chain does not contain the requested key ID.");
            privateKey = selected.PrivateKey; publicKey = selected.PublicKey;
        }

        public static List<E2eeKeyChainEntry> DecryptKeyChainEntries(byte[] serverPub, byte[] myPriv, byte[] encKeyChain)
        {
            var shared = CryptoHelper.X25519SharedSecret(myPriv, serverPub);
            var aesKey = CryptoHelper.Sha256(shared, Encoding.ASCII.GetBytes("Key"));
            var aesIv = CryptoHelper.XorHalf(CryptoHelper.Sha256(shared, Encoding.ASCII.GetBytes("IV")));
            // The key-chain payload is raw AES-CBC ciphertext. Do not strip
            // PKCS#7 bytes: the compact Thrift struct is decoded as-is.
            var plain = CryptoHelper.AesCbcDecryptNoPadding(aesKey, aesIv, encKeyChain);
            var topLevel = ThriftCompact.UnpackCompactStruct(plain);
            object chainValue;
            if (!topLevel.TryGetValue(1, out chainValue))
                throw new InvalidOperationException("E2EE key chain is missing field 1.");
            var chainEntries = chainValue as List<object>;
            if (chainEntries == null || chainEntries.Count == 0)
                throw new InvalidOperationException("E2EE key chain field 1 is not a non-empty list.");

            var entries = new List<E2eeKeyChainEntry>();
            var seen = new HashSet<int>();
            foreach (var entryObj in chainEntries)
            {
                var entryDict = entryObj as Dictionary<int, object>;
                if (entryDict == null)
                    throw new InvalidOperationException("E2EE key chain did not decode to a struct.");
                object privateValue, publicValue, idValue;
                int keyId;
                if (!entryDict.TryGetValue(2, out idValue) || idValue == null || !int.TryParse(idValue.ToString(), out keyId) || keyId <= 0 || !seen.Add(keyId))
                    throw new InvalidOperationException("E2EE key chain has an invalid or duplicate key ID.");
                if (!entryDict.TryGetValue(5, out privateValue) ||
                    !entryDict.TryGetValue(4, out publicValue))
                    throw new InvalidOperationException("E2EE key-chain entry is missing its key fields.");
                var privateKey = AsBytes(privateValue);
                var publicKey = AsBytes(publicValue);
                if (privateKey.Length != 32 || publicKey.Length != 32)
                    throw new InvalidOperationException("E2EE key-chain entry contains keys with invalid lengths.");
                var basePoint = new byte[32]; basePoint[0] = 9;
                var derived = CryptoHelper.X25519SharedSecret(privateKey, basePoint);
                for (int i = 0; i < 32; i++) if (derived[i] != publicKey[i])
                    throw new InvalidOperationException("E2EE key-chain public/private key mismatch.");
                entries.Add(new E2eeKeyChainEntry { KeyId = keyId, PrivateKey = privateKey, PublicKey = publicKey });
            }
            return entries;
        }

        public static byte[] EncryptDeviceSecret(
            byte[] serverPub, byte[] myPriv, byte[] encKeyChain)
        {
            var shared = CryptoHelper.X25519SharedSecret(myPriv, serverPub);
            var aesKey = CryptoHelper.Sha256(shared, Encoding.ASCII.GetBytes("Key"));
            var payload = CryptoHelper.XorHalf(CryptoHelper.Sha256(encKeyChain));
            return CryptoHelper.AesEcbEncrypt(aesKey, payload);
        }

        public static void EncryptTextMessage(
            string to, string myMid, string text, int contentType,
            byte[] myPriv, byte[] peerPub, int senderKeyId, int receiverKeyId,
            out List<byte[]> chunks, out int specVersion, int specVersionIn = 2, bool jsonPayload = false)
        {
            var shared = CryptoHelper.X25519SharedSecret(myPriv, peerPub);
            var salt = CryptoHelper.RandomBytes(16);
            var gcmKey = CryptoHelper.Sha256(shared, salt, Encoding.ASCII.GetBytes("Key"));
            // LINE E2EE v2 uses a 16-byte GCM nonce.
            var nonce = CryptoHelper.RandomBytes(16);

            var aad = new List<byte>();
            aad.AddRange(Encoding.UTF8.GetBytes(to));
            aad.AddRange(Encoding.UTF8.GetBytes(myMid));
            aad.AddRange(CryptoHelper.I32Be(senderKeyId));
            aad.AddRange(CryptoHelper.I32Be(receiverKeyId));
            aad.AddRange(CryptoHelper.I32Be(specVersionIn));
            aad.AddRange(CryptoHelper.I32Be(contentType));

            var payloadObject = new JsonObject();
            payloadObject["text"] = JsonValue.CreateStringValue(text ?? string.Empty);
            var payload = Encoding.UTF8.GetBytes(jsonPayload ? text : payloadObject.Stringify());
            byte[] enc;
            byte[] tag;
            CryptoHelper.AesGcmEncrypt(gcmKey, nonce, aad.ToArray(), payload, out enc, out tag);
            var encWithTag = new byte[enc.Length + 16];
            System.Buffer.BlockCopy(enc, 0, encWithTag, 0, enc.Length);
            System.Buffer.BlockCopy(tag, 0, encWithTag, enc.Length, 16);

            chunks = new List<byte[]>
            {
                salt,
                encWithTag,
                nonce,
                CryptoHelper.I32Be(senderKeyId),
                CryptoHelper.I32Be(receiverKeyId)
            };
            specVersion = specVersionIn;
        }

        public static Dictionary<string, object> DecryptMessage(
            List<byte[]> chunks, string to, string sender, string myMid,
            byte[] myPriv, int selfKeyId, int peerKeyId, string peerMid,
            byte[] peerPub, int contentType, Dictionary<string, string> contentMetadata,
            bool isGroup = false)
        {
            if (chunks.Count < 5)
                throw new ArgumentException($"Unexpected chunk count {chunks.Count}");

            var salt = chunks[0];
            var enc = chunks[1];
            var nonce = chunks[2];
            var senderKid = ReadInt32BigEndian(chunks[3]);
            var receiverKid = ReadInt32BigEndian(chunks[4]);

            int selfKid, peerKid;
            string peerMidStr;
            if (isGroup)
            {
                selfKid = receiverKid;
                peerKid = senderKid;
                peerMidStr = peerMid ?? string.Empty;
            }
            else
            {
                var myMidStr = myMid ?? string.Empty;
                if (sender == myMidStr)
                {
                    selfKid = senderKid;
                    peerKid = receiverKid;
                    peerMidStr = to ?? string.Empty;
                }
                else
                {
                    selfKid = receiverKid;
                    peerKid = senderKid;
                    peerMidStr = sender ?? string.Empty;
                }
            }

            var shared = CryptoHelper.X25519SharedSecret(myPriv, peerPub);

            int spec = 2;
            if (contentMetadata != null && contentMetadata.TryGetValue("e2eeVersion", out var sv))
                spec = int.Parse(sv);

            var aad = new List<byte>();
            aad.AddRange(Encoding.UTF8.GetBytes(to ?? string.Empty));
            aad.AddRange(Encoding.UTF8.GetBytes(sender ?? string.Empty));
            aad.AddRange(CryptoHelper.I32Be(senderKid));
            aad.AddRange(CryptoHelper.I32Be(receiverKid));
            aad.AddRange(CryptoHelper.I32Be(spec));
            aad.AddRange(CryptoHelper.I32Be(contentType));

            byte[] plain;
            if (spec == 2)
            {
                var gcmKey = CryptoHelper.Sha256(shared, salt, Encoding.ASCII.GetBytes("Key"));
                CryptoHelper.AesGcmDecrypt(gcmKey, nonce, aad.ToArray(), enc, out plain);
            }
            else
            {
                var aesKey = CryptoHelper.Sha256(shared, salt, Encoding.ASCII.GetBytes("Key"));
                var aesIv = CryptoHelper.XorHalf(CryptoHelper.Sha256(shared, salt, Encoding.ASCII.GetBytes("IV")));
                plain = CryptoHelper.AesCbcDecrypt(aesKey, aesIv, enc);
            }

            var json = Encoding.UTF8.GetString(plain);
            return ParseSimpleJson(json);
        }

        public static byte[] DecryptMediaKeyMaterial(byte[] data, object keyMaterial)
        {
            var kmBytes = keyMaterial is string s
                ? Convert.FromBase64String(s)
                : keyMaterial is byte[] b ? b : throw new ArgumentException("Invalid keyMaterial");

            var derived = CryptoHelper.HkdfSha256(kmBytes, null, Encoding.ASCII.GetBytes("FileEncryption"), 32 + 32 + 12);
            var encKey = new byte[32];
            var macKey = new byte[32];
            var nonce = new byte[12];
            Buffer.BlockCopy(derived, 0, encKey, 0, 32);
            Buffer.BlockCopy(derived, 32, macKey, 0, 32);
            Buffer.BlockCopy(derived, 64, nonce, 0, 12);

            if (data == null || data.Length < 32) throw new InvalidOperationException("画像の暗号化データが不完全です。");
            var work = new byte[data.Length - 32];
            Buffer.BlockCopy(data, 0, work, 0, work.Length);
            var expected = CryptoHelper.HmacSha256(macKey, work);
            var actual = new byte[32];
            Buffer.BlockCopy(data, work.Length, actual, 0, 32);
            if (!BytesEqual(expected, actual)) throw new InvalidOperationException("画像の復号キーまたは認証データが一致しません。");
            return CryptoHelper.AesCtrEncryptDecrypt(encKey, nonce, work);
        }

        public static byte[] EncryptMediaKeyMaterial(byte[] data, byte[] keyMaterial)
        {
            if (keyMaterial == null || keyMaterial.Length != 32) throw new ArgumentException("Invalid media key material");
            var derived = CryptoHelper.HkdfSha256(keyMaterial, null, Encoding.ASCII.GetBytes("FileEncryption"), 76);
            var enc = new byte[32]; var mac = new byte[32]; var nonce = new byte[12];
            Buffer.BlockCopy(derived, 0, enc, 0, 32); Buffer.BlockCopy(derived, 32, mac, 0, 32); Buffer.BlockCopy(derived, 64, nonce, 0, 12);
            var ciphertext = CryptoHelper.AesCtrEncryptDecrypt(enc, nonce, data);
            var signature = CryptoHelper.HmacSha256(mac, ciphertext);
            var result = new byte[ciphertext.Length + signature.Length];
            Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
            Buffer.BlockCopy(signature, 0, result, ciphertext.Length, signature.Length);
            return result;
        }

        private static byte[] AsBytes(object v)
        {
            return v is string s ? Encoding.UTF8.GetBytes(s) : (byte[])v;
        }

        private static int ReadInt32BigEndian(byte[] data)
        {
            return (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        }

        private static Dictionary<string, object> ParseSimpleJson(string json)
        {
            var result = new Dictionary<string, object>();
            var obj = JsonObject.Parse(json);
            foreach (var pair in obj)
            {
                switch (pair.Value.ValueType)
                {
                    case JsonValueType.String:
                        result[pair.Key] = pair.Value.GetString();
                        break;
                    case JsonValueType.Number:
                        result[pair.Key] = pair.Value.GetNumber();
                        break;
                    case JsonValueType.Boolean:
                        result[pair.Key] = pair.Value.GetBoolean();
                        break;
                    default:
                        result[pair.Key] = pair.Value.ToString();
                        break;
                }
            }
            return result;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}

