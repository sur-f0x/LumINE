using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LumINE.Utils
{
    public sealed class MessageTextPart
    {
        public string Text { get; set; }
        public Uri Uri { get; set; }
    }

    public static class MessageLinks
    {
        private static readonly Regex Url = new Regex(@"(?:https?://|www\.)[^\s<>""'\u3000「」『』【】]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static List<MessageTextPart> Split(string text)
        {
            text = text ?? "";
            var parts = new List<MessageTextPart>();
            int offset = 0;
            foreach (Match match in Url.Matches(text))
            {
                string candidate = match.Value.TrimEnd('.', ',', '!', '?', ';', ':', '。', '、', '！', '？', '）', '］', '｝');
                while (candidate.Length > 0 && (UnbalancedEnd(candidate, '(', ')') || UnbalancedEnd(candidate, '[', ']') || UnbalancedEnd(candidate, '{', '}')))
                    candidate = candidate.Substring(0, candidate.Length - 1);
                Uri uri;
                if (!Uri.TryCreate(candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + candidate : candidate, UriKind.Absolute, out uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host)) continue;
                if (match.Index > offset) parts.Add(new MessageTextPart { Text = text.Substring(offset, match.Index - offset) });
                parts.Add(new MessageTextPart { Text = candidate, Uri = uri });
                offset = match.Index + candidate.Length;
            }
            if (offset < text.Length) parts.Add(new MessageTextPart { Text = text.Substring(offset) });
            return parts;
        }

        private static bool UnbalancedEnd(string text, char open, char close)
        {
            if (text[text.Length - 1] != close) return false;
            int count = 0;
            foreach (char c in text) { if (c == open) count++; else if (c == close) count--; }
            return count < 0;
        }
    }
}
