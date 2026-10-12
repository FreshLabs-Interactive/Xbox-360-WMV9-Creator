using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace XboxWMVCreator
{
    /// <summary>
    /// Encodes video to classic WMV9 (WMV3 video + WMA v2 audio in an ASF container), the format the
    /// Xbox 360 expects, at the frame size and frame rate of the chosen <see cref="OutputPreset"/>.
    /// This is NOT the VC-1 "Advanced Profile" (WVC1) / WMA Pro that Windows picks by default, so the
    /// profile is forced and the output is verified afterwards.
    ///
    /// FFmpeg cannot do the encode: it only has WMV7 (wmv1) and WMV8 (wmv2) encoders, and "wmv3" is
    /// decode-only, so "-c:v wmv3" fails with "Unknown encoder 'wmv3'" and no file is ever written.
    /// Windows ships a WMV9 encoder in Media Foundation, so that does the encode.
    ///
    /// Windows' own H.264 decoder is far pickier than FFmpeg's, though. For example it does not support
    /// lossless / High 4:4:4 Predictive H.264, and when it can't decode it does NOT report an error: it
    /// just produces a WMV with an audio track and no video frames. So FFmpeg always decodes the source
    /// first into a plain, widely-supported H.264/AAC MP4 at exactly the chosen size and frame rate,
    /// Media Foundation encodes that, and the result is checked against what was asked for.
    /// </summary>
    internal static class Wmv9Converter
    {
        // WMA v2 audio bitrates to try, best first. The encoder only offers certain combinations, so
        // if the first isn't accepted we fall back to the next.
        private static readonly uint[] AudioBitrates = { 192_000, 128_000 };

        // How far the measured frame rate may differ from the preset's before the file is rejected.
        private const double FrameRateTolerance = 0.15;

        /// <param name="status">Optional, receives short status text such as "encoding 42%".</param>
        public static async Task ConvertAsync(
            string inputFile,
            string outputFile,
            string ffmpegPath,
            string workFolder,
            OutputPreset preset,
            Action<string>? status = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
            Directory.CreateDirectory(workFolder);

            string intermediate = Path.Combine(workFolder, Guid.NewGuid().ToString("N") + ".mp4");
            bool succeeded = false;
            try
            {
                status?.Invoke("preparing video...");
                await PrepareSourceWithFfmpegAsync(inputFile, intermediate, ffmpegPath, preset);

                PrepareTranscodeResult prepared = await PrepareAsync(intermediate, outputFile, preset);
                if (!prepared.CanTranscode)
                {
                    throw new InvalidOperationException(
                        $"Windows' WMV9 encoder would not accept the \"{preset.Label}\" setting " +
                        $"(reason: {prepared.FailureReason}). Try a lower setting.");
                }

                status?.Invoke("encoding 0%");
                var operation = prepared.TranscodeAsync();
                operation.Progress = (_, percent) => status?.Invoke($"encoding {percent:0}%");
                await operation;

                // Media Foundation reports success even when it wrote no video or used a different
                // profile than requested, so verify what actually ended up in the file.
                OutputInfo info = await InspectOutputAsync(outputFile, ffmpegPath);
                VerifyOutput(info, preset);

                succeeded = true;
            }
            finally
            {
                TryDelete(intermediate);
                if (!succeeded)
                    TryDelete(outputFile); // never leave a blank/partial .wmv in the done folder
            }
        }

        private static void VerifyOutput(OutputInfo info, OutputPreset preset)
        {
            if (info.Frames <= 0)
            {
                throw new InvalidOperationException(
                    "The WMV9 encoder finished but the output contains no video frames.");
            }

            // The profile is forced to WMV3 + WMA v2, but WMV files known to play on the console also use
            // VC-1 Advanced Profile (WVC1) video and WMA Pro audio, which is what Windows picks by default.
            // So if Windows ignored the forced profile, those combinations are still acceptable; anything
            // else (e.g. WMV8 or an unknown codec) is rejected.
            bool videoOk = IsOneOf(info.VideoCodec, "wmv3", "vc1");
            bool audioOk = info.AudioCodec == null || IsOneOf(info.AudioCodec, "wmav2", "wmapro");
            if (!videoOk || !audioOk)
            {
                throw new InvalidOperationException(
                    $"The output uses video '{info.VideoCodec}' / audio '{info.AudioCodec ?? "none"}'. " +
                    "Expected WMV3 or VC-1 video with WMA v2 or WMA Pro audio, so it may not play on the Xbox 360.");
            }

            if (info.Width != preset.Width || info.Height != preset.Height)
            {
                throw new InvalidOperationException(
                    $"The output is {info.Width}x{info.Height} instead of the {preset.Width}x{preset.Height} " +
                    $"requested by \"{preset.Label}\".");
            }

            double? fps = info.EffectiveFrameRate;
            if (fps != null && Math.Abs(fps.Value - preset.FrameRate) / preset.FrameRate > FrameRateTolerance)
            {
                throw new InvalidOperationException(
                    $"The output runs at about {fps.Value:0.#} fps instead of the {preset.FrameRate} fps " +
                    $"requested by \"{preset.Label}\".");
            }
        }

        private static async Task<PrepareTranscodeResult> PrepareAsync(string inputFile, string outputFile, OutputPreset preset)
        {
            StorageFile source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(inputFile));
            StorageFolder outputFolder = await StorageFolder.GetFolderFromPathAsync(
                Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
            StorageFile destination = await outputFolder.CreateFileAsync(
                Path.GetFileName(outputFile), CreationCollisionOption.ReplaceExisting);

            PrepareTranscodeResult? result = null;
            foreach (uint audioBitrate in AudioBitrates)
            {
                var transcoder = new MediaTranscoder();
                result = await transcoder.PrepareFileTranscodeAsync(
                    source, destination, CreateClassicWmv9Profile(preset, audioBitrate));

                if (result.CanTranscode)
                    break;
            }

            return result!;
        }

        private static MediaEncodingProfile CreateClassicWmv9Profile(OutputPreset preset, uint audioBitrate)
        {
            // Start from Windows' stock WMV profile, then override the codecs.
            MediaEncodingProfile profile = MediaEncodingProfile.CreateWmv(VideoEncodingQuality.HD720p);

            // Video: WMV3 (WMV9 Simple/Main profile), not the default VC-1 Advanced Profile (WVC1).
            profile.Video.Subtype = MediaEncodingSubtypes.Wmv3;
            profile.Video.Width = (uint)preset.Width;
            profile.Video.Height = (uint)preset.Height;
            profile.Video.Bitrate = preset.VideoBitrate;
            profile.Video.FrameRate.Numerator = (uint)preset.FrameRate;
            profile.Video.FrameRate.Denominator = 1;

            // Audio: WMA v2 (WMA Standard), not the default WMA Pro. 16-bit stereo at 48 kHz.
            profile.Audio.Subtype = MediaEncodingSubtypes.Wma8;
            profile.Audio.SampleRate = 48000;
            profile.Audio.ChannelCount = 2;
            profile.Audio.BitsPerSample = 16;
            profile.Audio.Bitrate = audioBitrate;

            return profile;
        }

        /// <summary>
        /// Decodes anything FFmpeg can read and re-encodes it as standard High-profile H.264 + AAC at
        /// exactly the preset's frame size and frame rate, a format Windows' decoders handle reliably.
        /// </summary>
        private static async Task PrepareSourceWithFfmpegAsync(
            string inputFile, string outputFile, string ffmpegPath, OutputPreset preset)
        {
            var (exitCode, errors) = await RunFfmpegAsync(ffmpegPath,
                FfmpegArgs.PrepareSource(inputFile, outputFile, preset));

            if (exitCode != 0 || !File.Exists(outputFile))
            {
                throw new InvalidOperationException(
                    $"FFmpeg could not read this file (exit code {exitCode}):\n{Tail(errors, 1200)}");
            }
        }

        /// <summary>
        /// Decodes the video track (a few hundred times faster than real time) to count its frames, and
        /// reads the codecs, frame size and duration ffmpeg reports for the file.
        /// </summary>
        private static async Task<OutputInfo> InspectOutputAsync(string file, string ffmpegPath)
        {
            var (_, errors) = await RunFfmpegAsync(ffmpegPath, FfmpegArgs.Inspect(file));
            return FfmpegArgs.ParseOutputInfo(errors);
        }

        private static async Task<(int ExitCode, string StdErr)> RunFfmpegAsync(string ffmpegPath, params string[] args)
        {
            if (!File.Exists(ffmpegPath))
            {
                throw new FileNotFoundException(
                    "ffmpeg.exe was not found next to the app. Restart the app to let it download FFmpeg, " +
                    "or place ffmpeg.exe in the app folder.",
                    ffmpegPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // ArgumentList handles quoting for us (file names with spaces, quotes, etc.).
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start ffmpeg.exe.");

            // Read both streams or ffmpeg can block on a full pipe.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;

            return (process.ExitCode, await stderr);
        }

        private static bool IsOneOf(string? value, params string[] allowed) =>
            Array.Exists(allowed, a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

        private static string Tail(string text, int maxLength) =>
            text.Length <= maxLength ? text.Trim() : "..." + text[^maxLength..].Trim();

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
