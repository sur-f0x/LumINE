using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using LumINE.Utils;
using LumINE.Models;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using System.Runtime.InteropServices.WindowsRuntime;

namespace LumINE.Services
{
    public class AuthenticationService
    {
        private LineApiClient _apiClient;

        public AuthenticationService(LineApiClient apiClient)
        {
            _apiClient = apiClient;
        }

        public async Task<RsaKeyInfo> GetRsaKeyInfoAsync()
        {
            try
            {
                var result = await _apiClient.GetRsaKeyInfoAsync();
                var rsaInfo = new RsaKeyInfo();
                if (result.ContainsKey(1) && result[1] != null) rsaInfo.KeyName = result[1].ToString();
                if (result.ContainsKey(2) && result[2] != null) rsaInfo.NValue = result[2].ToString();
                if (result.ContainsKey(3) && result[3] != null) rsaInfo.EValue = result[3].ToString();
                if (result.ContainsKey(4) && result[4] != null) rsaInfo.SessionKey = result[4].ToString();
                if (string.IsNullOrEmpty(rsaInfo.SessionKey))
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var k in result.Keys)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(k);
                    }
                    throw new Exception("getRSAKeyInfo: SessionKey is empty. Response keys=[" + sb.ToString() + "]");
                }
                return rsaInfo;
            }
            catch (Exception ex)
            {
                throw new Exception("getRSAKeyInfo: " + ex.Message, ex);
            }
        }

        public async Task<Dictionary<int, object>> LoginV2Async(
            int type, int identityProvider, string identifier, string password,
            bool keepLoggedIn, string accessLocation, string systemName,
            string certificate, string verifier, string secret, int e2eeVersion, string modelName)
        {
            // Do not log the account identifier, E2EE secret, or one-time verifier.

            try
            {
                var result = await _apiClient.LoginV2Async(
                    type, identityProvider, identifier, password,
                    keepLoggedIn, accessLocation, systemName,
                    certificate, verifier, secret, e2eeVersion, modelName);
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LoginV2] Error: " + ex.Message);
                throw;
            }
        }

        public static byte[] GenerateE2EESecret(
            string pincode, out byte[] privateKey, out byte[] publicKey)
        {
            publicKey = new byte[32];
            privateKey = new byte[32];
            Curve25519.GenerateKeypair(privateKey, publicKey);

            byte[] aesKey = CryptoHelper.Sha256(Encoding.UTF8.GetBytes(pincode));

            byte[] secret = CryptoHelper.AesEcbEncrypt(aesKey, publicKey);
            return secret;
        }

        public async Task<Dictionary<int, object>> ConfirmE2EELoginAsync(string verifier, byte[] deviceSecret)
        {
            // New API: confirmE2EELogin with JSON
            // For now, using the old method - will need to update when we know the new format
            var fields = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.String, 1, verifier),
                // Thrift BINARY is wire-compatible with STRING, but the value
                // must be the raw bytes. Base64 changes the device secret.
                new ThriftField(ThriftTypes.String, 2, deviceSecret)
            };
            byte[] data = ThriftBinary.PackBinary("confirmE2EELogin", fields);
            return await _apiClient.PostLegyAsync("/api/v3p/rs", data);
        }

        public async Task<JsonObject> CheckPinCodeV2Async(string accessSession)
        {
            try
            {
                string result = await _apiClient.GetLegyPinAsync(accessSession);
                if (string.IsNullOrEmpty(result)) return null;
                return JsonObject.Parse(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Login] PIN poll failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        public string RsaEncrypt(string nValue, string eValue, string sessionKey, string email, string password)
        {
            return CryptoHelper.RsaEncryptLineFormat(nValue, eValue, sessionKey, email, password);
        }

        public void DecryptKeyChain(byte[] serverPub, byte[] myPriv, byte[] encKeyChain, out byte[] privateKey, out byte[] publicKey)
        {
            E2ee.DecryptKeyChain(serverPub, myPriv, encKeyChain, out privateKey, out publicKey);
        }

        public byte[] EncryptDeviceSecret(byte[] serverPub, byte[] myPriv, byte[] encKeyChain)
        {
            return E2ee.EncryptDeviceSecret(serverPub, myPriv, encKeyChain);
        }

        public void EncryptTextMessage(
            string to, string myMid, string text, int contentType,
            byte[] myPriv, byte[] peerPub, int senderKeyId, int receiverKeyId,
            out List<byte[]> chunks, out int specVersion, int specVersionIn = 2)
        {
            E2ee.EncryptTextMessage(to, myMid, text, contentType, myPriv, peerPub, senderKeyId, receiverKeyId, out chunks, out specVersion, specVersionIn);
        }

        public Dictionary<string, object> DecryptMessage(
            List<byte[]> chunks, string to, string sender, string myMid,
            byte[] myPriv, int selfKeyId, int peerKeyId, string peerMid,
            byte[] peerPub, int contentType, Dictionary<string, string> contentMetadata,
            bool isGroup = false)
        {
            return E2ee.DecryptMessage(chunks, to, sender, myMid, myPriv, selfKeyId, peerKeyId, peerMid, peerPub, contentType, contentMetadata, isGroup);
        }

        public byte[] DecryptMediaKeyMaterial(byte[] data, object keyMaterial)
        {
            return E2ee.DecryptMediaKeyMaterial(data, keyMaterial);
        }

        public byte[] X25519SharedSecret(byte[] myPriv, byte[] theirPub)
        {
            return CryptoHelper.X25519SharedSecret(myPriv, theirPub);
        }

        private byte[] HexToByteArray(string hex)
        {
            hex = hex.Replace(" ", "");
            if (hex.Length % 2 != 0)
                hex = "0" + hex;
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
