using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XboxWMVCreator
{
    /// <summary>What ffmpeg reports about a finished file (see FfmpegArgs.ParseOutputInfo).</summary>
    /// <param name="Frames">Number of video frames that could be decoded; 0 if none.</param>
    /// <param name="DurationSeconds">Container duration, if reported.</param>
    internal sealed record OutputInfo(
        int Frames,
        string? VideoCodec,
        string? AudioCodec,
        int? Width,
        int? Height,
        double? DurationSeconds)
    {
        /// <summary>Frames per second actually present in the file (decoded frames / duration).</summary>
        public double? EffectiveFrameRate =>
            DurationSeconds is > 0 && Frames > 0 ? Frames / DurationSeconds.Value : null;
    }

    /// <summary>
    /// Builds and parses ffmpeg command lines. Kept free of any Windows/WPF types so it can be tested
    /// with a real ffmpeg on any platform.
    /// </summary>
    internal static class FfmpegArgs
    {
        /// <summary>
        /// Arguments that turn any input into a plain High-profile H.264 + AAC MP4 at exactly the preset's
        /// frame size and frame rate. Windows' own decoders are picky (they silently produce no video for
        /// e.g. lossless H.264), so FFmpeg decodes and Media Foundation encodes this clean file instead.
        /// </summary>
        public static string[] PrepareSource(string inputFile, string outputFile, OutputPreset preset)
        {
            int w = preset.Width, h = preset.Height;

            // fps first (cheaper when it drops frames), then fit the picture inside w x h keeping its aspect
            // ratio, then pad the rest with black bars so the result is exactly w x h and never stretched.
            string filter =
                $"fps={preset.FrameRate}," +
                $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
                $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black," +
                "setsar=1";

            return new[]
            {
                "-hide_banner", "-y", "-nostdin",
                "-i", inputFile,
                "-map", "0:v:0", "-map", "0:a:0?", "-sn",
                "-vf", filter,
                "-c:v", "libx264", "-profile:v", "high", "-pix_fmt", "yuv420p",
                "-preset", "veryfast", "-crf", "14",
                "-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-ac", "2", // matches the WMA profile
                outputFile
            };
        }

        /// <summary>Arguments that decode a file's first video stream and discard it, to count frames and read stream info.</summary>
        public static string[] Inspect(string file) => new[]
        {
            "-hide_banner", "-nostdin",
            "-i", file,
            "-map", "0:v:0", "-f", "null", "-"
        };

        /// <summary>Reads frame count, codecs, frame size and duration out of ffmpeg's stderr text.</summary>
        public static OutputInfo ParseOutputInfo(string ffmpegStderr)
        {
            // Running stats look like "frame=  123 fps=..."; the last one is the total.
            MatchCollection frameMatches = Regex.Matches(ffmpegStderr, @"frame=\s*(\d+)");
            int frames = frameMatches.Count == 0
                ? 0
                : int.Parse(frameMatches[frameMatches.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);

            // The input's stream list comes first, e.g.
            // "Stream #0:1: Video: wmv3 (Main) (WMV3 / 0x33564D57), yuv420p, 1280x720, 3000 kb/s, 30 fps"
            string? video = Group(ffmpegStderr, @"Video:\s*(\w+)");
            string? audio = Group(ffmpegStderr, @"Audio:\s*(\w+)");

            int? width = null, height = null;
            Match size = Regex.Match(ffmpegStderr, @"Video:[^\r\n]*?,\s*(\d{2,5})x(\d{2,5})\b");
            if (size.Success)
            {
                width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            double? duration = null;
            Match dur = Regex.Match(ffmpegStderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (dur.Success)
            {
                duration = int.Parse(dur.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                         + int.Parse(dur.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                         + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture);
            }

            return new OutputInfo(frames, video, audio, width, height, duration);
        }

        private static string? Group(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
