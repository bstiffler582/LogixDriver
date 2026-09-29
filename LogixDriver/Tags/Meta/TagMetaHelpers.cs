namespace Logix.Tags
{
    public static class TagMetaHelpers
    {
        public enum Code : ushort
        {
            BOOL = 0xC1, 
            SINT = 0xC2, INT = 0xC3, DINT = 0xC4, LINT = 0xC5, USINT = 0xC6, UINT = 0xC7, UDINT = 0xC8, ULINT = 0xC9,
            REAL = 0xCA, LREAL = 0xCB,
            STRING = 0xD0, STRING2 = 0xD5, STRINGN = 0xD9, SHORT_STRING = 0xDA, EPATH = 0xDC, STRINGI = 0xDE, STRING_STRUCT = 0x8FCE,
            STIME = 0xCC, DATE = 0xCD, TIME_OF_DAY = 0xCE, DATE_AND_TIME = 0xCF, FTIME = 0xD6, LTIME = 0xD7, ITIME = 0xD8, TIME = 0xDB,
            ABBREV_STRUCT = 0xA0, ABBREV_ARRAY = 0xA1, FULL_STRUCT = 0xA2, FULL_ARRAY = 0xA3,
            ENGUNIT = 0xDD
        }

        const ushort TYPE_IS_ARRAY = 0x6000;
        const ushort TYPE_IS_STRUCT = 0x8000;
        const ushort TYPE_IS_SYSTEM = 0x1000;
        const ushort TYPE_UDT_ID_MASK = 0x0FFF;

        // Logix packs BOOL arrays into 32-bit words and reports them as arrays of 0xD3 (CIP DWORD),
        // with dimensions counted in words. 0xD3 is never a declarable type on its own.
        const ushort BOOL_ARRAY_BASE_TYPE = 0xD3;
        public const int BOOL_ARRAY_WORD_BITS = 32;

        public static bool IsPrimitive(ushort typeCode) =>
            (typeCode >= 0xC1 && typeCode <= 0xD4);

        public static bool IsUdt(ushort typeCode) =>
            ((typeCode & TYPE_IS_STRUCT) != 0) && !((typeCode & TYPE_IS_SYSTEM) != 0);

        public static bool IsArray(ushort typeCode) =>
            ((typeCode & TYPE_IS_ARRAY) != 0) && !((typeCode & TYPE_IS_SYSTEM) != 0);

        public static bool IsSystem(ushort typeCode) =>
            ((typeCode & TYPE_IS_SYSTEM) != 0);

        public static ushort GetUdtId(ushort typeCode) =>
            (ushort)(typeCode & TYPE_UDT_ID_MASK);

        public static ushort GetArrayBaseType(ushort typeCode) =>
            (ushort)(typeCode & ~TYPE_IS_ARRAY);

        public static bool IsBoolArray(ushort typeCode) =>
            IsArray(typeCode) && GetArrayBaseType(typeCode) == BOOL_ARRAY_BASE_TYPE;

        public static string ResolveTypeName(ushort code, string name = "")
        {
            if (Enum.IsDefined(typeof(Code), code))
                return ((Code)code).ToString();
            else if (IsArray(code))
                return "array";
            else if (IsUdt(code))
                return "object";
            else if (name.StartsWith("Program:"))
                return "program";
            else if (IsSystem(code))
                return $"SystemType(0x{code:X4})";
            else
                return $"UnknownType(0x{code:X4})";
        }

        public static ushort GetTypeLength(ushort typeCode)
        {
            return (Code)(typeCode) switch
            {
                Code.BOOL => 1,
                Code.SINT or Code.USINT => 1,
                Code.INT or Code.UINT => 2,
                Code.DINT or Code.UDINT or Code.TIME or Code.REAL => 4,
                Code.LINT or Code.ULINT or Code.DATE_AND_TIME or Code.LREAL => 8,
                Code.STRING or Code.STRING2 or Code.STRINGI or Code.STRINGN or Code.STRING_STRUCT
                    => 88,
                _ => 0
            };
        }
    }
}
