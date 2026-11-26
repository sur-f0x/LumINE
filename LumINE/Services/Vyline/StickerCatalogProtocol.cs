using System;
using System.Collections.Generic;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public static class StickerCatalogProtocol
    {
        public static List<ThriftField> OwnedArgs(int offset)
        {
            return new List<ThriftField> {
                new ThriftField(ThriftTypes.Struct, 1, new List<ThriftField>()),
                new ThriftField(ThriftTypes.String, 2, "stickershop"),
                new ThriftField(ThriftTypes.I32, 3, offset), new ThriftField(ThriftTypes.I32, 4, 100),
                new ThriftField(ThriftTypes.Struct, 5, new List<ThriftField> { new ThriftField(ThriftTypes.String, 1, "ja"), new ThriftField(ThriftTypes.String, 2, "JP") })
            };
        }
        public static StickerPack ReadPack(Dictionary<int, object> product)
        {
            if (product == null) return null;
            long id, version, expiry;
            if (!long.TryParse(Text(product, 1), out id) || id <= 0) return null;
            long.TryParse(Text(product, 21), out version);
            long.TryParse(Text(product, 94), out expiry);
            return new StickerPack { Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture), Name = Text(product, 11) ?? id.ToString(), Version = Math.Max(1, version).ToString(System.Globalization.CultureInfo.InvariantCulture), ExpiresAt = ChatActivityProtocol.Timestamp(expiry) };
        }
        private static string Text(Dictionary<int, object> fields, int id) { object value; return fields.TryGetValue(id, out value) && value != null ? value.ToString() : null; }
    }
}
