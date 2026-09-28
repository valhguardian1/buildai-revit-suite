using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace BuildAI.Core.Presentation
{
    public enum TableExportFormat { Xlsx, Csv }

    public static class TableExport
    {
        public const string DialogFilter="Excel Workbook (*.xlsx)|*.xlsx|CSV UTF-8 (*.csv)|*.csv";

        public static (string Path,TableExportFormat Format) Resolve(string path,int filterIndex)
        {
            var extension=System.IO.Path.GetExtension(path)?.ToLowerInvariant();
            var format=extension==".csv"?TableExportFormat.Csv:extension==".xlsx"?TableExportFormat.Xlsx:
                filterIndex==2?TableExportFormat.Csv:TableExportFormat.Xlsx;
            var expected=format==TableExportFormat.Csv?".csv":".xlsx";
            return (System.IO.Path.ChangeExtension(path,expected),format);
        }

        public static void Write(string path,TableExportFormat format,IReadOnlyList<string> headers,IEnumerable<IReadOnlyList<string>> values)
        {
            if(headers==null||headers.Count==0)throw new ArgumentException("Export columns are empty.",nameof(headers));
            var rows=(values??Enumerable.Empty<IReadOnlyList<string>>()).ToList();
            if(rows.Any(x=>x==null||x.Count!=headers.Count))throw new ArgumentException("Export row width does not match headers.",nameof(values));
            if(format==TableExportFormat.Csv)WriteCsv(path,headers,rows);else WriteXlsx(path,headers,rows);
        }

        private static void WriteCsv(string path,IReadOnlyList<string> headers,IReadOnlyList<IReadOnlyList<string>> rows)
        {
            using(var writer=new StreamWriter(path,false,new UTF8Encoding(true)))
            {
                writer.WriteLine(string.Join(",",headers.Select(CsvValue)));
                foreach(var row in rows)writer.WriteLine(string.Join(",",row.Select(CsvValue)));
            }
        }
        private static string CsvValue(string value)=>"\""+(value??string.Empty).Replace("\"","\"\"")+"\"";

        private static void WriteXlsx(string path,IReadOnlyList<string> headers,IReadOnlyList<IReadOnlyList<string>> rows)
        {
            using(var stream=new FileStream(path,FileMode.Create,FileAccess.ReadWrite,FileShare.None))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,false))
            {
                Add(zip,"[Content_Types].xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Add(zip,"_rels/.rels","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Add(zip,"xl/workbook.xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Add(zip,"xl/_rels/workbook.xml.rels","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Add(zip,"xl/styles.xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF176A81\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\"/></xf><xf numFmtId=\"49\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\"/></xf></cellXfs></styleSheet>");
                var entry=zip.CreateEntry("xl/worksheets/sheet1.xml");
                using(var writer=XmlWriter.Create(entry.Open(),new XmlWriterSettings{Encoding=new UTF8Encoding(false),CloseOutput=true}))
                {
                    writer.WriteStartDocument();writer.WriteStartElement("worksheet","http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                    writer.WriteStartElement("sheetViews");writer.WriteStartElement("sheetView");writer.WriteAttributeString("workbookViewId","0");writer.WriteStartElement("pane");writer.WriteAttributeString("ySplit","1");writer.WriteAttributeString("topLeftCell","A2");writer.WriteAttributeString("state","frozen");writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndElement();
                    writer.WriteStartElement("cols");for(var i=0;i<headers.Count;i++){writer.WriteStartElement("col");writer.WriteAttributeString("min",(i+1).ToString());writer.WriteAttributeString("max",(i+1).ToString());writer.WriteAttributeString("width",Math.Min(48,Math.Max(13,(headers[i]??"").Length+4)).ToString());writer.WriteAttributeString("customWidth","1");writer.WriteEndElement();}writer.WriteEndElement();
                    writer.WriteStartElement("sheetData");WriteRow(writer,headers,1,true);for(var i=0;i<rows.Count;i++)WriteRow(writer,rows[i],i+2,false);writer.WriteEndElement();
                    writer.WriteStartElement("autoFilter");writer.WriteAttributeString("ref","A1:"+ColumnName(headers.Count)+(rows.Count+1));writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndDocument();
                }
            }
        }
        private static void WriteRow(XmlWriter writer,IReadOnlyList<string> values,int index,bool header)
        {
            writer.WriteStartElement("row");writer.WriteAttributeString("r",index.ToString());
            for(var i=0;i<values.Count;i++)
            {
                writer.WriteStartElement("c");writer.WriteAttributeString("r",ColumnName(i+1)+index);writer.WriteAttributeString("s",header?"1":"0");writer.WriteAttributeString("t","inlineStr");writer.WriteStartElement("is");writer.WriteStartElement("t");writer.WriteAttributeString("xml","space",null,"preserve");writer.WriteString(values[i]??string.Empty);writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        private static string ColumnName(int index){var text="";while(index>0){index--;text=(char)('A'+index%26)+text;index/=26;}return text;}
        private static void Add(ZipArchive archive,string name,string value){var entry=archive.CreateEntry(name);using(var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false)))writer.Write(value);}
    }
}
