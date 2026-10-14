using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RISC_CPU.Isa;
using Encoder = RISC_CPU.Isa.Encoder;

namespace RISC_CPU.Assembler {
    public sealed class Assembler {
        // ============================================================
        //  Public API
        // ============================================================
        public AssembledProgram Assemble(string source) {
            var lines = Parse(source);

            var symbols = new SymbolTable();
            var (codeSize, dataSize) = Pass1CollectSymbols(lines, symbols);

            var code = Pass2EmitCode(lines, symbols);
            var data = Pass2EmitData(lines, symbols);

            int entry = ResolveEntryPoint(symbols);

            return new AssembledProgram {
                Code = code,
                Data = data,
                EntryPoint = entry,
                Symbols = symbols,
            };
        }

        // ============================================================
        //  Parser: text -> List<SourceLine>
        //  TODO (Step 3)
        // ============================================================
        private List<SourceLine> Parse(string source) {
            Parser parser = new Parser();

            return parser.Parse(source);
        }

        // ============================================================
        //  Pass 1: walk the parsed lines, advance the location counters,
        //  and collect every symbol with its offset relative to its section.
        //
        //  Rules:
        //    - a label binds to the current offset BEFORE the directive /
        //      instruction on the same line is accounted for
        //    - .text and .data are counted independently
        //    - .word / .half / .byte sizes are width * argCount, no value
        //      parsing is performed here
        //    - .string sizes require unescaping, because they depend on the
        //      actual characters
        //
        //  Returns (bytes in .text, bytes in .data).
        // ============================================================
        private (int codeSize, int dataSize) Pass1CollectSymbols(
            List<SourceLine> lines, SymbolTable symbols
        ) {

            int codeOffset = 0;
            int dataOffset = 0;

            foreach (var line in lines) {
                // ------------------------------------------------
                //  1. A label binds to the current offset of whichever
                //     section we are in.
                // ------------------------------------------------
                if (line.Label != null) {
                    SymbolKind kind = line.Section switch {
                        Section.Text => SymbolKind.Code,
                        Section.Data => SymbolKind.Data,
                        _ => throw new AsmException(line.LineNumber, $"label '{line.Label}' is not inside a section"),
                    };

                    int offset = kind == SymbolKind.Code ? codeOffset : dataOffset;

                    symbols.Add(new Symbol(
                        Name: line.Label,
                        Kind: kind,
                        Offset: offset,
                        Size: 0,
                        LineNumber: line.LineNumber
                    ));
                }

                // ------------------------------------------------
                //  2. Advance the appropriate location counter.
                // ------------------------------------------------
                if (line.Mnemonic != null) {
                    codeOffset += 4;
                }
                else if (line.Directive == ".align") {
                    if (line.Args.Length != 1)
                        throw new AsmException(line.LineNumber, $"'{line.Directive}' expects 1 argument(s), got {line.Args.Length}");

                    int n = OperandParser.ParseImmediate(line.Args[0], line.LineNumber);
                    if (n < 0 || n > 12)
                        throw new AsmException(line.LineNumber, $".align exponent must be in 0..12, got {n}");
                    
                    int align = 1 << n;
                    int padding = (align - (dataOffset % align)) % align;
                    dataOffset += padding;
                } else if (line.Directive != null) {
                    dataOffset += SizeOfDirective(line);
                }
            }

            return (codeOffset, dataOffset);
        }

        // ============================================================
        //  Compute how many bytes a data directive occupies in Pass 1.
        //  Sizes are computed without parsing numeric values.
        // ============================================================
        private int SizeOfDirective(SourceLine line) {
            switch (line.Directive) {
                case ".word":
                    return 4 * line.Args.Length;
                case ".half":
                    return 2 * line.Args.Length;
                case ".byte":
                    return 1 * line.Args.Length;
                case ".string": {
                    int total = 0;
                    foreach (var arg in line.Args) {
                        // Excepts error if the string literal is malformed (e.g. missing quotes, invalid escape).
                        string unescaped = StringLiterals.Unescape(arg, line.LineNumber);
                        total += unescaped.Length + 1; // + NUL terminator
                    }
                    return total;
                }
                case ".space": {
                    if (line.Args.Length != 1)
                        throw new AsmException(line.LineNumber, $"'{line.Directive}' expects 1 argument(s), got {line.Args.Length}");

                    int n = OperandParser.ParseImmediate(line.Args[0], line.LineNumber);
                    if (n < 0)
                        throw new AsmException(line.LineNumber, $".space size must be non-negative, got {n}");

                    return n;
                }
                case ".align": {
                    // .align consumes padding bytes; the actual amount depends on
                    // the current data offset, which we do not have here. The
                    // caller is expected to handle alignment specially.
                    throw new InvalidOperationException(".align must be handled by the caller, not SizeOfDirective");
                }
                case ".text":
                case ".data":
                    // Section switch directives take zero bytes.
                    return 0;

                default:
                    throw new AsmException(line.LineNumber, $"unknown data directive '{line.Directive}'");
            }
        }

       // ============================================================
        //  Pass 2: encode instructions into uint[].
        //  Walks the same list as Pass 1, in the same order, so the
        //  code offsets match.
        // ============================================================
        private uint[] Pass2EmitCode(List<SourceLine> lines, SymbolTable symbols) {
            // Count instructions first so we can size the array exactly.
            int count = 0;
            foreach (var l in lines) {
                if (l.Mnemonic != null) count++;
            }

            var code = new uint[count];
            int pc = 0;
            int idx = 0;

            foreach (var line in lines) {
                if (line.Mnemonic == null) continue;

                uint word = EncodeOne(line, symbols, pc);
                code[idx++] = word;
                pc += 4;
            }

            return code;
        }

        // ============================================================
        //  Encode a single instruction line.
        // ============================================================
        private uint EncodeOne(SourceLine line, SymbolTable symbols, int pc) {
            if (!InstructionSet.TryGetCommand(line.Mnemonic!, out var cmd)) {
                // The parser already rejected unknown mnemonics; this is a
                // defensive check only.
                throw new AsmException(line.LineNumber,
                    $"unknown instruction '{line.Mnemonic}'", line.RawText);
            }

            byte rd = 0, rs1 = 0, rs2 = 0;
            int imm = 0;

            switch (cmd) {
                // ---------------- R-type ----------------
                case COMMANDS.ADD: case COMMANDS.SUB: case COMMANDS.SLL:
                case COMMANDS.SLT: case COMMANDS.SLTU: case COMMANDS.XOR:
                case COMMANDS.SRL: case COMMANDS.SRA: case COMMANDS.OR:
                case COMMANDS.AND: {
                    RequireArgCount(line, 3);
                    rd  = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    rs1 = OperandParser.ParseRegister(line.Args[1], line.LineNumber);
                    rs2 = OperandParser.ParseRegister(line.Args[2], line.LineNumber);
                    break;
                }

                // ---------------- I-type arithmetic ----------------
                case COMMANDS.ADDI: case COMMANDS.SLTI: case COMMANDS.SLTIU:
                case COMMANDS.XORI: case COMMANDS.ORI:  case COMMANDS.ANDI: {
                    RequireArgCount(line, 3);
                    rd  = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    rs1 = OperandParser.ParseRegister(line.Args[1], line.LineNumber);
                    imm = OperandParser.ParseImmediate(line.Args[2], line.LineNumber);
                    break;
                }

                // ---------------- Shifts with immediate ----------------
                // The shift amount must fit in 5 bits (0..31).
                case COMMANDS.SLLI: case COMMANDS.SRLI: case COMMANDS.SRAI: {
                    RequireArgCount(line, 3);
                    rd  = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    rs1 = OperandParser.ParseRegister(line.Args[1], line.LineNumber);
                    imm = OperandParser.ParseImmediate(line.Args[2], line.LineNumber);
                    if (imm < 0 || imm > 31) {
                        throw new AsmException(line.LineNumber,
                            $"shift amount {imm} must be in 0..31", line.RawText);
                    }
                    break;
                }

                // ---------------- Loads ----------------
                case COMMANDS.LB: case COMMANDS.LH: case COMMANDS.LW:
                case COMMANDS.LBU: case COMMANDS.LHU: {
                    RequireArgCount(line, 2);
                    rd = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    var (memRs1, memImm) = OperandParser.ParseMemoryOperand(
                        line.Args[1], line.LineNumber);
                    if (memRs1 == 0xFF) {
                        throw new AsmException(line.LineNumber,
                            $"load requires a memory operand of the form imm(rs1)", line.RawText);
                    }
                    rs1 = memRs1;
                    imm = memImm;
                    break;
                }

                // ---------------- Stores ----------------
                case COMMANDS.SB: case COMMANDS.SH: case COMMANDS.SW: {
                    RequireArgCount(line, 2);
                    rs2 = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    var (memRs1, memImm) = OperandParser.ParseMemoryOperand(
                        line.Args[1], line.LineNumber);
                    if (memRs1 == 0xFF) {
                        throw new AsmException(line.LineNumber,
                            $"store requires a memory operand of the form imm(rs1)", line.RawText);
                    }
                    rs1 = memRs1;
                    imm = memImm;
                    break;
                }

                // ---------------- Branches ----------------
                // Third argument is a label (or a literal offset). We convert
                // it into a PC-relative byte offset before encoding.
                case COMMANDS.BEQ: case COMMANDS.BNE: case COMMANDS.BLT:
                case COMMANDS.BGE: case COMMANDS.BLTU: case COMMANDS.BGEU: {
                    RequireArgCount(line, 3);
                    rs1 = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    rs2 = OperandParser.ParseRegister(line.Args[1], line.LineNumber);
                    imm = ResolveBranchTarget(line.Args[2], symbols, pc, line);
                    break;
                }

                // ---------------- JAL (J-type) ----------------
                case COMMANDS.JAL: {
                    RequireArgCount(line, 2);
                    rd  = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    imm = ResolveBranchTarget(line.Args[1], symbols, pc, line);
                    break;
                }

                // ---------------- JALR (I-type) ----------------
                case COMMANDS.JALR: {
                    RequireArgCount(line, 2);
                    rd = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    var (memRs1, memImm) = OperandParser.ParseMemoryOperand(
                        line.Args[1], line.LineNumber);
                    if (memRs1 == 0xFF) {
                        throw new AsmException(line.LineNumber,
                            $"jalr requires a memory operand of the form imm(rs1)", line.RawText);
                    }
                    rs1 = memRs1;
                    imm = memImm;
                    break;
                }

                // ---------------- U-type ----------------
                case COMMANDS.LUI: case COMMANDS.AUIPC: {
                    RequireArgCount(line, 2);
                    rd  = OperandParser.ParseRegister(line.Args[0], line.LineNumber);
                    imm = OperandParser.ParseImmediate(line.Args[1], line.LineNumber);
                    break;
                }

                // ---------------- System ----------------
                case COMMANDS.ECALL: {
                    RequireArgCount(line, 0);
                    imm = 0;
                    break;
                }
                case COMMANDS.EBREAK: {
                    RequireArgCount(line, 0);
                    imm = 1;
                    break;
                }

                default:
                    throw new AsmException(line.LineNumber,
                        $"instruction '{line.Mnemonic}' is not handled by the assembler",
                        line.RawText);
            }

            try {
                return Encoder.Encode(cmd, rd, rs1, rs2, imm);
            }
            catch (EncodeException ex) {
                throw new AsmException(line.LineNumber, ex.Message, line.RawText);
            }
        }

        // ============================================================
        //  Resolve the target of a branch or jal.
        //  If the argument names a symbol, compute the PC-relative offset
        //  and verify that the symbol belongs to the .text section.
        //  Otherwise parse it as a literal byte offset.
        // ============================================================
        private int ResolveBranchTarget(string arg, SymbolTable symbols, int pc, SourceLine line) {
            arg = arg.Trim();

            if (symbols.TryGet(arg, out var sym)) {
                if (sym.Kind != SymbolKind.Code) {
                    throw new AsmException(line.LineNumber,
                        $"branch/jump target '{arg}' is in .data, not .text", line.RawText);
                }

                // TextBase is 0, so the symbol's absolute address is its offset.
                int target = sym.Offset;
                return target - pc;
            }

            // Not a symbol; must be a numeric byte offset.
            return OperandParser.ParseImmediate(arg, line.LineNumber);
        }

        // ============================================================
        //  Pass 2: emit the .data section as a byte array.
        //  Walks the same list as Pass 1, in the same order.
        // ============================================================
        private byte[] Pass2EmitData(List<SourceLine> lines, SymbolTable symbols) {
            // First pass over the data directives to compute the total size,
            // so we can allocate exactly one output buffer.
            int dataSize = ComputeDataSize(lines);

            var data = new byte[dataSize];
            int offset = 0;

            foreach (var line in lines) {
                if (line.Directive == null) continue;

                offset = EmitDataDirective(line, symbols, data, offset);
            }

            return data;
        }

        // ============================================================
        //  Compute the size of the .data section by walking the list.
        //  Mirrors Pass 1 but does not touch the symbol table.
        // ============================================================
        private int ComputeDataSize(List<SourceLine> lines) {
            int size = 0;

            foreach (var line in lines) {
                if (line.Directive == null) continue;

                switch (line.Directive) {
                    case ".word":   size += 4 * line.Args.Length; break;
                    case ".half":   size += 2 * line.Args.Length; break;
                    case ".byte":   size += 1 * line.Args.Length; break;

                    case ".string": {
                        foreach (var arg in line.Args) {
                            string s = StringLiterals.Unescape(arg, line.LineNumber);
                            size += s.Length + 1; // + NUL terminator
                        }
                        break;
                    }

                    case ".space": {
                        int n = OperandParser.ParseImmediate(line.Args[0], line.LineNumber);
                        if (n < 0)
                            throw new AsmException(line.LineNumber,
                                $".space size must be non-negative, got {n}", line.RawText);
                        size += n;
                        break;
                    }

                    case ".align": {
                        int n = OperandParser.ParseImmediate(line.Args[0], line.LineNumber);
                        if (n < 0 || n > 12)
                            throw new AsmException(line.LineNumber,
                                $".align exponent must be in 0..12, got {n}", line.RawText);
                        int align = 1 << n;
                        int padding = (align - (size % align)) % align;
                        size += padding;
                        break;
                    }

                    default:
                        // .text / .data are zero-sized section switches.
                        break;
                }
            }

            return size;
        }

        // ============================================================
        //  Emit one data directive into the output buffer.
        //  Returns the new offset.
        // ============================================================
        private int EmitDataDirective(SourceLine line, SymbolTable symbols,
                                      byte[] data, int offset) {
            switch (line.Directive) {
                case ".word": {
                    foreach (var arg in line.Args) {
                        int value = ResolveDataWord(arg, symbols, line);
                        WriteInt32LE(data, offset, value);
                        offset += 4;
                    }
                    return offset;
                }

                case ".half": {
                    foreach (var arg in line.Args) {
                        int value = OperandParser.ParseImmediate(arg, line.LineNumber);
                        if (value < short.MinValue || value > ushort.MaxValue)
                            throw new AsmException(line.LineNumber,
                                $".half value {value} does not fit in 16 bits", line.RawText);
                        data[offset++] = (byte)(value & 0xFF);
                        data[offset++] = (byte)((value >> 8) & 0xFF);
                    }
                    return offset;
                }

                case ".byte": {
                    foreach (var arg in line.Args) {
                        int value = OperandParser.ParseImmediate(arg, line.LineNumber);
                        if (value < sbyte.MinValue || value > byte.MaxValue)
                            throw new AsmException(line.LineNumber,
                                $".byte value {value} does not fit in 8 bits", line.RawText);
                        data[offset++] = (byte)(value & 0xFF);
                    }
                    return offset;
                }

                case ".string": {
                    foreach (var arg in line.Args) {
                        string s = StringLiterals.Unescape(arg, line.LineNumber);
                        foreach (char c in s) {
                            if (c > 0xFF)
                                throw new AsmException(line.LineNumber,
                                    $"character U+{(int)c:X4} does not fit in a byte", line.RawText);
                            data[offset++] = (byte)c;
                        }
                        data[offset++] = 0; // NUL terminator
                    }
                    return offset;
                }

                case ".space": {
                    int n = OperandParser.ParseImmediate(line.Args[0], line.LineNumber);
                    if (n < 0)
                        throw new AsmException(line.LineNumber,
                            $".space size must be non-negative, got {n}", line.RawText);
                    offset += n; // buffer is already zeroed
                    return offset;
                }

                case ".align": {
                    int n = OperandParser.ParseImmediate(line.Args[0], line.LineNumber);
                    if (n < 0 || n > 12)
                        throw new AsmException(line.LineNumber,
                            $".align exponent must be in 0..12, got {n}", line.RawText);
                    int align = 1 << n;
                    int padding = (align - (offset % align)) % align;
                    offset += padding; // buffer is already zeroed
                    return offset;
                }

                case ".text":
                case ".data":
                    return offset; // section switches emit nothing

                default:
                    throw new AsmException(line.LineNumber,
                        $"unknown directive '{line.Directive}'", line.RawText);
            }
        }

        // ============================================================
        //  Resolve a .word argument: either a literal number or a label.
        //  Labels are encoded as absolute addresses:
        //    .text symbol  -> offset (TextBase == 0)
        //    .data symbol  -> DataBase + offset
        // ============================================================
        private int ResolveDataWord(string arg, SymbolTable symbols, SourceLine line) {
            arg = arg.Trim();

            // Try a literal first. A bare identifier will fail ParseImmediate,
            // and we then try the symbol table.
            if (TryParseLiteral(arg, out int literal)) {
                return literal;
            }

            if (symbols.TryGet(arg, out var sym)) {
                int baseAddr = sym.Kind == SymbolKind.Code ? 0 : DataBase(symbols);
                return baseAddr + sym.Offset;
            }

            throw new AsmException(line.LineNumber,
                $".word operand '{arg}' is neither a number nor a known label",
                line.RawText);
        }

        // ============================================================
        //  Try to parse a token as an integer literal without throwing.
        //  Returns false for anything that is not a valid number.
        // ============================================================
        private static bool TryParseLiteral(string token, out int value) {
            value = 0;
            token = token.Trim();
            if (token.Length == 0) return false;

            // Reject tokens that look like labels up front; ParseImmediate
            // would throw on them, which is not what we want here.
            char c0 = token[0];
            if (char.IsLetter(c0) && c0 != 'x' && c0 != 'X') {
                // Could still be a char literal; those start with '\''.
                if (c0 != '\'') return false;
            }

            try {
                value = OperandParser.ParseImmediate(token, 0);
                return true;
            }
            catch (AsmException) {
                return false;
            }
        }

        // ============================================================
        //  Absolute address of the start of the .data section.
        //  Equals the size of .text in bytes.
        // ============================================================
        private int DataBase(SymbolTable symbols) {
            // We cannot query the assembler's own pass state from here, so
            // recompute it from the symbol table: the .text section size is
            // not stored as a symbol. Instead, we rely on the fact that the
            // AssembledProgram knows DataBase = Code.Length * 4. During the
            // data emission pass we already know the code array length, so
            // the caller passes it in. This method is intentionally a stub
            // that must be replaced by a proper parameter.
            throw new InvalidOperationException(
                "DataBase must be provided by Pass2EmitData; see caller.");
        }

        // ============================================================
        //  Write a 32-bit little-endian value at the given offset.
        // ============================================================
        private static void WriteInt32LE(byte[] buf, int offset, int value) {
            uint u = (uint)value;
            buf[offset + 0] = (byte)(u & 0xFF);
            buf[offset + 1] = (byte)((u >> 8)  & 0xFF);
            buf[offset + 2] = (byte)((u >> 16) & 0xFF);
            buf[offset + 3] = (byte)((u >> 24) & 0xFF);
        }

        // ============================================================
        //  Argument count check with a clear error message.
        // ============================================================
        private static void RequireArgCount(SourceLine line, int expected) {
            if (line.Args.Length != expected) {
                throw new AsmException(line.LineNumber,
                    $"'{line.Mnemonic}' expects {expected} operand(s), got {line.Args.Length}",
                    line.RawText);
            }
        }

        // ============================================================
        //  Resolve entry point:
        //  1) label _start in .text
        //  2) fallback: 0
        // ============================================================
        private int ResolveEntryPoint(SymbolTable symbols) {
            if (symbols.TryGet("_start", out var s)) {
                if (s.Kind != SymbolKind.Code)
                    throw new AsmException(s.LineNumber, "label '_start' must be inside the .text section");
                return s.Offset; // TextBase = 0
            }
            return 0;
        }
    }
}