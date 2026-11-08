using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace BindIPManager.Services;

/// <summary>The notification icon controls only the paid adapter's independent global guard.</summary>
public sealed class TrayController : IDisposable {
 private readonly Views.MainWindow _window;
 private readonly Forms.NotifyIcon _icon;
 private readonly Forms.ContextMenuStrip _menu;
 private readonly Forms.ToolStripMenuItem _state;
 private readonly Forms.ToolStripMenuItem _allow;
 private readonly Forms.ToolStripMenuItem _block;
 private readonly Forms.ToolStripMenuItem _install;
 private readonly DispatcherTimer _timer;
 private readonly SemaphoreSlim _operation=new(1,1);
 private readonly Icon _lockedIcon=CreateIcon(Color.FromArgb(226,90,93));
 private readonly Icon _allowedIcon=CreateIcon(Color.FromArgb(52,211,153));
 private readonly Icon _unknownIcon=CreateIcon(Color.FromArgb(148,163,184));
 private bool _showTray=true,_closeToTray=true,_minimizeToTray=true,_autoStatus=true;
 private bool _disposed;
 private bool _exiting;
 private bool _busy;
 public event EventHandler? Changed;
 public PaidNetworkStatus? Status {get;private set;}
 public string Error {get;private set;}="";
 public bool IsBusy=>_busy;
 public bool IsExiting=>_exiting;
 public string StatusText=>_busy?"正在處理有線網路保護":Error.Length>0?"狀態無法確認":Status==null?"正在讀取狀態":!Status.Installed?"尚未啟用開機保護":!Status.ServiceRunning?"保護服務未執行，請重新啟用保護":Status.Locked?"已禁止 計費網卡":"已允許 計費網卡 本次開機";
 public string DetailText=>(!_autoStatus?"狀態自動更新已關閉，以下為上次讀取結果。 ":"")+(Error.Length>0?Error:Status==null?"":!Status.Installed?"啟用後，每次重新開機均預設禁止使用計費網卡。":$"保護網卡 {Status.AdapterName} ({Status.Guid})  每次重新開機預設禁止");
 public bool CanAllow=>!_busy&&Error.Length==0&&Status is {Installed:true,ServiceRunning:true,Locked:true};
 public bool CanBlock=>!_busy&&Status is {Installed:true}&&(!Status.Locked||!Status.ServiceRunning||Error.Length>0);
 public bool CanInstall=>!_busy&&(Error.Length>0||Status is {Installed:false} or {ServiceRunning:false});

 public TrayController(Views.MainWindow window){
  _window=window;
  _menu=new Forms.ContextMenuStrip();
  _state=new Forms.ToolStripMenuItem("正在讀取保護狀態"){Enabled=false};_menu.Items.Add(_state);_menu.Items.Add(new Forms.ToolStripSeparator());
  _allow=new Forms.ToolStripMenuItem("允許 計費網卡 本次開機",null,(_,_)=>Dispatch(()=>Change("allow")));_menu.Items.Add(_allow);
  _block=new Forms.ToolStripMenuItem("禁止 計費網卡",null,(_,_)=>Dispatch(()=>Change("block")));_menu.Items.Add(_block);
  _install=new Forms.ToolStripMenuItem("啟用開機保護",null,(_,_)=>Dispatch(async()=>{if(Status is {Installed:true,Guid.Length:>0} status)await Install(status.Guid);else ShowSettings();}));_menu.Items.Add(_install);
  _menu.Items.Add(new Forms.ToolStripSeparator());
  _menu.Items.Add(new Forms.ToolStripMenuItem("開啟設定",null,(_,_)=>Dispatch(()=>{ShowSettings();return Task.CompletedTask;})));
  _menu.Items.Add(new Forms.ToolStripMenuItem("結束圖示 不解除保護",null,(_,_)=>Dispatch(()=>{Exit();return Task.CompletedTask;})));
  _icon=new Forms.NotifyIcon{Icon=_unknownIcon,Text="計費網卡保護 正在讀取狀態",ContextMenuStrip=_menu,Visible=true};
  _icon.MouseClick+=(_,e)=>{if(e.Button==Forms.MouseButtons.Left)_menu.Show(Forms.Cursor.Position);};
  _icon.DoubleClick+=(_,_)=>Dispatch(()=>{_menu.Close();ShowSettings();return Task.CompletedTask;});
  _window.Closing+=OnClosing;
  _window.StateChanged+=(_,_)=>{if(ShouldHide(_showTray,_minimizeToTray)&&_window.WindowState==System.Windows.WindowState.Minimized)_window.Hide();};
  _timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};_timer.Tick+=async(_,_)=>await Refresh();
  UpdatePresentation();
 }
 public void Start(){_ = Refresh();}
 private void Dispatch(Func<Task> action)=>_window.Dispatcher.BeginInvoke(new Action(async()=>{if(!_disposed)await action();}));
 private void OnClosing(object? sender,CancelEventArgs e){if(_exiting)return;if(ShouldHide(_showTray,_closeToTray)){e.Cancel=true;_window.Hide();}else{_exiting=true;System.Windows.Application.Current.Shutdown();}}
 internal static bool ShouldHide(bool showTray,bool hidePreference)=>showTray&&hidePreference;
 public void ApplyPreferences(bool showTray,bool closeToTray,bool minimizeToTray,bool autoStatus,int pollSeconds){_showTray=showTray;_closeToTray=closeToTray;_minimizeToTray=minimizeToTray;_autoStatus=autoStatus;_icon.Visible=showTray;_timer.Interval=TimeSpan.FromSeconds(Math.Clamp(pollSeconds,1,30));if(autoStatus)_timer.Start();else _timer.Stop();if(!showTray&&!_window.IsVisible)ShowSettings();UpdatePresentation();}
 public void ShowSettings(){if(_disposed)return;_window.Show();_window.WindowState=System.Windows.WindowState.Normal;_window.Activate();}
 public void HideSettings(){if(_showTray)_window.Hide();else _window.WindowState=System.Windows.WindowState.Minimized;}
 public void Exit(){_exiting=true;System.Windows.Application.Current.Shutdown();}
 public async Task Refresh(){
  if(_disposed||_busy||!await _operation.WaitAsync(0))return;
  try {Status=await PaidNetworkClient.Call("status");Error="";}
  catch(Exception failure){Error=failure.Message;}
  finally{_operation.Release();if(!_disposed)UpdatePresentation();}
 }
 public Task Change(string action)=>Execute(async()=>{
  var result=await PaidNetworkClient.Call(action);
  if(!result.Installed||(action=="allow"&&!result.ServiceRunning)||result.Locked!=(action=="block"))throw new InvalidOperationException("保護工具回傳的結果與操作不一致，請重新讀取狀態。");
  Status=result;
 });
 public Task Install(string guid)=>Execute(async()=>{await PaidNetworkClient.Install(guid);var result=await PaidNetworkClient.Call("status");
  if(!result.Installed||!result.ServiceRunning)throw new InvalidOperationException("尚未確認開機保護服務已正常啟動，請查看錯誤記錄。");Status=result;});
 public Task Uninstall()=>Execute(async()=>{await PaidNetworkClient.Uninstall();var result=await PaidNetworkClient.Call("status");if(result.Installed)throw new InvalidOperationException("尚未確認保護已移除。");Status=result;});
 private async Task Execute(Func<Task> operation){
  if(_disposed||_busy)return;
  _busy=true;UpdatePresentation();
  var acquired=false;
  try {
   // Polling may already be reading status when the user clicks. Queue the click
   // behind that read instead of silently dropping the requested operation.
   acquired=await _operation.WaitAsync(TimeSpan.FromSeconds(20));
   if(!acquired)throw new IOException("正在等待保護狀態讀取，操作尚未執行，請稍後再試。");
   if(_disposed)return;
   Error="";await operation();
  }
  catch(Exception failure){Error=failure.Message;RouterBridge.LogError(failure,"計費網卡總開關");
   System.Windows.MessageBox.Show(failure.Message,"計費網卡保護",System.Windows.MessageBoxButton.OK,System.Windows.MessageBoxImage.Error);}
  finally{_busy=false;if(acquired)_operation.Release();if(!_disposed)UpdatePresentation();}
 }
 private void UpdatePresentation(){
  _state.Text=StatusText;_allow.Enabled=CanAllow;_block.Enabled=CanBlock;_install.Enabled=CanInstall;
  _install.Visible=Status is not {Installed:true,ServiceRunning:true}||Error.Length>0;
  _icon.Icon=Error.Length>0||Status is not {Installed:true,ServiceRunning:true}?_unknownIcon:Status.Locked?_lockedIcon:_allowedIcon;
  var tooltip="計費網卡保護 "+StatusText;_icon.Text=tooltip[..Math.Min(63,tooltip.Length)];
  Changed?.Invoke(this,EventArgs.Empty);
 }
 [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr handle);
 private static Icon CreateIcon(Color color){
  using var bitmap=new Bitmap(32,32);using var graphics=Graphics.FromImage(bitmap);
  graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
  using var fill=new SolidBrush(color);graphics.FillEllipse(fill,2,2,28,28);
  using var pen=new Pen(Color.White,2.5f);graphics.DrawRectangle(pen,9,9,14,10);graphics.DrawLine(pen,16,19,16,24);graphics.DrawLine(pen,11,24,21,24);
  var handle=bitmap.GetHicon();try{using var icon=Icon.FromHandle(handle);return (Icon)icon.Clone();}finally{DestroyIcon(handle);}
 }
 public void Dispose(){if(_disposed)return;_disposed=true;_timer.Stop();_window.Closing-=OnClosing;
  _icon.Visible=false;_icon.Dispose();_menu.Dispose();_lockedIcon.Dispose();_allowedIcon.Dispose();_unknownIcon.Dispose();}
}
