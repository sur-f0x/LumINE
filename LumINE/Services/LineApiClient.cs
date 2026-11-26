using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using LumINE.Utils;
using LumINE.Services.Vyline;

namespace LumINE.Services
{
    // Binary desktop authentication over HTTPS. Talk, sync and refresh use Vyline services.
    public class LineApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly LegyClient _legyClient;
        public LineApiClient()
        {
            var filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            _httpClient = new HttpClient(filter);
            _legyClient = new LegyClient();
        }
        public async Task<Dictionary<int, object>> GetRsaKeyInfoAsync()
        {
            // Binary authentication over the desktop HTTPS service path.
            var fields = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 2, 1) // provider = LINE
            };
            byte[] data = ThriftBinary.PackBinary("getRSAKeyInfo", fields);
            return await PostLegyAsync(LineApiConfig.EpLegyTalkDo, data);
        }

        public async Task<Dictionary<int, object>> LoginV2Async(
            int type, int identityProvider, string identifier, string password,
            bool keepLoggedIn, string accessLocation, string systemName,
            string certificate, string verifier, string secret, int e2eeVersion, string modelName)
        {
            // Binary authentication over the desktop HTTPS service path.
            // secret field 10 is Thrift BINARY: decode base64 to raw bytes when present.
            // Optional Thrift fields must be omitted when absent. Sending an
            // empty STRING is not equivalent to an absent field (the server
            // may attempt to parse it as a certificate, verifier, or secret).
            object secretVal = null;
            if (!string.IsNullOrEmpty(secret))
            {
                try { secretVal = Convert.FromBase64String(secret); }
                catch { secretVal = secret; }
            }
            var loginFields = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.I32, 1, type),
                new ThriftField(ThriftTypes.I32, 2, identityProvider),
                new ThriftField(ThriftTypes.String, 3, identifier),
                new ThriftField(ThriftTypes.String, 4, password),
                new ThriftField(ThriftTypes.Bool, 5, keepLoggedIn),
                new ThriftField(ThriftTypes.String, 6, accessLocation ?? ""),
                new ThriftField(ThriftTypes.String, 7, systemName),
                new ThriftField(ThriftTypes.String, 8, string.IsNullOrEmpty(certificate) ? null : certificate),
                new ThriftField(ThriftTypes.String, 9, string.IsNullOrEmpty(verifier) ? null : verifier),
                new ThriftField(ThriftTypes.String, 10, secretVal),
                new ThriftField(ThriftTypes.I32, 11, e2eeVersion),
                new ThriftField(ThriftTypes.String, 12, modelName),
            };
            var outer = new List<ThriftField>
            {
                new ThriftField(ThriftTypes.Struct, 2, loginFields)
            };
            byte[] data = ThriftBinary.PackBinary("loginV2", outer);
            return await PostLegyAsync(LineApiConfig.EpLegyRs, data);
        }

        public async Task<string> GetLegyPinAsync(string accessSession)
        {
            Uri uri = new Uri(LineApiConfig.HostLegyPin + LineApiConfig.EpLegyPin);
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAppendWithoutValidation("x-line-application", DesktopIdentity.ApplicationHeader);
            request.Headers.TryAppendWithoutValidation("x-line-access", accessSession);
            request.Headers.TryAppendWithoutValidation("x-lal", DesktopIdentity.Lal);
            request.Headers.TryAppendWithoutValidation("x-lpv", DesktopIdentity.Lpv);
            request.Headers.TryAppendWithoutValidation("x-lhm", "GET");
            request.Headers.TryAppendWithoutValidation("User-Agent", DesktopIdentity.UserAgent);
            request.Headers.TryAppendWithoutValidation("accept", "application/x-thrift");
            request.Headers.TryAppendWithoutValidation("accept-encoding", "gzip");

            var response = await _httpClient.SendRequestAsync(request);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)response.StatusCode}: {responseString}");
            }

            return responseString;
        }

        public Task<Dictionary<int, object>> PostLegyAsync(string endpoint, byte[] data, string innerToken = null)
        {
            return _legyClient.PostLegyAsync(endpoint, data, innerToken);
        }
    }
}
