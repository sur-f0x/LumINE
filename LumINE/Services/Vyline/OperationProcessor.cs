using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// processFetchedOperations port: stage events → commit → push to TalkEventBuffer.
    /// Operation type matching mirrors vyline lineService.processSingleOperation.
    /// </summary>
    public class OperationProcessor
    {
        private readonly LineSession _session;

        public OperationProcessor(LineSession session)
        {
            _session = session;
        }

        public async Task ProcessAsync(List<Dictionary<int, object>> ops)
        {
            if (ops == null || ops.Count == 0) return;
            var staged = new List<TalkPollEvent>();
            foreach (var op in ops)
            {
                try
                {
                    var ev = await ProcessSingleAsync(op).ConfigureAwait(false);
                    if (ev != null) staged.Add(ev);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Ops] process failed; cursor will not advance: " + ex.Message);
                    throw;
                }
            }
            // commit: push staged events
            foreach (var ev in staged)
            {
                if (ev.Kind == TalkEventKind.Revoke)
                    await _session.Store.ApplyRemoteRevokeAsync(ev.ChatMid, ev.MessageId).ConfigureAwait(false);
                if (ev.Kind == TalkEventKind.Reaction && ev.ReactionType.HasValue && !string.IsNullOrEmpty(ev.ReactionActor))
                    await _session.Store.ApplyReactionAsync(ev.ChatMid, ev.MessageId, ev.ReactionActor, ev.ReactionType.Value, ev.ReactionAtMillis).ConfigureAwait(false);
                else if (ev.Kind == TalkEventKind.Reaction && ev.CustomReaction != null && !string.IsNullOrEmpty(ev.ReactionActor))
                    await _session.Store.ApplyCustomReactionAsync(ev.ChatMid, ev.MessageId, ev.ReactionActor, ev.CustomReaction, ev.ReactionAtMillis).ConfigureAwait(false);
                // Notification delivery must not wait for the hidden UI's
                // event-drain timer: the long-poll loop remains the receiver.
                if (ev.Kind == TalkEventKind.Message && ev.RawMessage != null)
                    await BackgroundNotifications.NotifyAsync(_session, ChatStore.MapMessage(ev.RawMessage, ev.ChatMid)).ConfigureAwait(false);
                _session.Events.Push(ev);
            }
        }

        public async Task<TalkPollEvent> ProcessSingleAsync(Dictionary<int, object> op)
        {
            op = NormalizeOperation(op);
            // Operation fields (common LINE layout):
            //   1=type (i32/string), 2=param1, 3=param2, 4=param3, 5=message, 6=revision / etc.
            // Type may be int or string depending on server.
            string type = OpTypeString(op);
            string param1 = OpParam(op, 2);
            string param2 = OpParam(op, 3);
            string param3 = OpParam(op, 4);

            // RECEIVE_MESSAGE variants
            if (IsReceiveMessageType(type))
            {
                var msg = OpStruct(op, 5) ?? OpStruct(op, 1);
                if (msg == null) return null;
                var chatMid = ChatMidFromRaw(msg);
                var ev = new TalkPollEvent
                {
                    Kind = TalkEventKind.Message,
                    ChatMid = chatMid,
                    RawMessage = msg,
                    MessageId = GetStr(msg, 4)
                };
                return ev;
            }

            // DESTROY_MESSAGE / NOTIFIED_DESTROY_MESSAGE
            if (type == "DESTROY_MESSAGE" || type == "7" ||
                type == "NOTIFIED_DESTROY_MESSAGE" || type == "8")
            {
                string chat = IsChatMid(param1) ? param1 : IsChatMid(param2) ? param2 : null;
                string id = IsChatMid(param1) ? param2 : param1;
                if (!string.IsNullOrEmpty(id) && chat != null)
                {
                    return new TalkPollEvent
                    {
                        Kind = TalkEventKind.Revoke,
                        ChatMid = chat,
                        MessageId = id
                    };
                }
                return null;
            }

            // Read notifications
            if (IsReadType(type))
            {
                if (IsChatMid(param1))
                    return new TalkPollEvent { Kind = TalkEventKind.Read, ChatMid = param1 };
                return null;
            }

            // Reactions
            if (type == "SEND_REACTION" || type == "139" ||
                type == "NOTIFIED_SEND_REACTION" || type == "140" ||
                type == "NOTIFIED_GCS_REACTION" || type == "154")
            {
                var reaction = ReactionEventDecoder.Parse(type, param2, param3, _session.Mid);
                if (!string.IsNullOrEmpty(param1) && IsChatMid(reaction.ChatMid))
                {
                    long at = 0;
                    long.TryParse(OpParam(op, 7), out at);
                    if (at > 0 && at < 100000000000L) at *= 1000;
                    return new TalkPollEvent
                    {
                        Kind = TalkEventKind.Reaction,
                        ChatMid = reaction.ChatMid,
                        MessageId = param1,
                        ReactionActor = reaction.ActorMid,
                        ReactionType = reaction.Type,
                        CustomReaction = reaction.CustomReaction,
                        ReactionAtMillis = at > 0 ? at : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }
                return null;
            }

            // Calls
            if (type == "NOTIFIED_RECEIVED_CALL" || type == "3")
            {
                // best-effort: param1=callMid, param2=chatMid/caller
                var chatMid = IsChatMid(param2) ? param2 : (IsChatMid(param1) ? param1 : null);
                if (chatMid == null) return null;
                return new TalkPollEvent
                {
                    Kind = TalkEventKind.CallIncoming,
                    ChatMid = chatMid,
                    CallMid = param1,
                    CallerMid = param2
                };
            }
            if (type == "NOTIFIED_CANCEL_CALL" || type == "NOTIFIED_MISSED_CALL" || type == "4")
            {
                var chatMid = IsChatMid(param1) ? param1 : (IsChatMid(param2) ? param2 : null);
                if (chatMid == null) return null;
                return new TalkPollEvent
                {
                    Kind = TalkEventKind.CallCancel,
                    ChatMid = chatMid,
                    CallMid = param1,
                    CallerMid = param2
                };
            }
            if (type == "NOTIFIED_CALL_STATUS" || type == "5")
            {
                if (IsChatMid(param1))
                    return new TalkPollEvent { Kind = TalkEventKind.CallEnd, ChatMid = param1 };
                return null;
            }

            // Membership
            if (type == "NOTIFIED_INVITE_INTO_CHAT" || type == "33")
            {
                if (IsChatMid(param1))
                    return new TalkPollEvent
                    {
                        Kind = TalkEventKind.Membership,
                        ChatMid = param1,
                        MembershipEvent = "invited"
                    };
                return null;
            }
            if (type == "NOTIFIED_ACCEPT_CHAT_INVITATION" || type == "NOTIFIED_JOIN_CHAT" || type == "35")
            {
                if (IsChatMid(param1))
                    return new TalkPollEvent
                    {
                        Kind = TalkEventKind.Membership,
                        ChatMid = param1,
                        MembershipEvent = "joined"
                    };
                return null;
            }
            if (type == "NOTIFIED_LEAVE_CHAT" || type == "32" || type == "NOTIFIED_KICKOUT_FROM_CHAT" || type == "34")
            {
                if (IsChatMid(param1))
                {
                    bool kick = type == "NOTIFIED_KICKOUT_FROM_CHAT" || type == "34";
                    return new TalkPollEvent
                    {
                        Kind = TalkEventKind.Membership,
                        ChatMid = param1,
                        MembershipEvent = kick ? "kicked" : "left",
                        TargetMid = param2
                    };
                }
                return null;
            }

            // Chat update
            if (type == "NOTIFIED_UPDATE_CHAT" || type == "13")
            {
                if (IsChatMid(param1))
                    return new TalkPollEvent { Kind = TalkEventKind.ChatUpdate, ChatMid = param1 };
                return null;
            }

            // Announce
            if (type == "CHATEVENT_NOTIFIED_ANNOUNCE" || type == "NOTIFIED_UPDATE_CHAT_ROOM_ANNOUNCEMENT")
            {
                if (IsChatMid(param1))
                {
                    var text = !string.IsNullOrEmpty(param3) ? param3 : param2;
                    return new TalkPollEvent
                    {
                        Kind = TalkEventKind.Announce,
                        ChatMid = param1,
                        Text = text
                    };
                }
                return null;
            }

            return null;
        }

        private static string OpTypeString(Dictionary<int, object> op)
        {
            object t;
            // try common field ids for type
            foreach (var k in new[] { 1, 0, 2, 7, 8 })
            {
                if (op.TryGetValue(k, out t) && t != null)
                {
                    // skip if looks like message struct
                    if (t is Dictionary<int, object>) continue;
                    return t.ToString();
                }
            }
            return "";
        }

        public static Dictionary<int, object> NormalizeOperation(Dictionary<int, object> op)
        {
            object type, revision, message, p1, p2, p3;
            if (op == null) return new Dictionary<int, object>();
            // Wire Operation: revision=1, createdTime=2, type=3,
            // param1/2/3=10/11/12 and message=20. The older internal
            // shape used type=1 and message=5; never treat a revision as type.
            if (!op.TryGetValue(3, out type) || !(type is int || type is short || type is long) ||
                !(op.ContainsKey(20) || op.ContainsKey(10) || (op.TryGetValue(1, out revision) && revision is long))) return op;
            string name;
            switch (Convert.ToInt32(type))
            {
                case 25: name = "SEND_MESSAGE"; break;
                case 26: name = "RECEIVE_MESSAGE"; break;
                case 64: name = "DESTROY_MESSAGE"; break;
                case 65: name = "NOTIFIED_DESTROY_MESSAGE"; break;
                case 55: case 28: case 91: name = "NOTIFIED_READ_MESSAGE"; break;
                case 139: name = "SEND_REACTION"; break;
                case 140: name = "NOTIFIED_SEND_REACTION"; break;
                case 154: name = "NOTIFIED_GCS_REACTION"; break;
                default: name = "WIRE_" + type; break;
            }
            var result = new Dictionary<int, object> { { 1, name } };
            if (op.TryGetValue(10, out p1)) result[2] = p1;
            if (op.TryGetValue(11, out p2)) result[3] = p2;
            if (op.TryGetValue(12, out p3)) result[4] = p3;
            if (op.TryGetValue(20, out message)) result[5] = message;
            if (op.TryGetValue(1, out revision)) result[6] = revision;
            object created;
            if (op.TryGetValue(2, out created)) result[7] = created;
            return result;
        }

        private static string OpParam(Dictionary<int, object> op, int field)
        {
            object v;
            if (op.TryGetValue(field, out v) && v != null) return v.ToString();
            return null;
        }

        private static Dictionary<int, object> OpStruct(Dictionary<int, object> op, int field)
        {
            object v;
            if (op.TryGetValue(field, out v)) return v as Dictionary<int, object>;
            return null;
        }

        private static string GetStr(Dictionary<int, object> d, int field)
        {
            object v;
            if (d.TryGetValue(field, out v) && v != null) return v.ToString();
            return null;
        }

        private static bool IsReceiveMessageType(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            return type == "RECEIVE_MESSAGE" || type == "SEND_MESSAGE" || type == "1" ||
                   type == "NOTIFIED_RECEIVE_MESSAGE" || type == "2" ||
                   type == "0" || type == "MESSAGE";
        }

        private static bool IsReadType(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            return type == "NOTIFIED_MARK_AS_READ" || type == "16" ||
                   type == "NOTIFIED_READ_MESSAGE" || type == "17" ||
                   type == "MARK_AS_READ" || type == "18" ||
                   type == "NOTIFIED_READ" || type == "15";
        }

        private static bool IsChatMid(string mid)
        {
            if (string.IsNullOrEmpty(mid)) return false;
            char c = mid[0];
            return c == 'u' || c == 'c' || c == 'r';
        }

        private string ChatMidFromRaw(Dictionary<int, object> msg)
        {
            string from = GetStr(msg, 1) ?? "";
            string to = GetStr(msg, 2) ?? "";
            if (to.Length > 0 && (to[0] == 'c' || to[0] == 'r')) return to;
            var myMid = _session.Mid ?? "";
            if (from == myMid) return to;
            return from;
        }
    }
}
