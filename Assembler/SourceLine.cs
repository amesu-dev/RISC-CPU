using System;
using System.Collections.Generic;
using System.Text;

namespace RISC_CPU.Assembler
{
    internal enum Section { None, Text, Data }

    // ============================================================
    //  Intermediate representation of a single parsed line.
    //  Filled by the parser, consumed by both passes.
    // ============================================================
    internal sealed class SourceLine{
        public int LineNumber;
        public string RawText = "";

        // What the line contains: label, mnemonic, directive, args
        public string? Label;              // null if none
        public string? Mnemonic;           // "add", "lw", ... or null
        public string? Directive;          // ".text", ".word", ... or null
        public string[] Args = Array.Empty<string>();
        public Section Section = Section.None;
    }
}
