using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace DupFree.Views
{
    public partial class DependencyWindow : Window
    {
        public DependencyWindow(string output)
        {
            InitializeComponent();
            OutputText.Text = output;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(OutputText.Text);
            }
            catch { }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void AutoUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            AutoUpdateButton.IsEnabled = false;
            try
            {
                var lines = OutputText.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var packages = new List<(string name, string latest)>();
                foreach (var line in lines)
                {
                    // look for rows with at least three columns
                    var parts = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 && Version.TryParse(parts[^1], out _))
                    {
                        // first token is package name, last token is latest
                        packages.Add((parts[0], parts[^1]));
                    }
                }

                if (packages.Count == 0)
                {
                    MessageBox.Show(this, "No outdated packages detected.", "Auto-update", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var sb = new StringBuilder();
                    foreach (var pkg in packages)
                    {
                        sb.AppendLine($"Updating {pkg.name} to {pkg.latest}...");
                        try
                        {
                            var psi = new ProcessStartInfo("dotnet", $"add package {pkg.name} --version {pkg.latest}")
                            {
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                            };
                            using var p = Process.Start(psi);
                            if (p != null)
                            {
                                sb.AppendLine(p.StandardOutput.ReadToEnd());
                                sb.AppendLine(p.StandardError.ReadToEnd());
                                p.WaitForExit();
                            }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("Failed to update " + pkg.name + ": " + ex.Message);
                        }
                    }
                    OutputText.Text = sb.ToString();
                    MessageBox.Show(this, "Auto-update completed. See details in the window.", "Auto-update", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                AutoUpdateButton.IsEnabled = true;
            }
        }
    }
}
