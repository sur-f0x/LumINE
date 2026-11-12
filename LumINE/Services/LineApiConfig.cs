namespace LumINE.Services
{
    // Endpoints used by the binary authentication/Legy transport.
    public static class LineApiConfig
    {
        public const string Legy = "https://gf.line.naver.jp";
        public const string EpLegy = "/enc";
        public const string HostLegyPin = "https://legy.line-apps.com";
        public const string EpLegyPin = "/LF1";
        public const string EpLegyRs = "/api/v3p/rs";
        public const string EpLegyTalkDo = "/api/v3/TalkService.do";
    }
}