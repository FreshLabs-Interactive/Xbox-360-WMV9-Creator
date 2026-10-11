using System.Windows;

namespace XboxWMVCreator
{
    public partial class ConversionWindow : Window
    {
        public ConversionWindow()
        {
            InitializeComponent();
        }

        public void UpdateProgress(string message)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressText.Text = message;
            });
        }
    }
}
