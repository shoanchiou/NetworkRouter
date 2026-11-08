using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace BindIPManager;

internal static class EntryPoint {
 [STAThread]
 public static int Main(string[] args) {
  try {
   if(NeedsElevation(args,ViewModels.MainViewModel.IsElevated)) {
    var info=new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("無法取得程式位置。")){UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
    foreach(var arg in args)info.ArgumentList.Add(arg);
    if(!TryElevatedLaunch(()=>Process.Start(info))) {
     MessageBox.Show("已取消管理員權限要求，網路分流管理未啟動，也未變更網路規則。","Network Router 網路分流管理",MessageBoxButton.OK,MessageBoxImage.Information);
     return 1;
    }
    return 0;
   }
   var app=new App();
   app.InitializeComponent();
   return app.Run();
  } catch(Exception error) {
   string? log=WriteStartupError(error);
   bool offline=IsOffline(args);
   if(!offline)MessageBox.Show("程式啟動失敗："+error.Message+(log==null?"\n無法保存錯誤記錄。":"\n錯誤記錄："+log),"三網路規則管理",MessageBoxButton.OK,MessageBoxImage.Error);
   return 1;
  }
 }
 internal static bool TryElevatedLaunch(Func<Process?> start) {
  try {using var process=start() ?? throw new InvalidOperationException("無法啟動管理員模式。");return true;}
  catch(Win32Exception error) when(error.NativeErrorCode==1223) {return false;}
 }
 internal static bool IsOffline(IEnumerable<string> args)=>args.Any(arg=>arg.StartsWith("--check",StringComparison.OrdinalIgnoreCase)||arg.StartsWith("--render",StringComparison.OrdinalIgnoreCase));
 internal static bool NeedsElevation(IEnumerable<string> args,bool elevated)=>!IsOffline(args)&&!elevated;
 internal static string? WriteStartupError(Exception error) {
  foreach(var root in new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UnifiedNetworkRouter"),Path.GetTempPath()}) {
   try {Directory.CreateDirectory(root);var path=Path.Combine(root,"gui-startup-errors.log");File.AppendAllText(path,DateTime.Now+" "+error+Environment.NewLine);return path;}catch {}
  }
  return null;
 }
}
