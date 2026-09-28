using System;
using System.IO;
using BuildAI.AccIssueReturn.Core;
namespace BuildAI.AccIssueReturn.Core;
public interface IDiagnosticExportService { void Export(string path, AccIssue issue); }
public sealed class DiagnosticExportService : IDiagnosticExportService
{
 public void Export(string path,AccIssue issue){if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("Export path is empty.");if(issue==null)throw new ArgumentNullException(nameof(issue));var json=CoordinateDiagnosticExporter.CreateSanitizedJson(issue);var directory=Path.GetDirectoryName(Path.GetFullPath(path));if(!string.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);File.WriteAllText(path,json,System.Text.Encoding.UTF8);}
}