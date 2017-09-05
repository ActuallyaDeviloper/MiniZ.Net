using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Test
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var outStream = new FileStream("T:\\domas_breaking_the_x86_isa_wp.compressed", FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var inStream = new FileStream("T:\\domas_breaking_the_x86_isa_wp.pdf", FileMode.Open, FileAccess.Read, FileShare.Read))
                    MiniZ.Functions.Compress(inStream, outStream, 6);
            
            using (var outStream = new FileStream("T:\\domas_breaking_the_x86_isa_wp.compressed.pdf", FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var inStream = new FileStream("T:\\domas_breaking_the_x86_isa_wp.compressed", FileMode.Open, FileAccess.Read, FileShare.Read))
                    MiniZ.Functions.Decompress(inStream, outStream);
        }
    }
}
