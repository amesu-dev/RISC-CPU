using System;
using System.Collections.Generic;

namespace RISC_CPU.Assembler
{
    // ============================================================
    //  Assembler exception — carries the source line number
    // ============================================================
    public sealed class AsmException : Exception {
        public int LineNumber { get; }
        public string SourceLine { get; }

        public AsmException(int lineNumber, string message, string sourceLine = "")
            : base($"Line {lineNumber}: {message}")
        {
            LineNumber = lineNumber;
            SourceLine = sourceLine;
        }
    }

    // ============================================================
    //  Symbol kind: code or data
    // ============================================================
    public enum SymbolKind {
        Code, Data
    }

    // ============================================================
    //  Symbol: a label with its address (offset relative to its section)
    // ============================================================
    public sealed record Symbol(
        string Name,
        SymbolKind Kind,
        int Offset,       // relative to the start of the section (TextBase / DataBase)
        int Size,         // 0 for Code, size in bytes for Data
        int LineNumber
    );

    // ============================================================
    //  Symbol table — with duplicate detection
    // ============================================================
    public sealed class SymbolTable {
        private readonly Dictionary<string, Symbol> _byName = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, Symbol> All => _byName;
        public int Count => _byName.Count;

        public void Add(Symbol symbol) {
            if (_byName.ContainsKey(symbol.Name))
                throw new AsmException(symbol.LineNumber, $"label '{symbol.Name}' is already defined");

            _byName[symbol.Name] = symbol;
        }

        public bool TryGet(string name, out Symbol symbol)
            => _byName.TryGetValue(name, out symbol!);

        public Symbol Get(string name, int lineNumber) {
            if (!_byName.TryGetValue(name, out var s))
                throw new AsmException(lineNumber, $"unknown label '{name}'");
            return s;
        }

        public bool Contains(string name) => _byName.ContainsKey(name);
    }

    // ============================================================
    //  Result of assembling a program
    // ============================================================
    public sealed class AssembledProgram {
        public required uint[] Code { get; init; }
        public required byte[] Data { get; init; }
        public required int EntryPoint { get; init; }
        public required SymbolTable Symbols { get; init; }

        public int TextBase { get; init; }
        public int DataBase { get; init; }

        // Convenience for the loader: a single flat memory image
        public byte[] ToMemoryImage() {
            var image = new byte[Code.Length * 4 + Data.Length];
            Buffer.BlockCopy(Code, 0, image, TextBase, Code.Length * 4);
            Buffer.BlockCopy(Data, 0, image, DataBase, Data.Length);
            return image;
        }
    }
}