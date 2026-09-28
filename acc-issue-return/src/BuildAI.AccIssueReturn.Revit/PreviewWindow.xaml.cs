using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BuildAI.AccIssueReturn.Core;
namespace BuildAI.AccIssueReturn.Revit;
public partial class PreviewWindow : Window
{
    public IReadOnlyList<PreviewRow> Rows { get; }
    public bool WindowOpenedSuccessfully { get; private set; }
    public PreviewWindow(IEnumerable<PreviewRow> rows,Window owner){InitializeComponent();Rows=rows?.ToList()??new List<PreviewRow>();Owner=owner;PreviewGrid.ItemsSource=Rows;ImportButton.IsEnabled=Rows.Any(x=>x.CanImport);}
    private void ImportClicked(object sender,RoutedEventArgs e){DialogResult=true;Close();}
    private void CloseClicked(object sender,RoutedEventArgs e){DialogResult=false;Close();}
    protected override void OnContentRendered(System.EventArgs e){base.OnContentRendered(e);WindowOpenedSuccessfully=true;AccIssueReturnLog.Event("ACC_PREVIEW_WINDOW_OPENED",new{rows=Rows.Count,ready=Rows.Count(x=>x.CanImport),blocked=Rows.Count(x=>!x.CanImport)});}
}
