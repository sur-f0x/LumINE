using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using Windows.Web.Http.Headers;
using LumINE.Utils;
using LumINE.Services.Vyline;

namespace LumINE.Services
{
    public class LegyClient
    {
        public static readonly byte[] LegyIv = new byte[] { 78, 9, 72, 62, 56, 245, 255, 114, 128, 18, 123, 158, 251, 92, 45, 51 };
        public static readonly string LegyPubKeyPem =
            "-----BEGIN PUBLIC KEY-----\n" +
            "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0LRokSkGDo8G5ObFfyKi" +
            "IdPAU5iOpj+UT+A3AcDxLuePyDt8IVp9HpOsJlf8uVk3Wr9fs+8y7cnF3WiY6Ro5" +
            "26hy3fbWR4HiD0FaIRCOTbgRlsoGNC2rthp2uxYad5up78krSDXNKBab8t1PteCm" +
            "Oq84TpDCRmainaZQN9QxzaSvYWUICVv27Kk97y2j3LS3H64NCqjS88XacAieivEL" +
            "fMr6rT2GutRshKeNSZOUR3YROV4THa77USBQwRI7ZZTe6GUFazpocTN58QY8jFYO" +
            "Dzfhdyoiym6rXJNNnUKatiSC/hmzdpX8/h4Y98KaGAZaatLAgPMRCe582q4JwHg7" +
            "rwIDAQAB\n" +
            "-----END PUBLIC KEY-----";

        private HttpClient _httpClient;
        private CryptographicKey _legyRsaKey;

        public LegyClient()
        {
            var filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            _httpClient = new HttpClient(filter);
            string importPath = "unknown";
            try
            {
                _legyRsaKey = ImportRsaOaepPublicKey(LegyPubKeyPem);
                importPath = "X509SubjectPublicKeyInfo";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Legy] X509 import failed: " + ex.Message + " -> fallback to manual");
                _legyRsaKey = ImportRsaPublicKeyManual(LegyPubKeyPem);
                importPath = "Capi1PublicKey fallback";
            }
            System.Diagnostics.Debug.WriteLine("[Legy] RSA key imported via " + importPath);
        }

        /// <summary>Build the x-lcs value used by the legacy Legy transport.</summary>
        public static string EncryptXcs(byte[] aesKey)
        {
            var key = ImportRsaOaepPublicKey(LegyPubKeyPem);
            var encrypted = CryptographicEngine.Encrypt(key, aesKey.AsBuffer(), null);
            byte[] result = new byte[(int)encrypted.Length];
            DataReader.FromBuffer(encrypted).ReadBytes(result);
            return "0005" + Convert.ToBase64String(result);
        }

        public async Task<Dictionary<int, object>> PostLegyAsync(string endpoint, byte[] data, string authToken = null)
        {
            var raw = await PostLegyRawAsync(endpoint, data, authToken, null, 15000, default(CancellationToken)).ConfigureAwait(false);
            if (raw.Status < 200 || raw.Status >= 300)
                throw new Exception("LINE HTTP " + raw.Status + " for " + endpoint + " via " + DesktopIdentity.Endpoint + " (bytes=" + (raw.Body == null ? 0 : raw.Body.Length) + ")");
            if (raw.Body == null || raw.Body.Length == 0)
                throw new InvalidDataException("Empty LINE response for " + endpoint + " via " + DesktopIdentity.Endpoint);
            return Utils.ThriftBinary.UnpackBinary(raw.Body);
        }

        public class LegyRawResult
        {
            public byte[] Body;
            public string NextAccess;
            public int Status;
        }

        /// <summary>
        /// Desktop HTTPS transport returning raw Thrift + x-line-next-access.
        /// The retired gf.line.naver.jp/enc gateway returns HTTP 404. Keep this
        /// entry point for existing callers, but send to the service path itself
        /// as current vyline-api does. TLS protects the request and credentials.
        /// </summary>
        public async Task<LegyRawResult> PostLegyRawAsync(
            string endpoint, byte[] data, string authToken,
            Dictionary<string, string> extraHeaders, int timeoutMs,
            System.Threading.CancellationToken cancellationToken)
        {
            Uri uri = new Uri(LineApiConfig.Legy + endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Content = new HttpBufferContent(data.AsBuffer());
                request.Content.Headers.ContentType = new HttpMediaTypeHeaderValue("application/x-thrift");

                var headers = ServiceHeaders(authToken, extraHeaders);
                foreach (var kv in headers)
                {
                    // Preserve TAB separators in the desktop identity on UWP.
                    if (!request.Headers.TryAppendWithoutValidation(kv.Key, kv.Value))
                        throw new InvalidOperationException("Could not add LINE request header: " + kv.Key);
                }

                using (var timeoutCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    if (timeoutMs > 0) timeoutCts.CancelAfter(timeoutMs);
                    var asyncOp = _httpClient.SendRequestAsync(request);
                    var response = await asyncOp.AsTask(timeoutCts.Token).ConfigureAwait(false);
                    using (response)
                    {
                        string nextAccess = null;
                        if (response.Headers.ContainsKey("x-line-next-access"))
                            nextAccess = response.Headers["x-line-next-access"];
                        var responseData = await response.Content.ReadAsBufferAsync().AsTask(timeoutCts.Token).ConfigureAwait(false);
                        byte[] responseBytes = new byte[(int)responseData.Length];
                        DataReader.FromBuffer(responseData).ReadBytes(responseBytes);
                        return new LegyRawResult { Body = responseBytes, NextAccess = nextAccess, Status = (int)response.StatusCode };
                    }
                }
            }
        }

        private static Dictionary<string, string> ServiceHeaders(string authToken, Dictionary<string, string> extraHeaders)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-line-application"] = DesktopIdentity.ApplicationHeader,
                ["User-Agent"] = DesktopIdentity.UserAgent,
                ["x-lap"] = DesktopIdentity.Lap,
                ["x-lpv"] = DesktopIdentity.Lpv,
                ["x-lhm"] = "POST",
                ["x-lal"] = DesktopIdentity.Lal,
                ["accept"] = "application/x-thrift",
                ["accept-encoding"] = "gzip, deflate",
            };
            if (extraHeaders != null)
                foreach (var header in extraHeaders)
                {
                    if (header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("x-le", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("x-lcs", StringComparison.OrdinalIgnoreCase)) continue;
                    headers[header.Key] = header.Value;
                }
            if (!string.IsNullOrEmpty(authToken)) headers["x-line-access"] = authToken;
            return headers;
        }

        private Dictionary<string, string> LeGyHeaders(byte[] aesKey, string authToken, Dictionary<string, string> extraHeaders = null)
        {
            // Desktop identity (vyline): DESKTOPWIN TAB-separated + DESKTOP:WINDOWS UA
            var appHeader = DesktopIdentity.ApplicationHeader;
            var userAgent = DesktopIdentity.UserAgent;
            var h = new Dictionary<string, string>
            {
                ["x-line-application"] = appHeader,
                ["x-le"] = "18",
                ["x-lcs"] = MakeXcs(aesKey),
                ["x-lap"] = DesktopIdentity.Lap,
                ["x-lpv"] = DesktopIdentity.Lpv,
                ["x-lhm"] = "POST",
                ["x-lal"] = DesktopIdentity.Lal,
                ["User-Agent"] = userAgent,
                ["content-type"] = "application/x-thrift; protocol=TBINARY",
                ["accept-encoding"] = "gzip, deflate",
            };
            if (extraHeaders != null)
            {
                foreach (var kv in extraHeaders)
                {
                    if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("connection", StringComparison.OrdinalIgnoreCase)) continue;
                    h[kv.Key] = kv.Value;
                }
            }
            if (!string.IsNullOrEmpty(authToken))
                h["X-Line-Access"] = authToken;
            return h;
        }

        private byte[] GenerateRandomBytes(int length)
        {
            var buffer = CryptographicBuffer.GenerateRandom((uint)length);
            byte[] result = new byte[length];
            DataReader.FromBuffer(buffer).ReadBytes(result);
            return result;
        }

        private Dictionary<string, string> LeGyHeaders(byte[] aesKey, string authToken)
        {
            return LeGyHeaders(aesKey, authToken, null);
        }

        private string MakeXcs(byte[] aesKey)
        {
            byte[] encrypted = RsaOaepEncrypt(_legyRsaKey, aesKey);
            string xcs = "0005" + Convert.ToBase64String(encrypted);
            return xcs;
        }

        private byte[] LeGyEncrypt(byte[] aesKey, string path, byte[] body, string authToken)
        {
            var innerHdrs = new Dictionary<string, string> { ["x-lpqs"] = path };
            if (!string.IsNullOrEmpty(authToken))
                innerHdrs["x-lt"] = authToken;

            byte[] hdrBytes = LeGyEncHeaders(innerHdrs);
            byte[] payload = hdrBytes.Concat(body).ToArray();
            byte[] encrypted = EncryptAesCbc(aesKey, payload);
            byte[] sig = LeGySig(aesKey, encrypted);
            return encrypted.Concat(sig).ToArray();
        }

        private byte[] LeGyDecrypt(byte[] aesKey, byte[] data)
        {
            // Python: data = pad(data, 16); plaintext = AES_CBC_decrypt(data)[:-16]; plaintext = unpad(plaintext, 16)
            byte[] padded = Pkcs7Pad(data, 16);
            byte[] decrypted = DecryptAesCbcRaw(aesKey, padded);
            // Strip last 16 bytes (Python's [:-16])
            if (decrypted.Length >= 16)
            {
                byte[] stripped = new byte[decrypted.Length - 16];
                Array.Copy(decrypted, 0, stripped, 0, stripped.Length);
                return LeGyDecHeaders(Pkcs7Unpad(stripped));
            }
            return LeGyDecHeaders(Pkcs7Unpad(decrypted));
        }

        private byte[] DecryptAesCbcRaw(byte[] key, byte[] data)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbc);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var decrypted = CryptographicEngine.Decrypt(cryptoKey, data.AsBuffer(), LegyIv.AsBuffer());
            byte[] result = new byte[(int)decrypted.Length];
            DataReader.FromBuffer(decrypted).ReadBytes(result);
            return result;
        }

        private byte[] LeGyEncHeaders(Dictionary<string, string> headers)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                byte[] count = BitConverter.GetBytes((ushort)headers.Count);
                if (BitConverter.IsLittleEndian) Array.Reverse(count);
                bw.Write(count);
                foreach (var kv in headers)
                {
                    byte[] k = Encoding.UTF8.GetBytes(kv.Key);
                    byte[] v = Encoding.UTF8.GetBytes(kv.Value);
                    byte[] kl = BitConverter.GetBytes((ushort)k.Length);
                    byte[] vl = BitConverter.GetBytes((ushort)v.Length);
                    if (BitConverter.IsLittleEndian) Array.Reverse(kl);
                    if (BitConverter.IsLittleEndian) Array.Reverse(vl);
                    bw.Write(kl);
                    bw.Write(k);
                    bw.Write(vl);
                    bw.Write(v);
                }
                byte[] headerData = ms.ToArray();
                byte[] totalLen = BitConverter.GetBytes((ushort)headerData.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(totalLen);
                return totalLen.Concat(headerData).ToArray();
            }
        }

        private byte[] LeGyDecHeaders(byte[] data)
        {
            if (data.Length < 4) return new byte[0];
            int total = BitConverter.ToUInt16(new byte[] { data[1], data[0] }, 0) + 2;
            int n = BitConverter.ToUInt16(new byte[] { data[3], data[2] }, 0);
            int pos = 4;
            for (int i = 0; i < n; i++)
            {
                if (pos + 4 > data.Length) break;
                int kl = BitConverter.ToUInt16(new byte[] { data[pos + 1], data[pos] }, 0); pos += 2;
                if (pos + kl > data.Length) break;
                pos += kl;
                if (pos + 2 > data.Length) break;
                int vl = BitConverter.ToUInt16(new byte[] { data[pos + 1], data[pos] }, 0); pos += 2;
                if (pos + vl > data.Length) break;
                pos += vl;
            }
            if (pos > data.Length) return new byte[0];
            byte[] body = new byte[data.Length - pos];
            Array.Copy(data, pos, body, 0, body.Length);
            return body;
        }

        private byte[] LeGySig(byte[] aesKey, byte[] encrypted)
        {
            // Python: n = xxh32(seed=0); n.update(bytes(r)); r ^= 106; s = xxh32(seed=0); s.update(bytes(r)); s.update(encrypted); n.update(bytes.fromhex(s.hexdigest())); return bytes.fromhex(n.hexdigest())
            byte[] r = new byte[16];
            for (int i = 0; i < 16; i++) r[i] = (byte)(aesKey[i] ^ 92);

            // n = XXH32(r_with_92_only, seed=0)
            uint n = XxHash32(r, 0);

            // r[i] ^= 106
            for (int i = 0; i < 16; i++) r[i] = (byte)(r[i] ^ 106);

            // s = XXH32(r_with_106 || encrypted, seed=0)
            byte[] sInput = r.Concat(encrypted).ToArray();
            uint s = XxHash32(sInput, 0);

            // s_bytes = bytes.fromhex(s.hexdigest()) = big-endian 4 bytes of s
            byte[] sBytes = new byte[4];
            sBytes[0] = (byte)((s >> 24) & 0xFF);
            sBytes[1] = (byte)((s >> 16) & 0xFF);
            sBytes[2] = (byte)((s >> 8) & 0xFF);
            sBytes[3] = (byte)(s & 0xFF);

            // n = XXH32(r_with_92_only || s_bytes, seed=0)
            byte[] rOrig = new byte[16];
            for (int i = 0; i < 16; i++) rOrig[i] = (byte)(aesKey[i] ^ 92);
            byte[] nInput = rOrig.Concat(sBytes).ToArray();
            uint finalN = XxHash32(nInput, 0);

            // Return finalN as 4-byte big-endian
            return new byte[] {
                (byte)((finalN >> 24) & 0xFF),
                (byte)((finalN >> 16) & 0xFF),
                (byte)((finalN >> 8) & 0xFF),
                (byte)(finalN & 0xFF)
            };
        }

        public static byte[] LegySig(byte[] aesKey, byte[] encrypted)
        {
            // Create a temporary instance to use the private method
            var instance = new LegyClient();
            return instance.LeGySig(aesKey, encrypted);
        }

        private static uint XxHash32(byte[] data, uint seed)
        {
            const uint Prime32_1 = 2654435761U;
            const uint Prime32_2 = 2246822519U;
            const uint Prime32_3 = 3266489917U;
            const uint Prime32_4 = 668265263U;
            const uint Prime32_5 = 374761393U;

            uint h32;
            int len = data.Length;
            int i = 0;

            if (len >= 16)
            {
                uint v1 = seed + Prime32_1 + Prime32_2;
                uint v2 = seed + Prime32_2;
                uint v3 = seed + 0;
                uint v4 = seed - Prime32_1;
                while (i + 16 <= len)
                {
                    v1 = Rotl32(v1 + BitConverter.ToUInt32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
                    v2 = Rotl32(v2 + BitConverter.ToUInt32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
                    v3 = Rotl32(v3 + BitConverter.ToUInt32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
                    v4 = Rotl32(v4 + BitConverter.ToUInt32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
                }
                h32 = Rotl32(v1, 1) + Rotl32(v2, 7) + Rotl32(v3, 12) + Rotl32(v4, 18);
            }
            else
            {
                h32 = seed + Prime32_5;
            }

            h32 += (uint)len;

            while (i + 4 <= len)
            {
                h32 += BitConverter.ToUInt32(data, i) * Prime32_3;
                h32 = Rotl32(h32, 17) * Prime32_4;
                i += 4;
            }

            while (i < len)
            {
                h32 += data[i] * Prime32_5;
                h32 = Rotl32(h32, 11) * Prime32_1;
                i++;
            }

            h32 ^= h32 >> 15;
            h32 *= Prime32_2;
            h32 ^= h32 >> 13;
            h32 *= Prime32_3;
            h32 ^= h32 >> 16;
            return h32;
        }

        private static uint Rotl32(uint x, int r) => (x << r) | (x >> (32 - r));

        private static byte[] Pkcs7Pad(byte[] data, int blockSize)
        {
            int pad = blockSize - (data.Length % blockSize);
            byte[] result = new byte[data.Length + pad];
            Array.Copy(data, result, data.Length);
            for (int i = data.Length; i < result.Length; i++) result[i] = (byte)pad;
            return result;
        }

        private static byte[] Pkcs7Unpad(byte[] data)
        {
            if (data.Length == 0) return data;
            int pad = data[data.Length - 1];
            if (pad < 1 || pad > 16 || pad > data.Length) pad = 0;
            byte[] result = new byte[data.Length - pad];
            Array.Copy(data, result, result.Length);
            return result;
        }

        private byte[] EncryptAesCbc(byte[] key, byte[] data)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbc);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            byte[] padded = Pkcs7Pad(data, 16);
            var encrypted = CryptographicEngine.Encrypt(cryptoKey, padded.AsBuffer(), LegyIv.AsBuffer());
            byte[] result = new byte[(int)encrypted.Length];
            DataReader.FromBuffer(encrypted).ReadBytes(result);
            return result;
        }

        private byte[] DecryptAesCbc(byte[] key, byte[] data)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var decrypted = CryptographicEngine.Decrypt(cryptoKey, data.AsBuffer(), LegyIv.AsBuffer());
            byte[] result = new byte[(int)decrypted.Length];
            DataReader.FromBuffer(decrypted).ReadBytes(result);
            return result;
        }

        private byte[] RsaOaepEncrypt(CryptographicKey key, byte[] data)
        {
            var encrypted = CryptographicEngine.Encrypt(key, data.AsBuffer(), null);
            byte[] result = new byte[(int)encrypted.Length];
            DataReader.FromBuffer(encrypted).ReadBytes(result);
            return result;
        }

        private static CryptographicKey ImportRsaOaepPublicKey(string pem)
        {
            try
            {
                string b64 = pem
                    .Replace("-----BEGIN PUBLIC KEY-----", "")
                    .Replace("-----END PUBLIC KEY-----", "")
                    .Replace("\n", "").Replace("\r", "").Trim();
                byte[] der = Convert.FromBase64String(b64);
                var buffer = der.AsBuffer();
                var provider = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(AsymmetricAlgorithmNames.RsaOaepSha1);
                return provider.ImportPublicKey(buffer, CryptographicPublicKeyBlobType.X509SubjectPublicKeyInfo);
            }
            catch
            {
                // Fallback: parse manually
                return ImportRsaPublicKeyManual(pem);
            }
        }

        private static CryptographicKey ImportRsaPublicKeyManual(string pem)
        {
            string b64 = pem
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("\n", "").Replace("\r", "").Trim();
            byte[] der = Convert.FromBase64String(b64);
            // Simple DER parse for RSAPublicKey ::= SEQUENCE { modulus INTEGER, publicExponent INTEGER }
            int pos = 0;
            // Outer SEQUENCE
            if (der[pos] != 0x30) throw new Exception("Invalid DER");
            pos++;
            int outerLen = ReadDerLength(der, ref pos);
            // AlgorithmIdentifier SEQUENCE
            if (der[pos] != 0x30) throw new Exception("Invalid DER");
            pos++;
            int algoLen = ReadDerLength(der, ref pos);
            pos += algoLen; // skip algorithm identifier
            // BIT STRING
            if (der[pos] != 0x03) throw new Exception("Invalid DER");
            pos++;
            int bitStrLen = ReadDerLength(der, ref pos);
            pos++; // skip unused bits byte
            // Inner SEQUENCE (RSAPublicKey)
            if (der[pos] != 0x30) throw new Exception("Invalid DER");
            pos++;
            int keySeqLen = ReadDerLength(der, ref pos);
            // MODULUS
            if (der[pos] != 0x02) throw new Exception("Invalid DER");
            pos++;
            int modLen = ReadDerLength(der, ref pos);
            byte[] modulus = new byte[modLen];
            Array.Copy(der, pos, modulus, 0, modLen);
            pos += modLen;
            // EXPONENT
            if (der[pos] != 0x02) throw new Exception("Invalid DER");
            pos++;
            int expLen = ReadDerLength(der, ref pos);
            byte[] exponent = new byte[expLen];
            Array.Copy(der, pos, exponent, 0, expLen);

            // Build CAPI1 PUBLICKEYBLOB:
            //   PUBLICKEYSTRUC (8 bytes) + RSAPUBKEY (12 bytes) + modulus
            // Normalize exponent
            byte[] expBytes = exponent;
            if (expBytes.Length > 0 && expBytes[0] == 0)
            {
                byte[] tmp = new byte[expBytes.Length - 1];
                Array.Copy(expBytes, 1, tmp, 0, tmp.Length);
                expBytes = tmp;
            }
            // Normalize modulus (remove leading 0x00 if present for sign)
            byte[] modBytes = modulus;
            if (modBytes.Length > 0 && modBytes[0] == 0)
            {
                byte[] tmp = new byte[modBytes.Length - 1];
                Array.Copy(modBytes, 1, tmp, 0, tmp.Length);
                modBytes = tmp;
            }
            // Ensure even length
            if ((modBytes.Length & 1) == 1)
            {
                byte[] tmp = new byte[modBytes.Length + 1];
                Array.Copy(modBytes, 0, tmp, 1, modBytes.Length);
                modBytes = tmp;
            }
            // CAPI blob stores modulus in little-endian
            Array.Reverse(modBytes);

            byte[] keyBlob = new byte[20 + modBytes.Length];
            int kidx = 0;
            // PUBLICKEYSTRUC
            keyBlob[kidx++] = 0x06; // bType = PUBLICKEYBLOB
            keyBlob[kidx++] = 0x02; // bVersion
            keyBlob[kidx++] = 0x00; keyBlob[kidx++] = 0x00; // reserved
            keyBlob[kidx++] = 0x00; keyBlob[kidx++] = 0xA4; keyBlob[kidx++] = 0x00; keyBlob[kidx++] = 0x00; // aiKeyAlg = CALG_RSA_KEYX
            // RSAPUBKEY
            keyBlob[kidx++] = 0x52; keyBlob[kidx++] = 0x53; keyBlob[kidx++] = 0x41; keyBlob[kidx++] = 0x31; // magic "RSA1"
            byte[] bitlenBytes = BitConverter.GetBytes(modBytes.Length * 8);
            Array.Copy(bitlenBytes, 0, keyBlob, kidx, 4); kidx += 4;
            // Public exponent: big-endian in DER -> pad to 4 bytes -> little-endian for CAPI1
            byte[] exp4 = new byte[4];
            Array.Copy(expBytes, 0, exp4, 4 - expBytes.Length, expBytes.Length);
            Array.Reverse(exp4);
            Array.Copy(exp4, 0, keyBlob, kidx, 4); kidx += 4;
            Array.Copy(modBytes, 0, keyBlob, kidx, modBytes.Length);

            var provider = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(AsymmetricAlgorithmNames.RsaOaepSha1);
            return provider.ImportPublicKey(keyBlob.AsBuffer(), CryptographicPublicKeyBlobType.Capi1PublicKey);
        }

        private static int ReadDerLength(byte[] der, ref int pos)
        {
            byte b = der[pos++];
            if ((b & 0x80) == 0) return b;
            int numBytes = b & 0x7F;
            int result = 0;
            for (int i = 0; i < numBytes; i++) result = (result << 8) | der[pos++];
            return result;
        }
    }
}
