using System;
using System.Diagnostics;
using LumINE.Utils;

namespace LumINE.Utils
{
    /// <summary>
    /// Curve25519 (X25519) implementation delegating to CryptoHelper.
    /// Compatible with .NET Native / UWP (Windows 10 Mobile 1703).
    /// </summary>
    public static class Curve25519
    {
        public static void GenerateKeypair(byte[] privateKey, byte[] publicKey)
        {
            if (privateKey == null || privateKey.Length < 32)
                throw new ArgumentException("privateKey must be 32 bytes", nameof(privateKey));
            if (publicKey == null || publicKey.Length < 32)
                throw new ArgumentException("publicKey must be 32 bytes", nameof(publicKey));

            byte[] priv, pub;
            CryptoHelper.X25519GenerateKeyPair(out priv, out pub);
            System.Buffer.BlockCopy(priv, 0, privateKey, 0, 32);
            System.Buffer.BlockCopy(pub, 0, publicKey, 0, 32);
        }

        public static void SharedSecret(byte[] shared, byte[] myPrivateKey, byte[] theirPublicKey)
        {
            if (shared == null || shared.Length < 32)
                throw new ArgumentException("shared must be 32 bytes", nameof(shared));
            if (myPrivateKey == null || myPrivateKey.Length < 32)
                throw new ArgumentException("myPrivateKey must be 32 bytes", nameof(myPrivateKey));
            if (theirPublicKey == null || theirPublicKey.Length < 32)
                throw new ArgumentException("theirPublicKey must be 32 bytes", nameof(theirPublicKey));

            var result = CryptoHelper.X25519SharedSecret(myPrivateKey, theirPublicKey);
            Buffer.BlockCopy(result, 0, shared, 0, 32);
        }
    }
}
