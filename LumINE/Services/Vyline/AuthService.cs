using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// AuthService port (vyline stack/base/service/auth/mod.ts).
    /// protocolType=4, requestPath=/AS4, refresh via /EXT/auth/tokenrefresh/v1.
    /// </summary>
    public class AuthService
    {
        public const string RequestPath = "/AS4";
        public const string RefreshPath = "/EXT/auth/tokenrefresh/v1";

        private readonly RequestClient _request;
        private readonly Func<string> _getRefreshToken;
        private readonly Action<string> _setRefreshToken;
        private readonly Action<string> _setAccessToken;
        private readonly Action<long> _setExpire;
        private Task _refreshInflight;

        public event Action<string> AccessTokenUpdated;

        public AuthService(
            RequestClient request,
            Func<string> getRefreshToken,
            Action<string> setRefreshToken,
            Action<string> setAccessToken,
            Action<long> setExpire)
        {
            _request = request;
            _getRefreshToken = getRefreshToken;
            _setRefreshToken = setRefreshToken;
            _setAccessToken = setAccessToken;
            _setExpire = setExpire;
        }

        /// <summary>
        /// tryRefreshToken: single-flight refresh of access token from refreshToken.
        /// </summary>
        public async Task TryRefreshTokenAsync()
        {
            var existing = _refreshInflight;
            if (existing != null)
            {
                await existing.ConfigureAwait(false);
                return;
            }

            var task = RefreshInnerAsync();
            _refreshInflight = task;
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_refreshInflight, task))
                    _refreshInflight = null;
            }
        }

        private async Task RefreshInnerAsync()
        {
            var refreshToken = _getRefreshToken();
            if (string.IsNullOrEmpty(refreshToken))
                throw new ThriftRpcException(0, "RefreshError: refreshToken not found", null);

            var args = ThriftStructs.RefreshArgs(refreshToken);
            var result = await _request.RequestCoreAsync(RefreshPath, args, "refresh", null, false, 15000, CancellationToken.None)
                .ConfigureAwait(false);

            var ratr = result.Success as Dictionary<int, object>;
            if (ratr == null)
                throw new ThriftRpcException(0, "refresh: invalid response", result.Fields);

            object accessToken;
            if (ratr.TryGetValue(1, out accessToken) && accessToken != null)
            {
                var token = accessToken.ToString();
                _setAccessToken(token);
                var handler = AccessTokenUpdated;
                if (handler != null) handler(token);
            }

            object newRefresh;
            if (ratr.TryGetValue(2, out newRefresh) && newRefresh != null)
            {
                var rt = newRefresh.ToString();
                if (!string.IsNullOrEmpty(rt)) _setRefreshToken(rt);
            }

            // expire = tokenIssueTimeEpochSec + durationUntilRefreshInSec
            // fields: 3=tokenIssueTimeEpochSec, 4=durationUntilRefreshInSec (common layout)
            long issue = 0, duration = 0;
            object o3, o4;
            if (ratr.TryGetValue(3, out o3) && o3 != null) long.TryParse(o3.ToString(), out issue);
            if (ratr.TryGetValue(4, out o4) && o4 != null) long.TryParse(o4.ToString(), out duration);
            if (issue > 0 || duration > 0)
                _setExpire(issue + duration);
        }

        public Task NoopAsync()
        {
            return _request.RequestAsync(RequestPath, ThriftStructs.NoopArgs(), "noop");
        }
    }
}
