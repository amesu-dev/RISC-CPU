using System;
using System.Collections.Generic;

namespace RISC_CPU {
    public enum FORMATS {
        R, // Register-to-Register
        I, // Immediate (арифметика с imm, load, jalr, ecall)
        S, // Store
        B, // Branch
        U, // Upper Immediate (lui, auipc)
        J  // Jump (jal)
    }

    public enum OPCODES : byte {
        LOAD = 0b000_0011, // lb, lh, lw, lbu, lhu
        MATH_I = 0b001_0011, // addi, slti, sltiu, xori, ori, andi, slli, srli, srai
        AUIPC = 0b001_0111, // auipc
        STORE = 0b010_0011, // sb, sh, sw
        MATH = 0b011_0011, // add, sub, sll, slt, sltu, xor, srl, sra, or, and
        LUI = 0b011_0111, // lui
        BRANCH = 0b110_0011, // beq, bne, blt, bge, bltu, bgeu
        JALR = 0b110_0111, // jalr
        JAL = 0b110_1111, // jal
        SYSTEM = 0b111_0011, // ecall, ebreak
    }

    // ============================================================
    //  Команды (мнемоники) — то, что видит программист
    // ============================================================
    public enum COMMANDS {
        // SYSTEM
        ECALL, EBREAK,

        // R-type
        ADD, SUB, SLL, SLT, SLTU, XOR, SRL, SRA, OR, AND,

        // I-type arithmetic
        ADDI, SLTI, SLTIU, XORI, ORI, ANDI, SLLI, SRLI, SRAI,

        // U-type
        LUI, AUIPC,

        // Load
        LB, LH, LW, LBU, LHU,

        // Store
        SB, SH, SW,

        // Branch
        BEQ, BNE, BLT, BGE, BLTU, BGEU,

        // Jump
        JAL, JALR
    }

    // ============================================================
    //  Дескриптор инструкции — метаданные ОДНОЙ команды.
    //  Здесь и только здесь живут кодировочные поля.
    // ============================================================
    public sealed record InstructionDescriptor(
        COMMANDS Command,
        string Mnemonic,
        FORMATS Format,
        OPCODES Opcode,
        byte? Funct3,
        byte? Funct7
    ) {
        // Для удобства декодера: ключ поиска по битам слова.
        // funct3 = null означает "не проверять" (U, J).
        // funct7 = null означает "не проверять" (I, S, B, U, J; а также R с одним вариантом по funct7).
        public (OPCODES, byte?, byte?) EncodingKey => (Opcode, Funct3, Funct7);
    }

    // ============================================================
    //  Декодированная инструкция — конкретный экземпляр в программе.
    //  Не содержит кодировочных полей (funct3/funct7/opcode) —
    //  они однозначно определяются через Command.
    // ============================================================
    public readonly record struct Instruction(
        COMMANDS Command,
        FORMATS Format,
        uint Raw,           // сырое 32-битное слово (для трассировки/дампа)
        uint Pc,            // адрес, откуда выбрана
        byte? Rd,           // регистр-приёмник (null, если нет)
        byte? Rs1,          // первый источник (null, если нет)
        byte? Rs2,          // второй источник (null, если нет)
        int Imm             // цельный immediate (уже разобран по формату)
    ) {
        public bool WritesRegister => Rd is byte rd && rd != 0;
        public bool ReadsRs1 => Rs1.HasValue;
        public bool ReadsRs2 => Rs2.HasValue;

        public bool IsLoad => Command is COMMANDS.LB or COMMANDS.LH or COMMANDS.LW
                                          or COMMANDS.LBU or COMMANDS.LHU;
        public bool IsStore => Command is COMMANDS.SB or COMMANDS.SH or COMMANDS.SW;
        public bool IsBranch => Format == FORMATS.B;
        public bool IsJump => Command is COMMANDS.JAL or COMMANDS.JALR;
        public bool IsSystem => Command is COMMANDS.ECALL or COMMANDS.EBREAK;
    }

    // ============================================================
    //  Реестр инструкций.
    //  - Descriptors: Command -> InstructionDescriptor
    //  - ByEncoding:  (opcode, funct3, funct7) -> Command  (для декодера, O(1))
    //  - ByMnemonic:  "add" -> Command                     (для ассемблера, O(1))
    // ============================================================
    public static class InstructionSet {
        private static readonly Dictionary<COMMANDS, InstructionDescriptor> _descriptors;
        private static readonly Dictionary<(OPCODES, byte?, byte?), COMMANDS> _byEncoding;
        private static readonly Dictionary<string, COMMANDS> _byMnemonic;

        public static IReadOnlyDictionary<COMMANDS, InstructionDescriptor> Descriptors => _descriptors;
        public static IReadOnlyDictionary<(OPCODES, byte?, byte?), COMMANDS> ByEncoding => _byEncoding;
        public static IReadOnlyDictionary<string, COMMANDS> ByMnemonic => _byMnemonic;

        static InstructionSet() {
            var all = new InstructionDescriptor[] {
                // ---------- SYSTEM ----------
                new(COMMANDS.ECALL,  "ecall",  FORMATS.I, OPCODES.SYSTEM, 0b000, null),
                new(COMMANDS.EBREAK, "ebreak", FORMATS.I, OPCODES.SYSTEM, 0b000, null),

                // ---------- U-type ----------
                new(COMMANDS.LUI,   "lui",   FORMATS.U, OPCODES.LUI,   null, null),
                new(COMMANDS.AUIPC, "auipc", FORMATS.U, OPCODES.AUIPC, null, null),

                // ---------- Jumps ----------
                new(COMMANDS.JAL,  "jal",  FORMATS.J, OPCODES.JAL,  null,  null),
                new(COMMANDS.JALR, "jalr", FORMATS.I, OPCODES.JALR, 0b000, null),

                // ---------- BRANCH ----------
                new(COMMANDS.BEQ,  "beq",  FORMATS.B, OPCODES.BRANCH, 0b000, null),
                new(COMMANDS.BNE,  "bne",  FORMATS.B, OPCODES.BRANCH, 0b001, null),
                new(COMMANDS.BLT,  "blt",  FORMATS.B, OPCODES.BRANCH, 0b100, null),
                new(COMMANDS.BGE,  "bge",  FORMATS.B, OPCODES.BRANCH, 0b101, null),
                new(COMMANDS.BLTU, "bltu", FORMATS.B, OPCODES.BRANCH, 0b110, null),
                new(COMMANDS.BGEU, "bgeu", FORMATS.B, OPCODES.BRANCH, 0b111, null),

                // ---------- LOAD (I-формат) ----------
                new(COMMANDS.LB,  "lb",  FORMATS.I, OPCODES.LOAD, 0b000, null),
                new(COMMANDS.LH,  "lh",  FORMATS.I, OPCODES.LOAD, 0b001, null),
                new(COMMANDS.LW,  "lw",  FORMATS.I, OPCODES.LOAD, 0b010, null),
                new(COMMANDS.LBU, "lbu", FORMATS.I, OPCODES.LOAD, 0b100, null),
                new(COMMANDS.LHU, "lhu", FORMATS.I, OPCODES.LOAD, 0b101, null),

                // ---------- STORE (S-формат) ----------
                new(COMMANDS.SB, "sb", FORMATS.S, OPCODES.STORE, 0b000, null),
                new(COMMANDS.SH, "sh", FORMATS.S, OPCODES.STORE, 0b001, null),
                new(COMMANDS.SW, "sw", FORMATS.S, OPCODES.STORE, 0b010, null),

                // ---------- R-type ----------
                new(COMMANDS.ADD,  "add",  FORMATS.R, OPCODES.MATH, 0b000, 0b0000000),
                new(COMMANDS.SUB,  "sub",  FORMATS.R, OPCODES.MATH, 0b000, 0b0100000),
                new(COMMANDS.SLL,  "sll",  FORMATS.R, OPCODES.MATH, 0b001, 0b0000000),
                new(COMMANDS.SLT,  "slt",  FORMATS.R, OPCODES.MATH, 0b010, 0b0000000),
                new(COMMANDS.SLTU, "sltu", FORMATS.R, OPCODES.MATH, 0b011, 0b0000000),
                new(COMMANDS.XOR,  "xor",  FORMATS.R, OPCODES.MATH, 0b100, 0b0000000),
                new(COMMANDS.SRL,  "srl",  FORMATS.R, OPCODES.MATH, 0b101, 0b0000000),
                new(COMMANDS.SRA,  "sra",  FORMATS.R, OPCODES.MATH, 0b101, 0b0100000),
                new(COMMANDS.OR,   "or",   FORMATS.R, OPCODES.MATH, 0b110, 0b0000000),
                new(COMMANDS.AND,  "and",  FORMATS.R, OPCODES.MATH, 0b111, 0b0000000),

                // ---------- I-type arithmetic ----------
                new(COMMANDS.ADDI,  "addi",  FORMATS.I, OPCODES.MATH_I, 0b000, null),
                new(COMMANDS.SLTI,  "slti",  FORMATS.I, OPCODES.MATH_I, 0b010, null),
                new(COMMANDS.SLTIU, "sltiu", FORMATS.I, OPCODES.MATH_I, 0b011, null),
                new(COMMANDS.XORI,  "xori",  FORMATS.I, OPCODES.MATH_I, 0b100, null),
                new(COMMANDS.ORI,   "ori",   FORMATS.I, OPCODES.MATH_I, 0b110, null),
                new(COMMANDS.ANDI,  "andi",  FORMATS.I, OPCODES.MATH_I, 0b111, null),
                new(COMMANDS.SLLI,  "slli",  FORMATS.I, OPCODES.MATH_I, 0b001, 0b0000000),
                new(COMMANDS.SRLI,  "srli",  FORMATS.I, OPCODES.MATH_I, 0b101, 0b0000000),
                new(COMMANDS.SRAI,  "srai",  FORMATS.I, OPCODES.MATH_I, 0b101, 0b0100000),
            };

            _descriptors = new Dictionary<COMMANDS, InstructionDescriptor>(all.Length);
            _byMnemonic = new Dictionary<string, COMMANDS>(all.Length, StringComparer.OrdinalIgnoreCase);
            _byEncoding = new Dictionary<(OPCODES, byte?, byte?), COMMANDS>(all.Length);

            foreach (var d in all) {
                _descriptors[d.Command] = d;
                _byMnemonic[d.Mnemonic] = d.Command;

                // ВАЖНО: ecall и ebreak имеют одинаковый EncodingKey (SYSTEM, 0, null).
                // Различить их можно только по imm (funct12). Поэтому кладём только ecall
                // как "канонический" вариант; ebreak декодер распознаёт отдельно после
                // извлечения imm. Здесь просто не перезаписываем уже существующий ключ.
                var key = d.EncodingKey;
                if (!_byEncoding.ContainsKey(key))
                    _byEncoding[key] = d.Command;
            }
        }

        // Удобные методы-фасады
        public static InstructionDescriptor GetDescriptor(COMMANDS cmd) => _descriptors[cmd];
        public static bool TryGetCommand(string mnemonic, out COMMANDS cmd) => _byMnemonic.TryGetValue(mnemonic, out cmd);
        public static bool TryGetCommand(OPCODES op, byte? f3, byte? f7, out COMMANDS cmd)
            => _byEncoding.TryGetValue((op, f3, f7), out cmd);
    }
}