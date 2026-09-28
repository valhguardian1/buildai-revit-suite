using System.Windows;
using System.Windows.Controls;
using BuildAI.Core.Localization;

namespace Plugin1.TimeModelAnalytics.UI
{
    /// <summary>
    /// Minimal modal dialog to capture the API token (masked). Built in code so
    /// there is no XAML build step. The token is never logged or echoed.
    /// </summary>
    public static class TokenDialog
    {
        public static bool TryPrompt(string current, out string token)
        {
            token = current;
            var win = new Window
            {
                Title = Loc.T("Btn_Connect"),
                Width = 420, Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());

            var label = new TextBlock { Text = Loc.T("Token_Prompt"), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(label, 0);

            var box = new PasswordBox { Password = current ?? "" };
            Grid.SetRow(box, 1);

            var ok = new Button { Content = "OK", Width = 90, Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetRow(ok, 2);

            bool result = false;
            ok.Click += (s, e) => { result = true; win.Close(); };

            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(ok);
            win.Content = grid;
            win.ShowDialog();

            token = box.Password;
            return result && !string.IsNullOrWhiteSpace(token);
        }
    }
}
