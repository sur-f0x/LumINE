using System;
using System.IO;
using System.Text;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace LumINE.Utils
{
    public static class CryptoHelper
    {
        // ------------------------------------------------------------------
        // SHA256 (matches Python hashlib.sha256)
        // ------------------------------------------------------------------
        public static byte[] Sha256(params byte[][] parts)
        {
            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hash = provider.CreateHash();
            foreach (var part in parts)
                hash.Append(part.AsBuffer());
            var resultBuffer = hash.GetValueAndReset();
            var result = new byte[(int)resultBuffer.Length];
            DataReader.FromBuffer(resultBuffer).ReadBytes(result);
            return result;
        }

        // ------------------------------------------------------------------
        // XOR half (matches Python _xor_half)
        // ------------------------------------------------------------------
        public static byte[] XorHalf(byte[] b)
        {
            var half = b.Length / 2;
            var result = new byte[half];
            for (var i = 0; i < half; i++)
                result[i] = (byte)(b[i] ^ b[half + i]);
            return result;
        }

        // ------------------------------------------------------------------
        // AES-ECB (matches Python Crypto.Cipher.AES.MODE_ECB, no padding)
        // ------------------------------------------------------------------
        public static byte[] AesEcbEncrypt(byte[] key, byte[] data)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var encrypted = CryptographicEngine.Encrypt(cryptoKey, data.AsBuffer(), null);
            var result = new byte[(int)encrypted.Length];
            DataReader.FromBuffer(encrypted).ReadBytes(result);
            return result;
        }

        public static byte[] AesEcbDecrypt(byte[] key, byte[] data)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var decrypted = CryptographicEngine.Decrypt(cryptoKey, data.AsBuffer(), null);
            var result = new byte[(int)decrypted.Length];
            DataReader.FromBuffer(decrypted).ReadBytes(result);
            return result;
        }

        // ------------------------------------------------------------------
        // AES-CBC (matches Python Crypto.Cipher.AES.MODE_CBC, PKCS7 pad)
        // ------------------------------------------------------------------
        public static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] data)
        {
            var padded = Pkcs7Pad(data, 16);
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbc);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var encrypted = CryptographicEngine.Encrypt(cryptoKey, padded.AsBuffer(), iv.AsBuffer());
            var result = new byte[(int)encrypted.Length];
            DataReader.FromBuffer(encrypted).ReadBytes(result);
            return result;
        }

        public static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] data)
        {
            return Pkcs7Unpad(AesCbcDecryptNoPadding(key, iv, data));
        }

        /// <summary>Decrypt AES-CBC ciphertext without modifying the plaintext.</summary>
        public static byte[] AesCbcDecryptNoPadding(byte[] key, byte[] iv, byte[] data)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbc);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var decrypted = CryptographicEngine.Decrypt(cryptoKey, data.AsBuffer(), iv.AsBuffer());
            var result = new byte[(int)decrypted.Length];
            DataReader.FromBuffer(decrypted).ReadBytes(result);
            return result;
        }

        // ------------------------------------------------------------------
        // AES-CTR (manual implementation using AES-ECB, UWP 1703 compatible)
        // ------------------------------------------------------------------
        public static byte[] AesCtrEncryptDecrypt(byte[] key, byte[] nonce12, byte[] data)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());

            var result = new byte[data.Length];
            var counter = new byte[16];
            System.Buffer.BlockCopy(nonce12, 0, counter, 0, System.Math.Min(nonce12.Length, 12));

            var processed = 0;
            while (processed < data.Length)
            {
                var counterBlock = new byte[16];
                System.Buffer.BlockCopy(counter, 0, counterBlock, 0, 16);
                var keystream = CryptographicEngine.Encrypt(cryptoKey, counterBlock.AsBuffer(), null);
                var ks = new byte[16];
                DataReader.FromBuffer(keystream).ReadBytes(ks);
                var chunk = System.Math.Min(16, data.Length - processed);
                for (var i = 0; i < chunk; i++)
                    result[processed + i] = (byte)(data[processed + i] ^ ks[i]);

                for (var i = 15; i >= 12; i--)
                {
                    counter[i]++;
                    if (counter[i] != 0) break;
                }
                processed += chunk;
            }
            return result;
        }

        // ------------------------------------------------------------------
        // AES-GCM (pure C# implementation for UWP 1703 compatibility)
        // Implements AES-GCM using AES-CTR + GHASH for authentication
        // ------------------------------------------------------------------
        public static void AesGcmEncrypt(byte[] key, byte[] nonce, byte[] aad, byte[] plaintext, out byte[] ciphertext, out byte[] tag)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var j0 = MakeGcmJ0(key, nonce);
            ciphertext = GcmCtr(cryptoKey, j0, plaintext);
            tag = ComputeGmac(key, nonce, aad, ciphertext);
        }

        public static void AesGcmDecrypt(byte[] key, byte[] nonce, byte[] aad, byte[] ciphertextWithTag, out byte[] plaintext)
        {
            if (ciphertextWithTag == null || ciphertextWithTag.Length < 16)
                throw new System.Security.Cryptography.CryptographicException("AES-GCM payload is shorter than its tag.");
            var ciphertext = new byte[ciphertextWithTag.Length - 16];
            var tag = new byte[16];
            System.Buffer.BlockCopy(ciphertextWithTag, 0, ciphertext, 0, ciphertext.Length);
            System.Buffer.BlockCopy(ciphertextWithTag, ciphertext.Length, tag, 0, 16);

            var computedTag = ComputeGmac(key, nonce, aad, ciphertext);
            if (!BytesEqual(tag, computedTag))
                throw new System.Security.Cryptography.CryptographicException("AES-GCM authentication tag mismatch.");

            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            plaintext = GcmCtr(cryptoKey, MakeGcmJ0(key, nonce), ciphertext);
        }

        private static byte[] GcmCtr(CryptographicKey key, byte[] j0, byte[] input)
        {
            var output = new byte[input.Length];
            var counter = (byte[])j0.Clone();
            for (int offset = 0; offset < input.Length; offset += 16)
            {
                IncrementGcmCounter(counter);
                var stream = CryptographicEngine.Encrypt(key, counter.AsBuffer(), null);
                var block = new byte[16];
                DataReader.FromBuffer(stream).ReadBytes(block);
                int count = System.Math.Min(16, input.Length - offset);
                for (int i = 0; i < count; i++) output[offset + i] = (byte)(input[offset + i] ^ block[i]);
            }
            return output;
        }

        private static void IncrementGcmCounter(byte[] counter)
        {
            for (int i = 15; i >= 12; i--) { counter[i]++; if (counter[i] != 0) break; }
        }

        private static byte[] MakeGcmJ0(byte[] key, byte[] nonce)
        {
            if (nonce == null || nonce.Length == 0) throw new System.ArgumentException("GCM nonce is empty.");
            if (nonce.Length == 12)
            {
                var result = new byte[16];
                System.Buffer.BlockCopy(nonce, 0, result, 0, 12);
                result[15] = 1;
                return result;
            }
            var h = EncryptAesBlock(key, new byte[16]);
            var paddedLength = ((nonce.Length + 15) / 16) * 16;
            var data = new byte[paddedLength + 16];
            System.Buffer.BlockCopy(nonce, 0, data, 0, nonce.Length);
            ulong bits = (ulong)nonce.Length * 8;
            for (int i = 0; i < 8; i++) data[data.Length - 1 - i] = (byte)(bits >> (8 * i));
            var y = new byte[16];
            for (int offset = 0; offset < data.Length; offset += 16)
            {
                for (int i = 0; i < 16; i++) y[i] ^= data[offset + i];
                y = GfMultiply(y, h);
            }
            return y;
        }

        private static byte[] EncryptAesBlock(byte[] key, byte[] block)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var buffer = CryptographicEngine.Encrypt(cryptoKey, block.AsBuffer(), null);
            var result = new byte[16];
            DataReader.FromBuffer(buffer).ReadBytes(result);
            return result;
        }

        private static byte[] ComputeGmac(byte[] key, byte[] nonce, byte[] aad, byte[] ciphertext)
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var cryptoKey = provider.CreateSymmetricKey(key.AsBuffer());
            var zeroBlock = new byte[16];
            var hBuffer = CryptographicEngine.Encrypt(cryptoKey, zeroBlock.AsBuffer(), null);
            var h = new byte[16];
            DataReader.FromBuffer(hBuffer).ReadBytes(h);

            var j0 = MakeGcmJ0(key, nonce);

            var y = new byte[16];
            var data = new System.Collections.Generic.List<byte>();
            data.AddRange(aad ?? new byte[0]);
            var aadPad = 16 - (data.Count % 16);
            if (aadPad < 16)
                for (var i = 0; i < aadPad; i++) data.Add(0);
            data.AddRange(ciphertext);
            var ctPad = 16 - (data.Count % 16);
            if (ctPad < 16)
                for (var i = 0; i < ctPad; i++) data.Add(0);
            var aadBits = System.BitConverter.GetBytes((ulong)((aad ?? new byte[0]).Length * 8));
            if (System.BitConverter.IsLittleEndian) System.Array.Reverse(aadBits);
            data.AddRange(aadBits);
            var ctBits = System.BitConverter.GetBytes((ulong)(ciphertext.Length * 8));
            if (System.BitConverter.IsLittleEndian) System.Array.Reverse(ctBits);
            data.AddRange(ctBits);

            var dataArray = data.ToArray();
            for (var i = 0; i < dataArray.Length; i += 16)
            {
                var block = new byte[16];
                System.Buffer.BlockCopy(dataArray, i, block, 0, System.Math.Min(16, dataArray.Length - i));
                for (var j = 0; j < 16; j++) y[j] ^= block[j];
                y = GfMultiply(y, h);
            }

            var encJ0Buffer = CryptographicEngine.Encrypt(cryptoKey, j0.AsBuffer(), null);
            var encJ0 = new byte[16];
            DataReader.FromBuffer(encJ0Buffer).ReadBytes(encJ0);

            var tag = new byte[16];
            for (var i = 0; i < 16; i++) tag[i] = (byte)(y[i] ^ encJ0[i]);
            return tag;
        }

        private static byte[] GfMultiply(byte[] x, byte[] y)
        {
            y = (byte[])y.Clone();
            var z = new byte[16];
            for (var i = 0; i < 128; i++)
            {
                var bit = (x[i / 8] >> (7 - (i % 8))) & 1;
                if (bit != 0)
                {
                    for (var j = 0; j < 16; j++) z[j] ^= y[j];
                }
                var lsb = (y[15] & 1);
                for (var j = 15; j > 0; j--)
                    y[j] = (byte)((y[j] >> 1) | ((y[j - 1] & 1) << 7));
                y[0] = (byte)(y[0] >> 1);
                if (lsb != 0)
                    y[0] ^= 0xE1;
            }
            return z;
        }

        // ------------------------------------------------------------------
        // HKDF-SHA256 (matches Python cryptography HKDF)
        // ------------------------------------------------------------------
        public static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int length)
        {
            if (salt == null || salt.Length == 0)
                salt = new byte[32];

            byte[] prk;
            var hmacProvider = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            var hmacKey = hmacProvider.CreateKey(salt.AsBuffer());
            var hmac = CryptographicEngine.Sign(hmacKey, ikm.AsBuffer());
            prk = new byte[(int)hmac.Length];
            DataReader.FromBuffer(hmac).ReadBytes(prk);

            var expandKey = hmacProvider.CreateKey(prk.AsBuffer());
            var output = new byte[length];
            var offset = 0;
            var counter = 1;
            var t = new byte[0];
            while (offset < length)
            {
                var tInput = new byte[t.Length + info.Length + 1];
                System.Buffer.BlockCopy(t, 0, tInput, 0, t.Length);
                System.Buffer.BlockCopy(info, 0, tInput, t.Length, info.Length);
                tInput[tInput.Length - 1] = (byte)counter;
                var tBuffer = CryptographicEngine.Sign(expandKey, tInput.AsBuffer());
                t = new byte[(int)tBuffer.Length];
                DataReader.FromBuffer(tBuffer).ReadBytes(t);
                var copy = System.Math.Min(t.Length, length - offset);
                System.Buffer.BlockCopy(t, 0, output, offset, copy);
                offset += copy;
                counter++;
            }
            return output;
        }

        // ------------------------------------------------------------------
        // HMAC-SHA256
        // ------------------------------------------------------------------
        public static byte[] HmacSha256(byte[] key, byte[] data)
        {
            var hmacProvider = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            var hmacKey = hmacProvider.CreateKey(key.AsBuffer());
            var resultBuffer = CryptographicEngine.Sign(hmacKey, data.AsBuffer());
            var result = new byte[(int)resultBuffer.Length];
            DataReader.FromBuffer(resultBuffer).ReadBytes(result);
            return result;
        }

        // ------------------------------------------------------------------
        // XXH32 (matches Python xxhash.xxh32(data, seed=0))
        // ------------------------------------------------------------------
        public static byte[] XxHash32(byte[] data, uint seed = 0)
        {
            return BitConverter.GetBytes(HostToNetworkOrder(XxHash32Uint(data, seed)));
        }

        private static uint XxHash32Uint(byte[] data, uint seed)
        {
            const uint Prime32_1 = 2654435761U;
            const uint Prime32_2 = 2246822519U;
            const uint Prime32_3 = 3266489917U;
            const uint Prime32_4 = 668265263U;
            const uint Prime32_5 = 374761393U;

            var len = data.Length;
            var i = 0;
            uint h32;

            if (len >= 16)
            {
                uint v1 = seed + Prime32_1 + Prime32_2;
                uint v2 = seed + Prime32_2;
                uint v3 = seed;
                uint v4 = seed - Prime32_1;

                while (i + 16 <= len)
                {
                    v1 = Rotl32(v1 + ReadLE32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
                    v2 = Rotl32(v2 + ReadLE32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
                    v3 = Rotl32(v3 + ReadLE32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
                    v4 = Rotl32(v4 + ReadLE32(data, i) * Prime32_2, 13) * Prime32_1; i += 4;
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
                h32 += ReadLE32(data, i) * Prime32_3;
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

        private static uint ReadLE32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static uint Rotl32(uint x, int r) => (x << r) | (x >> (32 - r));

        private static uint HostToNetworkOrder(uint value)
        {
            return ((value & 0xFF) << 24) |
                   ((value & 0xFF00) << 8) |
                   ((value >> 8) & 0xFF00) |
                   ((value >> 24) & 0xFF);
        }

        private static byte[] Pkcs7Pad(byte[] data, int blockSize)
        {
            var pad = blockSize - (data.Length % blockSize);
            var result = new byte[data.Length + pad];
            System.Buffer.BlockCopy(data, 0, result, 0, data.Length);
            for (var i = data.Length; i < result.Length; i++)
                result[i] = (byte)pad;
            return result;
        }

        private static byte[] Pkcs7Unpad(byte[] data)
        {
            if (data.Length == 0) return data;
            var pad = data[data.Length - 1];
            if (pad < 1 || pad > 16 || pad > data.Length) pad = 0;
            var result = new byte[data.Length - pad];
            System.Buffer.BlockCopy(data, 0, result, 0, result.Length);
            return result;
        }

        public static byte[] RandomBytes(int length)
        {
            var buffer = CryptographicBuffer.GenerateRandom((uint)length);
            var result = new byte[length];
            DataReader.FromBuffer(buffer).ReadBytes(result);
            return result;
        }

        public static byte[] I32Be(int n)
        {
            if (BitConverter.IsLittleEndian)
                return new byte[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
            return new byte[] { (byte)n, (byte)(n >> 8), (byte)(n >> 16), (byte)(n >> 24) };
        }

        // ==================================================================
        // X25519 using BigInteger (RFC 7748 compliant)
        // Compatible with UWP / .NET Native / Windows 10 Mobile 1703
        // ==================================================================
        private static readonly BigInteger X25519_P = (BigInteger.One << 255) - 19;
        private const int X25519_A24 = 121665;

        private static byte[] X25519ClampPrivateKey(byte[] key)
        {
            var k = new byte[32];
            System.Buffer.BlockCopy(key, 0, k, 0, 32);
            k[0] &= 248;
            k[31] &= 127;
            k[31] |= 64;
            return k;
        }

        private static BigInteger X25519Decode(byte[] data)
        {
            var bytes = new byte[33];
            System.Buffer.BlockCopy(data, 0, bytes, 0, 32);
            // RFC 7748 masks the most significant bit of the u-coordinate.
            bytes[31] &= 0x7F;
            bytes[32] = 0;
            return new BigInteger(bytes);
        }

        private static byte[] X25519Encode(BigInteger x)
        {
            // BigInteger remainder keeps the sign of the dividend in .NET.
            // Convert the field element back to its canonical [0, p-1] value
            // before serializing it as a 32-byte little-endian coordinate.
            x %= X25519_P;
            if (x.Sign < 0) x += X25519_P;
            var bytes = x.ToByteArray();
            var result = new byte[32];
            int len = Math.Min(bytes.Length, 32);
            System.Buffer.BlockCopy(bytes, 0, result, 0, len);
            return result;
        }

        private static void X25519Cswap(int swap, ref BigInteger x2, ref BigInteger x3,
                                         ref BigInteger z2, ref BigInteger z3)
        {
            if (swap != 0)
            {
                var t = x2; x2 = x3; x3 = t;
                t = z2; z2 = z3; z3 = t;
            }
        }

        private static BigInteger X25519ScalarMult(BigInteger k, BigInteger u)
        {
            var x1 = u;
            var x2 = BigInteger.One;
            var z2 = BigInteger.Zero;
            var x3 = u;
            var z3 = BigInteger.One;
            int swap = 0;

            for (int t = 254; t >= 0; t--)
            {
                int kt = (int)((k >> t) & 1);
                swap ^= kt;
                X25519Cswap(swap, ref x2, ref x3, ref z2, ref z3);
                swap = kt;

                var A = (x2 + z2) % X25519_P;
                var AA = (A * A) % X25519_P;
                var B = (x2 - z2) % X25519_P;
                var BB = (B * B) % X25519_P;
                var E = (AA - BB) % X25519_P;
                var C = (x3 + z3) % X25519_P;
                var D = (x3 - z3) % X25519_P;
                var DA = (D * A) % X25519_P;
                var CB = (C * B) % X25519_P;
                var DAplusCB = (DA + CB) % X25519_P;
                var DAminusCB = (DA - CB) % X25519_P;

                x3 = (DAplusCB * DAplusCB) % X25519_P;
                z3 = (x1 * DAminusCB * DAminusCB) % X25519_P;
                x2 = (AA * BB) % X25519_P;
                z2 = (E * (AA + X25519_A24 * E)) % X25519_P;
            }

            X25519Cswap(swap, ref x2, ref x3, ref z2, ref z3);

            var z2inv = BigInteger.ModPow(z2, X25519_P - 2, X25519_P);
            return (x2 * z2inv) % X25519_P;
        }

        public static void X25519GenerateKeyPair(out byte[] privateKey, out byte[] publicKey)
        {
            privateKey = RandomBytes(32);
            var clamped = X25519ClampPrivateKey(privateKey);
            System.Buffer.BlockCopy(clamped, 0, privateKey, 0, 32);

            publicKey = new byte[32];
            var basepoint = new byte[32];
            basepoint[0] = 9;

            var k = X25519Decode(privateKey);
            var u = X25519Decode(basepoint);
            var result = X25519ScalarMult(k, u);
            var resultBytes = X25519Encode(result);
            System.Buffer.BlockCopy(resultBytes, 0, publicKey, 0, 32);
        }

        public static byte[] X25519SharedSecret(byte[] myPrivateKey, byte[] theirPublicKey)
        {
            var clamped = X25519ClampPrivateKey(myPrivateKey);
            var k = X25519Decode(clamped);
            var u = X25519Decode(theirPublicKey);
            var result = X25519ScalarMult(k, u);
            return X25519Encode(result);
        }

        // ==================================================================
        // RSA using Windows.Security.Cryptography.Core (UWP compatible)
        // ==================================================================
        public static string RsaEncryptLineFormat(string nHex, string eHex, string sessionKey, string email, string password)
        {
            var ms = new MemoryStream();
            var sessionBytes = Encoding.UTF8.GetBytes(sessionKey);
            var emailBytes = Encoding.UTF8.GetBytes(email);
            var pwBytes = Encoding.UTF8.GetBytes(password);

            ms.WriteByte((byte)sessionBytes.Length);
            ms.Write(sessionBytes, 0, sessionBytes.Length);
            ms.WriteByte((byte)emailBytes.Length);
            ms.Write(emailBytes, 0, emailBytes.Length);
            ms.WriteByte((byte)pwBytes.Length);
            ms.Write(pwBytes, 0, pwBytes.Length);

            var nBytes = HexToBytes(nHex);
            var eBytes = HexToBytes(eHex);

            var keyBlob = BuildRsaPublicKeyBlob(nBytes, eBytes);
            var provider = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(AsymmetricAlgorithmNames.RsaPkcs1);
            var publicKey = provider.ImportPublicKey(keyBlob.AsBuffer(), CryptographicPublicKeyBlobType.Capi1PublicKey);
            var encryptedBuffer = CryptographicEngine.Encrypt(publicKey, ms.ToArray().AsBuffer(), null);
            var encrypted = new byte[(int)encryptedBuffer.Length];
            DataReader.FromBuffer(encryptedBuffer).ReadBytes(encrypted);
            return BytesToHex(encrypted);
        }

        public static byte[] RsaOaepEncrypt(string pemPublicKey, byte[] data)
        {
            var der = PemToDer(pemPublicKey);
            var provider = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(AsymmetricAlgorithmNames.RsaOaepSha1);
            var publicKey = provider.ImportPublicKey(der.AsBuffer(), CryptographicPublicKeyBlobType.X509SubjectPublicKeyInfo);
            var encryptedBuffer = CryptographicEngine.Encrypt(publicKey, data.AsBuffer(), null);
            var encrypted = new byte[(int)encryptedBuffer.Length];
            DataReader.FromBuffer(encryptedBuffer).ReadBytes(encrypted);
            return encrypted;
        }

        private static byte[] BuildRsaPublicKeyBlob(byte[] modulus, byte[] exponent)
        {
            byte[] modBytes = modulus;
            if (modBytes.Length > 0 && modBytes[0] == 0)
            {
                var tmp = new byte[modBytes.Length - 1];
                Array.Copy(modBytes, 1, tmp, 0, tmp.Length);
                modBytes = tmp;
            }
            if ((modBytes.Length & 1) == 1)
            {
                var tmp = new byte[modBytes.Length + 1];
                Array.Copy(modBytes, 0, tmp, 1, modBytes.Length);
                modBytes = tmp;
            }
            Array.Reverse(modBytes);

            byte[] expBytes = exponent;
            if (expBytes.Length > 0 && expBytes[0] == 0)
            {
                var tmp = new byte[expBytes.Length - 1];
                Array.Copy(expBytes, 1, tmp, 0, tmp.Length);
                expBytes = tmp;
            }

            // CAPI PUBLICKEYBLOB is PUBLICKEYSTRUC (8 bytes), RSAPUBKEY
            // (12 bytes), then the modulus (bitlen / 8 bytes). There is no
            // modulus-length DWORD in this format; adding one shifts the
            // modulus and makes LINE's RSA-encrypted credential unreadable.
            var keyBlob = new byte[20 + modBytes.Length];
            var kidx = 0;
            keyBlob[kidx++] = 0x06;
            keyBlob[kidx++] = 0x02;
            keyBlob[kidx++] = 0x00; keyBlob[kidx++] = 0x00;
            keyBlob[kidx++] = 0x00; keyBlob[kidx++] = 0xA4; keyBlob[kidx++] = 0x00; keyBlob[kidx++] = 0x00;
            keyBlob[kidx++] = 0x52; keyBlob[kidx++] = 0x53; keyBlob[kidx++] = 0x41; keyBlob[kidx++] = 0x31;
            var bitlenBytes = BitConverter.GetBytes(modBytes.Length * 8);
            Array.Copy(bitlenBytes, 0, keyBlob, kidx, 4); kidx += 4;
            var exp4 = new byte[4];
            Array.Copy(expBytes, 0, exp4, 4 - expBytes.Length, expBytes.Length);
            Array.Reverse(exp4);
            Array.Copy(exp4, 0, keyBlob, kidx, 4); kidx += 4;
            Array.Copy(modBytes, 0, keyBlob, kidx, modBytes.Length);
            return keyBlob;
        }

        public static byte[] HexToBytes(string hex)
        {
            hex = hex.Trim();
            if (hex.Length % 2 != 0) hex = "0" + hex;
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static byte[] PemToDer(string pem)
        {
            var b64 = pem
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("\n", "").Replace("\r", "").Trim();
            return Convert.FromBase64String(b64);
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
