using System;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace LumINE.Services
{
    public static class AppAuthentication
    {
        public static async Task<bool> VerifyAsync(string message)
        {
            var available = await UserConsentVerifier.CheckAvailabilityAsync();
            if (available != UserConsentVerifierAvailability.Available)
                throw new InvalidOperationException("Windows Helloを利用できません。端末のPIN・サインイン設定を確認してください（" + available + "）。");
            return await UserConsentVerifier.RequestVerificationAsync(message) == UserConsentVerificationResult.Verified;
        }
    }
}
