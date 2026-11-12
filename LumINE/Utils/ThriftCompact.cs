using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LumINE.Utils
{
    public class ThriftCompact
    {
        private static readonly Dictionary<int, int> CTypeToType = new Dictionary<int, int>
        {
            { 0x01, 2 }, { 0x02, 2 }, { 0x03, 3 }, { 0x04, 6 }, { 0x05, 8 }, { 0x06, 10 },
            { 0x07, 4 }, { 0x08, 11 }, { 0x09, 15 }, { 0x0A, 14 }, { 0x0B, 13 }, { 0x0C, 12 }
        };

        private static readonly Dictionary<int, byte> TypeToCType = new Dictionary<int, byte>
        {
            { 2, 0x01 }, { 3, 0x03 }, { 4, 0x07 }, { 6, 0x04 }, { 8, 0x05 }, { 10, 0x06 },
            { 11, 0x08 }, { 12, 0x0C }, { 13, 0x0B }, { 14, 0x0A }, { 15, 0x09 }
        };

        private static int ZigzagEncode(int n, int bits)
        {
            return (n << 1) ^ (n >> (bits - 1));
        }

        private static int ZigzagDecode(int n)
        {
            return (n >> 1) ^ -(n & 1);
        }

        private static long ZigzagDecodeLong(long n)
        {
            return (n >> 1) ^ -(n & 1);
        }

        private static void WriteVarint(BinaryWriter bw, ulong n)
        {
            while (true)
            {
                if ((n & ~0x7FUL) == 0)
                {
                    bw.Write((byte)n);
                    break;
                }
                bw.Write((byte)((n & 0xFF) | 0x80));
                n >>= 7;
            }
        }

        private static void WriteVarintInt(BinaryWriter bw, int n)
        {
            WriteVarint(bw, (ulong)(n >= 0 ? n : (n & 0xFFFFFFFF)));
        }

        private static ulong ReadVarint(BinaryReader br)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                result |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
        }

        public static byte[] PackCompact(string methodName, List<ThriftField> fields, int seqId = 0)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                // protocol_id=0x82, type=CALL(1) ver=1 -> 0x21
                bw.Write((byte)0x82);
                bw.Write((byte)0x21);
                WriteVarint(bw, (ulong)seqId);
                byte[] nameBytes = Encoding.UTF8.GetBytes(methodName);
                WriteVarint(bw, (ulong)nameBytes.Length);
                bw.Write(nameBytes);
                
                EncodeCompactFields(bw, fields);
                bw.Write((byte)0);
                
                return ms.ToArray();
            }
        }

        private static void EncodeCompactFields(BinaryWriter bw, List<ThriftField> fields)
        {
            if (fields == null) return;
            short lastFid = 0;
            foreach (var field in fields)
            {
                if (field.Value == null) continue;
                int ctype;
                if (field.Type == ThriftTypes.Bool)
                {
                    ctype = ToBool(field.Value) ? 0x01 : 0x02;
                }
                else if (TypeToCType.ContainsKey(field.Type))
                {
                    ctype = TypeToCType[field.Type];
                }
                else
                {
                    continue;
                }

                short delta = (short)(field.FieldId - lastFid);
                if (delta > 0 && delta <= 15)
                {
                    bw.Write((byte)((delta << 4) | ctype));
                }
                else
                {
                    bw.Write((byte)ctype);
                    WriteVarint(bw, (ulong)(uint)ZigzagEncode(field.FieldId, 16));
                }
                lastFid = field.FieldId;

                if (field.Type != ThriftTypes.Bool)
                {
                    EncodeCompactData(bw, field.Type, field.Value);
                }
            }
        }

        private static bool ToBool(object value)
        {
            if (value is bool b) return b;
            if (value == null) return false;
            if (value is int i) return i != 0;
            if (value is long l) return l != 0;
            try { return Convert.ToInt64(value) != 0; }
            catch { return false; }
        }

        private static void EncodeCompactData(BinaryWriter bw, int type, object value)
        {
            switch (type)
            {
                case ThriftTypes.Bool:
                    bw.Write((byte)(ToBool(value) ? 0x01 : 0x02));
                    break;
                case ThriftTypes.Byte:
                    bw.Write((byte)Convert.ToInt32(value));
                    break;
                case ThriftTypes.Double:
                    // Compact uses IEEE 754 little-endian, unlike binary
                    // Thrift's network byte order. BinaryWriter writes LE.
                    bw.Write(Convert.ToDouble(value));
                    break;
                case ThriftTypes.I32:
                    WriteVarint(bw, (ulong)(uint)ZigzagEncode(Convert.ToInt32(value), 32));
                    break;
                case ThriftTypes.I64:
                    WriteVarint(bw, (ulong)ZigzagEncodeLong(Convert.ToInt64(value)));
                    break;
                case ThriftTypes.String:
                    byte[] strBytes;
                    if (value is byte[] bArr) strBytes = bArr;
                    else if (value is string s) strBytes = Encoding.UTF8.GetBytes(s);
                    else strBytes = (byte[])value;
                    WriteVarint(bw, (ulong)strBytes.Length);
                    bw.Write(strBytes);
                    break;
                case ThriftTypes.Struct:
                    EncodeCompactFields(bw, (List<ThriftField>)value);
                    bw.Write((byte)0);
                    break;
                case ThriftTypes.Map:
                    ThriftMapValue mapVal = (ThriftMapValue)value;
                    if (mapVal.Map.Count == 0)
                    {
                        bw.Write((byte)0);
                        break;
                    }
                    WriteVarint(bw, (ulong)mapVal.Map.Count);
                    byte mapTypes = (byte)((TypeToCType[mapVal.KeyType] << 4) | TypeToCType[mapVal.ValueType]);
                    bw.Write(mapTypes);
                    foreach (var kvp in mapVal.Map)
                    {
                        EncodeCompactData(bw, mapVal.KeyType, kvp.Key);
                        EncodeCompactData(bw, mapVal.ValueType, kvp.Value);
                    }
                    break;
                case ThriftTypes.List:
                case ThriftTypes.Set:
                    ThriftListValue listVal = (ThriftListValue)value;
                    int n = listVal.List.Count;
                    if (n <= 14)
                    {
                        bw.Write((byte)((n << 4) | TypeToCType[listVal.ElementType]));
                    }
                    else
                    {
                        bw.Write((byte)(0xF0 | TypeToCType[listVal.ElementType]));
                        WriteVarint(bw, (ulong)n);
                    }
                    foreach (var item in listVal.List)
                    {
                        EncodeCompactData(bw, listVal.ElementType, item);
                    }
                    break;
                default:
                    throw new ArgumentException($"Unsupported TCompact type: {type}");
            }
        }

        private static long ZigzagEncodeLong(long n)
        {
            return (n << 1) ^ (n >> 63);
        }

        public static Dictionary<int, object> UnpackCompact(byte[] data)
        {
            var fields = UnpackCompactMessage(data);
            // Legacy behavior: first field is success/return value.
            if (fields.Count == 0) return new Dictionary<int, object>();
            if (fields.ContainsKey(0))
            {
                if (fields[0] is Dictionary<int, object> dict) return dict;
                return new Dictionary<int, object> { { 0, fields[0] } };
            }
            // Exception-only response (field 1)
            if (fields.ContainsKey(1) && fields[1] is Dictionary<int, object> exDict)
            {
                int code = exDict.ContainsKey(1) ? Convert.ToInt32(exDict[1]) : 0;
                string msg = exDict.ContainsKey(2) ? exDict[2]?.ToString() : "";
                throw new LineServiceError(code, msg);
            }
            var first = fields.GetEnumerator();
            first.MoveNext();
            if (first.Current.Value is Dictionary<int, object> d2) return d2;
            return new Dictionary<int, object> { { first.Current.Key, first.Current.Value } };
        }

        /// <summary>
        /// Parse a compact thrift message reply into all result-struct fields
        /// (field 0 = success, field 1 = exception, etc.) without throwing.
        /// </summary>
        public static Dictionary<int, object> UnpackCompactMessage(byte[] data)
        {
            var result = new Dictionary<int, object>();
            if (data == null || data.Length == 0) return result;
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader br = new BinaryReader(ms))
            {
                if (ms.Length < 2) return result;
                byte protoId = br.ReadByte();
                if (protoId != 0x82) throw new ArgumentException("Bad compact protocol id");
                byte verType = br.ReadByte();
                int version = verType & 0x1F;
                if (version != 1) throw new ArgumentException("Bad compact version");
                // seqId (zigzag varint) + method name — skip body fields fully
                ReadVarint(br);
                int nameLen = (int)ReadVarint(br);
                if (nameLen > 0) br.ReadBytes(nameLen);
                return ReadCompactStruct(br);
            }
        }

        private static object ReadCompactValue(BinaryReader br, int ctype, bool isBool = false)
        {
            if (ctype == 0x01) return true;
            if (ctype == 0x02) return false;
            if (!CTypeToType.ContainsKey(ctype)) throw new ArgumentException("Unknown compact ctype");;
            int ttype = CTypeToType[ctype];
            
            switch (ttype)
            {
                case 2: return false; // shouldn't happen
                case 3: return br.ReadSByte();
                case 4: return br.ReadDouble();
                case 6: return (short)ZigzagDecode((int)ReadVarint(br));
                case 8: return ZigzagDecode((int)ReadVarint(br));
                case 10: return (long)ZigzagDecodeLong((long)ReadVarint(br));
                case 11:
                    int strLen = (int)ReadVarint(br);
                    byte[] strBytes = br.ReadBytes(strLen);
                    // Thrift STRING also carries arbitrary binary E2EE chunks.
                    // A lossless round trip identifies UTF-8 without throwing
                    // DecoderFallbackException for every encrypted message.
                    string decoded = Encoding.UTF8.GetString(strBytes);
                    byte[] encoded = Encoding.UTF8.GetBytes(decoded);
                    if (encoded.Length != strBytes.Length) return strBytes;
                    for (int i = 0; i < encoded.Length; i++) if (encoded[i] != strBytes[i]) return strBytes;
                    return decoded;
                case 12: return ReadCompactStruct(br);
                case 13: return ReadCompactMap(br);
                case 14:
                case 15: return ReadCompactList(br);
                default:
                    throw new ArgumentException($"Unsupported compact ttype: {ttype}");;
            }
        }

        private static Dictionary<int, object> ReadCompactStruct(BinaryReader br)
        {
            var result = new Dictionary<int, object>();
            short lastFid = 0;
            while (true)
            {
                byte b = br.ReadByte();
                if (b == 0) break;
                int ctype = b & 0x0F;
                int delta = b >> 4;
                short fid = (short)(delta != 0 ? (lastFid + delta) : (int)ZigzagDecode((int)ReadVarint(br)));
                lastFid = fid;
                result[fid] = ReadCompactValue(br, ctype);
            }
            return result;
        }

        private static Dictionary<object, object> ReadCompactMap(BinaryReader br)
        {
            int n = (int)ReadVarint(br);
            if (n == 0) return new Dictionary<object, object>();
            byte types = br.ReadByte();
            int ktype = types >> 4;
            int vtype = types & 0x0F;
            var map = new Dictionary<object, object>();
            for (int i = 0; i < n; i++)
            {
                object k = ReadCompactValue(br, ktype);
                object v = ReadCompactValue(br, vtype);
                map[k] = v;
            }
            return map;
        }

        private static List<object> ReadCompactList(BinaryReader br)
        {
            byte b = br.ReadByte();
            int n = b >> 4;
            int etype = b & 0x0F;
            if (n == 15) n = (int)ReadVarint(br);
            var list = new List<object>();
            for (int i = 0; i < n; i++)
            {
                list.Add(ReadCompactValue(br, etype));
            }
            return list;
        }

        public static Dictionary<int, object> UnpackCompactStruct(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader br = new BinaryReader(ms))
            {
                return ReadCompactStruct(br);
            }
        }
    }
}
