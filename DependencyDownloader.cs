using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XboxWMVCreator
{
    /// <param name="Message">Status text, e.g. "Downloading FFmpeg (1 of 2) - 19.8 / 44.1 MB".</param>
    /// <param name="Percent">Overall progress across everything being downloaded, 0-100.</param>
    internal readonly record struct DependencyProgress(string Message, double Percent);

    /// <summary>
    /// Downloads the two external tools the app needs (ffmpeg.exe and yt-dlp.exe) next to the app,
    /// reporting overall progress so a single progress bar can cover both.
    /// </summary>
    internal static class DependencyDownloader
    {
        public const string FfmpegUrl = "https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v4.4.1/ffmpeg-4.4.1-win-64.zip";
        public const string YtdlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

        // Rough download sizes in MB. Only used to weight the single overall progress bar.
        private const double FfmpegWeight = 44;
        private const double YtdlpWeight = 18;

        public static string FfmpegPath(string appFolder) => Path.Combine(appFolder, "ffmpeg.exe");
        public static string YtdlpPath(string appFolder) => Path.Combine(appFolder, "yt-dlp.exe");

        public static bool AllPresent(string appFolder) =>
            File.Exists(FfmpegPath(appFolder)) && File.Exists(YtdlpPath(appFolder));

        /// <summary>Downloads whichever of ffmpeg.exe / yt-dlp.exe is missing. Throws on failure.</summary>
        public static Task EnsureAsync(string appFolder, IProgress<DependencyProgress> progress, CancellationToken ct) =>
            EnsureAsync(appFolder, FfmpegUrl, YtdlpUrl, progress, ct);

        // The URLs are parameters so the download/extract logic can be tested against a local server.
        internal static async Task EnsureAsync(
            string appFolder, string ffmpegUrl, string ytdlpUrl,
            IProgress<DependencyProgress> progress, CancellationToken ct)
        {
            bool needFfmpeg = !File.Exists(FfmpegPath(appFolder));
            bool needYtdlp = !File.Exists(YtdlpPath(appFolder));

            double totalWeight = (needFfmpeg ? FfmpegWeight : 0) + (needYtdlp ? YtdlpWeight : 0);
            if (totalWeight == 0)
                return;

            int steps = (needFfmpeg ? 1 : 0) + (needYtdlp ? 1 : 0);
            int step = 0;
            double doneWeight = 0;

            Directory.CreateDirectory(appFolder);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Xbox360WMV9Creator/1.0");

            void Report(string message, double fraction, double weight)
            {
                double percent = (doneWeight + weight * Math.Clamp(fraction, 0, 1)) / totalWeight * 100;
                progress.Report(new DependencyProgress(message, percent));
            }

            if (needFfmpeg)
            {
                step++;
                string label = $"Downloading FFmpeg ({step} of {steps})";
                string zipPath = Path.Combine(appFolder, "ffmpeg.zip.part");
                try
                {
                    await DownloadAsync(client, ffmpegUrl, zipPath,
                        (received, total) => Report($"{label}{Describe(received, total)}", Fraction(received, total), FfmpegWeight), ct);

                    Report($"Extracting FFmpeg ({step} of {steps})...", 1, FfmpegWeight);
                    ExtractFfmpeg(zipPath, FfmpegPath(appFolder));
                }
                finally
                {
                    TryDelete(zipPath);
                }
                doneWeight += FfmpegWeight;
            }

            if (needYtdlp)
            {
                step++;
                string label = $"Downloading yt-dlp ({step} of {steps})";
                string target = YtdlpPath(appFolder);
                string partPath = target + ".part";
                try
                {
                    await DownloadAsync(client, ytdlpUrl, partPath,
                        (received, total) => Report($"{label}{Describe(received, total)}", Fraction(received, total), YtdlpWeight), ct);

                    File.Move(partPath, target, overwrite: true);
                }
                finally
                {
                    TryDelete(partPath);
                }
                doneWeight += YtdlpWeight;
            }

            progress.Report(new DependencyProgress("All set!", 100));
        }

        private static async Task DownloadAsync(
            HttpClient client, string url, string destination,
            Action<long, long?> onProgress, CancellationToken ct)
        {
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using Stream source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            var throttle = Stopwatch.StartNew();

            onProgress(0, total);
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                // Don't flood the UI thread with an update for every 80 KB chunk.
                if (throttle.ElapsedMilliseconds >= 100)
                {
                    onProgress(received, total);
                    throttle.Restart();
                }
            }

            await target.FlushAsync(ct);
            onProgress(received, total);
        }

        // The zip contains just ffmpeg.exe, but look it up by name rather than assuming its path.
        private static void ExtractFfmpeg(string zipPath, string destination)
        {
            string partPath = destination + ".part";
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e =>
                            string.Equals(e.Name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("ffmpeg.exe was not found inside the downloaded archive.");

                    entry.ExtractToFile(partPath, overwrite: true);
                }

                File.Move(partPath, destination, overwrite: true);
            }
            finally
            {
                TryDelete(partPath);
            }
        }

        private static double Fraction(long received, long? total) =>
            total is > 0 ? (double)received / total.Value : 0;

        private static string Describe(long received, long? total) =>
            total is > 0
                ? $" - {received / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} MB"
                : $" - {received / 1048576.0:0.0} MB";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
