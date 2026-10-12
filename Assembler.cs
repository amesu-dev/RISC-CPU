using System;
using System.Text.RegularExpressions;



namespace RISC_CPU
{
    unsafe struct CodeSegment {
        public CodeSegment() { }
        Dictionary<string, int> Labels = new(); // name -> address
    };

    struct DataSegment { };

    public unsafe class Assembler {
        static readonly Dictionary<string, Regex> patterns = new() {
            { "label", new Regex(@"^\(\w+\):") },
            { "command", new Regex(@"^\s*(\w+)\s*(.*)") },

            // Only comments that start at the beginning of the line are considered valid
            { "comment", new Regex(@"^\s*#.*") }, 
            { "data", new Regex(@"^\s*\.data\s*$") },
            { "code", new Regex(@"^\s*\.code\s*$") }
        };
        public unsafe static byte[] Assemble(string assembly) {
            byte[] output_buffer = new byte[64 * 1024]; // 64KB output buffer for machine code

            uint segment = 0; // 1 = Code, 2 = Data; 0 - None
            uint current_address = 0; // Current address in the output buffer
            string[] lines = assembly.Split('\n').AsParallel()
                .Select(line => {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) return "";

                    // Check string validity + handle lines
                    string pattern = "";
                    foreach (string key in patterns.Keys) {
                        if (patterns[key].IsMatch(line)) pattern = key;
                    }

                    switch (pattern) {
                        case "label":
                            // Handle label
                            break;
                        case "data":
                            segment = 2;
                            return "";
                        case "code":
                            segment = 1;
                            return "";
                        case "comment":
                            return "";
                        case "command":
                            if (segment != 1) throw new Exception("Commands can only be in the code segment");
                            return line;
                        default:
                            throw new Exception("Invalid line");
                    }

                    return "";
                }).Where(line => !string.IsNullOrEmpty(line)).ToArray();


            for (int i = 0; i < lines.Length; i++) {
                
            }

            return output_buffer;
        }
    }
}