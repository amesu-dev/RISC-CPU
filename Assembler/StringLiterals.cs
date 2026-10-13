using System;
using System.Globalization;
using System.Text;

namespace RISC_CPU.Assembler {
    // ============================================================
    //  Helpers for parsing and unescaping quoted string literals.
    //  Used by both Pass 1 (to compute sizes) and Pass 2 (to emit
    //  bytes). Keeping the unescaping logic in one place guarantees
    //  that sizes and actual bytes always agree.
    // ============================================================
    internal static class StringLiterals {
        // ============================================================
        //  Check that a token is a quoted string literal, i.e. it
        //  starts and ends with a double quote.
        // ============================================================
        public static bool IsStringLiteral(string token) {
            token = token.Trim();
            return token.Length >= 2 && token[0] == '"' && token[^1] == '"';
        }

        // ============================================================
        //  Unescape a quoted string literal, e.g. "a\nb" -> a<LF>b.
        //  The quotes themselves are not included in the result.
        //  Supported escapes: \n \r \t \0 \\ \" \' \xHH
        // ============================================================
        public static string Unescape(string literal, int lineNumber) {
            literal = literal.Trim();

            if (!IsStringLiteral(literal))
                throw new AsmException(lineNumber, $"malformed string literal {literal}");

            string inner = literal[1..^1];
            var sb = new StringBuilder(inner.Length);

            for (int i = 0; i < inner.Length; i++) {
                char c = inner[i];

                if (c != '\\') {
                    sb.Append(c);
                    continue;
                }

                // escape sequence
                if (i + 1 >= inner.Length)
                    throw new AsmException(lineNumber, "trailing backslash in string literal");

                char next = inner[++i];
                switch (next) {
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    case '0':  sb.Append('\0'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"':  sb.Append('"');  break;
                    case '\'': sb.Append('\''); break;
                    case 'x': {
                        if (i + 2 >= inner.Length)
                            throw new AsmException(lineNumber, "incomplete hex escape in string literal");

                        string hex = inner.Substring(i + 1, 2);
                        if (!byte.TryParse(
                            hex, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out byte b
                        )) throw new AsmException(lineNumber, $"invalid hex escape '\\x{hex}' in string literal");

                        sb.Append((char)b);
                        i += 2;
                        break;
                    }
                    default:
                        throw new AsmException(lineNumber, $"unknown escape '\\{next}' in string literal");
                }
            }

            return sb.ToString();
        }
    }
}