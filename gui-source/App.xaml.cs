using System.IO;
using System.Windows;
namespace BindIPManager;
public partial class App : System.Windows.Application {
 public static bool IsOfflineMode {get;private set;}
 private Mutex? _instance;
 private EventWaitHandle? _showWindow;
 private readonly ManualResetEvent _closing=new(false);
 private Services.TrayController? _tray;
 private bool _ownsInstance;
 protected override void OnStartup(StartupEventArgs e) {
  IsOfflineMode=e.Args.Any(arg=>arg.StartsWith("--check",StringComparison.OrdinalIgnoreCase)||arg=="--render-preview"||arg=="--render-options"||arg=="--render-improvements");
  ShutdownMode=ShutdownMode.OnExplicitShutdown;
  base.OnStartup(e);
  DispatcherUnhandledException += (_, failure) => {
   try { Directory.CreateDirectory(Services.RouterBridge.DataRoot); File.AppendAllText(Path.Combine(Services.RouterBridge.DataRoot,"gui-errors.log"), DateTime.Now+" "+failure.Exception+Environment.NewLine); } catch {}
   if(IsOfflineMode){Shutdown(1);failure.Handled=true;return;}
   MessageBox.Show(failure.Exception.Message+"\n\n可在主視窗選單使用「恢復網路」。","三網路規則管理",MessageBoxButton.OK,MessageBoxImage.Error);
   failure.Handled=true;
  };
  if(e.Args.Contains("--check-improvements")){Views.MainWindow.CheckImprovements();Shutdown();return;}
  if(e.Args.Contains("--render-improvements")){Views.MainWindow.RenderImprovements();Shutdown();return;}
  if(e.Args.Contains("--check-options")){Views.MainWindow.CheckOptions();Shutdown();return;}
  if(e.Args.Contains("--render-options")){Views.MainWindow.RenderOptions();Shutdown();return;}
  if(e.Args.Contains("--render-preview")) { Views.MainWindow.RenderPreview(); Shutdown(); return; }
  if(e.Args.Contains("--check-interface")) { Views.MainWindow.CheckInterface(); Shutdown(); return; }
  if(e.Args.Contains("--check-actual-state")) { Views.MainWindow.CheckActualState(); Shutdown(); return; }
  if(IsOfflineMode){Shutdown(1);return;}
  var scope=System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value??Environment.UserName;
  _showWindow=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\UnifiedNetworkRouter.Show."+scope);
  _instance=new Mutex(false,@"Local\UnifiedNetworkRouter.UI."+scope);
  // A normal instance may be closing just after starting its elevated replacement.
  try {_ownsInstance=_instance.WaitOne(TimeSpan.FromSeconds(1));}catch(AbandonedMutexException){_ownsInstance=true;}
  if(!_ownsInstance){
   _showWindow.Set();
   Shutdown();return;
  }
  var window=new Views.MainWindow(); MainWindow=window;
  _tray=new Services.TrayController(window);window.AttachPaidProtection(_tray);
  _=Task.Run(()=>{while(WaitHandle.WaitAny([_closing,_showWindow])==1)Dispatcher.BeginInvoke(new Action(()=>_tray?.ShowSettings()));});
  _tray.Start();
  window.Loaded+=async (_,_)=>{if(window.DataContext is ViewModels.MainViewModel vm){await vm.Refresh();if(e.Args.Contains("--tray")&&vm.Option("StartHidden",true)&&vm.Option("ShowTray",true))_tray?.HideSettings();}};
  window.Show();
 }
 protected override void OnExit(ExitEventArgs e){
  _closing.Set();_tray?.Dispose();
  if(_ownsInstance){_instance?.ReleaseMutex();_ownsInstance=false;}
  _instance?.Dispose();base.OnExit(e);
 }
}
