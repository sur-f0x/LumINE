using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LumINE.Utils
{
    public class ThriftBinary
    {
        // TBinary constants
        public static byte[] PackBinary(string methodName, List<ThriftField> fields, int seqId = 0)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                // Header: 0x80010001 (big-endian), name length (big-endian), name, seqId (big-endian)
                bw.Write(new byte[] { 0x80, 0x01, 0x00, 0x01 });
                byte[] nameBytes = Encoding.UTF8.GetBytes(methodName);
                WriteBigEndianInt32(bw, nameBytes.Length);
                bw.Write(nameBytes);
                WriteBigEndianInt32(bw, seqId);
                
                // Encode fields
                EncodeBinaryFields(bw, fields);
                bw.Write((byte)0); // stop
                
                return ms.ToArray();
            }
        }

        private static void WriteBigEndianInt32(BinaryWriter bw, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            bw.Write(bytes);
        }

        private static void WriteBigEndianInt64(BinaryWriter bw, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            bw.Write(bytes);
        }

        private static void WriteBigEndianDouble(BinaryWriter bw, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            bw.Write(bytes);
        }

        private static int ReadBigEndianInt32(BinaryReader br)
        {
            byte[] bytes = br.ReadBytes(4);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        private static long ReadBigEndianInt64(BinaryReader br)
        {
            byte[] bytes = br.ReadBytes(8);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToInt64(bytes, 0);
        }

        private static double ReadBigEndianDouble(BinaryReader br)
        {
            byte[] bytes = br.ReadBytes(8);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        private static void EncodeBinaryFields(BinaryWriter bw, List<ThriftField> fields)
        {
            if (fields == null) return;
            foreach (var field in fields)
            {
                if (field.Value == null) continue;
                bw.Write((byte)field.Type);
                byte[] fidBytes = BitConverter.GetBytes((ushort)field.FieldId);
                if (BitConverter.IsLittleEndian) Array.Reverse(fidBytes);
                bw.Write(fidBytes);
                EncodeBinaryData(bw, field.Type, field.Value);
            }
        }

        private static void EncodeBinaryData(BinaryWriter bw, int type, object value)
        {
            switch (type)
            {
                case ThriftTypes.Bool:
                    bw.Write((byte)((bool)value ? 1 : 0));
                    break;
                case ThriftTypes.Byte:
                    bw.Write((byte)value);
                    break;
                case ThriftTypes.Double:
                    WriteBigEndianDouble(bw, (double)value);
                    break;
                case ThriftTypes.I32:
                    WriteBigEndianInt32(bw, (int)value);
                    break;
                case ThriftTypes.I64:
                    WriteBigEndianInt64(bw, (long)value);
                    break;
case ThriftTypes.String:
                    if (value is byte[] bArr)
                    {
                        byte[] lenBytes = BitConverter.GetBytes(bArr.Length);
                        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                        bw.Write(lenBytes);
                        bw.Write(bArr);
                    }
                    else if (value is string s)
                    {
                        byte[] sBytes = Encoding.UTF8.GetBytes(s);
                        byte[] lenBytes = BitConverter.GetBytes(sBytes.Length);
                        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                        bw.Write(lenBytes);
                        bw.Write(sBytes);
                    }
                    else
                    {
                        byte[] bytes = (byte[])value;
                        byte[] lenBytes = BitConverter.GetBytes(bytes.Length);
                        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                        bw.Write(lenBytes);
                        bw.Write(bytes);
                    }
                    break;
                case ThriftTypes.Struct:
                    List<ThriftField> structFields = (List<ThriftField>)value;
                    EncodeBinaryFields(bw, structFields);
                    bw.Write((byte)0);
                    break;
                case ThriftTypes.Map:
                    ThriftMapValue mapVal = (ThriftMapValue)value;
                    bw.Write((byte)mapVal.KeyType);
                    bw.Write((byte)mapVal.ValueType);
                    WriteBigEndianInt32(bw, mapVal.Map.Count);
                    foreach (var kvp in mapVal.Map)
                    {
                        EncodeBinaryData(bw, mapVal.KeyType, kvp.Key);
                        EncodeBinaryData(bw, mapVal.ValueType, kvp.Value);
                    }
                    break;
                case ThriftTypes.List:
                case ThriftTypes.Set:
                    ThriftListValue listVal = (ThriftListValue)value;
                    bw.Write((byte)listVal.ElementType);
                    WriteBigEndianInt32(bw, listVal.List.Count);
                    foreach (var item in listVal.List)
                    {
                        EncodeBinaryData(bw, listVal.ElementType, item);
                    }
                    break;
                default:
                    throw new ArgumentException($"Unsupported TBinary type: {type}");
            }
        }

        public static Dictionary<int, object> UnpackBinaryMessage(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            using (var reader = new BinaryReader(stream))
            {
                int header = ReadBigEndianInt32(reader);
                if ((header & unchecked((int)0xffff0000)) != unchecked((int)0x80010000)) throw new InvalidDataException("Invalid TBinary version.");
                int length = ReadBigEndianInt32(reader);
                if (length < 0 || length > stream.Length - stream.Position - 4) throw new InvalidDataException("Invalid TBinary method length.");
                reader.ReadBytes(length);
                ReadBigEndianInt32(reader);
                var result = ReadBinaryStruct(reader);
                if ((header & 255) == 3) throw new InvalidDataException("TBinary application exception.");
                return result;
            }
        }

        public static Dictionary<int, object> UnpackBinary(byte[] data)
        {
            if (data == null || data.Length == 0) return new Dictionary<int, object>();
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader br = new BinaryReader(ms))
            {
                try
                {
                    // Read message
                    if (ms.Length < 12) return new Dictionary<int, object>();
                    int first = ReadBigEndianInt32(br);
                    if (first >= 0) throw new ArgumentException("Old-style TBinary not supported");
                    int versionRaw = first & unchecked((int)0xFFFF0000);
                    uint version = (uint)versionRaw;
                    if (version != 0x80010000u) throw new ArgumentException("Bad TBinary version: 0x" + version.ToString("X8") + " (first=0x" + first.ToString("X8") + ")");
                    int mtype = first & 0x000000FF;
                    if (ms.Position + 8 > ms.Length) return new Dictionary<int, object>();
                    int nameLen = ReadBigEndianInt32(br);
                    if (nameLen < 0 || nameLen > 10000 || ms.Position + nameLen + 4 > ms.Length) return new Dictionary<int, object>();
                    br.ReadBytes(nameLen); // skip name
                    int seqId = ReadBigEndianInt32(br);
                    
                    if (ms.Position >= ms.Length) return new Dictionary<int, object>();
                    byte ttype = br.ReadByte();
                    if (ttype == 0) return new Dictionary<int, object>();
                    if (ms.Position + 2 > ms.Length) return new Dictionary<int, object>();
                    byte[] fidBytes = br.ReadBytes(2);
                    if (BitConverter.IsLittleEndian) Array.Reverse(fidBytes);
                    short fid = BitConverter.ToInt16(fidBytes, 0);
                    if (ms.Position >= ms.Length) return new Dictionary<int, object>();
                    object value = ReadBinaryValue(br, ttype);
                    
                    if (fid == 0)
                    {
                        if (value is Dictionary<int, object> dict) return dict;
                        return new Dictionary<int, object> { { 0, value } };
                    }
                    // Exception
                    if (value is Dictionary<int, object> exDict)
                    {
                        int code = exDict.ContainsKey(1) ? Convert.ToInt32(exDict[1]) : 0;
                        string msg = exDict.ContainsKey(2) ? exDict[2]?.ToString() : value.ToString();
                        throw new LineServiceError(code, msg);
                    }
                    throw new LineServiceError(0, value?.ToString() ?? "");
                }
                catch (LineServiceError)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new Dictionary<int, object>();
                }
            }
        }

        private static object ReadBinaryValue(BinaryReader br, byte ttype)
        {
            try
            {
                switch (ttype)
                {
                    case 2: return br.ReadByte() != 0;
                    case 3: return br.ReadSByte();
                    case 4: return ReadBigEndianDouble(br);
                    case 6:
                        {
                            byte[] b = br.ReadBytes(2);
                            if (BitConverter.IsLittleEndian) Array.Reverse(b);
                            return BitConverter.ToInt16(b, 0);
                        }
                    case 8: return ReadBigEndianInt32(br);
                    case 10: return ReadBigEndianInt64(br);
                    case 11:
                        if (br.BaseStream.Position + 4 > br.BaseStream.Length) return "";
                        int len = ReadBigEndianInt32(br);
                        if (len < 0 || len > 100000 || br.BaseStream.Position + len > br.BaseStream.Length) return "";
                        byte[] strBytes = br.ReadBytes(len);
                        try { return Encoding.UTF8.GetString(strBytes); }
                        catch { return strBytes; }
                    case 12: return ReadBinaryStruct(br);
                    case 13:
                        if (br.BaseStream.Position + 9 > br.BaseStream.Length) return new Dictionary<object, object>();
                        byte ktype = br.ReadByte();
                        byte vtype = br.ReadByte();
                        int mapCount = ReadBigEndianInt32(br);
                        var map = new Dictionary<object, object>();
                        for (int i = 0; i < mapCount; i++)
                        {
                            if (br.BaseStream.Position >= br.BaseStream.Length) break;
                            object k = ReadBinaryValue(br, ktype);
                            if (br.BaseStream.Position >= br.BaseStream.Length) break;
                            object v = ReadBinaryValue(br, vtype);
                            if (k != null) map[k] = v;
                        }
                        return map;
                    case 14:
                    case 15:
                        if (br.BaseStream.Position + 5 > br.BaseStream.Length) return new List<object>();
                        byte etype = br.ReadByte();
                        int listCount = ReadBigEndianInt32(br);
                        var list = new List<object>();
                        for (int i = 0; i < listCount; i++)
                        {
                            if (br.BaseStream.Position >= br.BaseStream.Length) break;
                            list.Add(ReadBinaryValue(br, etype));
                        }
                        return list;
                    default:
                        throw new ArgumentException("Unknown TBinary type: " + ttype);
                }
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<int, object> ReadBinaryStruct(BinaryReader br)
        {
            var result = new Dictionary<int, object>();
            while (true)
            {
                try
                {
                    if (br.BaseStream.Position >= br.BaseStream.Length) break;
                    byte ttype = br.ReadByte();
                    if (ttype == 0) break;
                    if (br.BaseStream.Position + 2 > br.BaseStream.Length) break;
                    byte[] fidBytes = br.ReadBytes(2);
                    if (BitConverter.IsLittleEndian) Array.Reverse(fidBytes);
                    short fid = BitConverter.ToInt16(fidBytes, 0);
                    if (br.BaseStream.Position >= br.BaseStream.Length) break;
                    object val = ReadBinaryValue(br, ttype);
                    if (val != null) result[fid] = val;
                }
                catch
                {
                    break;
                }
            }
            return result;
        }

        public static byte[] PackBinaryStruct(List<ThriftField> fields)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                EncodeBinaryFields(bw, fields);
                bw.Write((byte)0);
                return ms.ToArray();
            }
        }
    }
}
