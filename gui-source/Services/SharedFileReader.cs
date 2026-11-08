using System.IO;
using System.Text;

namespace BindIPManager.Services;

internal static class SharedFileReader {
 public static string ReadAllText(string path) {
  using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
  using var reader=new StreamReader(stream,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
  return reader.ReadToEnd();
 }
}
