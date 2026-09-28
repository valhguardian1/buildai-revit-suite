using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BuildAI.Core;
using BuildAI.Core.Localization;
using Plugin1.TimeModelAnalytics.Tracking;

namespace Plugin1.TimeModelAnalytics.UI
{
    /// <summary>
    /// Modeless, RTL-aware session dashboard. Shows connection state, active
    /// time, events sent, and pending (offline) count. Refreshes every second.
    /// Single-instance so repeated ribbon clicks just focus the open window.
    /// </summary>
    public sealed class StatusWindow : Window
    {
        private static StatusWindow _instance;

        private readonly SessionManager _session;
        private readonly TextBlock _state = new TextBlock();
        private readonly TextBlock _activeTime = new TextBlock();
        private readonly TextBlock _eventsSent = new TextBlock();
        private readonly TextBlock _pending = new TextBlock();
        private readonly DispatcherTimer _timer;

        public static void ShowSingleton(SessionManager session)
        {
            if (session == null) return;
            if (_instance != null) { _instance.Activate(); return; }
            _instance = new StatusWindow(session);
            _instance.Closed += (s, e) => _instance = null;
            _instance.Show();
        }

        private StatusWindow(SessionManager session)
        {
            _session = session;
            Title = Loc.T("Btn_Status");
            Width = 320; Height = 200;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            var grid = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition());
            AddRow(grid, 0, _state);
            AddRow(grid, 1, _activeTime);
            AddRow(grid, 2, _eventsSent);
            AddRow(grid, 3, _pending);
            Content = grid;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();
        }

        private static void AddRow(Grid grid, int row, TextBlock tb)
        {
            tb.Margin = new Thickness(0, 4, 0, 4);
            tb.FontSize = 14;
            Grid.SetRow(tb, row);
            grid.Children.Add(tb);
        }

        private void Refresh()
        {
            var c = _session.Client;
            string stateText =
                c.State == ConnectionState.Connected ? Loc.T("Status_Connected") :
                c.State == ConnectionState.Paused    ? Loc.T("Status_Paused") :
                                                       Loc.T("Status_Disconnected");
            _state.Text      = stateText;
            _activeTime.Text = $"{Loc.T("Status_ActiveTime")}: {_session.Activity.ActiveTime:hh\\:mm\\:ss}";
            _eventsSent.Text = $"{Loc.T("Status_EventsSent")}: {_session.Activity.EventsSent}";
            _pending.Text    = $"{Loc.T("Status_Pending")}: {c.PendingCount}";
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }
    }
}
