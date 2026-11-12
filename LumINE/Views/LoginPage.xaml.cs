using System;
using System.Collections.Generic;
using System.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using LumINE.Services;
using LumINE.Services.Vyline;
using LumINE.Utils;
using Windows.Data.Json;

namespace LumINE.Views
{
    public sealed partial class LoginPage : Page
    {
        private LineApiClient _apiClient;
        private AuthenticationService _authService;

        public LoginPage()
        {
            System.Diagnostics.Debug.WriteLine("[Login] page construction started");
            this.InitializeComponent();
            System.Diagnostics.Debug.WriteLine("[Login] XAML initialized; networking remains lazy");
        }

private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "ログイン中...";
            try
            {
                // Create WinRT networking objects only after the app is activated
                // and the user starts login, not while Frame.Navigate loads XAML.
                if (_apiClient == null)
                {
                    _apiClient = new LineApiClient();
                    _authService = new AuthenticationService(_apiClient);
                }

                string email = EmailBox.Text;
                string password = PasswordBox.Password;

                if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                {
                    StatusText.Text = "メールアドレスとパスワードを入力してください";
                    return;
                }

                var rsaInfo = await _authService.GetRsaKeyInfoAsync();
                if (string.IsNullOrEmpty(rsaInfo.SessionKey))
                {
                    StatusText.Text = "RSA鍵の取得に失敗しました";
                    return;
                }

                string encData = _authService.RsaEncrypt(rsaInfo.NValue, rsaInfo.EValue, rsaInfo.SessionKey, email, password);

                string pincode = GeneratePinCode();

                // Generate Curve25519 keypair and E2EE secret for primary device flow
                byte[] e2eeSecret = AuthenticationService.GenerateE2EESecret(
                    pincode, out byte[] e2eePrivateKey, out byte[] e2eePubKey);

                // First loginV2 attempt with E2EE secret (loginType=2)
                // New API: type, identityProvider, identifier, password, keepLoggedIn, accessLocation, systemName, certificate, verifier, secret, e2eeVersion, modelName
                var loginRes = await _authService.LoginV2Async(
                    type: 2,                    // loginType: 2 = email/password with E2EE
                    identityProvider: 1,        // provider=LINE
                    identifier: rsaInfo.KeyName,
                    password: encData,          // RSA-encrypted password
                    keepLoggedIn: false,
                    accessLocation: "",
                    systemName: "WindowsPC",
                    certificate: "",
                    verifier: "",
                    secret: Convert.ToBase64String(e2eeSecret),
                    e2eeVersion: 1,
                    modelName: "WindowsPC"
                );
                System.Diagnostics.Debug.WriteLine("[Login] initial loginV2 response received; token=" + loginRes.ContainsKey(9) + ", verifier=" + loginRes.ContainsKey(3));

                // Check if server rejected E2EE secret (code 89 = E2EE secret not allowed)
                if (loginRes.ContainsKey(3) && !loginRes.ContainsKey(9))
                {
                    // Server returned verifier but no token - need PIN confirmation
                    string verifier = loginRes[3].ToString();
                    StatusText.Text = "PINコードを端末で承認してください: " + pincode;

                    for (int i = 0; i < 30; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(2000);
                        var pinRes = await _authService.CheckPinCodeV2Async(verifier);
                        if (pinRes != null)
                        {
                            if (pinRes.ContainsKey("result"))
                            {
                                var result = pinRes.GetNamedObject("result");
                                if (result.ContainsKey("metadata"))
                                {
                                    var metadata = result.GetNamedObject("metadata");
                                    if (metadata.ContainsKey("errorCode"))
                                    {
                                        string errCode = metadata.GetNamedString("errorCode");
                                        if (errCode == "SUCCESS")
                                        {
                                            System.Diagnostics.Debug.WriteLine("[Login] primary-device approval received; E2EE metadata present=" + (metadata.ContainsKey("publicKey") && metadata.ContainsKey("encryptedKeyChain")));
                                            StatusText.Text = "認証成功。E2EE鍵を処理中...";

                                            // Get E2EE key data from PIN confirmation
                                            string pubKeyB64 = metadata.GetNamedString("publicKey");
                                            string encKcB64 = metadata.GetNamedString("encryptedKeyChain");
                                            if (string.IsNullOrEmpty(pubKeyB64) || string.IsNullOrEmpty(encKcB64))
                                            {
                                                StatusText.Text = "E2EE鍵データが取得できませんでした";
                                                return;
                                            }

                                            byte[] serverPub = Convert.FromBase64String(pubKeyB64);
                                            byte[] encKeyChain = Convert.FromBase64String(encKcB64);

                                            // Decrypt key chain using Curve25519 shared secret
                                            var entries = LumINE.Utils.E2ee.DecryptKeyChainEntries(serverPub, e2eePrivateKey, encKeyChain);
                                            System.Diagnostics.Debug.WriteLine("[Login] E2EE key chain decrypted");

                                            // Keep the key chain issued by the primary device. It is the
                                            // account's E2EE identity and must survive app restarts.
                                            int e2eeKeyId = ReadMetadataInt(metadata, "keyId", 0);
                                            if (e2eeKeyId <= 0 || !entries.Exists(k => k.KeyId == e2eeKeyId))
                                                throw new InvalidOperationException("E2EE key chain does not contain the primary device's selected key ID.");
                                            var keys = new List<E2eeSelfKey>();
                                            foreach (var entry in entries)
                                                keys.Add(new E2eeSelfKey { KeyId = entry.KeyId, IsActive = entry.KeyId == e2eeKeyId, PrivateKey = entry.PrivateKey, PublicKey = entry.PublicKey,
                                                    CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                                            await E2eeKeyStore.SaveSelfKeysAsync(BuildAccountId(email), keys);

                                            // Generate device secret
                                            byte[] deviceSecret = _authService.EncryptDeviceSecret(serverPub, e2eePrivateKey, encKeyChain);

                                            // Confirm E2EE login
                                            var confirmRes = await _authService.ConfirmE2EELoginAsync(verifier, deviceSecret);
                                            System.Diagnostics.Debug.WriteLine("[Login] confirmE2EELogin response received; fields=" + string.Join(",", confirmRes.Keys));
                                            string e2eeVerifier = confirmRes.ContainsKey(0) ? confirmRes[0].ToString() :
                                                                   confirmRes.ContainsKey(1) ? confirmRes[1].ToString() : null;

                                            if (string.IsNullOrEmpty(e2eeVerifier))
                                            {
                                                StatusText.Text = "E2EE確認でverifierが取得できませんでした";
                                                return;
                                            }

                                            // Final loginV2 with new verifier
                                            var finalRes = await _authService.LoginV2Async(
                                                type: 1,                    // loginType: 1 = QR/verifier
                                                identityProvider: 1,
                                                identifier: rsaInfo.KeyName,
                                                password: encData,
                                                keepLoggedIn: false,
                                                accessLocation: "",
                                                systemName: "WindowsPC",
                                                certificate: "",
                                                verifier: e2eeVerifier,
                                                secret: Convert.ToBase64String(e2eeSecret),
                                                e2eeVersion: 1,
                                                modelName: "WindowsPC"
                                            );
                                            System.Diagnostics.Debug.WriteLine("[Login] final loginV2 response received; token=" + finalRes.ContainsKey(9));

                                            if (finalRes.ContainsKey(9))
                                            {
                                                var tokenInfo = finalRes[9] as Dictionary<int, object>;
                                                if (tokenInfo != null && tokenInfo.ContainsKey(1))
                                                {
                                                    await FinishLoginAsync(tokenInfo);
                                                    return;
                                                }
                                            }

                                            StatusText.Text = "最終ログインでトークン取得に失敗しました";
                                            return;
                                        }
                                        else if (errCode == "CANCEL")
                                        {
                                            StatusText.Text = "認証がキャンセルされました";
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    StatusText.Text = "PIN認証がタイムアウトしました";
                    return;
                }

                // If we got token directly (trusted device or no PIN needed)
                if (loginRes.ContainsKey(9))
                {
                    var tokenInfo = loginRes[9] as Dictionary<int, object>;
                    if (tokenInfo != null && tokenInfo.ContainsKey(1))
                    {
                        await FinishLoginAsync(tokenInfo);
                        return;
                    }
                }

                // If server rejected E2EE secret (code 89), retry without secret
                if (loginRes.ContainsKey(3) && loginRes.ContainsKey(1))
                {
                    // Check if error code 89
                    // For now, retry without secret
                    StatusText.Text = "E2EE秘密鍵が拒否されました。再試行中...";
                    var retryRes = await _authService.LoginV2Async(
                        type: 0,                    // loginType: 0 = no E2EE
                        identityProvider: 1,
                        identifier: rsaInfo.KeyName,
                        password: encData,
                        keepLoggedIn: false,
                        accessLocation: "",
                        systemName: "WindowsPC",
                        certificate: "",
                        verifier: "",
                        secret: "",
                        e2eeVersion: 1,
                        modelName: "WindowsPC"
                    );
                    if (retryRes.ContainsKey(9))
                    {
                        var tokenInfo = retryRes[9] as Dictionary<int, object>;
                        if (tokenInfo != null && tokenInfo.ContainsKey(1))
                        {
                            await FinishLoginAsync(tokenInfo);
                            return;
                        }
                    }
                }

                StatusText.Text = "ログインレスポンスの解析に失敗しました";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.ToString();
                Windows.ApplicationModel.DataTransfer.DataPackage dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(ex.ToString());
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
        }

        /// <summary>
        /// Bind tokens to a LineSession (vyline login success → onLoginSuccess),
        /// persist, warm profile and start the ops loop.
        /// </summary>
        private async System.Threading.Tasks.Task FinishLoginAsync(Dictionary<int, object> tokenInfo)
        {
            string auth = tokenInfo[1].ToString();
            string refresh = tokenInfo.ContainsKey(2) && tokenInfo[2] != null
                ? tokenInfo[2].ToString() : null;

            StatusText.Text = "セッションを開始中...";

            string accountId = BuildAccountId(EmailBox.Text);
            await SessionManager.CompleteLoginAsync(accountId, auth, refresh, null);
            var frame = Frame;
            frame.BackStack.Clear();
            frame.Navigate(typeof(ChatListPage));
            LumINE.Utils.NotificationNavigation.OpenPending(frame);
        }

        /// <summary>Local account slot key — must match TokenStore.AssertSafeAccountId.</summary>
        private static string BuildAccountId(string email)
        {
            if (string.IsNullOrEmpty(email)) return "default";
            var chars = email.Trim().ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                          c == '.' || c == '_' || c == '-' || c == ':';
                if (!ok) chars[i] = '_';
            }
            return "acc." + new string(chars);
        }

        private static string GeneratePinCode()
        {
            // Generate a uniformly distributed six-digit PIN using the OS CSPRNG.
            const uint limit = 4294000000U; // largest multiple of 1,000,000 below 2^32
            while (true)
            {
                byte[] bytes = CryptoHelper.RandomBytes(4);
                uint value = BitConverter.ToUInt32(bytes, 0);
                if (value < limit)
                    return (value % 1000000U).ToString("D6");
            }
        }

        private static int ReadMetadataInt(JsonObject metadata, string key, int defaultValue)
        {
            try
            {
                if (metadata == null || !metadata.ContainsKey(key)) return defaultValue;
                var value = metadata.GetNamedValue(key);
                if (value.ValueType == JsonValueType.Number)
                    return (int)value.GetNumber();
                if (value.ValueType == JsonValueType.String)
                {
                    int parsed;
                    if (int.TryParse(value.GetString(), out parsed)) return parsed;
                }
            }
            catch { }
            return defaultValue;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(StatusText.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            }
            catch { }
        }
    }
}
