using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// Per-account LINE session (BaseClient port): token, reqseq, services, ops loop.
    /// </summary>
    public class LineSession : IDisposable
    {
        public string AccountId { get; private set; }
        public string Mid { get; set; }
        public string AuthToken { get; set; }
        public string RefreshToken { get; set; }
        public long Expire { get; set; }
        public Profile Profile { get; set; }
        public bool Disabled { get; private set; }

        public LegyClient Legy { get; private set; }
        public RequestClient Request { get; private set; }
        public AuthService Auth { get; private set; }
        public TalkService Talk { get; private set; }
        public OpsPoller Ops { get; private set; }
        public TalkEventBuffer Events { get; private set; }

        private readonly object _reqseqLock = new object();
        private int _talkReqseq;
        private OpsRevisionCursor _cursor = new OpsRevisionCursor();

        // Shared frontend services (created lazily, one per session)
        private readonly object _svcLock = new object();
        private ChatStore _store;
        private ReadEngine _read;
        private LineService _line;
        private SyncScheduler _sync;
        private bool _syncStarted;

        public event Action<string> AccessTokenChanged;
        public event Action ProfileReady;

        public LineSession(string accountId, string authToken, string refreshToken, string mid)
        {
            AccountId = accountId;
            AuthToken = authToken;
            RefreshToken = refreshToken;
            Mid = mid;
            Events = TalkEventBuffers.Get(accountId);

            Legy = new LegyClient();
            Request = new RequestClient(
                Legy,
                () => AuthToken,
                t =>
                {
                    AuthToken = t;
                    var h = AccessTokenChanged;
                    if (h != null) h(t);
                    PersistTokenFireAndForget();
                },
                () => RefreshToken,
                () => Auth.TryRefreshTokenAsync());

            Auth = new AuthService(
                Request,
                () => RefreshToken,
                rt => { RefreshToken = rt; PersistTokenFireAndForget(); },
                t =>
                {
                    AuthToken = t;
                    var h = AccessTokenChanged;
                    if (h != null) h(t);
                    PersistTokenFireAndForget();
                },
                e => { Expire = e; PersistTokenFireAndForget(); });

            Auth.AccessTokenUpdated += t =>
            {
                var h = AccessTokenChanged;
                if (h != null) h(t);
            };

            Talk = new TalkService(Request, () => GetReqseq());

            Ops = new OpsPoller(
                Talk, accountId,
                () => _cursor,
                c => { _cursor = c; },
                c => OpsRevisionCursor.PersistAsync(accountId, c),
                ProcessOpsAsync);
        }

        public int GetReqseq(string name = "talk")
        {
            lock (_reqseqLock)
            {
                // vyline: incrementing per-name seq stored in storage; start from time-based
                if (_talkReqseq == 0)
                    _talkReqseq = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x3fffffff);
                _talkReqseq++;
                if (_talkReqseq > 0x3fffffff) _talkReqseq = 1;
                return _talkReqseq;
            }
        }

        private void PersistTokenFireAndForget()
        {
            if (Disabled) return;
            try
            {
                var account = new StoredAccount
                {
                    AccountId = AccountId,
                    Mid = Mid,
                    AuthToken = AuthToken,
                    RefreshToken = RefreshToken,
                    Expire = Expire,
                    DisplayName = Profile != null ? Profile.DisplayName : null
                };
                Task.Run(() => TokenStore.SaveAsync(account));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Session] persist token failed: " + ex.Message);
            }
        }

        public async Task LoadCursorAsync()
        {
            _cursor = await OpsRevisionCursor.LoadAsync(AccountId).ConfigureAwait(false);
        }

        public OpsRevisionCursor Cursor { get { return _cursor; } }

        /// <summary>Local-first chat roster store.</summary>
        public ChatStore Store
        {
            get { EnsureServices(); return _store; }
        }

        /// <summary>Read receipt engine.</summary>
        public ReadEngine Read
        {
            get { EnsureServices(); return _read; }
        }

        /// <summary>sendMessage / markAsRead / fetch helpers.</summary>
        public LineService Line
        {
            get { EnsureServices(); return _line; }
        }

        /// <summary>Frontend sync timers (events / chats / read).</summary>
        public SyncScheduler Sync
        {
            get { EnsureServices(); return _sync; }
        }

        private void EnsureServices()
        {
            lock (_svcLock)
            {
                if (_sync != null) return;
                var store = new ChatStore(this);
                var read = new ReadEngine(this, store);
                var line = new LineService(this, store, read);
                var sync = new SyncScheduler(this, store, read);
                _store = store; _read = read; _line = line; _sync = sync;
            }
        }

        /// <summary>
        /// Start sync timers — call once from the UI thread (window focus handlers).
        /// </summary>
        public void EnsureSyncStarted()
        {
            if (Disabled) return;
            EnsureServices();
            lock (_svcLock)
            {
                if (_syncStarted) return;
                _syncStarted = true;
            }
            try { _sync.Start(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Session] sync start failed: " + ex.Message);
                lock (_svcLock) _syncStarted = false;
            }
        }

        /// <summary>vyline startTalkListeners: delay 5s then ops loop.</summary>
        public void StartOpsListener()
        {
            if (Ops != null) Ops.StartDelayed();
        }

        public void StopOpsListener()
        {
            if (Ops != null) Ops.Stop();
        }

        private async Task ProcessOpsAsync(List<Dictionary<int, object>> ops)
        {
            var processor = new OperationProcessor(this);
            await processor.ProcessAsync(ops).ConfigureAwait(false);
        }

        public async Task WarmAsync()
        {
            try
            {
                var p = await Talk.GetProfileAsync().ConfigureAwait(false);
                Profile = MapProfile(p);
                if (Profile != null) Mid = Profile.Mid;
                var h = ProfileReady;
                if (h != null) h();
                PersistTokenFireAndForget();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Session] warm failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Ensure local E2EE identity exists (vyline ensureE2EE).
        /// If no self key stored, generate one and register with LINE.
        /// </summary>
        public async Task EnsureE2eeIdentityAsync()
        {
            try
            {
                var selfKeys = await E2eeKeyStore.LoadSelfKeysAsync(AccountId).ConfigureAwait(false);
                if (selfKeys != null && selfKeys.Count > 0) return;

                // Generate new keypair (Curve25519 / X25519)
                byte[] priv, pub;
                LumINE.Utils.CryptoHelper.X25519GenerateKeyPair(out priv, out pub);

                // Register with LINE
                int version = 1; // Letter Sealing v2
                int keyId = 1;   // first key
                long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await Talk.RegisterE2eePublicKeyAsync(version, keyId, pub, created).ConfigureAwait(false);

                // Persist locally
                var key = new E2eeSelfKey
                {
                    KeyId = keyId,
                    PrivateKey = priv,
                    PublicKey = pub,
                    CreatedTime = created
                };
                await E2eeKeyStore.SaveSelfKeysAsync(AccountId, new List<E2eeSelfKey> { key }).ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine("[Session] E2EE identity created and registered");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Session] ensureE2EE failed: " + ex.Message);
            }
        }

        public static Profile MapProfile(Dictionary<int, object> raw)
        {
            if (raw == null) return null;
            var p = new Profile();
            object v;
            if (raw.TryGetValue(1, out v) && v != null) p.Mid = v.ToString();
            if (raw.TryGetValue(3, out v) && v != null) p.UserId = v.ToString();
            if (raw.TryGetValue(20, out v) && v != null) p.DisplayName = v.ToString();
            if (raw.TryGetValue(24, out v) && v != null) p.StatusMessage = v.ToString();
            if (raw.TryGetValue(22, out v) && v != null) p.PicturePath = v.ToString();
            if (raw.TryGetValue(33, out v) && v != null) p.PicturePath = v.ToString();
            return p;
        }

        public void Dispose()
        {
            Disabled = true;
            StopOpsListener();
            try
            {
                SyncScheduler s;
                lock (_svcLock) s = _sync;
                if (s != null) s.Dispose();
            }
            catch { }
            TalkEventBuffers.Clear(AccountId);
        }
    }

    /// <summary>
    /// Application-level session holder — shared across Login / ChatList / ChatRoom.
    /// </summary>
    public static class SessionManager
    {
        private static LineSession _current;
        private static readonly object _lock = new object();

        public static LineSession Current
        {
            get { lock (_lock) return _current; }
        }

        public static bool IsLoggedIn
        {
            get { lock (_lock) return _current != null && !_current.Disabled && !string.IsNullOrEmpty(_current.AuthToken); }
        }

        public static event Action SessionChanged;

        public static LineSession SetCurrent(LineSession session)
        {
            lock (_lock)
            {
                if (_current != null && !ReferenceEquals(_current, session))
                {
                    try { _current.Dispose(); } catch { }
                }
                _current = session;
            }
            var h = SessionChanged;
            if (h != null) h();
            return session;
        }

        public static void Clear()
        {
            lock (_lock)
            {
                if (_current != null)
                {
                    try { _current.Dispose(); } catch { }
                    _current = null;
                }
            }
            var h = SessionChanged;
            if (h != null) h();
        }

        /// <summary>Stops the active session and removes its saved tokens.</summary>
        public static async Task LogoutAsync()
        {
            string accountId = null;
            lock (_lock)
                if (_current != null) accountId = _current.AccountId;
            if (!string.IsNullOrEmpty(accountId)) await BackgroundNotifications.SetEnabledAsync(accountId, false);
            Clear();
            if (!string.IsNullOrEmpty(accountId))
                await TokenStore.DeleteAsync(accountId).ConfigureAwait(false);
        }

        /// <summary>Complete login: bind tokens, persist, warm profile, start ops after 5s.</summary>
        public static async Task<LineSession> CompleteLoginAsync(
            string accountId, string authToken, string refreshToken, string mid)
        {
            if (!TokenStore.AssertSafeAccountId(accountId))
                throw new ArgumentException("invalid accountId");

            var session = new LineSession(accountId, authToken, refreshToken, mid);
            SetCurrent(session);

            await TokenStore.SaveAsync(new StoredAccount
            {
                AccountId = accountId,
                Mid = mid,
                AuthToken = authToken,
                RefreshToken = refreshToken,
                LoggedInAt = DateTime.UtcNow
            }).ConfigureAwait(false);

            await session.LoadCursorAsync().ConfigureAwait(false);
            await session.WarmAsync().ConfigureAwait(false);
            await session.EnsureE2eeIdentityAsync().ConfigureAwait(false);
            session.StartOpsListener();
            return session;
        }

        /// <summary>Restore session from TokenStore on app launch.</summary>
        public static async Task<bool> TryRestoreAsync(string preferredAccountId = null)
        {
            try
            {
                // A background receive can leave a live session in this process.
                // Do not dispose its timers/listener just to reopen the window.
                var current = Current;
                if (current != null && !current.Disabled && !string.IsNullOrEmpty(current.AuthToken) &&
                    (preferredAccountId == null || current.AccountId == preferredAccountId))
                {
                    current.StartOpsListener();
                    return true;
                }
                System.Diagnostics.Debug.WriteLine("[Session] loading stored accounts");
                var accounts = await TokenStore.ListAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine("[Session] stored account count: " + accounts.Count);
                StoredAccount best = null;
                foreach (var a in accounts)
                {
                    if (string.IsNullOrEmpty(a.AuthToken)) continue;
                    if (preferredAccountId != null && a.AccountId != preferredAccountId) continue;
                    if (best == null) best = a;
                }
                if (best == null) return false;

                var session = new LineSession(best.AccountId, best.AuthToken, best.RefreshToken, best.Mid);
                SetCurrent(session);
                await session.LoadCursorAsync().ConfigureAwait(false);
                // Warm in background; do not block launch
                var warmTask = session.WarmAsync().ContinueWith(_ =>
                    session.EnsureE2eeIdentityAsync().Wait());
                session.StartOpsListener();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Session] restore failed: " + ex.Message);
                return false;
            }
        }
    }
}
