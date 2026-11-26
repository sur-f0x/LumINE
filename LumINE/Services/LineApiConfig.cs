namespace LumINE.Services
{
    // Desktop service paths are posted directly over HTTPS (vyline-api).
    public static class LineApiConfig
    {
        public const string Legy = "https://" + Vyline.DesktopIdentity.Endpoint;
        public const string HostLegyPin = Legy;
        public const string EpLegyPin = "/LF1";
        public const string EpLegyRs = "/api/v3p/rs";
        public const string EpLegyTalkDo = "/api/v3/TalkService.do";
    }
}
