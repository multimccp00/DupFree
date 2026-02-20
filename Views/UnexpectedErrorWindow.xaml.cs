using System;
using System.IO;
using System.Windows;

namespace DupFree.Views
{
    public partial class UnexpectedErrorWindow : Window
    {
        private readonly string _logPath;

        public UnexpectedErrorWindow(string logPath)
        {
            InitializeComponent();
            _logPath = logPath;
            LogPathText.Text = logPath;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_logPath))
                {
                    var text = File.ReadAllText(_logPath);
                    Clipboard.SetText(text);
                }
                else
                {
                    Clipboard.SetText(string.Empty);
                }
            }
            catch
            {
                // ignore; copying failed
            }
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_logPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_logPath}\"");
                }
            }
            catch
            {
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}