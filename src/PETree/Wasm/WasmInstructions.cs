using System;

namespace GuiLabs.FileFormat.Wasm;

/// <summary>
/// Reads the instructions of a function body one after another. For each
/// instruction the visitor is called with the opcode and the first operand:
/// the constant, the index, or for a memory access the offset.
/// An opcode after the prefix 0xFC, 0xFD or 0xFE is reported as the prefix
/// times 0x10000 plus the number that follows it.
/// </summary>
public static class WasmInstructions
{
    public const int LocalGet = 0x20;
    public const int I32Load = 0x28;
    public const int I32Const = 0x41;
    public const int I32LessThanSigned = 0x48;
    public const int I32LessThanUnsigned = 0x49;
    public const int I32GreaterThanSigned = 0x4A;
    public const int I32GreaterThanUnsigned = 0x4B;
    public const int I32LessOrEqualSigned = 0x4C;
    public const int I32LessOrEqualUnsigned = 0x4D;
    public const int I32GreaterOrEqualSigned = 0x4E;
    public const int I32GreaterOrEqualUnsigned = 0x4F;

    /// <summary>
    /// Returns false when the bytes are not a sequence of known instructions
    /// that ends exactly at the end offset.
    /// </summary>
    public static bool Decode(byte[] bytes, int position, int end, Action<int, long> visit)
    {
        try
        {
            while (position < end)
            {
                int opcode = bytes[position++];
                long operand = 0;
                switch (opcode)
                {
                    case 0x02: // block
                    case 0x03: // loop
                    case 0x04: // if
                    case 0x06: // try
                        operand = WasmReader.ReadSignedLeb(bytes, ref position);
                        break;

                    case 0x07: // catch
                    case 0x08: // throw
                    case 0x09: // rethrow
                    case 0x0C: // br
                    case 0x0D: // br_if
                    case 0x10: // call
                    case 0x12: // return_call
                    case 0x18: // delegate
                    case 0x20: // local.get
                    case 0x21: // local.set
                    case 0x22: // local.tee
                    case 0x23: // global.get
                    case 0x24: // global.set
                    case 0x25: // table.get
                    case 0x26: // table.set
                    case 0x3F: // memory.size
                    case 0x40: // memory.grow
                    case 0xD2: // ref.func
                    case 0xD5: // br_on_null
                    case 0xD6: // br_on_non_null
                        operand = (long)WasmReader.ReadLeb(bytes, ref position);
                        break;

                    case 0x0E: // br_table
                        {
                            ulong count = WasmReader.ReadLeb(bytes, ref position);
                            operand = (long)count;
                            for (ulong i = 0; i <= count; i++)
                            {
                                WasmReader.ReadLeb(bytes, ref position);
                            }

                            break;
                        }

                    case 0x11: // call_indirect
                    case 0x13: // return_call_indirect
                        operand = (long)WasmReader.ReadLeb(bytes, ref position);
                        WasmReader.ReadLeb(bytes, ref position);
                        break;

                    case 0x1C: // select with types
                        {
                            ulong count = WasmReader.ReadLeb(bytes, ref position);
                            for (ulong i = 0; i < count; i++)
                            {
                                SkipValueType(bytes, ref position);
                            }

                            break;
                        }

                    case 0x1F: // try_table
                        {
                            WasmReader.ReadSignedLeb(bytes, ref position);
                            ulong count = WasmReader.ReadLeb(bytes, ref position);
                            for (ulong i = 0; i < count; i++)
                            {
                                int kind = bytes[position++];
                                if (kind < 2)
                                {
                                    WasmReader.ReadLeb(bytes, ref position);
                                }

                                WasmReader.ReadLeb(bytes, ref position);
                            }

                            break;
                        }

                    case 0x41: // i32.const
                        operand = (int)WasmReader.ReadSignedLeb(bytes, ref position);
                        break;
                    case 0x42: // i64.const
                        operand = WasmReader.ReadSignedLeb(bytes, ref position);
                        break;
                    case 0x43: // f32.const
                        position += 4;
                        break;
                    case 0x44: // f64.const
                        position += 8;
                        break;
                    case 0xD0: // ref.null
                        operand = WasmReader.ReadSignedLeb(bytes, ref position);
                        break;

                    case 0xFC:
                        {
                            int code = (int)WasmReader.ReadLeb(bytes, ref position);
                            opcode = 0xFC0000 | code;
                            if (code > 17)
                            {
                                return false;
                            }

                            // 0 to 7 are the saturating conversions. The others
                            // are the bulk memory and table instructions, with
                            // one or two indices.
                            if (code >= 8)
                            {
                                operand = (long)WasmReader.ReadLeb(bytes, ref position);
                                if (code == 8 || code == 10 || code == 12 || code == 14)
                                {
                                    WasmReader.ReadLeb(bytes, ref position);
                                }
                            }

                            break;
                        }

                    case 0xFD:
                        {
                            int code = (int)WasmReader.ReadLeb(bytes, ref position);
                            opcode = 0xFD0000 | code;
                            if (code > 0x1FF)
                            {
                                return false;
                            }

                            if (code <= 11 || code == 92 || code == 93)
                            {
                                operand = ReadMemoryOperand(bytes, ref position);
                            }
                            else if (code == 12 || code == 13)
                            {
                                // v128.const and i8x16.shuffle
                                position += 16;
                            }
                            else if (code >= 21 && code <= 34)
                            {
                                // extract_lane and replace_lane
                                position++;
                            }
                            else if (code >= 84 && code <= 91)
                            {
                                // load and store of one lane
                                operand = ReadMemoryOperand(bytes, ref position);
                                position++;
                            }

                            break;
                        }

                    case 0xFE:
                        {
                            int code = (int)WasmReader.ReadLeb(bytes, ref position);
                            opcode = 0xFE0000 | code;
                            if (code > 0x4E)
                            {
                                return false;
                            }

                            if (code == 3)
                            {
                                // atomic.fence
                                position++;
                            }
                            else
                            {
                                operand = ReadMemoryOperand(bytes, ref position);
                            }

                            break;
                        }

                    default:
                        if (opcode >= 0x28 && opcode <= 0x3E)
                        {
                            // Loads and stores
                            operand = ReadMemoryOperand(bytes, ref position);
                        }
                        else if (!HasNoOperands(opcode))
                        {
                            return false;
                        }

                        break;
                }

                if (position > end)
                {
                    return false;
                }

                visit?.Invoke(opcode, operand);
            }

            return position == end;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException || ex is System.IO.InvalidDataException)
        {
            return false;
        }
    }

    private static bool HasNoOperands(int opcode)
    {
        switch (opcode)
        {
            case 0x00: // unreachable
            case 0x01: // nop
            case 0x05: // else
            case 0x0A: // throw_ref
            case 0x0B: // end
            case 0x0F: // return
            case 0x19: // catch_all
            case 0x1A: // drop
            case 0x1B: // select
            case 0xD1: // ref.is_null
            case 0xD3: // ref.eq
            case 0xD4: // ref.as_non_null
                return true;
            default:
                // Comparisons, arithmetic, conversions and sign extension
                return opcode >= 0x45 && opcode <= 0xC4;
        }
    }

    /// <summary>
    /// Alignment and offset of a memory access. Bit 6 of the alignment says
    /// that a memory index follows it. Returns the offset.
    /// </summary>
    private static long ReadMemoryOperand(byte[] bytes, ref int position)
    {
        const int HasMemoryIndex = 0x40;
        ulong alignment = WasmReader.ReadLeb(bytes, ref position);
        if ((alignment & HasMemoryIndex) != 0)
        {
            WasmReader.ReadLeb(bytes, ref position);
        }

        return (long)WasmReader.ReadLeb(bytes, ref position);
    }

    private static void SkipValueType(byte[] bytes, ref int position)
    {
        byte b = bytes[position++];
        if (b == 0x63 || b == 0x64)
        {
            WasmReader.ReadSignedLeb(bytes, ref position);
        }
    }
}
