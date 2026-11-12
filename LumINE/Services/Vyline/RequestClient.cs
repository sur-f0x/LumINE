using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public class ThriftRpcException : Exception
    {
        public int Code { get; private set; }
        public string RpcMessage { get; private set; }
        public Dictionary<int, object> ErrorFields { get; private set; }

        public ThriftRpcException(int code, string message, Dictionary<int, object> fields)
            : base("[code=" + code + "] " + message)
        {
            Code = code;
            RpcMessage = message;
            ErrorFields = fields;
        }
    }

    public class ThriftResult
    {
        public object Success { get; set; }
        public Dictionary<int, object> Fields { get; set; }
        public bool HasRefreshPending { get; set; }
        public Dictionary<int, object> ErrorFields { get; set; }
    }

    /// <summary>
    /// vyline RequestClient port: Desktop headers, compact thrift,
    /// MUST_REFRESH_V3_TOKEN retry, x-line-next-access rotation, Legy path routing.
    /// </summary>
    public class RequestClient
    {
        private static readonly HashSet<string> LegyTalkPaths = new HashSet<string>
        {
            "/S3", "/S4", "/V4", "/SYNC3", "/SYNC4", "/P4", "/P5", "/NP4", "/NP5",
            "/C5", "/CA5", "/ECA5", "/TSHOP4"
        };

        public string Endpoint { get; private set; }
        public string SystemType { get; private set; }
        public string UserAgent { get; private set; }

        private readonly HttpClient _http;
        private readonly LegyClient _legy;
        private readonly Func<string> _getToken;
        private readonly Action<string> _setToken;
        private readonly Func<string> _getRefreshToken;
        private readonly Func<Task> _tryRefreshToken;

        public RequestClient(
            LegyClient legy,
            Func<string> getToken,
            Action<string> setToken,
            Func<string> getRefreshToken,
            Func<Task> tryRefreshToken)
        {
            Endpoint = DesktopIdentity.Endpoint;
            SystemType = DesktopIdentity.ApplicationHeader;
            UserAgent = DesktopIdentity.UserAgent;
            _legy = legy;
            _getToken = getToken;
            _setToken = setToken;
            _getRefreshToken = getRefreshToken;
            _tryRefreshToken = tryRefreshToken;

            var filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.MostRecent;
            _http = new HttpClient(filter);
        }

        public Dictionary<string, string> GetHeader(string overrideMethod = "POST")
        {
            var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Host", Endpoint },
                { "accept", "application/x-thrift" },
                { "user-agent", UserAgent },
                { "x-line-application", SystemType },
                { "content-type", "application/x-thrift" },
                { "x-lal", DesktopIdentity.Lal },
                { "x-lpv", DesktopIdentity.Lpv },
                { "x-lhm", overrideMethod },
                { "accept-encoding", "gzip" },
            };
            var token = _getToken();
            if (!string.IsNullOrEmpty(token))
                header["x-line-access"] = token;
            header["x-lap"] = DesktopIdentity.Lap;
            return header;
        }

        public static bool IsLegyTalkPath(string path)
        {
            return LegyTalkPaths.Contains(path);
        }

        public static bool ShouldUseLegyEncryptedAccess(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            var value = token.Trim();
            if (IsJwt(value)) return true;
            int colon = value.IndexOf(':');
            if (colon < 0) return false;
            var payload = value.Substring(colon + 1);
            if (IsJwt(payload)) return true;
            if (IsPrimaryAccessToken(value)) return true;
            return LooksLikeAuthKey(value);
        }

        private static bool IsJwt(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split('.');
            if (parts.Length != 3) return false;
            try
            {
                var headerJson = Base64UrlDecodeToString(parts[0]);
                return headerJson.Contains("\"alg\"");
            }
            catch { return false; }
        }

        private static bool IsPrimaryAccessToken(string value)
        {
            int colon = value.IndexOf(':');
            if (colon < 0) return false;
            var payload = value.Substring(colon + 1);
            int dot = payload.IndexOf('.');
            if (dot < 0) return false;
            try
            {
                var first = Convert.FromBase64String(PadBase64(payload.Substring(0, dot)));
                var text = System.Text.Encoding.UTF8.GetString(first);
                return text.StartsWith("iat:");
            }
            catch { return false; }
        }

        private static bool LooksLikeAuthKey(string value)
        {
            int colon = value.IndexOf(':');
            if (colon < 0) return false;
            var mid = value.Substring(0, colon);
            if (mid.Length != 34) return false;
            if (!char.IsLetter(mid[0])) return false;
            for (int i = 1; i < mid.Length; i++)
            {
                char c = mid[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            try
            {
                var payload = value.Substring(colon + 1);
                return Convert.FromBase64String(PadBase64(payload)).Length > 0;
            }
            catch { return false; }
        }

        private static string PadBase64(string s)
        {
            var t = s.Replace('-', '+').Replace('_', '/');
            while (t.Length % 4 != 0) t += "=";
            return t;
        }

        private static string Base64UrlDecodeToString(string s)
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(s)));
        }

        private bool ShouldUseLegyEncryptedRequest(string path, Dictionary<string, string> headers)
        {
            string access;
            if (!headers.TryGetValue("x-line-access", out access)) return false;
            if (string.IsNullOrEmpty(access)) return false;
            return IsLegyTalkPath(path) && ShouldUseLegyEncryptedAccess(access);
        }

        public async Task<ThriftResult> RequestCoreAsync(
            string path,
            List<ThriftField> value,
            string methodName,
            Dictionary<string, string> appendHeaders,
            bool isReRequest,
            int timeoutMs,
            CancellationToken cancellationToken,
            bool binaryProtocol = false)
        {
            var headers = GetHeader("POST");
            if (appendHeaders != null)
            {
                foreach (var kv in appendHeaders)
                {
                    if (kv.Value == null) headers.Remove(kv.Key);
                    else headers[kv.Key] = kv.Value;
                }
            }
            headers["connection"] = "close";

            byte[] body = binaryProtocol ? ThriftBinary.PackBinary(methodName, value, 0) : ThriftCompact.PackCompact(methodName, value, 0);

            byte[] parsedBody;
            string nextAccess;
            int httpStatus;

            if (ShouldUseLegyEncryptedRequest(path, headers))
            {
                string access;
                headers.TryGetValue("x-line-access", out access);
                var legyRes = await _legy.PostLegyRawAsync(path, body, access, headers, timeoutMs, cancellationToken).ConfigureAwait(false);
                parsedBody = legyRes.Body;
                nextAccess = legyRes.NextAccess;
                httpStatus = legyRes.Status;
            }
            else
            {
                var plain = await PostPlainAsync(path, body, headers, timeoutMs, cancellationToken).ConfigureAwait(false);
                parsedBody = plain.Body;
                nextAccess = plain.NextAccess;
                httpStatus = plain.Status;
            }

            if (!string.IsNullOrEmpty(nextAccess))
                _setToken(nextAccess);

            if (parsedBody == null || parsedBody.Length == 0)
            {
                if (httpStatus < 200 || httpStatus >= 300)
                    throw new ThriftRpcException(httpStatus, "HTTP " + httpStatus, null);
                return new ThriftResult { Success = true, Fields = new Dictionary<int, object>() };
            }

            Dictionary<int, object> fields;
            try
            {
                fields = parsedBody[0] == 0x80 ? ThriftBinary.UnpackBinaryMessage(parsedBody) : ThriftCompact.UnpackCompactMessage(parsedBody);
            }
            catch (Exception ex)
            {
                throw new ThriftRpcException(0, "Invalid response buffer for " + methodName + "(" + path + "): status=" + httpStatus + " bytes=" + parsedBody.Length + " " + ex.Message, null);
            }

            bool successPresent = fields.ContainsKey(0);
            bool errorPresent = fields.ContainsKey(1);

            string errorCode = null;
            Dictionary<int, object> exFields = null;
            if (errorPresent)
            {
                exFields = fields[1] as Dictionary<int, object>;
                if (exFields != null)
                {
                    object codeObj;
                    if (exFields.TryGetValue(1, out codeObj) && codeObj != null)
                        errorCode = codeObj.ToString();
                }
            }

            bool isRefresh = errorCode == "MUST_REFRESH_V3_TOKEN" && !string.IsNullOrEmpty(_getRefreshToken());

            if (errorPresent && !isRefresh)
            {
                int code = 0;
                string msg = "";
                if (exFields != null)
                {
                    object c, m;
                    if (exFields.TryGetValue(1, out c) && c != null) int.TryParse(c.ToString(), out code);
                    if (exFields.TryGetValue(2, out m) && m != null) msg = m.ToString();
                }
                throw new ThriftRpcException(code, msg, exFields);
            }

            if (!successPresent && errorPresent && !isRefresh)
            {
                throw new ThriftRpcException(0, "Request internal failed, " + methodName + "(" + path + ")", fields);
            }

            if (!successPresent && !errorPresent && httpStatus >= 400)
            {
                throw new ThriftRpcException(httpStatus, "Request internal failed, " + methodName + "(" + path + ") status=" + httpStatus, null);
            }

            if (isRefresh && !isReRequest)
            {
                await _tryRefreshToken().ConfigureAwait(false);
                return await RequestCoreAsync(path, value, methodName, appendHeaders, true, timeoutMs, cancellationToken, binaryProtocol).ConfigureAwait(false);
            }

            object success = null;
            if (successPresent) success = fields[0];
            return new ThriftResult
            {
                Success = success,
                Fields = fields,
                HasRefreshPending = errorPresent && isRefresh,
                ErrorFields = exFields
            };
        }

        public async Task<object> RequestAsync(
            string path,
            List<ThriftField> value,
            string methodName,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var res = await RequestCoreAsync(path, value, methodName, null, false, timeoutMs, cancellationToken).ConfigureAwait(false);
            return res.Success;
        }

        private class PlainPostResult
        {
            public byte[] Body;
            public string NextAccess;
            public int Status;
        }

        private async Task<PlainPostResult> PostPlainAsync(
            string path, byte[] body, Dictionary<string, string> headers,
            int timeoutMs, CancellationToken cancellationToken)
        {
            var uri = new Uri("https://" + Endpoint + path);
            var request = new HttpRequestMessage(HttpMethod.Post, uri);
            foreach (var kv in headers)
            {
                if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                try { request.Headers.Add(kv.Key, kv.Value); }
                catch { }
            }
            request.Content = new HttpBufferContent(body.AsBuffer());
            request.Content.Headers.ContentType = new Windows.Web.Http.Headers.HttpMediaTypeHeaderValue("application/x-thrift");

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (timeoutMs > 0) timeoutCts.CancelAfter(timeoutMs);
                var asyncOp = _http.SendRequestAsync(request);
                var response = await asyncOp.AsTask(timeoutCts.Token).ConfigureAwait(false);
                using (response)
                {
                    string nextAccess = null;
                    if (response.Headers.ContainsKey("x-line-next-access"))
                        nextAccess = response.Headers["x-line-next-access"];
                    var buffer = await response.Content.ReadAsBufferAsync().AsTask(timeoutCts.Token).ConfigureAwait(false);
                    var bytes = new byte[buffer.Length];
                    DataReader.FromBuffer(buffer).ReadBytes(bytes);
                    return new PlainPostResult
                    {
                        Body = bytes,
                        NextAccess = nextAccess,
                        Status = (int)response.StatusCode
                    };
                }
            }
        }
    }
}
