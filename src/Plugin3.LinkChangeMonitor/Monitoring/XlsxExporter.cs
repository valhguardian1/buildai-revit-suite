using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using Plugin3.LinkChangeMonitor.Models;
using BuildAI.Core.Sorting;

namespace Plugin3.LinkChangeMonitor.Monitoring
{
    /// <summary>
    /// Dependency-free XLSX exporter. The report intentionally follows the visual logic
    /// of the supplied estimate workbook: blue header, yellow model sections, dark level/category
    /// separators, light data rows and green subtotal rows.
    /// </summary>
    public static class XlsxExporter
    {
        private const int ColumnCount = 12;

        public static void Export(string path, IEnumerable<LinkChangeItem> items)
        {
            var rows = (items ?? Enumerable.Empty<LinkChangeItem>())
                .OrderBy(x => x.CategorySortOrder)
                .ThenBy(x => x.Category, NaturalStringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ChangeTypeSortOrder)
                .ThenBy(x => x.NaturalSortName, NaturalStringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.LevelElevation)
                .ThenBy(x => x.ElementId, NaturalStringComparer.OrdinalIgnoreCase)
                .ToList();

            if (File.Exists(path)) File.Delete(path);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                AddText(archive, "[Content_Types].xml", ContentTypes());
                AddText(archive, "_rels/.rels", RootRelationships());
                AddText(archive, "xl/workbook.xml", Workbook());
                AddText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
                AddText(archive, "xl/styles.xml", Styles());
                AddText(archive, "xl/worksheets/sheet1.xml", Sheet(rows));
            }
        }

        private static string Sheet(IList<LinkChangeItem> items)
        {
            var sb = new StringBuilder(64 * 1024);
            using (var writer = XmlWriter.Create(sb, Settings()))
            {
                writer.WriteStartDocument(true);
                writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

                writer.WriteStartElement("sheetViews");
                writer.WriteStartElement("sheetView");
                writer.WriteAttributeString("workbookViewId", "0");
                writer.WriteStartElement("pane");
                writer.WriteAttributeString("ySplit", "4");
                writer.WriteAttributeString("topLeftCell", "A5");
                writer.WriteAttributeString("activePane", "bottomLeft");
                writer.WriteAttributeString("state", "frozen");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();

                writer.WriteStartElement("cols");
                WriteCol(writer, 1, 1, 7);   // #
                WriteCol(writer, 2, 2, 13);  // status
                WriteCol(writer, 3, 3, 14);  // id
                WriteCol(writer, 4, 4, 34);  // description
                WriteCol(writer, 5, 5, 24);  // link
                WriteCol(writer, 6, 6, 18);  // level
                WriteCol(writer, 7, 7, 22);  // category
                WriteCol(writer, 8, 8, 24);  // type
                WriteCol(writer, 9, 9, 34);  // changed params
                WriteCol(writer, 10, 11, 42); // before/after
                WriteCol(writer, 12, 12, 21); // date
                writer.WriteEndElement();

                writer.WriteStartElement("sheetData");
                var row = 1;
                WriteRow(writer, row++, 30, new[] { Cell("A", "LINKED MODEL CHANGE REPORT", 1) });
                WriteRow(writer, row++, 20, new[] { Cell("A", "Sformirovano: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture), 11) });
                WriteRow(writer, row++, 20, new[]
                {
                    Cell("A", "IDENTIFIKATsIYa ELEMENTA", 12),
                    Cell("I", "IZMENENIYa", 12)
                });
                WriteRow(writer, row++, 42, HeaderCells());

                var number = 1;
                foreach (var linkGroup in items.GroupBy(x => Safe(x.LinkInstanceName, "Unnamed linked model")))
                {
                    WriteRow(writer, row++, 23, new[] { Cell("A", "LINKED MODEL: " + linkGroup.Key, 3) });
                    foreach (var levelGroup in linkGroup.GroupBy(x => Safe(x.Level, "No level")))
                    {
                        WriteRow(writer, row++, 21, new[] { Cell("A", "LEVEL: " + levelGroup.Key, 4) });
                        foreach (var categoryGroup in levelGroup.GroupBy(x => Safe(x.Category, "No category")))
                        {
                            WriteRow(writer, row++, 20, new[] { Cell("A", categoryGroup.Key, 5) });
                            foreach (var item in categoryGroup)
                            {
                                var style = item.ChangeType == LinkChangeType.Added ? 7 :
                                            item.ChangeType == LinkChangeType.Deleted ? 9 : 8;
                                WriteRow(writer, row++, 36, new[]
                                {
                                    NumberCell("A", number++, style),
                                    Cell("B", item.ChangeTypeText, style),
                                    Cell("C", item.PositionCode, style),
                                    Cell("D", Safe(item.ElementName, "(unnamed)"), style),
                                    Cell("E", item.LinkInstanceName, style),
                                    Cell("F", Safe(item.Level, "—"), style),
                                    Cell("G", Safe(item.Category, "—"), style),
                                    Cell("H", Safe(item.TypeName, "—"), style),
                                    Cell("I", item.ChangedParameters, style),
                                    Cell("J", item.BeforeSummary, style),
                                    Cell("K", item.AfterSummary, style),
                                    Cell("L", item.DetectedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture), style)
                                });
                            }
                        }
                    }

                    var added = linkGroup.Count(x => x.ChangeType == LinkChangeType.Added);
                    var modified = linkGroup.Count(x => x.ChangeType == LinkChangeType.Modified);
                    var deleted = linkGroup.Count(x => x.ChangeType == LinkChangeType.Deleted);
                    WriteRow(writer, row++, 22, new[]
                    {
                        Cell("A", "MODEL TOTAL", 10),
                        Cell("I", $"Added: {added}; modified: {modified}; deleted: {deleted}; total: {linkGroup.Count()}", 10)
                    });
                }

                WriteRow(writer, row++, 24, new[]
                {
                    Cell("A", "CHANGES TOTAL", 10),
                    Cell("I", $"Added: {items.Count(x => x.ChangeType == LinkChangeType.Added)}; " +
                              $"modified: {items.Count(x => x.ChangeType == LinkChangeType.Modified)}; " +
                              $"deleted: {items.Count(x => x.ChangeType == LinkChangeType.Deleted)}; total: {items.Count}", 10)
                });

                writer.WriteEndElement(); // sheetData

                writer.WriteStartElement("mergeCells");
                var linkCount = items.GroupBy(x => Safe(x.LinkInstanceName, "Unnamed linked model")).Count();
                var mergeCount = 6 + linkCount * 3
                    + items.GroupBy(x => Safe(x.LinkInstanceName, "Unnamed linked model") + "|" + Safe(x.Level, "No level")).Count()
                    + items.GroupBy(x => Safe(x.LinkInstanceName, "Unnamed linked model") + "|" + Safe(x.Level, "No level") + "|" + Safe(x.Category, "No category")).Count();
                writer.WriteAttributeString("count", mergeCount.ToString(CultureInfo.InvariantCulture));
                Merge(writer, "A1:L1");
                Merge(writer, "A2:L2");
                Merge(writer, "A3:H3");
                Merge(writer, "I3:L3");

                var mergeRow = 5;
                foreach (var linkGroup in items.GroupBy(x => Safe(x.LinkInstanceName, "Unnamed linked model")))
                {
                    Merge(writer, $"A{mergeRow}:L{mergeRow}");
                    mergeRow++;
                    foreach (var levelGroup in linkGroup.GroupBy(x => Safe(x.Level, "No level")))
                    {
                        Merge(writer, $"A{mergeRow}:L{mergeRow}");
                        mergeRow++;
                        foreach (var categoryGroup in levelGroup.GroupBy(x => Safe(x.Category, "No category")))
                        {
                            Merge(writer, $"A{mergeRow}:L{mergeRow}");
                            mergeRow += categoryGroup.Count() + 1;
                        }
                    }
                    Merge(writer, $"A{mergeRow}:H{mergeRow}");
                    Merge(writer, $"I{mergeRow}:L{mergeRow}");
                    mergeRow++;
                }
                Merge(writer, $"A{mergeRow}:H{mergeRow}");
                Merge(writer, $"I{mergeRow}:L{mergeRow}");
                writer.WriteEndElement(); // mergeCells

                writer.WriteStartElement("autoFilter");
                writer.WriteAttributeString("ref", "A4:L4");
                writer.WriteEndElement();

                writer.WriteStartElement("pageMargins");
                writer.WriteAttributeString("left", "0.25");
                writer.WriteAttributeString("right", "0.25");
                writer.WriteAttributeString("top", "0.4");
                writer.WriteAttributeString("bottom", "0.4");
                writer.WriteAttributeString("header", "0.2");
                writer.WriteAttributeString("footer", "0.2");
                writer.WriteEndElement();

                writer.WriteStartElement("pageSetup");
                writer.WriteAttributeString("orientation", "landscape");
                writer.WriteAttributeString("fitToWidth", "1");
                writer.WriteAttributeString("fitToHeight", "0");
                writer.WriteEndElement();

                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            return sb.ToString();
        }

        private static IEnumerable<XCell> HeaderCells()
        {
            var headers = new[]
            {
                "#", "Status", "Element ID", "Description", "Linked model", "Level",
                "Category", "Type", "Changed parameters", "Before", "After", "Detected at"
            };
            for (var i = 0; i < headers.Length; i++) yield return Cell(Column(i + 1), headers[i], 2);
        }

        private static void WriteRow(XmlWriter writer, int index, double height, IEnumerable<XCell> cells)
        {
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("ht", height.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customHeight", "1");
            foreach (var cell in cells) WriteCell(writer, cell.Column + index, cell.Value, cell.Style, cell.Numeric);
            writer.WriteEndElement();
        }

        private static void WriteCell(XmlWriter writer, string reference, string value, int style, bool numeric)
        {
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", reference);
            writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            if (numeric)
            {
                writer.WriteStartElement("v"); writer.WriteString(value ?? "0"); writer.WriteEndElement();
            }
            else
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteStartElement("t");
                writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                writer.WriteString(value ?? "");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private static XCell Cell(string column, string value, int style) => new XCell(column, value, style, false);
        private static XCell NumberCell(string column, int value, int style) => new XCell(column, value.ToString(CultureInfo.InvariantCulture), style, true);

        private static void WriteCol(XmlWriter writer, int min, int max, double width)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", min.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", max.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", width.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }

        private static void Merge(XmlWriter writer, string reference)
        {
            writer.WriteStartElement("mergeCell");
            writer.WriteAttributeString("ref", reference);
            writer.WriteEndElement();
        }

        private static string Styles()
        {
            return @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <fonts count=""6"">
    <font><sz val=""10""/><name val=""Arial""/></font>
    <font><b/><sz val=""16""/><name val=""Arial""/></font>
    <font><b/><color rgb=""FFFFFFFF""/><sz val=""10""/><name val=""Arial""/></font>
    <font><b/><sz val=""10""/><name val=""Arial""/></font>
    <font><b/><color rgb=""FFFFFFFF""/><sz val=""10""/><name val=""Arial""/></font>
    <font><i/><color rgb=""FF666666""/><sz val=""9""/><name val=""Arial""/></font>
  </fonts>
  <fills count=""11"">
    <fill><patternFill patternType=""none""/></fill>
    <fill><patternFill patternType=""gray125""/></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF95B9DA""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFFFF00""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF1F1F1F""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF404040""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFFE699""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFE2F0D9""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFFF2CC""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFF4CCCC""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF70AD47""/><bgColor indexed=""64""/></patternFill></fill>
  </fills>
  <borders count=""2"">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style=""thin""><color rgb=""FF000000""/></left><right style=""thin""><color rgb=""FF000000""/></right><top style=""thin""><color rgb=""FF000000""/></top><bottom style=""thin""><color rgb=""FF000000""/></bottom><diagonal/></border>
  </borders>
  <cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>
  <cellXfs count=""13"">
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
    <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""0"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""2"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center"" wrapText=""1""/></xf>
    <xf numFmtId=""0"" fontId=""3"" fillId=""3"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""4"" fillId=""4"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""left"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""4"" fillId=""5"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""left"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""0"" fillId=""6"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment vertical=""top"" wrapText=""1""/></xf>
    <xf numFmtId=""0"" fontId=""0"" fillId=""7"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment vertical=""top"" wrapText=""1""/></xf>
    <xf numFmtId=""0"" fontId=""0"" fillId=""8"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment vertical=""top"" wrapText=""1""/></xf>
    <xf numFmtId=""0"" fontId=""0"" fillId=""9"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment vertical=""top"" wrapText=""1""/></xf>
    <xf numFmtId=""0"" fontId=""3"" fillId=""10"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center"" wrapText=""1""/></xf>
    <xf numFmtId=""0"" fontId=""5"" fillId=""0"" borderId=""0"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""right"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""3"" fillId=""2"" borderId=""1"" xfId=""0"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
  </cellXfs>
  <cellStyles count=""1""><cellStyle name=""Normal"" xfId=""0"" builtinId=""0""/></cellStyles>
</styleSheet>";
        }

        private static string ContentTypes() => @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>";

        private static string RootRelationships() => @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>";

        private static string Workbook() => @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets><sheet name=""Link changes"" sheetId=""1"" r:id=""rId1""/></sheets>
</workbook>";

        private static string WorkbookRelationships() => @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>";

        private static void AddText(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
        }

        private static XmlWriterSettings Settings() => new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
            Indent = false,
            CheckCharacters = true
        };

        private static string Safe(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        private static string EmptyLast(string value) => string.IsNullOrWhiteSpace(value) ? "~~~~" : value;

        private static string Column(int number)
        {
            var result = "";
            while (number > 0)
            {
                number--;
                result = (char)('A' + number % 26) + result;
                number /= 26;
            }
            return result;
        }

        private sealed class XCell
        {
            public XCell(string column, string value, int style, bool numeric)
            {
                Column = column; Value = value; Style = style; Numeric = numeric;
            }
            public string Column { get; }
            public string Value { get; }
            public int Style { get; }
            public bool Numeric { get; }
        }
    }
}
