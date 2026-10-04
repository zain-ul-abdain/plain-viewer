using System.IO;
using System.Text;
using System.Text.Json;
using PlainViewer.Core;

Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
// Parent assigns a Job Object before releasing this handshake.
if (Console.ReadLine() != "START") return 2;
// A password the user typed for this file may follow, base64-encoded, on the next line (never on the command line).
if (Console.ReadLine() is { } line && line.StartsWith("PASSWORD ", StringComparison.Ordinal))
    try { OfficeEncryption.Password = Encoding.UTF8.GetString(Convert.FromBase64String(line[9..])); } catch (FormatException) { }
WorkerResponse response;
try
{
    // Before touching the document: from here on this process cannot write the user's files or change other programs.
    // Output for Word/PowerPoint preparation goes under LowIntegrity.Root, which allows low-integrity writes.
    try { LowIntegrity.LowerCurrentProcess(); }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    { throw new DocumentException("The document worker could not start in its restricted mode, so the file was not opened."); }
    if (args.Length is < 1 or > 4) throw new DocumentException("Choose a file to open.");
    string extension = Path.GetExtension(args[0]).ToLowerInvariant();
    // "<file> --prepare-office <output>": validate a document for LibreOffice and write a sanitised copy for conversion.
    // "<file> <encoding> <delimiter> <work folder>": large rows go to a RowStore in the work folder.
    var document = args.ElementAtOrDefault(1) == "--prepare-office" ? (ConvertedDocuments.Handles(args[0]) ? ConvertedDocuments.Prepare(args[0], args[2]) : OfficePackages.Prepare(args[0], args[2]))
        : ImageFiles.IsImage(args[0]) && args.Length == 4 ? PlainViewer.Worker.HeifPictures.Load(args[0], args[3])
        : WebDocuments.Handles(args[0]) && args.Length == 4 ? WebDocuments.Load(args[0], args[3])
        : LegacySpreadsheets.Handles(args[0]) ? LegacySpreadsheets.Load(args[0], storeFolder: args.ElementAtOrDefault(3))
        : Spreadsheets.IsWorkbook(args[0]) || extension is ".xlsb" ? Spreadsheets.Load(args[0], storeFolder: args.ElementAtOrDefault(3))
        : TextFiles.Load(args[0], args.ElementAtOrDefault(1) ?? "Auto", args.ElementAtOrDefault(2) ?? "Auto", args.ElementAtOrDefault(3));
    response = new WorkerResponse(document, null);
}
catch (PasswordException ex) { response = new(null, ex.Message, ex.Incorrect ? "incorrect" : "required"); }
catch (DocumentException ex) { response = new(null, ex.Message); }
catch (InvalidDataException) { response = new(null, "This file is damaged or incomplete, so it cannot be shown. Try another copy of the file."); }
catch (System.Xml.XmlException) { response = new(null, "This file is damaged or incomplete, so it cannot be shown. Try another copy of the file."); }
catch (OutOfMemoryException) { response = new(null, "This file needs more memory than the viewer allows for one document."); }
catch (DecoderFallbackException) { response = new(null, "The text encoding could not be read. Choose a different encoding and try again."); }
catch (UnauthorizedAccessException) { response = new(null, "The file cannot be read. Check its permissions or copy it to a local folder."); }
catch (FileNotFoundException) { response = new(null, "The file was moved or deleted. Choose it again from its current location."); }
catch (IOException ex) when (DiskSpace.IsFull(ex)) { response = new(null, DiskSpace.Message); }
catch (IOException) { response = new(null, "The file could not be read. It may be locked, moved, or damaged. Close other applications and try again."); }
catch (Exception) { response = new(null, "The file could not be displayed. Try another file or report this problem."); }
Console.Write(JsonSerializer.Serialize(response));
return response.Error is null ? 0 : 1;
