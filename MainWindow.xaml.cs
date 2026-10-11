using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace XboxWMVCreator
{
    public partial class MainWindow : Window
    {
        private List<string> conversionQueue = new List<string>();
        private ConversionWindow conversionWindow;
        private const string FfmpegUrl = "https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v4.4.1/ffmpeg-4.4.1-win-64.zip";
        private const string YtdlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private string appFolder = AppDomain.CurrentDomain.BaseDirectory;
        private string ffmpegPath => Path.Combine(appFolder, "ffmpeg.exe");
        private string ytdlpPath => Path.Combine(appFolder, "yt-dlp.exe");
        
        public MainWindow()
        {
            InitializeComponent();
            CheckDependencies();


        }

       

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.flv;*.wmv|All Files|*.*",
                Multiselect = true
            };
            if (openFileDialog.ShowDialog() == true)
            {
                foreach (var filename in openFileDialog.FileNames)
                {
                    conversionQueue.Add(filename);
                    QueueListBox.Items.Add(filename);
                }
            }
        }

        private async void DownloadUrlButton_Click(object sender, RoutedEventArgs e)
        {
            string url = Microsoft.VisualBasic.Interaction.InputBox("Enter YouTube URL:", "Download Video", "");

            if (string.IsNullOrWhiteSpace(url))
                return;

            string tempFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            Directory.CreateDirectory(tempFolder);

            string outputPath = Path.Combine(tempFolder, "%(title)s.%(ext)s");

            var processInfo = new ProcessStartInfo
            {
                FileName = ytdlpPath,
                Arguments = $"-o \"{outputPath}\" \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = processInfo })
            {
                process.Start();
                await Task.WhenAll(
                    process.StandardOutput.ReadToEndAsync(),
                    process.StandardError.ReadToEndAsync()
                );
                await process.WaitForExitAsync();
            }

            foreach (var file in Directory.GetFiles(tempFolder))
            {
                conversionQueue.Add(file);
                QueueListBox.Items.Add(file);
            }

            MessageBox.Show("Download complete and added to queue!");
        }

        

        private async void ConvertButton_Click(object sender, RoutedEventArgs e)
        {
            if (conversionQueue.Count == 0)
            {
                MessageBox.Show("No files to convert.");
                return;
            }

            conversionWindow = new ConversionWindow();
            conversionWindow.Show();

            int total = conversionQueue.Count;
            int count = 0;

            foreach (var inputFile in conversionQueue)
            {
                count++;
                conversionWindow.UpdateProgress($"Converting ({count}/{total}): {Path.GetFileName(inputFile)}");

                try
                {
                    await ConvertToWMV(inputFile);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error converting {Path.GetFileName(inputFile)}:\n{ex.Message}");
                }
            }

            conversionWindow.Close();
            conversionQueue.Clear();
            QueueListBox.Items.Clear();

            MessageBox.Show("All files converted!");

            string doneFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "done");
            
            if (Properties.Settings.Default.AutoOpenFolder)
            {
                Process.Start("explorer.exe", doneFolder);
            }
        }

        private async Task ConvertToWMV(string inputFile)
        {
            string doneFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "done");
            Directory.CreateDirectory(doneFolder);

            string outputFile = Path.Combine(doneFolder, Path.GetFileNameWithoutExtension(inputFile) + ".wmv");

            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{inputFile}\" -c:v wmv3 -b:v 3000k -threads 0 -s 1280x720 -c:a wmav2 \"{outputFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true })
            {
                process.Start();

                await Task.WhenAll(
                    process.StandardOutput.ReadToEndAsync(),
                    process.StandardError.ReadToEndAsync()
                );

                await process.WaitForExitAsync();
            }
            
            CleanupTempFolder();
        }

        private void ViewOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string doneFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "done");

            if (!Directory.Exists(doneFolder))
                Directory.CreateDirectory(doneFolder);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{doneFolder}\"",
                UseShellExecute = true
            });
        }

        private void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = QueueListBox.SelectedItems.Cast<string>().ToList();

            foreach (var item in selectedItems)
            {
                QueueListBox.Items.Remove(item);
                conversionQueue.Remove(item);
            }
        }

        private async void CheckDependencies()
        {
            if (!File.Exists(ffmpegPath))
            {
                MessageBox.Show("FFmpeg not found. Downloading...");
                await DownloadAndExtractFfmpeg();
            }

            if (!File.Exists(ytdlpPath))
            {
                MessageBox.Show("yt-dlp not found. Downloading...");
                await DownloadFile(YtdlpUrl, ytdlpPath);
            }
        }

        private async Task DownloadFile(string url, string outputPath)
        {
            using (var client = new System.Net.Http.HttpClient())
            {
                var data = await client.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(outputPath, data);
            }
        }

        private async Task DownloadAndExtractFfmpeg()
        {
            string tempZip = Path.Combine(appFolder, "ffmpeg.zip");

            await DownloadFile(FfmpegUrl, tempZip);

            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, appFolder);

            // Find the actual ffmpeg.exe inside extracted folders
            string ffmpegRealPath = Directory.GetFiles(appFolder, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(ffmpegRealPath))
            {
                File.Copy(ffmpegRealPath, ffmpegPath, true);
            }

            File.Delete(tempZip);
        }
        private void CleanupTempFolder()
        {
            string tempFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");

            if (Directory.Exists(tempFolder))
            {
                try
                {
                    Directory.Delete(tempFolder, true); // true = recursive delete
                }
                catch 
                {
                    //silent
                }
            }
        }
    }
}
