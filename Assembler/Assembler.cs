using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RISC_CPU.Assembler
{
    public sealed class Assembler
    {
        // ============================================================
        //  Pass 1 state: which section we are currently in
        // ============================================================
        private enum Section { None, Text, Data }

        

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
            throw new NotImplementedException("Step 3: line parser");
        }

        // ============================================================
        //  Pass 1: expand pseudo-instructions, advance location counters,
        //          collect symbols.
        //  Returns (bytes in .text, bytes in .data).
        //  TODO (Step 4)
        // ============================================================
        private (int codeSize, int dataSize) Pass1CollectSymbols(
            List<SourceLine> lines, SymbolTable symbols
        ) {
            throw new NotImplementedException("Step 4: first pass");
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