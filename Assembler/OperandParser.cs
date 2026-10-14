using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RISC_CPU.Assembler {
    // ============================================================
    //  Parses individual operands: registers, immediates, memory
    //  operands of the form imm(rs1), and label references.
    // ============================================================
    internal static class OperandParser {
        // ABI register names, per the RISC-V calling convention.
        private static readonly Dictionary<string, byte> AbiNames = new(StringComparer.OrdinalIgnoreCase) {
            ["zero"] = 0, ["ra"] = 1,  ["sp"] = 2,  ["gp"] = 3,
            ["tp"] = 4,   ["t0"] = 5,  ["t1"] = 6,  ["t2"] = 7,
            ["s0"] = 8,   ["fp"] = 8,  ["s1"] = 9,  ["a0"] = 10,
            ["a1"] = 11,  ["a2"] = 12, ["a3"] = 13, ["a4"] = 14,
            ["a5"] = 15,  ["a6"] = 16, ["a7"] = 17, ["s2"] = 18,
            ["s3"] = 19,  ["s4"] = 20, ["s5"] = 21, ["s6"] = 22,
            ["s7"] = 23,  ["s8"] = 24, ["s9"] = 25, ["s10"] = 26,
            ["s11"] = 27, ["t3"] = 28, ["t4"] = 29, ["t5"] = 30,
            ["t6"] = 31,
        };

        // ============================================================
        //  Case insensitive. Returns the register number (0-31).
        //  Parse a register operand: "r5", "a0", "zero", ...
        // ============================================================
        public static byte ParseRegister(string token, int lineNumber) {
            token = token.Trim();

            // Decline string and char literals as registers, since they are valid immediates.
            if (token.Length > 0 && (token[0] == '"' || token[0] == '\''))
                throw new AsmException(lineNumber, $"expected a register, got literal {token}");

            // rN form
            if (token.Length >= 2 && token[0] == 'R') {
                if (
                    int.TryParse(
                        token.AsSpan(1), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int n
                    ) && n >= 0 && n <= 31
                ) return (byte)n;

                throw new AsmException(lineNumber, $"invalid register '{token}'");
            }

            // ABI name
            if (AbiNames.TryGetValue(token, out byte abi))
                return abi;

            throw new AsmException(lineNumber, $"invalid register '{token}'");
        }

        // ============================================================
        //  Case sensitive. Returns the integer value of the immediate.
        //  Parse an integer literal:
        //    decimal       10, -5, +42
        //    hex           0x1F
        //    binary        0b1010
        //    char literal  'A'
        // ============================================================
        public static int ParseImmediate(string token, int lineNumber) {
            token = token.Trim();
            if (token.Length == 0)
                throw new AsmException(lineNumber, "empty immediate");


            if (token[0] == '"')
                throw new AsmException(lineNumber, $"string literal '{token}' cannot be used as an immediate");

            // char literal
            if (token[0] == '\'') {
                if (token.Length < 3 || token[^1] != '\'')
                    throw new AsmException(lineNumber, $"malformed char literal {token}");

                string inner = token[1..^1];
                if (inner.Length == 1)
                    return inner[0];

                // escape sequence: '\n', '\t', '\\', '\'', '\0', '\xHH'
                if (inner.Length == 2 && inner[0] == '\\')
                    return UnescapeChar(inner[1], lineNumber);

                if (inner.Length == 4 && inner[0] == '\\' && inner[1] == 'x') {
                    if (byte.TryParse(
                        inner.AsSpan(2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out byte b
                    )) return b;
                    throw new AsmException(lineNumber, $"invalid hex escape {token}");
                }

                throw new AsmException(lineNumber, $"malformed char literal {token}");
            }

            // hex / binary
            int sign = 1;
            if (token[0] == '+') token = token[1..];
            else if (token[0] == '-') { sign = -1; token = token[1..]; }

            try {
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return sign * (int)Convert.ToUInt32(token[2..], 16);
                if (token.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                    return sign * (int)Convert.ToUInt32(token[2..], 2);

                return sign * int.Parse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
            catch {
                throw new AsmException(lineNumber, $"invalid immediate '{token}'");
            }
        }

        private static int UnescapeChar(char c, int lineNumber) => c switch {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '0' => '\0',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            _ => throw new AsmException(lineNumber, $"unknown escape '\\{c}'"),
        };

        // ============================================================
        //  Case insensitive
        //  Parse a memory operand: "imm(rs1)" or "(rs1)" or "imm".
        //  Returns (register, immediate). The register is -1 if absent.
        // ============================================================
        public static (byte rs1, int imm) ParseMemoryOperand(string token, int lineNumber) {
            token = token.Trim().ToUpper();

            int open = token.IndexOf('(');
            if (open == -1) {
                // no register form; caller decides whether that is OK
                int immOnly = ParseImmediate(token, lineNumber);
                return (unchecked((byte)0xFF), immOnly);
            }

            int close = token.LastIndexOf(')');
            if (close < open)
                throw new AsmException(lineNumber, $"malformed memory operand '{token}'");

            string immText = token[..open].Trim();
            string regText = token[(open + 1)..close].Trim();

            int imm = immText.Length == 0 ? 0 : ParseImmediate(immText, lineNumber);
            byte rs1 = ParseRegister(regText, lineNumber);

            return (rs1, imm);
        }

        // ============================================================
        //  Check that a value fits into a signed N-bit field.
        // ============================================================
        public static bool FitsSigned(int value, int bits) {
            int min = -(1 << (bits - 1));
            int max = (1 << (bits - 1)) - 1;
            return value >= min && value <= max;
        }
    }
}