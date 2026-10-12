using System;
using System.Threading;
using System.Windows;

namespace XboxWMVCreator
{
    /// <summary>
    /// Modal first-run dialog that downloads ffmpeg.exe and yt-dlp.exe with a progress bar.
    /// Shown with ShowDialog(), so the main window can't be used until it finishes.
    /// DialogResult is true when everything is downloaded, false if the user quit.
    /// </summary>
    public partial class SetupWindow : Window
    {
        private readonly string appFolder;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool finished;

        public SetupWindow(string appFolder)
        {
            InitializeComponent();
            this.appFolder = appFolder;

            ContentRendered += SetupWindow_ContentRendered;
            Closing += SetupWindow_Closing;
        }

        private async void SetupWindow_ContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= SetupWindow_ContentRendered; // run once

            // Created on the UI thread, so progress callbacks are marshalled back to it automatically.
            var progress = new Progress<DependencyProgress>(p =>
            {
                StatusText.Text = p.Message;
                DownloadProgressBar.Value = p.Percent;
            });

            while (true)
            {
                try
                {
                    await DependencyDownloader.EnsureAsync(appFolder, progress, cancellation.Token);
                    Finish(true);
                    return;
                }
                catch (OperationCanceledException)
                {
                    Finish(false);
                    return;
                }
                catch (Exception ex)
                {
                    MessageBoxResult retry = MessageBox.Show(
                        this,
                        $"Could not download the required tools:\n\n{ex.Message}\n\nTry again?",
                        "Download failed",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (retry != MessageBoxResult.Yes)
                    {
                        Finish(false);
                        return;
                    }

                    DownloadProgressBar.Value = 0;
                    StatusText.Text = "Retrying...";
                }
            }
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            // Cancels the download; the ContentRendered handler then closes the dialog with false.
            cancellation.Cancel();
        }

        private void SetupWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // The title-bar X must not dismiss the dialog mid-download; use the Quit button instead.
            if (!finished)
                e.Cancel = true;
        }

        private void Finish(bool success)
        {
            finished = true;
            DialogResult = success; // closes the dialog
        }
    }
}
