using System;
using System.Collections.Generic;
using System.Text;

namespace LumINE.Utils
{
    public static class ThriftTypes
    {
        public const int Bool = 2;
        public const int Byte = 3;
        public const int Double = 4;
        public const int I16 = 6;
        public const int I32 = 8;
        public const int I64 = 10;
        public const int String = 11; // binary/string
        public const int Struct = 12;
        public const int Map = 13;
        public const int Set = 14;
        public const int List = 15;
    }

    // Param format: (type, fieldId, value) like Python version
    public class ThriftField
    {
        public int Type { get; set; }
        public short FieldId { get; set; }
        public object Value { get; set; }

        public ThriftField(int type, short fieldId, object value)
        {
            Type = type;
            FieldId = fieldId;
            Value = value;
        }
    }

    public class ThriftMapValue
    {
        public int KeyType { get; set; }
        public int ValueType { get; set; }
        public Dictionary<object, object> Map { get; set; }

        public ThriftMapValue(int keyType, int valueType, Dictionary<object, object> map)
        {
            KeyType = keyType;
            ValueType = valueType;
            Map = map ?? new Dictionary<object, object>();
        }
    }

    public class ThriftListValue
    {
        public int ElementType { get; set; }
        public List<object> List { get; set; }

        public ThriftListValue(int elementType, List<object> list)
        {
            ElementType = elementType;
            List = list ?? new List<object>();
        }
    }
}
