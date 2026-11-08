using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
namespace BindIPManager.Services;
public sealed class ProgramEntry {
 public int Id {get;set;} public string Name {get;set;}=""; public string[] Paths {get;set;}=[]; public string Pattern {get;set;}="";
 public string ProgramName => Paths.Length>1 ? $"{Paths.Length} 個程式位置" : Paths.FirstOrDefault() is string p ? Path.GetFileName(p) : "程式群組";
 public string AdapterName {get;set;}="尚未設定規則";
}
public sealed class SavedRule { public bool Enabled {get;set;}=true; public int Id {get;set;} public string Mode {get;set;}="Allow"; public int[] Networks {get;set;}=[]; public int[] Priority {get;set;}=[]; }
 public sealed class RouterState { public Dictionary<string,JsonElement> Options {get;set;}=new(StringComparer.OrdinalIgnoreCase); public int Revision {get;set;} public bool Enabled {get;set;} public ProgramEntry[] Apps {get;set;}=[]; public SavedRule[] Rules {get;set;}=[]; public NetworkCatalogEntry[] NetworkCatalog {get;set;}=[]; }
 public sealed class NetworkCatalogEntry { public int Id {get;set;} public string Name {get;set;}=""; public string Guid {get;set;}=""; public string Alias {get;set;}=""; public string Description {get;set;}=""; public bool Present {get;set;} public bool Up4 {get;set;} public bool Up6 {get;set;} }
public sealed class AdapterSnapshot { public int Id {get;set;} public string Name {get;set;}=""; public string Alias {get;set;}=""; public bool Up4 {get;set;} public bool Up6 {get;set;} }
public sealed class ReportRow { public int Id {get;set;} public string IPv4 {get;set;}=""; public string IPv6 {get;set;}=""; public string Reason4 {get;set;}=""; public string Reason6 {get;set;}=""; public string Code4 {get;set;}=""; public string Code6 {get;set;}=""; }
public sealed class AppPathHealth {public string Path {get;set;}="";public bool Exists {get;set;}}
public sealed class AppHealth {public int Id {get;set;} public bool ExactOnly {get;set;} public string Scope {get;set;}=""; public AppPathHealth[] Paths {get;set;}=[];}
public sealed class DnsReport {public string Route {get;set;}="";public string Reason {get;set;}="";public string Code {get;set;}="";}
public sealed class RouterStatus { public int Revision {get;set;} public string Phase {get;set;}=""; public string Message {get;set;}=""; [JsonConverter(typeof(StatusTimeConverter))] public DateTime Time {get;set;} public ReportRow[] Rows {get;set;}=[]; public DnsReport? Dns {get;set;} }
public sealed class StatusTimeConverter : JsonConverter<DateTime> {
 public override DateTime Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options) {
  if(reader.TokenType==JsonTokenType.String) {
   var value=reader.GetString();
   if(DateTime.TryParseExact(value,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var legacy))return legacy;
   if(DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var parsed))return parsed.Kind==DateTimeKind.Utc?parsed.ToLocalTime():parsed;
  } else if(reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)reader.Skip();
  return DateTime.MinValue;
 }
 public override void Write(Utf8JsonWriter writer,DateTime value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString("o",CultureInfo.InvariantCulture));
}
 public sealed class ApplicationEntry { public string Name {get;set;}=""; public string Path {get;set;}=""; public string Source {get;set;}=""; public bool Available {get;set;}=true; public bool CanSelect=>Available&&!string.IsNullOrWhiteSpace(Path); }
public sealed class BridgeResponse {
 public AppHealth[] AppHealth {get;set;}=[]; public bool Ok {get;set;} public string Message {get;set;}=""; public RouterState? State {get;set;}
  public AdapterSnapshot[] Snapshot {get;set;}=[]; public RouterStatus? Status {get;set;} public ApplicationEntry[] Applications {get;set;}=[]; public NetworkCatalogEntry[] NetworkCatalog {get;set;}=[];
}
public sealed class RouterOperationException(BridgeResponse response) : IOException(response.Message) { public BridgeResponse Response {get;}=response; }
public static class RouterBridge {
 public static readonly string DataRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UnifiedNetworkRouter");
 public static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
  public static int ReadStateRevision(string json) {
   using var document=JsonDocument.Parse(json);
   foreach(var property in document.RootElement.EnumerateObject())if(property.Name.Equals("Revision",StringComparison.OrdinalIgnoreCase))return property.Value.GetInt32();
   throw new JsonException("設定檔缺少版本編號，請重新整理或使用恢復網路。");
  }
  public static bool LogError(Exception error,string operation) {
   try { Directory.CreateDirectory(DataRoot);File.AppendAllText(Path.Combine(DataRoot,"gui-errors.log"),DateTime.Now.ToString("o",CultureInfo.InvariantCulture)+" "+operation+Environment.NewLine+error+Environment.NewLine,new UTF8Encoding(false));return true; } catch {return false;}
  }
 public static async Task<BridgeResponse> CallAsync(object request) {
  var dir=Path.Combine(DataRoot,"gui-requests");Directory.CreateDirectory(dir);
  var token=Guid.NewGuid().ToString("N"); var input=Path.Combine(dir,token+".in.json");var output=Path.Combine(dir,token+".out.json");
  try {
   await File.WriteAllTextAsync(input,JsonSerializer.Serialize(request),new UTF8Encoding(false));
   var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")) {
    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory
   };
   foreach(var arg in new[]{"-NoProfile","-STA","-ExecutionPolicy","Bypass","-File",Path.Combine(AppContext.BaseDirectory,"GuiBridge.ps1"),"-InputPath",input,"-OutputPath",output}) info.ArgumentList.Add(arg);
   using var process=Process.Start(info)??throw new IOException("無法啟動設定服務。");
   var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
   using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));
   try { await process.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { try{process.Kill();}catch{} throw new IOException("操作尚未完成，請重新整理查看狀態。仍可使用「恢復網路」。"); }
   if(!File.Exists(output)) throw new IOException("設定服務未回傳結果："+await stderr);
   var response=JsonSerializer.Deserialize<BridgeResponse>(await File.ReadAllTextAsync(output),JsonOptions)??throw new IOException("設定服務回傳空白結果。");
   if(!response.Ok) throw new RouterOperationException(response);
   return response;
  } finally { foreach(var file in new[]{input,output})try{File.Delete(file);}catch{} }
 }
  public static string NetworkName(int id,IEnumerable<NetworkCatalogEntry>? catalog=null) {
   var network=catalog?.FirstOrDefault(n=>n.Id==id);
   if(network!=null)return !string.IsNullOrWhiteSpace(network.Name)?network.Name:!string.IsNullOrWhiteSpace(network.Alias)?network.Alias:!string.IsNullOrWhiteSpace(network.Description)?network.Description:$"網路 {id}";
   return $"網路 {id}";
  }
 public static string TranslateNetwork(string text)=>text switch{"WiFi"=>"Wi-Fi","iPhone 有線"=>"iPhone 有線","100 有線"=>"100 有線","斷線"=>"斷線",_=>text};
  public static string AllowedText(SavedRule rule,IEnumerable<NetworkCatalogEntry>? catalog=null) {
   var networks=catalog?.ToArray()??[];
   var ids=rule.Mode=="Allow"?rule.Priority.Concat(rule.Networks).Where(rule.Networks.Contains).Distinct().ToArray():networks.Select(n=>n.Id).Except(rule.Networks).ToArray();
   var names=ids.Select(id=>NetworkName(id,networks)).ToList();if(rule.Mode=="Deny")names.Add("其他未列網路");
  return names.Count==0?"全部封鎖並斷線":string.Join("  /  ",names);
 }
}
