using System;

namespace RISC_CPU.Isa {
   public sealed class EncodeException : Exception {
        public EncodeException(string message) : base(message) { }
    }

    // ============================================================
    //  Packs a semantic instruction into its 32-bit RISC-V encoding.
    //
    //  The encoder is a pure function: it takes the command, the
    //  three register operands (unused ones are ignored) and the
    //  immediate, and returns the encoded word. It performs range
    //  and alignment checks and throws EncodeException on failure.
    //
    //  PC-relative computation is NOT done here. Callers resolve
    //  labels to absolute addresses and pass the byte offset in
    //  `imm`. This keeps the encoder independent of the symbol table.
    // ============================================================
    public static class Encoder {
        // ============================================================
        //  Encode one instruction.
        //
        //  `rd`, `rs1`, `rs2` are ignored for commands that do not use
        //  them. `imm` is interpreted according to the descriptor's
        //  format. For branches and jal, `imm` is a byte offset
        //  relative to the branch instruction.
        // ============================================================
        public static uint Encode(
            COMMANDS cmd, byte rd, byte rs1, byte rs2, int imm
        ) {

            if (!InstructionSet.Descriptors.TryGetValue(cmd, out var d)) {
                throw new EncodeException($"no descriptor for command {cmd}");
            }

            return d.Format switch {
                FORMATS.R => PackR(d, rd, rs1, rs2),
                FORMATS.I => PackI(d, rd, rs1, imm),
                FORMATS.S => PackS(d, rs1, rs2, imm),
                FORMATS.B => PackB(d, rs1, rs2, imm),
                FORMATS.U => PackU(d, rd, imm),
                FORMATS.J => PackJ(d, rd, imm),
                _         => throw new EncodeException($"unsupported format {d.Format}"),
            };
        }

        // ============================================================
        //  R-type: [ funct7 | rs2 | rs1 | funct3 | rd | opcode ]
        // ============================================================
        private static uint PackR(InstructionDescriptor d, byte rd, byte rs1, byte rs2) {
            uint op = (uint)d.Opcode;
            uint f3 = d.Funct3 ?? 0;
            uint f7 = d.Funct7 ?? 0;

            return (f7 << 25)
                 | ((uint)rs2 << 20)
                 | ((uint)rs1 << 15)
                 | (f3 << 12)
                 | ((uint)rd << 7)
                 | op;
        }

        // ============================================================
        //  I-type: [ imm[11:0] | rs1 | funct3 | rd | opcode ]
        //
        //  For SLLI/SRLI/SRAI the top 7 bits of the immediate field
        //  are not shamt but funct7, so we OR the descriptor's funct7
        //  into the immediate field. This is the classic RV32I
        //  gotcha: the shift amount lives in the low 5 bits only.
        // ============================================================
        private static uint PackI(InstructionDescriptor d, byte rd, byte rs1, int imm) {
            uint op = (uint)d.Opcode;
            uint f3 = d.Funct3 ?? 0;

            // Range check: signed 12-bit field.
            if (!FitsSigned(imm, 12)) {
                throw new EncodeException($"immediate {imm} does not fit in 12 bits");
            }

            uint immField;

            if (d.Command == COMMANDS.SLLI
                || d.Command == COMMANDS.SRLI
                || d.Command == COMMANDS.SRAI) {
                int shamt = imm & 0x1F;
                uint f7 = d.Funct7 ?? 0;
                immField = (f7 << 5) | (uint)shamt;
            }
            else {
                immField = (uint)(imm & 0xFFF);
            }

            return (immField << 20)
                 | ((uint)rs1 << 15)
                 | (f3 << 12)
                 | ((uint)rd << 7)
                 | op;
        }

        // ============================================================
        //  S-type: [ imm[11:5] | rs2 | rs1 | funct3 | imm[4:0] | opcode ]
        // ============================================================
        private static uint PackS(InstructionDescriptor d, byte rs1, byte rs2, int imm) {
            uint op = (uint)d.Opcode;
            uint f3 = d.Funct3 ?? 0;

            if (!FitsSigned(imm, 12)) {
                throw new EncodeException($"store offset {imm} does not fit in 12 bits");
            }

            uint uimm = (uint)(imm & 0xFFF);
            uint immHi = (uimm >> 5) & 0x7F; // bits [11:5]
            uint immLo = uimm & 0x1F;        // bits [4:0]

            return (immHi << 25)
                 | ((uint)rs2 << 20)
                 | ((uint)rs1 << 15)
                 | (f3 << 12)
                 | (immLo << 7)
                 | op;
        }

        // ============================================================
        //  B-type: [ imm[12] | imm[10:5] | rs2 | rs1 | funct3
        //            | imm[4:1] | imm[11] | opcode ]
        //
        //  The immediate is a byte offset, always even (bit 0 = 0).
        //  Signed 13-bit field, range -4096..4094.
        // ============================================================
        private static uint PackB(InstructionDescriptor d, byte rs1, byte rs2, int imm) {
            uint op = (uint)d.Opcode;
            uint f3 = d.Funct3 ?? 0;

            if ((imm & 1) != 0) {
                throw new EncodeException($"branch offset {imm} must be a multiple of 2");
            }
            if (!FitsSigned(imm, 13)) {
                throw new EncodeException($"branch offset {imm} does not fit in 13 bits");
            }

            uint u = (uint)(imm & 0x1FFF);

            uint b12   = (u >> 12) & 1;
            uint b11   = (u >> 11) & 1;
            uint b10_5 = (u >> 5)  & 0x3F;
            uint b4_1  = (u >> 1)  & 0xF;

            return (b12   << 31)
                 | (b10_5 << 25)
                 | ((uint)rs2 << 20)
                 | ((uint)rs1 << 15)
                 | (f3 << 12)
                 | (b4_1  << 8)
                 | (b11   << 7)
                 | op;
        }

        // ============================================================
        //  U-type: [ imm[31:12] | rd | opcode ]
        //
        //  The immediate is the upper 20 bits. Callers may pass either
        //  the already-shifted form (0x12345000) or the bare 20-bit
        //  form (0x12345); if the low 12 bits are non-zero we shift
        //  left. This is friendlier than strict RISC-V and does not
        //  affect the encoded result.
        // ============================================================
        private static uint PackU(InstructionDescriptor d, byte rd, int imm) {
            uint op = (uint)d.Opcode;

            uint uimm = (uint)imm;
            if ((uimm & 0xFFF) != 0) {
                uimm <<= 12;
            }

            return (uimm & 0xFFFFF000)
                 | ((uint)rd << 7)
                 | op;
        }

        // ============================================================
        //  J-type: [ imm[20] | imm[10:1] | imm[11] | imm[19:12]
        //            | rd | opcode ]
        //
        //  The immediate is a byte offset, always even. Signed 21-bit
        //  field, range -1048576..1048574.
        // ============================================================
        private static uint PackJ(InstructionDescriptor d, byte rd, int imm) {
            uint op = (uint)d.Opcode;

            if ((imm & 1) != 0) {
                throw new EncodeException($"jump offset {imm} must be a multiple of 2");
            }
            if (!FitsSigned(imm, 21)) {
                throw new EncodeException($"jump offset {imm} does not fit in 21 bits");
            }

            uint u = (uint)(imm & 0x1FFFFF);

            uint b20    = (u >> 20) & 1;
            uint b19_12 = (u >> 12) & 0xFF;
            uint b11    = (u >> 11) & 1;
            uint b10_1  = (u >> 1)  & 0x3FF;

            return (b20    << 31)
                 | (b10_1  << 21)
                 | (b11    << 20)
                 | (b19_12 << 12)
                 | ((uint)rd << 7)
                 | op;
        }

        // ============================================================
        //  Check whether a value fits into a signed N-bit field.
        // ============================================================
        private static bool FitsSigned(int value, int bits) {
            int min = -(1 << (bits - 1));
            int max = (1 << (bits - 1)) - 1;
            return value >= min && value <= max;
        }
    }
}