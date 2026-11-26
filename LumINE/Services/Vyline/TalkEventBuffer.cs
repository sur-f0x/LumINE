using System;
using System.Collections.Generic;

namespace LumINE.Services.Vyline
{
    public enum TalkEventKind
    {
        Message,
        Revoke,
        Read,
        Reaction,
        CallIncoming,
        CallEnd,
        CallCancel,
        Membership,
        ChatUpdate,
        Announce
    }

    public class TalkPollEvent
    {
        public long Seq { get; set; }
        public TalkEventKind Kind { get; set; }
        public string ChatMid { get; set; }
        public string MessageId { get; set; }
        public string Text { get; set; }
        public Dictionary<int, object> RawMessage { get; set; }
        public string MembershipEvent { get; set; } // invited|joined|left|kicked
        public string TargetMid { get; set; }
        public string CallerMid { get; set; }
        public string ReactionActor { get; set; }
        public int? ReactionType { get; set; }
        public LumINE.Models.MessageReaction CustomReaction { get; set; }
        public long ReactionAtMillis { get; set; }
        public string CallMid { get; set; }
    }

    /// <summary>
    /// Per-account talk event buffer (vyline talkEventBuffer.ts).
    /// MAX_EVENTS=400, monotonically increasing seq.
    /// </summary>
    public class TalkEventBuffer
    {
        public const int MaxEvents = 400;

        private readonly object _lock = new object();
        private long _seq;
        private readonly List<TalkPollEvent> _events = new List<TalkPollEvent>();

        public long CurrentSeq
        {
            get { lock (_lock) return _seq; }
        }

        public TalkPollEvent Push(TalkPollEvent payload)
        {
            lock (_lock)
            {
                _seq += 1;
                payload.Seq = _seq;
                _events.Add(payload);
                if (_events.Count > MaxEvents)
                    _events.RemoveRange(0, _events.Count - MaxEvents);
                return payload;
            }
        }

        public void PushAtSequence(TalkPollEvent payload, long seq)
        {
            lock (_lock)
            {
                foreach (var e in _events)
                {
                    if (e.Seq == seq) return;
                }
                payload.Seq = seq;
                _events.Add(payload);
                _events.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                if (_events.Count > MaxEvents)
                    _events.RemoveRange(0, _events.Count - MaxEvents);
                if (seq > _seq) _seq = seq;
            }
        }

        public void Restore(List<TalkPollEvent> events, long seq)
        {
            lock (_lock)
            {
                if (_seq >= seq && _events.Count > 0) return;
                var merged = new Dictionary<long, TalkPollEvent>();
                foreach (var e in _events) merged[e.Seq] = e;
                if (events != null)
                {
                    foreach (var e in events) merged[e.Seq] = e;
                }
                var ordered = new List<TalkPollEvent>(merged.Values);
                ordered.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                if (ordered.Count > MaxEvents)
                    ordered.RemoveRange(0, ordered.Count - MaxEvents);
                _events.Clear();
                _events.AddRange(ordered);
                long maxSeq = seq;
                foreach (var e in _events) if (e.Seq > maxSeq) maxSeq = e.Seq;
                _seq = maxSeq;
            }
        }

        public TalkEventBatch Drain(long afterSeq)
        {
            lock (_lock)
            {
                var batch = new TalkEventBatch { Seq = _seq, Cursor = afterSeq, Reset = false };
                bool reset = afterSeq > 0 && (_events.Count == 0 || _seq < afterSeq || afterSeq < _seq - MaxEvents);
                if (reset)
                {
                    batch.Reset = true;
                    batch.Cursor = _seq;
                    return batch;
                }
                var events = new List<TalkPollEvent>();
                foreach (var e in _events)
                {
                    if (e.Seq > afterSeq) events.Add(e);
                }
                batch.Events = events;
                if (events.Count > 0)
                    batch.Cursor = events[events.Count - 1].Seq;
                else
                    batch.Cursor = afterSeq;
                return batch;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _events.Clear();
                _seq = 0;
            }
        }
    }

    public class TalkEventBatch
    {
        public long Cursor { get; set; }
        public List<TalkPollEvent> Events { get; set; } = new List<TalkPollEvent>();
        public bool Reset { get; set; }
        public long Seq { get; set; }
    }

    /// <summary>Static registry of per-account buffers (vyline module-level Map).</summary>
    public static class TalkEventBuffers
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, TalkEventBuffer> _buffers =
            new Dictionary<string, TalkEventBuffer>();

        public static TalkEventBuffer Get(string accountId)
        {
            lock (_lock)
            {
                TalkEventBuffer buf;
                if (!_buffers.TryGetValue(accountId, out buf))
                {
                    buf = new TalkEventBuffer();
                    _buffers[accountId] = buf;
                }
                return buf;
            }
        }

        public static void Clear(string accountId)
        {
            lock (_lock)
            {
                TalkEventBuffer buf;
                if (_buffers.TryGetValue(accountId, out buf))
                    buf.Clear();
                _buffers.Remove(accountId);
            }
        }
    }
}
