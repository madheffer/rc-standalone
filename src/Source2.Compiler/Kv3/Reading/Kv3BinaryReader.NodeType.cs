// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Resource/ResourceTypes/BinaryKV3.NodeType.cs at d9926fcb with this project's guard patches applied, so the compiler reads KV3 without
// depending on VRF (docs/INDEPENDENCE.md). Changes: no Block base class (the input is the block's
// bytes), Kv3Formats for KV3IDLookup, InvalidDataException for UnexpectedMagicException, and
// the writing half dropped (Kv3V5Writer and Kv3Tree write).
namespace Source2.Compiler.Kv3.Reading
{
    internal sealed partial class Kv3BinaryReader
    {
        private enum KV3BinaryNodeType : byte
        {
            NULL = 1,
            BOOLEAN = 2,
            INT64 = 3,
            UINT64 = 4,
            DOUBLE = 5,
            STRING = 6,
            BINARY_BLOB = 7,
            ARRAY = 8,
            OBJECT = 9,
            ARRAY_TYPED = 10,
            INT32 = 11,
            UINT32 = 12,
            BOOLEAN_TRUE = 13,
            BOOLEAN_FALSE = 14,
            INT64_ZERO = 15,
            INT64_ONE = 16,
            DOUBLE_ZERO = 17,
            DOUBLE_ONE = 18,
            FLOAT = 19,
            INT16 = 20,
            UINT16 = 21,
            INT8 = 22,
            UINT8 = 23,
            ARRAY_TYPE_BYTE_LENGTH = 24,
            ARRAY_TYPE_AUXILIARY_BUFFER = 25,
        }
    }
}
