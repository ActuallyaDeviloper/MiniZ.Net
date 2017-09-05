using System;
using System.IO;
using System.Runtime.InteropServices;

// Based on official example program 5:
// https://github.com/richgel999/miniz/blob/master/examples/example5.c



namespace MiniZ
{
    public abstract class MiniZException : Exception
    {
        public string ComponentName { get; }
        public int Status { get; }

        public MiniZException(string componentName, int status)
        {
            ComponentName = componentName;
            Status = status;
        }

    }
    public class MiniZCompressException : MiniZException
    {
        public MiniZCompressException(string componentName, int status)
            : base(componentName, status)
        { }

        public override string Message
        {
            get
            {
                return String.Format("MiniZ compression routine {0} failed with error code {1}.", ComponentName, Status);
            }
        }
    }


    public class MiniZDecompressException : MiniZException
    {
        public MiniZDecompressException(string componentName, int status)
            : base(componentName, status)
        { }

        public override string Message
        {
            get
            {
                return String.Format("MiniZ decompression routine {0} failed with error code {1}.", ComponentName, Status);
            }
        }
    }

    public unsafe static class Functions
    {
        public static void Compress(Stream inputStream, Stream outputStream, int compressionLevel)
        {
            if (compressionLevel < 0)
                compressionLevel = 0;
            if (compressionLevel > 10)
                compressionLevel = 10;

            // create tdefl() compatible flags (we have to compose the low-level flags ourselves, or use tdefl_create_comp_flags_from_zip_params() but that means MINIZ_NO_ZLIB_APIS can't be defined).
            uint comp_flags = TDEFL_WRITE_ZLIB_HEADER | s_tdefl_num_probes[compressionLevel] | ((compressionLevel <= 3) ? TDEFL_GREEDY_PARSING_FLAG : 0);
            if (compressionLevel <= 0)
                comp_flags |= TDEFL_FORCE_ALL_RAW_BLOCKS;

            // Initialize the low-level compressor.
            void* g_deflator = stackalloc byte[sizeof_tdefl_compressor()];
            {
                int status = tdefl_init(g_deflator, null, null, comp_flags);
                if (status != 0)
                    throw new MiniZCompressException("tdefl_init", status);
            }

            // Initialize buffers
            byte[] s_inbufGC = new byte[IN_BUF_SIZE];
            byte[] s_outbufGC = new byte[OUT_BUF_SIZE];

            fixed (byte* s_inbuf = s_inbufGC)
            fixed (byte* s_outbuf = s_outbufGC)
            {

                // Compression
                int avail_in = 0;
                int avail_out = COMP_OUT_BUF_SIZE;
                int total_in = 0;
                int total_out = 0;
                byte* next_in = s_inbuf; // const ptr
                byte* next_out = s_outbuf;
                bool flush = false;
                do
                {
                    if ((avail_in == 0) && !flush)
                    {
                        next_in = s_inbuf;
                        avail_in = inputStream.Read(s_inbufGC, 0, IN_BUF_SIZE);

                        flush = avail_in < IN_BUF_SIZE; //Detect EOF
                    }

                    IntPtr in_bytes = new IntPtr(avail_in);
                    IntPtr out_bytes = new IntPtr(avail_out);

                    // Compress as much of the input as possible (or all of it) to the output buffer.
                    int status = tdefl_compress(g_deflator, next_in, ref in_bytes, next_out, ref out_bytes, flush ? TDEFL_FINISH : TDEFL_NO_FLUSH);

                    int in_bytes32 = in_bytes.ToInt32();
                    int out_bytes32 = out_bytes.ToInt32();

                    next_in += in_bytes32;
                    avail_in -= in_bytes32;
                    total_in += in_bytes32;

                    next_out += out_bytes32;
                    avail_out -= out_bytes32;
                    total_out += out_bytes32;

                    if ((status != TDEFL_STATUS_OKAY) || (avail_out <= 0))
                    {
                        // Output buffer is full, or compression is done or failed, so write buffer to output file.
                        outputStream.Write(s_outbufGC, 0, COMP_OUT_BUF_SIZE - avail_out);

                        next_out = s_outbuf;
                        avail_out = COMP_OUT_BUF_SIZE;
                    }

                    if (status == TDEFL_STATUS_DONE)
                        break;
                    else if (status != TDEFL_STATUS_OKAY)
                        throw new MiniZCompressException("tdefl_compress", status);
                } while (true);
            }
        }

        public static void Decompress(Stream inputStream, Stream outputStream)
        {
            // Initialize decompressor
            void* inflator = stackalloc byte[sizeof_tinfl_decompressor()];
            *((ulong*)(inflator)) = 0; //tinfl_init(inflator);

            // Initialize buffers
            byte[] s_inbufGC = new byte[IN_BUF_SIZE];
            byte[] s_outbufGC = new byte[OUT_BUF_SIZE];

            fixed (byte* s_inbuf = s_inbufGC)
            fixed (byte* s_outbuf = s_outbufGC)
            {

                // Decompress
                int avail_in = 0;
                int avail_out = COMP_OUT_BUF_SIZE;
                int total_in = 0;
                int total_out = 0;
                byte* next_in = s_inbuf; // const ptr
                byte* next_out = s_outbuf;
                bool flush = false;
                do
                {
                    if ((avail_in == 0) && !flush)
                    {
                        // Input buffer is empty, so read more bytes from input file.
                        next_in = s_inbuf;
                        avail_in = inputStream.Read(s_inbufGC, 0, IN_BUF_SIZE);

                        flush = avail_in < IN_BUF_SIZE;
                    }
                    
                    IntPtr in_bytes = new IntPtr(avail_in);
                    IntPtr out_bytes = new IntPtr(avail_out);

                    int status = tinfl_decompress(inflator, next_in, ref in_bytes, s_outbuf, next_out, ref out_bytes, (flush ? 0 : TINFL_FLAG_HAS_MORE_INPUT) | TINFL_FLAG_PARSE_ZLIB_HEADER | TINFL_FLAG_COMPUTE_ADLER32);

                    int in_bytes32 = in_bytes.ToInt32();
                    int out_bytes32 = out_bytes.ToInt32();

                    avail_in -= in_bytes32;
                    next_in += in_bytes32;
                    total_in += in_bytes32;

                    avail_out -= out_bytes32;
                    next_out += out_bytes32;
                    total_out += out_bytes32;

                    if ((status <= TINFL_STATUS_DONE) || (avail_out <= 0))
                    {
                        // Output buffer is full, or decompression is done, so write buffer to output file.
                        outputStream.Write(s_outbufGC, 0, OUT_BUF_SIZE - avail_out);

                        next_out = s_outbuf;
                        avail_out = OUT_BUF_SIZE;
                    }

                    // If status is <= TINFL_STATUS_DONE then either decompression is done or something went wrong.
                    if (status <= TINFL_STATUS_DONE)
                        if (status == TINFL_STATUS_DONE)
                            break;
                        else
                            throw new MiniZDecompressException("tinfl_decompress", status);
                } while (true);
            }
        }
        
        // The number of dictionary probes to use at each compression level(0-10). 0=implies fastest/minimal possible probing.
        private static uint[] s_tdefl_num_probes = new uint[11] { 0, 1, 6, 32, 16, 32, 128, 256, 512, 768, 1500 };

        // IN_BUF_SIZE is the size of the file read buffer.
        // IN_BUF_SIZE must be >= 1
        private const int IN_BUF_SIZE = 1024 * 512;

        // COMP_OUT_BUF_SIZE is the size of the output buffer used during compression.
        // COMP_OUT_BUF_SIZE must be >= 1 and <= OutBugSize
        private const int COMP_OUT_BUF_SIZE = 1024 * 512;

        // OUT_BUF_SIZE is the size of the output buffer used during decompression.
        // OUT_BUF_SIZE must be a power of 2 >= TINFL_LZ_DICT_SIZE (because the low-level decompressor not only writes, but reads from the output buffer as it decompresses)
        //#define OutBugSize (TINFL_LZ_DICT_SIZE)
        private const int OUT_BUF_SIZE = 1024 * 512;

        private const uint TDEFL_HUFFMAN_ONLY = 0;
        private const uint TDEFL_DEFAULT_MAX_PROBES = 128;
        private const uint TDEFL_MAX_PROBES_MASK = 0xFFF;
        private const uint TDEFL_WRITE_ZLIB_HEADER = 0x01000;
        private const uint TDEFL_COMPUTE_ADLER32 = 0x02000;
        private const uint TDEFL_GREEDY_PARSING_FLAG = 0x04000;
        private const uint TDEFL_NONDETERMINISTIC_PARSING_FLAG = 0x08000;
        private const uint TDEFL_RLE_MATCHES = 0x10000;
        private const uint TDEFL_FILTER_MATCHES = 0x20000;
        private const uint TDEFL_FORCE_ALL_STATIC_BLOCKS = 0x40000;
        private const uint TDEFL_FORCE_ALL_RAW_BLOCKS = 0x80000;

        private const int TDEFL_STATUS_BAD_PARAM = -2;
        private const int TDEFL_STATUS_PUT_BUF_FAILED = -1;
        private const int TDEFL_STATUS_OKAY = 0;
        private const int TDEFL_STATUS_DONE = 1;

        private const int TDEFL_NO_FLUSH = 0;
        private const int TDEFL_SYNC_FLUSH = 2;
        private const int TDEFL_FULL_FLUSH = 3;
        private const int TDEFL_FINISH = 4;

        private const uint TINFL_FLAG_PARSE_ZLIB_HEADER = 1;
        private const uint TINFL_FLAG_HAS_MORE_INPUT = 2;
        private const uint TINFL_FLAG_USING_NON_WRAPPING_OUTPUT_BUF = 4;
        private const uint TINFL_FLAG_COMPUTE_ADLER32 = 8;

        private const int TINFL_STATUS_FAILED_CANNOT_MAKE_PROGRESS = -4;
        private const int TINFL_STATUS_BAD_PARAM = -3;
        private const int TINFL_STATUS_ADLER32_MISMATCH = -2;
        private const int TINFL_STATUS_FAILED = -1;
        private const int TINFL_STATUS_DONE = 0;
        private const int TINFL_STATUS_NEEDS_MORE_INPUT = 1;
        private const int TINFL_STATUS_HAS_MORE_OUTPUT = 2;

        private const string Dll64 = "MiniZ64.dll";
        private const string Dll32 = "MiniZ32.dll";

        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tdefl_compressor_size")]
        private static extern int tdefl_compressor_size64();
        [DllImport(Dll32, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tdefl_compressor_size")]
        private static extern int tdefl_compressor_size32();
        
        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tinfl_decompressor_size")]
        private static extern int tinfl_decompressor_size64();
        [DllImport(Dll32, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tinfl_decompressor_size")]
        private static extern int tinfl_decompressor_size32();

        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tdefl_init")]
        private static extern int tdefl_init64(void* d, void* pPut_buf_func, void* pPut_buf_user, uint flags);
        [DllImport(Dll32, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tdefl_init")]
        private static extern int tdefl_init32(void* d, void* pPut_buf_func, void* pPut_buf_user, uint flags);
        
        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tdefl_compress")]
        private static extern int tdefl_compress64(void* d, /*const*/ void* pIn_buf, ref IntPtr pIn_buf_size, void* pOut_buf, ref IntPtr pOut_buf_size, int flush);
        [DllImport(Dll32, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tdefl_compress")]
        private static extern int tdefl_compress32(void* d, /*const*/ void* pIn_buf, ref IntPtr pIn_buf_size, void* pOut_buf, ref IntPtr pOut_buf_size, int flush);
        
        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tinfl_decompress")]
        private static extern int tinfl_decompress64(void* r, /*const*/ void* pIn_buf_next, ref IntPtr pIn_buf_size, void* pOut_buf_start, void* pOut_buf_next, ref IntPtr pOut_buf_size, uint decomp_flags);
        [DllImport(Dll32, CallingConvention = CallingConvention.Cdecl, SetLastError = false, EntryPoint = "wrapper_tinfl_decompress")]
        private static extern int tinfl_decompress32(void* r, /*const*/ void* pIn_buf_next, ref IntPtr pIn_buf_size, void* pOut_buf_start, void* pOut_buf_next, ref IntPtr pOut_buf_size, uint decomp_flags);

        private static int sizeof_tdefl_compressor()
        {
            switch (IntPtr.Size)
            {
                case 8:
                    return tdefl_compressor_size64();
                case 4:
                    return tdefl_compressor_size32();
                default:
                    throw new PlatformNotSupportedException("\"IntPtr.Size\" neither 4 nor 8.");
            }
        }

        private static int sizeof_tinfl_decompressor()
        {
            switch (IntPtr.Size)
            {
                case 8:
                    return tinfl_decompressor_size64();
                case 4:
                    return tinfl_decompressor_size32();
                default:
                    throw new PlatformNotSupportedException("\"IntPtr.Size\" neither 4 nor 8.");
            }
        }

        private static int tdefl_init(void* d, void* pPut_buf_func, void* pPut_buf_user, uint flags)
        {
            switch (IntPtr.Size)
            {
                case 8:
                    return tdefl_init64(d, pPut_buf_func, pPut_buf_user, flags);
                case 4:
                    return tdefl_init32(d, pPut_buf_func, pPut_buf_user, flags);
                default:
                    throw new PlatformNotSupportedException("\"IntPtr.Size\" neither 4 nor 8.");
            }
        }

        private static int tdefl_compress(void* d, /*const*/ void* pIn_buf, ref IntPtr pIn_buf_size, void* pOut_buf, ref IntPtr pOut_buf_size, int flush)
        {
            switch (IntPtr.Size)
            {
                case 8:
                    return tdefl_compress64(d, pIn_buf, ref pIn_buf_size, pOut_buf, ref pOut_buf_size, flush);
                case 4:
                    return tdefl_compress32(d, pIn_buf, ref pIn_buf_size, pOut_buf, ref pOut_buf_size, flush);
                default:
                    throw new PlatformNotSupportedException("\"IntPtr.Size\" neither 4 nor 8.");
            }
        }

        private static int tinfl_decompress(void* r, /*const*/ void* pIn_buf_next, ref IntPtr pIn_buf_size, void* pOut_buf_start, void* pOut_buf_next, ref IntPtr pOut_buf_size, uint decomp_flags)
        {
            switch (IntPtr.Size)
            {
                case 8:
                    return tinfl_decompress64(r, pIn_buf_next, ref pIn_buf_size, pOut_buf_start, pOut_buf_next, ref pOut_buf_size, decomp_flags);
                case 4:
                    return tinfl_decompress32(r, pIn_buf_next, ref pIn_buf_size, pOut_buf_start, pOut_buf_next, ref pOut_buf_size, decomp_flags);
                default:
                    throw new PlatformNotSupportedException("\"IntPtr.Size\" neither 4 nor 8.");
            }
        }
    }
}