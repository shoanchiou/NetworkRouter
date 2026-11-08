using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BindIPManager.Services;

public sealed class PaidNetworkStatus {
 public bool Installed {get;set;}
 public bool Locked {get;set;}
 public bool ServiceRunning {get;set;}
 public string AdapterName {get;set;}="未選擇網卡";
 public string Guid {get;set;}="";
 public string Error {get;set;}="";
}

public static class PaidNetworkClient {
 private static async Task<(int Code,string Output,string Error)> Run(ProcessStartInfo info,TimeSpan timeout){
  info.UseShellExecute=false;info.CreateNoWindow=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
  info.StandardOutputEncoding=System.Text.Encoding.UTF8;info.StandardErrorEncoding=System.Text.Encoding.UTF8;
  info.WorkingDirectory=AppContext.BaseDirectory;
  using var process=Process.Start(info)??throw new IOException("無法啟動有線網路保護工具。");
  var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
  using var timer=new CancellationTokenSource(timeout);
  try {await process.WaitForExitAsync(timer.Token);}catch(OperationCanceledException){
   try {process.Kill(entireProcessTree:true);}catch{}
   throw new IOException("有線網路保護操作逾時，請重新讀取狀態。尚未確認操作是否完成。");
  }
  return (process.ExitCode,await output,await error);
 }
 public static async Task<PaidNetworkStatus> Call(string action){
  if(App.IsOfflineMode)throw new InvalidOperationException("離線檢查不能呼叫有線網路保護工具。");
  if(action is not ("status" or "allow" or "block"))throw new ArgumentException("不支援的保護操作。",nameof(action));
  var executable=Path.Combine(AppContext.BaseDirectory,"paid-network-guard.exe");
  if(!File.Exists(executable))throw new FileNotFoundException("找不到有線網路保護工具，請重新解壓完整工具包。",executable);
  var info=new ProcessStartInfo(executable);info.ArgumentList.Add(action);
  var result=await Run(info,TimeSpan.FromSeconds(action=="status"?12:45));
  return ParseStatus(result.Output,result.Code,result.Error);
 }
 internal static PaidNetworkStatus ParseStatus(string json,int exitCode,string stderr){
  PaidNetworkStatus? status;
  try {
   using var document=JsonDocument.Parse(json);
   if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException("保護狀態不是物件。");
   foreach(var required in new[]{"Installed","Locked","ServiceRunning"})
    if(!document.RootElement.TryGetProperty(required,out var value)||value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
     throw new JsonException("缺少保護狀態欄位 "+required);
   status=JsonSerializer.Deserialize<PaidNetworkStatus>(json,RouterBridge.JsonOptions);
  }catch(JsonException failure){
   throw new IOException("有線網路保護工具回傳的狀態無法讀取。"+Environment.NewLine+stderr,failure);
  }
  if(exitCode!=0||status==null||!string.IsNullOrWhiteSpace(status.Error))
   throw new IOException("有線網路保護操作未完成。"+Environment.NewLine+(status?.Error is {Length:>0} message?message:stderr));
  return status;
 }
 public static async Task Install(string guid){
  if(!Guid.TryParse(guid,out _))throw new ArgumentException("請先選擇有效的保護網卡。",nameof(guid));
  if(App.IsOfflineMode)throw new InvalidOperationException("離線檢查不能安裝有線網路保護。");
  var script=Path.Combine(AppContext.BaseDirectory,"Install-Paid-Protection.ps1");
  if(!File.Exists(script))throw new FileNotFoundException("找不到開機保護安裝工具，請重新解壓完整工具包。",script);
  var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"));
  foreach(var argument in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-NoLaunch"})info.ArgumentList.Add(argument);
  info.ArgumentList.Add("-Guid");info.ArgumentList.Add(guid);
  var result=await Run(info,TimeSpan.FromSeconds(90));
  if(result.Code!=0)throw new IOException("開機保護安裝未完成。"+Environment.NewLine+result.Output+Environment.NewLine+result.Error);
 }
 public static async Task Uninstall(){
  if(App.IsOfflineMode)throw new InvalidOperationException("離線檢查不能移除保護。");
  var script=Path.Combine(AppContext.BaseDirectory,"Remove-Paid-Protection.ps1");
  var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"));
  foreach(var argument in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script})info.ArgumentList.Add(argument);
  var result=await Run(info,TimeSpan.FromSeconds(90));if(result.Code!=0)throw new IOException("保護移除未完成。"+result.Output+result.Error);
 }
}
