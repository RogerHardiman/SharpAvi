﻿// Thanks to NAudio.Lame project (by Corey Murtagh) for inspiration
// https://github.com/Corey-M/NAudio.Lame

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SharpAvi;
using SharpAvi.Codecs;

namespace SharpAvi.Codecs
{
    /// <summary>
    /// Mpeg Layer 3 (MP3) audio encoder using the LAME codec in external DLL.
    /// </summary>
    /// <remarks>
    /// The class is designed for using only a single instance at a time.
    /// Find information about and downloads of the LAME project at http://lame.sourceforge.net/
    /// </remarks>
    public partial class LameMp3AudioEncoder : IAudioEncoder, IDisposable
    {
        /// <summary>
        /// Supported output bit rates (in kilobits per second).
        /// </summary>
        /// <remarks>
        /// Currently supported are 64, 96, 128, 192 and 320 kbps.
        /// </remarks>
        public static readonly int[] SupportedBitRates = new[] { 64, 96, 128, 160, 192, 320 };


        #region Loading LAME DLL

        private static Type lameFacadeType;

        /// <summary>
        /// Sets the location of LAME DLL for using by this class.
        /// </summary>
        /// <remarks>
        /// This method may be called before creating any instances of this class.
        /// The LAME DLL should have the appropriate bitness (32/64), depending on the current process.
        /// If it is not already loaded into the process, the method loads it automatically.
        /// </remarks>
        public static void SetLameDllLocation(string lameDllPath)
        {
            Contract.Requires(!string.IsNullOrEmpty(lameDllPath));

            var libraryName = Path.GetFileName(lameDllPath);
            if (!IsLibraryLoaded(libraryName))
            {
                var loadResult = LoadLibrary(lameDllPath);
                if (loadResult == IntPtr.Zero)
                {
                    throw new DllNotFoundException(string.Format("Library '{0}' could not be loaded.", lameDllPath));
                }
            }

            var facadeAsm = GenerateLameFacadeAssembly(libraryName);
            lameFacadeType = facadeAsm.GetType(typeof(LameMp3AudioEncoder).Namespace + ".Runtime.LameFacadeImpl");
        }

        private static Assembly GenerateLameFacadeAssembly(string lameDllName)
        {
            var thisAsm = typeof(LameMp3AudioEncoder).Assembly;
            var compiler = new Microsoft.CSharp.CSharpCodeProvider();
            var compilerOptions = new System.CodeDom.Compiler.CompilerParameters()
            {
                 GenerateInMemory = true,
                 GenerateExecutable = false,
                 IncludeDebugInformation = false,
                 CompilerOptions = "/optimize",
                 ReferencedAssemblies = {"mscorlib.dll", thisAsm.Location}
            };
            var source = GetLameFacadeAssemblySource(lameDllName, thisAsm);
            var compilerResult = compiler.CompileAssemblyFromSource(compilerOptions, source);
            if (compilerResult.Errors.HasErrors)
            {
                throw new Exception("Could not generate LAME facade assembly.");
            }
            return compilerResult.CompiledAssembly;
        }

        private static string GetLameFacadeAssemblySource(string lameDllName, Assembly resourceAsm)
        {
            string source;
            using (var sourceStream = resourceAsm.GetManifestResourceStream("SharpAvi.Codecs.LameFacadeImpl.cs"))
            using (var sourceReader = new StreamReader(sourceStream))
            {
                source = sourceReader.ReadToEnd();
                sourceReader.Close();
            }

            var lameDllNameLiteral = string.Format("\"{0}\"", lameDllName);
            source = source.Replace("\"lame_enc.dll\"", lameDllNameLiteral);

            return source;
        }

        private static bool IsLibraryLoaded(string libraryName)
        {
            var process = Process.GetCurrentProcess();
            return process.Modules.Cast<ProcessModule>().
                Any(m => string.Compare(m.ModuleName, libraryName, StringComparison.InvariantCultureIgnoreCase) == 0);
        }

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string fileName);

        #endregion


        private const int SAMPLE_BYTE_SIZE = 2;

        private readonly ILameFacade lame;
        private readonly byte[] finalBuffer = new byte[7200];

        /// <summary>
        /// Creates a new instance of <see cref="LameMp3AudioEncoder"/>.
        /// </summary>
        /// <param name="channelCount">Channel count.</param>
        /// <param name="sampleRate">Sample rate (in samples per second).</param>
        /// <param name="outputBitRateKbps">Output bit rate (in kilobits per second).</param>
        /// <remarks>
        /// Encoder expects audio data in 16-bit samples.
        /// Stereo data should be interleaved: left sample first, right sample second.
        /// </remarks>
        public LameMp3AudioEncoder(int channelCount, int sampleRate, int outputBitRateKbps)
        {
            Contract.Requires(channelCount == 1 || channelCount == 2);
            Contract.Requires(sampleRate > 0);
            Contract.Requires(SupportedBitRates.Contains(outputBitRateKbps));

            if (lameFacadeType == null)
            {
                throw new InvalidOperationException("LAME DLL is not loaded. Call SetLameDllLocation first.");
            }

            lame = (ILameFacade)Activator.CreateInstance(lameFacadeType);
            lame.ChannelCount = channelCount;
            lame.InputSampleRate = sampleRate;
            lame.OutputBitRate = outputBitRateKbps;

            lame.PrepareEncoding();
        }

        public int EncodeBlock(byte[] source, int sourceOffset, int sourceCount, byte[] destination, int destinationOffset)
        {
            return lame.Encode(source, sourceOffset, sourceCount / SAMPLE_BYTE_SIZE, destination, destinationOffset);
        }

        public int GetMaxEncodedLength(int sourceCount)
        {
            // Estimate taken from the description of 'lame_encode_buffer' method in 'lame.h'
            var numberOfSamples = sourceCount / SAMPLE_BYTE_SIZE;
            return (int)Math.Ceiling(1.25 * numberOfSamples + 7200);
        }

        public void InitializeStream(SharpAvi.Output.IAviAudioStream stream)
        {
            stream.Format = AudioFormats.Mp3;
            stream.ChannelCount = lame.ChannelCount;
            stream.SamplesPerSecond = lame.OutputSampleRate;
            stream.BitsPerSample = 16;
            stream.BytesPerSecond = lame.OutputBitRate * 1000 / 8;
            stream.Granularity = 1;

            // See MPEGLAYER3WAVEFORMAT structure
            var mp3Data = new MemoryStream(4 * sizeof(ushort) + sizeof(uint));
            using (var writer = new BinaryWriter(mp3Data))
            {
                writer.Write((ushort)1); // MPEGLAYER3_ID_MPEG
                writer.Write(0x00000002U); // MPEGLAYER3_FLAG_PADDING_OFF
                writer.Write((ushort)lame.FrameSize); // nBlockSize
                writer.Write((ushort)1); // nFramesPerBlock
                writer.Write((ushort)lame.EncoderDelay);
            }
            stream.FormatSpecificData = mp3Data.ToArray();
        }

        /// <summary>
        /// Writes final data to the stream.
        /// </summary>
        /// <remarks>
        /// Should be called before <see cref="AviWriter.Close"/>.
        /// </remarks>
        public void FinalizeStream(SharpAvi.Output.IAviAudioStream stream)
        {
            Contract.Requires(stream != null);

            var length = lame.FinishEncoding(finalBuffer, 0);
            stream.WriteBlock(finalBuffer, 0, length);
        }

        public void Dispose()
        {
            var lameDisposable = lame as IDisposable;
            if (lameDisposable != null)
            {
                lameDisposable.Dispose();
            }
        }
    }
}
