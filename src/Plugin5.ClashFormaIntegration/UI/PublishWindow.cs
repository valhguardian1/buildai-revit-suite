using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BuildAI.Core.Logging;
using Plugin5.ClashFormaIntegration.Publishing;
using Plugin5.ClashFormaIntegration.Revit;

using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Plugin5.ClashFormaIntegration.UI
{
    public sealed class PublishWindow : Window
    {
        private readonly string _filePath;
        private readonly string _revitModelUid;
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap };
        private readonly Button _run = new Button { Content = "Publish", Margin = new Thickness(10), Padding = new Thickness(14, 8, 14, 8) };
        private readonly Button _settings = new Button { Content = "Settings", Margin = new Thickness(10), Padding = new Thickness(14, 8, 14, 8) };

        public PublishWindow(string filePath, string revitModelUid)
        {
            _filePath = filePath;
            _revitModelUid = revitModelUid;
            Title = "Publish model to BuildAI";
            Width = 680;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new StackPanel { Margin = new Thickness(8) };
            root.Children.Add(new TextBlock { Text = "File: " + filePath, Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap });
            root.Children.Add(new TextBlock { Text = "revit_model_uid: " + revitModelUid, Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap });
            root.Children.Add(_status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_settings);
            buttons.Children.Add(_run);
            root.Children.Add(buttons);

            _settings.Click += (_, __) => new SettingsWindow().ShowDialog();
            _run.Click += async (_, __) => await PublishAsync();
            Content = root;
        }

        private async System.Threading.Tasks.Task PublishAsync()
        {
            _run.IsEnabled = false;
            _settings.IsEnabled = false;
            try
            {
                var token = PluginContext.CredentialStore.GetApiToken();
                var progress = new Progress<string>(message => _status.Text = message);
                PluginLog.Info("Plugin5 publish started. UID=" + _revitModelUid + ", file=" + _filePath);

                var result = await new BuildAiPublicationClient().PublishRvtAsync(
                    _filePath,
                    _revitModelUid,
                    PluginContext.Settings.BuildAiBaseUrl,
                    token,
                    progress,
                    CancellationToken.None);

                _status.Text =
                    "The model was accepted by BuildAI.\n" +
                    "File ID: " + result.Id + "\n" +
                    "BuildAI model ID: " + result.RevitModelId + "\n" +
                    "Model UID: " + result.RevitModelUid + "\n" +
                    "File name: " + result.FileName;

                PluginLog.Info("Plugin5 publish completed. UploadId=" + result.Id + ", modelId=" + result.RevitModelId);
            }
            catch (Exception ex)
            {
                _status.Text = "Publish error: " + ex.Message;
                PluginLog.Error("Plugin5 publish failed", ex);
            }
            finally
            {
                _run.IsEnabled = true;
                _settings.IsEnabled = true;
            }
        }
    }
}
