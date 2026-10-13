using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
            List<SourceLine> lines, SymbolTable symbols) {

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
        //  Pass 2: encode instructions into uint[]
        //  TODO (Step 5)
        // ============================================================
        private uint[] Pass2EmitCode(List<SourceLine> lines, SymbolTable symbols)
        {
            throw new NotImplementedException("Step 5: encode .text");
        }

        // ============================================================
        //  Pass 2: pack data into byte[]
        //  TODO (Step 5)
        // ============================================================
        private byte[] Pass2EmitData(List<SourceLine> lines, SymbolTable symbols)
        {
            throw new NotImplementedException("Step 5: emit .data");
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