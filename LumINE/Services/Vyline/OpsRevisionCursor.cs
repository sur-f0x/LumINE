using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// Per-account ops revision cursor (vyline OpsRevisionCursor).
    /// Persisted as JSON under LocalFolder/accounts/{id}/ops-cursor.json.
    /// </summary>
    public class OpsRevisionCursor
    {
        public long Revision { get; set; }
        public long GlobalRev { get; set; }
        public long IndividualRev { get; set; }

        public OpsRevisionCursor Clone()
        {
            return new OpsRevisionCursor
            {
                Revision = Revision,
                GlobalRev = GlobalRev,
                IndividualRev = IndividualRev
            };
        }

        public static long MaxValue(long a, long b) { return a >= b ? a : b; }

        public static OpsRevisionCursor Next(OpsRevisionCursor current, Dictionary<int, object> syncResult)
        {
            var next = current.Clone();
            if (syncResult == null) return next;
            object canonical;
            object value;
            if (syncResult.TryGetValue(2, out canonical) && canonical is Dictionary<int, object> full && full.TryGetValue(2, out value))
            {
                next.Revision = Math.Max(next.Revision, ParseLong(value));
                return next;
            }
            if (syncResult.TryGetValue(1, out canonical) && canonical is Dictionary<int, object> operation &&
                ((operation.TryGetValue(1, out value) && value is List<object>) ||
                 (operation.TryGetValue(2, out value) && value is bool)))
            {
                object events;
                if (operation.TryGetValue(1, out events) && events is List<object> operations)
                    foreach (var item in operations)
                    {
                        var fields = item as Dictionary<int, object>;
                        if (fields != null && fields.TryGetValue(1, out value)) next.Revision = Math.Max(next.Revision, ParseLong(value));
                    }
                if (operation.TryGetValue(3, out events) && events is Dictionary<int, object> global && global.TryGetValue(2, out value)) next.GlobalRev = Math.Max(next.GlobalRev, ParseLong(value));
                if (operation.TryGetValue(4, out events) && events is Dictionary<int, object> individual && individual.TryGetValue(2, out value)) next.IndividualRev = Math.Max(next.IndividualRev, ParseLong(value));
                return next;
            }

            // sync_result layout (vyline):
            //   fullSyncResponse.nextRevision
            //   operationResponse.globalEvents.lastRevision / individualEvents.lastRevision
            //   operationResponse.operations[last].revision
            object fullSync;
            if (syncResult.TryGetValue(2, out fullSync) || syncResult.TryGetValue(1, out fullSync))
            {
                var fs = fullSync as Dictionary<int, object>;
                if (fs != null)
                {
                    object nr;
                    // FullSyncResponse.nextRevision commonly field 1
                    if (fs.TryGetValue(1, out nr) && nr != null)
                        next.Revision = ParseLong(nr);
                }
            }

            object opResp = null;
            // Prefer explicit operationResponse field ids used by LINE thrift
            if (syncResult.TryGetValue(1, out opResp) && opResp is Dictionary<int, object> maybeOp && maybeOp.ContainsKey(3))
                opResp = maybeOp; // nested
            else if (!syncResult.TryGetValue(3, out opResp))
            {
                // scan for operation-like struct with operations field
                foreach (var kv in syncResult)
                {
                    var d = kv.Value as Dictionary<int, object>;
                    if (d != null && (d.ContainsKey(1) || d.ContainsKey(2) || d.ContainsKey(3)))
                    {
                        // could be operationResponse; check for globalEvents
                        if (d.ContainsKey(4) || d.ContainsKey(5) || d.ContainsKey(6) || d.ContainsKey(1) || d.ContainsKey(2))
                        {
                            opResp = d;
                            break;
                        }
                    }
                }
            }

            var op = opResp as Dictionary<int, object>;
            if (op != null)
            {
                object globalEvents;
                if (op.TryGetValue(1, out globalEvents) || op.TryGetValue(4, out globalEvents))
                {
                    var ge = globalEvents as Dictionary<int, object>;
                    if (ge != null)
                    {
                        object lr;
                        if (ge.TryGetValue(1, out lr) && lr != null)
                            next.GlobalRev = ParseLong(lr);
                    }
                }
                object individualEvents;
                if (op.TryGetValue(2, out individualEvents) || op.TryGetValue(5, out individualEvents))
                {
                    var ie = individualEvents as Dictionary<int, object>;
                    if (ie != null)
                    {
                        object lr;
                        if (ie.TryGetValue(1, out lr) && lr != null)
                            next.IndividualRev = ParseLong(lr);
                    }
                }
                object opsList;
                if (op.TryGetValue(3, out opsList) || op.TryGetValue(6, out opsList))
                {
                    var list = opsList as List<object>;
                    if (list != null && list.Count > 0)
                    {
                        var last = list[list.Count - 1] as Dictionary<int, object>;
                        if (last != null)
                        {
                            object rev;
                            // Operation.revision commonly field 1 or last field
                            if (last.TryGetValue(1, out rev) && rev != null)
                                next.Revision = ParseLong(rev);
                            else if (last.TryGetValue(last.Count > 0 ? MaxKey(last) : 1, out rev) && rev != null)
                                next.Revision = ParseLong(rev);
                        }
                    }
                }
            }
            return next;
        }

        private static int MaxKey(Dictionary<int, object> d)
        {
            int max = 1;
            foreach (var k in d.Keys) if (k > max) max = k;
            return max;
        }

        public static long ParseLong(object o)
        {
            if (o == null) return 0;
            if (o is long l) return l;
            if (o is int i) return i;
            if (o is string s)
            {
                long v;
                if (long.TryParse(s, out v)) return v;
            }
            try { return Convert.ToInt64(o); }
            catch { return 0; }
        }

        public static string CursorPath(string accountId)
        {
            return "accounts/" + accountId + "/ops-cursor.json";
        }

        public static async Task<OpsRevisionCursor> LoadAsync(string accountId)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(CursorPath(accountId));
                var text = await FileIO.ReadTextAsync(file);
                var obj = Windows.Data.Json.JsonObject.Parse(text);
                var c = new OpsRevisionCursor();
                c.Revision = ParseJsonLong(obj, "revision");
                c.GlobalRev = ParseJsonLong(obj, "globalRev");
                c.IndividualRev = ParseJsonLong(obj, "individualRev");
                return c;
            }
            catch
            {
                // Missing/corrupt → replay from zero (vyline behavior)
                return new OpsRevisionCursor();
            }
        }

        private static long ParseJsonLong(Windows.Data.Json.JsonObject obj, string key)
        {
            try
            {
                if (!obj.ContainsKey(key)) return 0;
                var token = obj.GetNamedValue(key);
                if (token.ValueType == Windows.Data.Json.JsonValueType.String)
                {
                    long v;
                    long.TryParse(token.GetString(), out v);
                    return v;
                }
                if (token.ValueType == Windows.Data.Json.JsonValueType.Number)
                    return (long)token.GetNumber();
            }
            catch { }
            return 0;
        }

        public static async Task PersistAsync(string accountId, OpsRevisionCursor cursor)
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "accounts", CreationCollisionOption.OpenIfExists);
                var sub = await folder.CreateFolderAsync(accountId, CreationCollisionOption.OpenIfExists);
                var obj = new Windows.Data.Json.JsonObject();
                obj["version"] = Windows.Data.Json.JsonValue.CreateNumberValue(1);
                obj["revision"] = Windows.Data.Json.JsonValue.CreateStringValue(cursor.Revision.ToString());
                obj["globalRev"] = Windows.Data.Json.JsonValue.CreateStringValue(cursor.GlobalRev.ToString());
                obj["individualRev"] = Windows.Data.Json.JsonValue.CreateStringValue(cursor.IndividualRev.ToString());
                obj["savedAt"] = Windows.Data.Json.JsonValue.CreateStringValue(DateTime.UtcNow.ToString("o"));
                var file = await sub.CreateFileAsync("ops-cursor.json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, obj.Stringify());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[OpsCursor] persist failed: " + ex.Message);
            }
        }
    }
}
