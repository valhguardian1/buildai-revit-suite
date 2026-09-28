using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfImage = System.Windows.Controls.Image;
using WpfPanel = System.Windows.Controls.Panel;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfWindow = System.Windows.Window;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Autodesk.Revit.UI;
using BuildAI.Core.Configuration;
using BuildAI.Core.Localization;
using BuildAI.Core.Logging;
using BuildAI.Core.Presentation;
using BuildAI.Core.Models;
using Plugin2.VolumeEstimator.Estimation;
using Plugin2.VolumeEstimator.Revit;

namespace Plugin2.VolumeEstimator.UI
{
    public sealed class ResultsPane : WpfWindow
    {
        private static ResultsPane _current;
        private readonly DataGrid _grid = new DataGrid();
        private readonly WpfTextBox _search = new WpfTextBox();
        private readonly TextBlock _publication = new TextBlock();
        private readonly TextBlock _summary = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private EstimationResult _result = new EstimationResult();

        private ResultsPane()
        {
            Title = "BuildAI — Material Volumes Results";
            Icon = Plugin2.VolumeEstimator.Revit.RibbonIconLoader.LoadBrand();
            Width = 1220; Height = 760; MinWidth = 850; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Closed += (s, e) => _current = null;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,10) };
            header.Children.Add(BrandIcon());
            header.Children.Add(new TextBlock { Text = "MATERIAL VOLUMES RESULTS", FontSize = 17, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,0,0) });
            root.Children.Add(header);

            _publication.FontWeight = FontWeights.SemiBold; _publication.TextWrapping = TextWrapping.Wrap; _publication.Margin = new Thickness(0,0,0,10);
            _publication.FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Grid.SetRow(_publication,1); root.Children.Add(_publication);

            _summary.FontWeight = FontWeights.SemiBold; _summary.TextWrapping = TextWrapping.Wrap; _summary.Margin = new Thickness(0,0,0,8);
            Grid.SetRow(_summary,2); root.Children.Add(_summary);

            var toolbar = new DockPanel { Margin = new Thickness(0,0,0,8), LastChildFill = false };
            toolbar.Children.Add(new TextBlock { Text = "Search:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) });
            _search.Width = 280; _search.ToolTip = "Filter by category, material, class, level, or section."; _search.TextChanged += (s,e)=>ApplyFilter(); toolbar.Children.Add(_search);
            toolbar.Children.Add(ActionButton("Export", (s,e)=>ExportTable()));
            toolbar.Children.Add(ActionButton("Open in BuildAI", (s,e)=>OpenBuildAI()));
            Grid.SetRow(toolbar,3); root.Children.Add(toolbar);

            _grid.AutoGenerateColumns=false; _grid.IsReadOnly=true; _grid.CanUserAddRows=false; _grid.CanUserSortColumns=true; _grid.CanUserResizeColumns=true;
            _grid.SelectionUnit=DataGridSelectionUnit.CellOrRowHeader; _grid.SelectionMode=DataGridSelectionMode.Extended;
            _grid.ClipboardCopyMode=DataGridClipboardCopyMode.IncludeHeader; _grid.EnableRowVirtualization=true; _grid.EnableColumnVirtualization=true;
            _grid.GridLinesVisibility=DataGridGridLinesVisibility.All; _grid.ColumnHeaderStyle=HeaderStyle();
            VirtualizingPanel.SetIsVirtualizing(_grid,true); VirtualizingPanel.SetVirtualizationMode(_grid,VirtualizationMode.Recycling);
            Add("Category","Category",150,"Revit category of the measured element.");
            Add("Material","MaterialName",170,"Material assigned to the measured geometry.");
            Add("Class","MaterialClass",130,"Material class used for grouping and analysis.");
            Add("Level","Level",125,"Level associated with the element.");
            Add("Section","Section",90,"Discipline section: AR, ST, or MEP.");
            Add("Volume, m³","VolumeM3",110,"Total material volume in cubic metres.","N3");
            Add("Area, m²","AreaM2",110,"Total material surface area in square metres.","N3");
            Add("Count","Count",80,"Number of grouped elements represented by the row.");
            Grid.SetRow(_grid,4); root.Children.Add(_grid);

            _status.Margin=new Thickness(0,8,0,0); Grid.SetRow(_status,5); root.Children.Add(_status);
            Content=root;
        }

        public static void ShowPane(UIApplication app, EstimationResult result)
        {
            if (_current == null) _current = new ResultsPane();
            _current.Populate(result);
            if (!_current.IsVisible) _current.Show();
            _current.Activate();
        }

        private void Populate(EstimationResult result)
        {
            _result=result ?? new EstimationResult();
            _summary.Text=$"Elements scanned: {_result.ElementsScanned}    Counted: {_result.ElementsCounted}    Rows: {_result.Rows.Count}\n{string.Join("   ",_result.SourceNotes)}";
            RenderPublication();
            _status.Text=$"Updated {DateTime.Now:G}"; ApplyFilter();
        }
        public static void UpdatePublication(EstimationResult result)
        {
            var current = _current;
            if (current == null || result == null) return;
            current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_current != current || !ReferenceEquals(current._result, result)) return;
                current.RenderPublication();
            }));
        }
        public static void SetStatus(string text)
        {
            var current = _current;
            if (current == null) return;
            current._status.Text = text ?? "";
        }
        private void RenderPublication()
        {
            _publication.Inlines.Clear();
            if (_result.PublicationStatus == "preparing-acc")
            {
                _publication.Inlines.Add(new Run(Loc.T("Vol_AccPreparing")));
                return;
            }
            if (_result.PublicationStatus == "published-acc")
            {
                _publication.Inlines.Add(new Run(Loc.T("Vol_AccReady")));
                return;
            }
            if (_result.PublicationStatus == "publishing-acc")
            {
                _publication.Inlines.Add(new Run(Loc.T("Vol_AccPublishing")));
                return;
            }
            if (string.Equals(_result.PublicationStatus, "published", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_result.PublicationUrl))
            {
                _publication.Inlines.Add(new Run(Loc.T("Vol_PublishReady") + " "));
                var link = new Hyperlink(new Run(_result.PublicationUrl)) { NavigateUri = new Uri(_result.PublicationUrl) };
                link.RequestNavigate += (s,e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute=true }); } catch(Exception ex) { PluginLog.Error("Open published BuildAI Viewer URL failed", ex); } };
                _publication.Inlines.Add(link);
                _publication.Inlines.Add(new LineBreak());
                _publication.Inlines.Add(new Run(Loc.T("Vol_PublishWait10")));
                return;
            }
            if (string.Equals(_result.PublicationStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var diagnostic = Loc.IsRightToLeft ? "" : " " + (_result.PublicationError ?? "");
                _publication.Inlines.Add(new Run(Loc.T("Vol_PublishFailed") + diagnostic));
                return;
            }
            _publication.Inlines.Add(new Run(Loc.T("Vol_PublishUploading")));
        }
        private void ApplyFilter(){IEnumerable<RevitMaterialItem> q=_result.Rows;var s=(_search.Text??"").Trim();if(s.Length>0)q=q.Where(x=>Contains(x.Category,s)||Contains(x.MaterialName,s)||Contains(x.MaterialClass,s)||Contains(x.Level,s)||Contains(x.Section,s));_grid.ItemsSource=q.ToList();}
        private void ExportTable()
        {
            var dialog=new SaveFileDialog{Filter=TableExport.DialogFilter,FileName="BuildAI_Material_Volumes.xlsx",AddExtension=true,OverwritePrompt=true};
            if(dialog.ShowDialog(this)!=true)return;
            try
            {
                var rows=(_grid.ItemsSource as IEnumerable<RevitMaterialItem>??_result.Rows).ToList();
                var target=TableExport.Resolve(dialog.FileName,dialog.FilterIndex);
                var headers=new[]{"Category","Material","Class","Level","Section","Volume, m³","Area, m²","Count"};
                var values=rows.Select(r=>(IReadOnlyList<string>)new[]{r.Category,r.MaterialName,r.MaterialClass,r.Level,r.Section,r.VolumeM3.ToString("N3",CultureInfo.CurrentCulture),r.AreaM2?.ToString("N3",CultureInfo.CurrentCulture)??"",r.Count.ToString(CultureInfo.CurrentCulture)}).ToList();
                TableExport.Write(target.Path,target.Format,headers,values);
                PluginLog.Info("ACC_TABLE_EXPORTED module=MaterialVolumes format="+target.Format+" rows="+values.Count+" columns="+headers.Length);
                _status.Text="Report saved: "+target.Path;
                MessageBox.Show(this,_status.Text,"BuildAI",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            catch(Exception ex){PluginLog.Error("ACC_TABLE_EXPORT_FAILED module=MaterialVolumes",ex);MessageBox.Show(this,"Report export failed.","BuildAI",MessageBoxButton.OK,MessageBoxImage.Error);}
        }
        private void Add(string h,string p,double w,string tip,string fmt=null){var header=new TextBlock{Text=h,ToolTip=tip};_grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new Binding(p){StringFormat=fmt},Width=w});}
        private static Style HeaderStyle(){var st=new Style(typeof(DataGridColumnHeader));st.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(149,185,218))));st.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black));st.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.Bold));st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));st.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(5,8,5,8)));st.Setters.Add(new Setter(Control.BorderBrushProperty,Brushes.Black));st.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0,0,1,1)));return st;}
        private static WpfImage BrandIcon(){return new WpfImage{Source=Plugin2.VolumeEstimator.Revit.RibbonIconLoader.LoadBrand(),Width=28,Height=28};}
        private static WpfButton ActionButton(string text,RoutedEventHandler h){var b=new WpfButton{Content=text,Margin=new Thickness(8,0,0,0),Padding=new Thickness(10,5,10,5)};b.Click+=h;return b;}
        private static string Esc(string s)=>"\""+(s??"").Replace("\"","\"\"")+"\"";
        private static bool Contains(string a,string b)=>(a??"").IndexOf(b,StringComparison.OrdinalIgnoreCase)>=0;
        private static void OpenBuildAI(){try{var o=PluginContext.Options??BuildAiOptions.Load();var publishedUrl=_current?._result?.PublicationUrl;var url=!string.IsNullOrWhiteSpace(publishedUrl)?publishedUrl:o.BaseUrl;if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)|| (uri.Scheme!=Uri.UriSchemeHttps&&uri.Scheme!=Uri.UriSchemeHttp))throw new InvalidOperationException("BuildAI did not provide a valid viewer URL.");var process=Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});if(process==null)throw new InvalidOperationException("Windows did not open the browser.");}catch(Exception ex){PluginLog.Error("Open BuildAI from Volume Results failed",ex);MessageBox.Show("Unable to open BuildAI: "+ex.Message,"BuildAI",MessageBoxButton.OK,MessageBoxImage.Error);}}
    }
}
