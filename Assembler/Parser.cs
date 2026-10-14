using RISC_CPU.Isa;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RISC_CPU.Assembler {
    // ============================================================
    //  Parses the source text into a flat list of SourceLine
    //  entries. Pseudo-instructions are expanded here, so later
    //  passes only deal with real instructions and directives.
    // ============================================================
    internal sealed class Parser {
        private readonly List<SourceLine> _lines = new();
        private Section _current = Section.None;
        private bool _textSeen;
        private bool _dataSeen;

        // ============================================================
        //  Entry point
        // ============================================================
        public List<SourceLine> Parse(string source) {
            _lines.Clear();
            _current = Section.None;
            _textSeen = false;
            _dataSeen = false;

            string[] rawLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < rawLines.Length; i++) {
                int lineNumber = i + 1;
                string text = StripComment(rawLines[i]).Trim();
                if (text.Length == 0) continue;

                ParseLine(text, lineNumber);
            }

            return _lines;
        }

        // ============================================================
        //  Strip a trailing "#" comment.
        //  A '#' inside a char/string literal is not a comment.
        // ============================================================
        private static string StripComment(string line) {
            bool inChar = false;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < line.Length; i++) {
                char c = line[i];

                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }

                if (c == '\'' && !inString) { inChar = !inChar; continue; }
                if (c == '"' && !inChar) { inString = !inString; continue; }

                if (c == '#' && !inChar && !inString)
                    return line[..i];
            }

            return line;
        }

        //TODO: Review
        // ============================================================
        //  Parse a single non-empty line.
        // ============================================================
        private void ParseLine(string text, int lineNumber) {
            // 1. Extract a leading "label:" if present.
            string? label = null;
            int colon = FindLabelColon(text);
            if (colon >= 0) {
                label = text[..colon].Trim();
                if (!IsValidLabel(label))
                    throw new AsmException(lineNumber, $"invalid label '{label}'");

                // Every label must be inside a declared section.
                if (_current == Section.None)
                    throw new AsmException(lineNumber, $"label '{label}' appears before any section directive");

                text = text[(colon + 1)..].Trim();
            }

            // 2. Empty rest means the line is just a label.
            if (text.Length == 0) {
                if (label == null) return;
                EmitLabelOnly(label, lineNumber);
                return;
            }

            // 3. Directive or instruction?
            if (text[0] == '.')
                ParseDirective(text, label, lineNumber);
            else
                ParseInstruction(text, label, lineNumber);
        }

        // ============================================================
        //  Find the colon that terminates a label, if the line starts
        //  with one. Returns -1 if there is no leading label.
        //  A colon inside "(rs1)" or inside a string is ignored.
        // ============================================================
        private static int FindLabelColon(string text) {
            // Label must be a single identifier followed by ':'.
            int i = 0;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                i++;

            if (i == 0 || i >= text.Length || text[i] != ':') return -1;

            // Reject "r5:" style tokens if we ever add register aliases.
            return i;
        }

        private static bool IsValidLabel(string s) {
            if (s.Length == 0) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_') return false;
            for (int i = 1; i < s.Length; i++)
                if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
            return true;
        }

        // ============================================================
        //  Emit a line that only declares a label.
        //  The label is attached to an empty SourceLine; Pass 1 will
        //  record the symbol and move on.
        // ============================================================
        private void EmitLabelOnly(string label, int lineNumber) {
            _lines.Add(new SourceLine {
                LineNumber = lineNumber,
                RawText = label + ":",
                Label = label,
                Section = _current
            });
        }

        // ============================================================
        //  Directives
        // ============================================================
        private void ParseDirective(string text, string? label, int lineNumber) {
            var (name, args) = SplitMnemonicAndArgs(text, lineNumber);

            switch (name) {
                case ".text":
                    RequireNoArgs(name, args, lineNumber);
                    if (_textSeen)
                        throw new AsmException(lineNumber, ".text is declared more than once");
                    _textSeen = true;
                    _current = Section.Text;
                    // The label, if any, belongs to the new section.
                    EmitDirective(label, ".text", args, lineNumber);
                    return;

                case ".data":
                    RequireNoArgs(name, args, lineNumber);
                    if (_dataSeen)
                        throw new AsmException(lineNumber, ".data is declared more than once");
                    _dataSeen = true;
                    _current = Section.Data;
                    EmitDirective(label, ".data", args, lineNumber);
                    return;

                case ".space":
                case ".align":
                    if (args.Length != 1)
                        throw new AsmException(lineNumber, $"'{name}' expects exactly one argument, got {args.Length}");
                    
                    goto case ".word";
                case ".word":
                case ".half":
                case ".byte":
                case ".string":
                    if (_current != Section.Data)
                        throw new AsmException(lineNumber, $"data directive '{name}' is only allowed inside .data");

                    EmitDirective(label, name, args, lineNumber);
                    return;

                case ".globl":
                    // Not supported yet; fail loudly so we notice.
                    throw new AsmException(lineNumber, ".globl is not supported yet");

                default:
                    throw new AsmException(lineNumber, $"unknown directive '{name}'");
            }
        }

        private static void RequireNoArgs(string name, string[] args, int lineNumber) {
            if (args.Length != 0)
                throw new AsmException(lineNumber, $"directive '{name}' takes no arguments");
        }

        private void EmitDirective(string? label, string directive, string[] args, int lineNumber) {
            _lines.Add(new SourceLine {
                LineNumber = lineNumber,
                RawText = label == null ? directive : $"{label}: {directive}",
                Label = label,
                Directive = directive,
                Args = args,
                Section = _current
            });
        }

        // ============================================================
        //  Instructions (including pseudo-instruction expansion)
        // ============================================================
        private void ParseInstruction(string text, string? label, int lineNumber) {
            if (_current != Section.Text)
                throw new AsmException(lineNumber, "instructions are only allowed inside .text");

            var (mnemonic, args) = SplitMnemonicAndArgs(text, lineNumber);

            // If not valid command, try to expand as pseudo-instruction. If that fails, throw an error.
            if (!InstructionSet.TryGetCommand(mnemonic, out var cmd)) {
                // Maybe a pseudo-instruction?
                if (TryExpandPseudo(mnemonic, args, label, lineNumber))
                    return;

                throw new AsmException(lineNumber, $"unknown instruction '{mnemonic}'");
            }

            // Real instruction: pass it through.
            EmitInstruction(label, mnemonic, args, lineNumber);
        }

        private void EmitInstruction(string? label, string mnemonic, string[] args, int lineNumber) {
            _lines.Add(new SourceLine {
                LineNumber = lineNumber,
                RawText = label == null ? mnemonic : $"{label}: {mnemonic}",
                Label = label,
                Mnemonic = mnemonic.ToLowerInvariant(),
                Args = args,
                Section = _current
            });
        }

        // ============================================================
        //  Pseudo-instruction expansion.
        //  Each expansion appends one or more SourceLine entries to
        //  _lines; only the first one carries the original label.
        // ============================================================
        private bool TryExpandPseudo(string mnemonic, string[] args, string? label, int lineNumber) {
            string m = mnemonic.ToLowerInvariant();

            switch (m) {
                case "nop":
                    RequireArgs(m, args, 0, lineNumber);
                    EmitPseudo(label, lineNumber, "addi", "zero", "zero", "0");
                    return true;

                case "mv":
                    RequireArgs(m, args, 2, lineNumber);
                    EmitPseudo(label, lineNumber, "addi", args[0], args[1], "0");
                    return true;

                case "neg":
                    RequireArgs(m, args, 2, lineNumber);
                    EmitPseudo(label, lineNumber, "sub", args[0], "zero", args[1]);
                    return true;

                case "j":
                    RequireArgs(m, args, 1, lineNumber);
                    EmitPseudo(label, lineNumber, "jal", "zero", args[0]);
                    return true;

                case "jr":
                    RequireArgs(m, args, 1, lineNumber);
                    // jalr zero, 0(rs)
                    EmitPseudo(label, lineNumber, "jalr", "zero", $"0({args[0]})");
                    return true;

                case "ret":
                    RequireArgs(m, args, 0, lineNumber);
                    EmitPseudo(label, lineNumber, "jalr", "zero", "0(ra)");
                    return true;

                case "call":
                    RequireArgs(m, args, 1, lineNumber);
                    EmitPseudo(label, lineNumber, "jal", "ra", args[0]);
                    return true;

                case "beqz":
                    RequireArgs(m, args, 2, lineNumber);
                    EmitPseudo(label, lineNumber, "beq", args[0], "zero", args[1]);
                    return true;

                case "bnez":
                    RequireArgs(m, args, 2, lineNumber);
                    EmitPseudo(label, lineNumber, "bne", args[0], "zero", args[1]);
                    return true;

                case "li":
                    RequireArgs(m, args, 2, lineNumber);
                    ExpandLi(label, args[0], args[1], lineNumber);
                    return true;

                default:
                    return false;
            }
        }

        // ============================================================
        //  li rd, imm
        //  If imm fits in a signed 12-bit field:  addi rd, zero, imm
        //  Otherwise:                             lui rd, hi
        //                                         addi rd, rd, lo
        //  where hi = (imm + 0x800) >> 12 and lo = signed(imm & 0xFFF).
        //  This reconstructs the exact 32-bit value.
        // ============================================================
        private void ExpandLi(string? label, string rd, string immText, int lineNumber) {
            int imm = OperandParser.ParseImmediate(immText, lineNumber);

            if (OperandParser.FitsSigned(imm, 12)) {
                EmitPseudo(label, lineNumber, "addi", rd, "zero", imm.ToString(CultureInfo.InvariantCulture));
                return;
            }

            int hi = (imm + 0x800) >> 12;
            int lo = imm & 0xFFF;
            // sign-extend the low 12 bits
            if ((lo & 0x800) != 0) lo -= 0x1000;

            EmitPseudo(label, lineNumber, "lui", rd, hi.ToString(CultureInfo.InvariantCulture));
            EmitPseudo(null, lineNumber, "addi", rd, rd, lo.ToString(CultureInfo.InvariantCulture));
        }

        private void EmitPseudo(string? label, int lineNumber, string mnemonic, params string[] args) {
            _lines.Add(new SourceLine {
                LineNumber = lineNumber,
                RawText = (label == null ? "" : label + ": ") + mnemonic + " " + string.Join(", ", args),
                Label = label,
                Mnemonic = mnemonic,
                Args = args,
                Section = _current
            });
        }

        private static void RequireArgs(string mnemonic, string[] args, int expected, int lineNumber) {
            if (args.Length != expected)
                throw new AsmException(lineNumber, $"'{mnemonic}' expects {expected} operand(s), got {args.Length}");
        }

        // ============================================================
        //  Split a line into (mnemonic, args[]).
        //  Args are comma-separated; whitespace around commas is trimmed.
        // ============================================================
        private static (string mnemonic, string[] args) SplitMnemonicAndArgs(string text, int lineNumber) {
            text = text.Trim();

            int space = IndexOfWhitespace(text);
            if (space < 0)
                return (text.ToLowerInvariant(), Array.Empty<string>());

            string mnemonic = text[..space].ToLowerInvariant();
            string rest = text[space..].Trim();

            var parts = new List<string>();
            var current = new StringBuilder();
            bool inChar = false;
            bool inString = false;
            bool escaped = false;

            // Split on commas, but ignore commas inside char or string literals.
            foreach (char c in rest) {
                if (escaped) { current.Append(c); escaped = false; continue; }
                if (c == '\\' && (inChar || inString)) { current.Append(c); escaped = true; continue; }

                if (c == '\'' && !inString) { inChar = !inChar; current.Append(c); continue; }
                if (c == '"' && !inChar) { inString = !inString; current.Append(c); continue; }

                if (c == ',' && !inChar && !inString) {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            if (inChar || inString)
                throw new AsmException(lineNumber, "unterminated literal");

            parts.Add(current.ToString().Trim());

            return (mnemonic, parts.ToArray());
        }

        private static int IndexOfWhitespace(string s) {
            for (int i = 0; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i])) return i;
            return -1;
        }
    }
}