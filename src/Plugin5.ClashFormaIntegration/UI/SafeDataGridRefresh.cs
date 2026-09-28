using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Plugin5.ClashFormaIntegration.UI
{
    /// <summary>
    /// Coalesces DataGrid refresh requests and executes them only after WPF has
    /// finished the current checkbox/cell edit. This prevents the unhandled
    /// CollectionView exception: "Refresh is not allowed during an AddNew or
    /// EditItem transaction".
    /// </summary>
    internal static class SafeDataGridRefresh
    {
        private static readonly HashSet<DataGrid> Pending = new HashSet<DataGrid>();
        private const int MaxAttempts = 3;

        public static void Request(DataGrid grid, Action afterRefresh = null, Action<string> log = null)
        {
            if (grid == null || grid.Dispatcher == null || grid.Dispatcher.HasShutdownStarted)
                return;

            if (!grid.Dispatcher.CheckAccess())
            {
                grid.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => Request(grid, afterRefresh, log)));
                return;
            }

            if (!Pending.Add(grid))
                return;

            Schedule(grid, afterRefresh, log, 1);
        }

        private static void Schedule(DataGrid grid, Action afterRefresh, Action<string> log, int attempt)
        {
            grid.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => RefreshCore(grid, afterRefresh, log, attempt)));
        }

        private static void RefreshCore(DataGrid grid, Action afterRefresh, Action<string> log, int attempt)
        {
            if (grid.Dispatcher.HasShutdownStarted)
            {
                Pending.Remove(grid);
                return;
            }

            try
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);

                var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
                var editable = view as IEditableCollectionView;
                if (editable != null)
                {
                    if (editable.IsAddingNew)
                        editable.CommitNew();
                    if (editable.IsEditingItem)
                        editable.CommitEdit();

                    if (editable.IsAddingNew || editable.IsEditingItem)
                    {
                        RetryOrSkip(grid, afterRefresh, log, attempt,
                            "CollectionView is still editing; refresh deferred.");
                        return;
                    }
                }

                view?.Refresh();
                Pending.Remove(grid);
                afterRefresh?.Invoke();
            }
            catch (InvalidOperationException ex) when (IsEditRefreshConflict(ex))
            {
                RetryOrSkip(grid, afterRefresh, log, attempt, ex.Message);
            }
            catch (Exception ex)
            {
                Pending.Remove(grid);
                log?.Invoke("Safe grid refresh failed without crashing Revit: " + ex);
            }
        }

        private static void RetryOrSkip(DataGrid grid, Action afterRefresh, Action<string> log, int attempt, string reason)
        {
            if (attempt < MaxAttempts)
            {
                log?.Invoke("Grid refresh retry " + attempt + ": " + reason);
                Schedule(grid, afterRefresh, log, attempt + 1);
                return;
            }

            Pending.Remove(grid);
            log?.Invoke("Grid refresh skipped after " + MaxAttempts + " attempts: " + reason);
            afterRefresh?.Invoke();
        }

        private static bool IsEditRefreshConflict(InvalidOperationException ex)
        {
            var message = ex.Message ?? string.Empty;
            return message.IndexOf("Refresh", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (message.IndexOf("AddNew", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("EditItem", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
