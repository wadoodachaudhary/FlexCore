using Microsoft.AspNetCore.Components.Forms;
namespace Fx.ControlKit;
public enum UploadStatus { Queued, Uploading, Succeeded, Failed, Cancelled }
public sealed class UploadFile
{
 public string Id {get;}=Guid.NewGuid().ToString("N");
 public required string Name {get;init;}
 public long Size {get;init;}
 public string ContentType {get;init;}="application/octet-stream";
 public UploadStatus Status {get;internal set;}
 public long UploadedBytes {get;internal set;}
 public string? Error {get;internal set;}
 internal Guid BrowserBatch {get;init;}
 internal IBrowserFile? BrowserFile {get;init;}
 internal CancellationTokenSource? Cancellation {get;set;}
}
public sealed record UploadChunk(string FileId,string FileName,string ContentType,long TotalSize,long Offset,ReadOnlyMemory<byte> Bytes,bool IsLast);
public sealed record UploadProgress(UploadFile File,long UploadedBytes,long TotalBytes);
public static class UploadValidation
{
 public static string? Validate(string name,long size,long maxSize,IReadOnlyCollection<string> allowedExtensions)
 {
  if(size<0||size>maxSize)return $"File exceeds the {maxSize:N0} byte limit.";
  if(string.IsNullOrWhiteSpace(name))return "A file name is required.";
  if(allowedExtensions.Count>0&&!allowedExtensions.Any(e=>string.Equals(e.StartsWith('.')?e:"."+e,System.IO.Path.GetExtension(name),StringComparison.OrdinalIgnoreCase)))return "This file extension is not allowed.";
  return null;
 }
}
