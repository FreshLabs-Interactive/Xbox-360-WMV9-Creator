using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace XboxWMVCreator
{
    public partial class MainWindow : Window
    {
        private List<string> conversionQueue = new List<string>();
        private ConversionWindow conversionWindow;
        // The folder the .exe itself lives in. ProcessPath is used first so this stays correct for the
        // single-file publish (where files extracted at startup must not be mistaken for the app folder).
        private string appFolder =
            Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } processDir
                ? processDir
                : AppDomain.CurrentDomain.BaseDirectory;
        private string ffmpegPath => DependencyDownloader.FfmpegPath(appFolder);
        private string ytdlpPath => DependencyDownloader.YtdlpPath(appFolder);
        private string TempFolder => Path.Combine(appFolder, "temp");

        public MainWindow()
        {
            InitializeComponent();

            // Fill the Output Settings dropdown and restore the user's last choice. The change handler is
            // attached afterwards so that setting the initial selection doesn't trigger a needless save.
            PresetComboBox.ItemsSource = OutputPreset.All;
            PresetComboBox.SelectedItem = OutputPreset.FromId(Properties.Settings.Default.OutputPreset);
            PresetComboBox.SelectionChanged += PresetComboBox_SelectionChanged;

            Loaded += MainWindow_Loaded;
            Closed += (s, e) => CleanupTempFolder();
        }

        private void PresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PresetComboBox.SelectedItem is not OutputPreset preset)
                return;

            try
            {
                Properties.Settings.Default.OutputPreset = preset.Id;
                Properties.Settings.Default.Save();
            }
            catch
            {
                // Not being able to remember the choice shouldn't stop anyone from converting.
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;

            // The download queue only lives in memory, so anything left in "temp" is from an earlier
            // session. Clear it so those files can't show up in a new download's queue.
            CleanupTempFolder();

            if (DependencyDownloader.AllPresent(appFolder))
                return;

            // First run: ffmpeg.exe and/or yt-dlp.exe are missing. ShowDialog() blocks the main
            // window until the downloads finish, so the app can't be used without them.
            var setup = new SetupWindow(appFolder) { Owner = this };
            if (setup.ShowDialog() != true)
                Application.Current.Shutdown();
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

            string tempFolder = TempFolder;
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
                if (conversionQueue.Contains(file))
                    continue; // already queued from an earlier download

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

            var convertButton = sender as System.Windows.Controls.Button;

            // Lock the controls while converting: a second run over the same queue, or a different
            // setting picked halfway through, would only cause confusion.
            if (convertButton != null)
                convertButton.IsEnabled = false;
            PresetComboBox.IsEnabled = false;

            try
            {
                await ConvertQueueAsync(PresetComboBox.SelectedItem as OutputPreset ?? OutputPreset.Default);
            }
            finally
            {
                if (convertButton != null)
                    convertButton.IsEnabled = true;
                PresetComboBox.IsEnabled = true;
            }
        }

        private async Task ConvertQueueAsync(OutputPreset preset)
        {
            conversionWindow = new ConversionWindow();
            conversionWindow.Show();

            // Iterate a copy so removing items from the queue mid-run can't break the loop.
            var files = conversionQueue.ToList();
            int total = files.Count;
            int count = 0;
            var succeeded = new List<string>();
            var failed = new List<string>();

            foreach (var inputFile in files)
            {
                count++;
                string label = $"Converting ({count}/{total}): {Path.GetFileName(inputFile)}";
                conversionWindow.UpdateProgress(label);

                try
                {
                    await ConvertToWMV(inputFile, preset, status =>
                        conversionWindow.UpdateProgress($"{label} - {status}"));

                    succeeded.Add(inputFile);
                }
                catch (Exception ex)
                {
                    failed.Add($"{Path.GetFileName(inputFile)}: {ex.Message}");
                }
            }

            // Everything downloaded from YouTube lives in "temp"; the batch is finished, so remove it.
            CleanupTempFolder();

            conversionWindow.Close();
            conversionQueue.Clear();
            QueueListBox.Items.Clear();

            if (failed.Count == 0)
            {
                MessageBox.Show($"All {succeeded.Count} file(s) converted ({preset.Label}).");
            }
            else
            {
                MessageBox.Show(
                    $"{succeeded.Count} of {total} file(s) converted ({preset.Label}).\n\nFailed:\n" + string.Join("\n\n", failed),
                    "Conversion finished with errors");
            }

            if (succeeded.Count > 0 && Properties.Settings.Default.AutoOpenFolder)
            {
                OpenDoneFolder();
            }
        }

        private string DoneFolder => Path.Combine(appFolder, "done");

        private async Task ConvertToWMV(string inputFile, OutputPreset preset, Action<string>? status = null)
        {
            string outputFile = Path.Combine(DoneFolder, Path.GetFileNameWithoutExtension(inputFile) + ".wmv");

            // Work files (FFmpeg-prepared intermediate) go in the system temp folder, NOT the "temp" folder
            // yt-dlp downloads into, so they can never delete queued downloads.
            string workFolder = Path.Combine(Path.GetTempPath(), "X360WMV9");

            await Wmv9Converter.ConvertAsync(inputFile, outputFile, ffmpegPath, workFolder, preset, status);
        }

        private void OpenDoneFolder()
        {
            Directory.CreateDirectory(DoneFolder);

            // The path must be quoted: if the app lives in a folder with spaces, an unquoted
            // path makes Explorer open the wrong location.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{DoneFolder}\"",
                UseShellExecute = true
            });
        }

        private void ViewOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            OpenDoneFolder();
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

        // Deletes the "temp" folder that yt-dlp downloads into (the folder itself, not just its files).
        // Retries briefly because antivirus or Explorer can hold a file open for a moment.
        private void CleanupTempFolder()
        {
            for (int attempt = 0; attempt < 3 && Directory.Exists(TempFolder); attempt++)
            {
                try
                {
                    Directory.Delete(TempFolder, true);
                }
                catch
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }
    }
}
