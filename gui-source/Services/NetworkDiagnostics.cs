using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BindIPManager.Services;

public sealed record DiagnosticStep(string Name, string Status, string Detail);
public sealed record DiagnosticReport(string AdapterName, string AdapterGuid, IReadOnlyList<DiagnosticStep> Steps);

/// <summary>Explicit, on-demand adapter checks through an isolated, non-TUN child core.</summary>
public static class NetworkDiagnostics {
 private const string StunHost="stun.cloudflare.com";
 private static readonly TimeSpan Timeout=TimeSpan.FromSeconds(8);
 public static async Task<DiagnosticReport> RunAsync(NetworkCatalogEntry adapter,IReadOnlyDictionary<string,JsonElement> options,IProgress<DiagnosticStep>? progress,CancellationToken token) {
  ArgumentNullException.ThrowIfNull(adapter); ArgumentNullException.ThrowIfNull(options);
  var steps=new List<DiagnosticStep>();
  void Emit(string name,string status,string detail){var step=new DiagnosticStep(name,status,detail);if(status!="進行中")steps.Add(step);progress?.Report(step);}
  var guid=ParseGuid(adapter.Guid); var nic=FindNic(guid); string alias=nic.Name;
  var addresses=nic.GetIPProperties().UnicastAddresses.Select(x=>x.Address).ToArray(); var (has4,has6)=AvailableFamilies(addresses);
  if(!has4&&!has6)throw new InvalidOperationException("選定網卡沒有可用的 IPv4 或 IPv6 位址。");
  string root=Path.Combine(Path.GetTempPath(),"router-diagnostics-"+Guid.NewGuid().ToString("N"));
  using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token); token=lifetime.Token;
  Process? child=null; Exception? primary=null; Task? watcher=null; bool nicLost=false;
  try {
   Directory.CreateDirectory(root);
   int socks=ReservePort(); int dns=ReservePort(); while(dns==socks)dns=ReservePort();
   string core=Path.Combine(AppContext.BaseDirectory,"core","mihomo.exe");
   if(!File.Exists(core))throw new FileNotFoundException("找不到內附診斷核心。",core);
   var servers=GetDnsServers(options,has4,has6);
   var config=BuildCoreConfig(alias,socks,dns,has6,servers);
   string path=Path.Combine(root,"diagnostic.json");await File.WriteAllTextAsync(path,JsonSerializer.Serialize(config),new UTF8Encoding(false),token);
   EnsureSameNic(guid,alias);
   var start=new ProcessStartInfo(core){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true};
   start.ArgumentList.Add("-d");start.ArgumentList.Add(root);start.ArgumentList.Add("-f");start.ArgumentList.Add(path);
   child=Process.Start(start)??throw new IOException("無法啟動私人診斷核心。");
   _=child.StandardOutput.ReadToEndAsync();_=child.StandardError.ReadToEndAsync();
   var watchedChild=child;
   watcher=Task.Run(async()=>{try{while(true){await Task.Delay(100,lifetime.Token);try{EnsureSameNic(guid,alias);}catch{nicLost=true;lifetime.Cancel();try{if(!watchedChild.HasExited)watchedChild.Kill(entireProcessTree:true);}catch(Exception){}return;}}}catch(OperationCanceledException){} });
   Emit("準備","進行中","啟動私人核心（不建立 TUN、不改系統路由）。");
   await WaitReady(child,socks,token);EnsureSameNic(guid,alias);Emit("準備","成功",$"已綁定 {adapter.Name}（{alias}），GUID {guid}。");
   IPAddress? stunIp=null;
   EnsureSameNic(guid,alias);
   if(servers.Count==0) Emit("DNS","略過","DNS 已停用或沒有與網卡位址家族相符的上游；不使用系統 DNS。");
   else {
    Emit("DNS","進行中",$"透過指定網卡查詢 {StunHost}。");
    try { stunIp=await WithTimeout(ct=>ResolveDns(dns,StunHost,has4,has6,ct),token);EnsureSameNic(guid,alias);Emit("DNS","成功",$"{StunHost} → {stunIp}；已核對 DNS 回應。"); }
    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
    catch(Exception ex){EnsureSameNic(guid,alias);Emit("DNS","失敗",$"DNS 查詢未完成：{ex.Message}");}
   }
   EnsureSameNic(guid,alias);
   Emit("TCP","進行中",$"透過指定網卡與 {(has4?"1.1.1.1":"2606:4700:4700::1111")}:443 建立 TLS，讀取 HTTPS 回應。");
   try { var status=await WithTimeout(ct=>TestHttps(socks,has4,ct),token);EnsureSameNic(guid,alias);Emit("TCP","成功",$"TCP/TLS 傳輸成功；HTTP 狀態 {status}"+(status>=200&&status<300?"。":"（HTTP 回應異常）。")); }
   catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
   catch(Exception ex){EnsureSameNic(guid,alias);Emit("TCP","失敗",$"TCP/TLS 測試未完成：{ex.Message}");}
   EnsureSameNic(guid,alias);
   if(stunIp is not null&&!IsBindable(stunIp)){Emit("UDP","略過","DNS 回傳的 STUN 位址不允許安全綁定指定網卡。");stunIp=null;}
   else if(stunIp is null)Emit("UDP","略過","DNS 失敗，未解析 STUN 端點；不能據此判定 UDP 被封鎖。");
   else {Emit("UDP","進行中",$"透過 SOCKS5 UDP 測試 {StunHost}:3478（{stunIp}）。");
    try {await WithTimeout(ct=>TestStun(socks,stunIp,ct),token);EnsureSameNic(guid,alias);Emit("UDP","成功","收到並核對 STUN 交易回應。");}
    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
    catch(Exception ex){EnsureSameNic(guid,alias);Emit("UDP","失敗",$"未收到有效 STUN 回應：{ex.Message}；可能是端點、網路或防護影響，不能單憑此判定防火牆封鎖。");}}
   return new(adapter.Name,adapter.Guid,steps);
  } catch(Exception ex){primary=ex;if(nicLost)throw new IOException("選定網卡已更名或離線；診斷已停止。",ex);throw;} finally {
   try {
   try { lifetime.Cancel(); if(watcher is not null)await watcher; } finally {
   if(child!=null){try{if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}}catch(InvalidOperationException){}finally{child.Dispose();}}
   SafeDelete(root);
   }
   } catch when(primary is not null) { /* Preserve the original failure. */ }
  }
 }
 private static Dictionary<string,object> BuildCoreConfig(string alias,int socks,int dns,bool has6,IReadOnlyList<string> servers)=>new Dictionary<string,object>{["mode"]="rule",["log-level"]="silent",["allow-lan"]=false,["bind-address"]="127.0.0.1",["socks-port"]=socks,["ipv6"]=has6,["tun"]=new { enable=false },["dns"]=new Dictionary<string,object>{["enable"]=servers.Count>0,["listen"]=$"127.0.0.1:{dns}",["ipv6"]=has6,["enhanced-mode"]="redir-host",["nameserver"]=servers.Select(x=>x+"#DIAGNOSTIC").ToArray()},["proxies"]=new[]{new Dictionary<string,object>{["name"]="DIAGNOSTIC",["type"]="direct",["interface-name"]=alias,["udp"]=true}},["rules"]=new[]{"MATCH,DIAGNOSTIC"}};
 private static Guid ParseGuid(string value)=>Guid.TryParse(value,out var g)&&g!=Guid.Empty?g:throw new InvalidOperationException("網卡沒有有效的穩定 GUID，無法診斷。");
 private static NetworkInterface FindNic(Guid guid)=>NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n=>Guid.TryParse(n.Id,out var id)&&id==guid&&n.OperationalStatus==OperationalStatus.Up)??throw new InvalidOperationException("選定網卡不存在或已離線。");
 private static void EnsureSameNic(Guid guid,string alias){if(!string.Equals(FindNic(guid).Name,alias,StringComparison.Ordinal))throw new IOException("選定網卡已更名或離線；診斷停止，未改走其他網卡。");}
 private static List<string> GetDnsServers(IReadOnlyDictionary<string,JsonElement> options,bool has4,bool has6){
  if(options.TryGetValue("DnsEnabled",out var enabled)&&enabled.ValueKind==JsonValueKind.False)return [];
  var result=new List<string>();
  foreach(var item in new[]{(has4,"DnsIPv4Upstreams","DnsIPv4Servers","https://1.1.1.1/dns-query\nhttps://8.8.8.8/dns-query",AddressFamily.InterNetwork),(has6,"DnsIPv6Upstreams","DnsIPv6Servers","https://[2606:4700:4700::1111]/dns-query",AddressFamily.InterNetworkV6)}){
   if(!item.Item1||(options.TryGetValue(item.Item2,out var enabledFamily)&&enabledFamily.ValueKind==JsonValueKind.False))continue;
   string raw=options.TryGetValue(item.Item3,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":item.Item4;
   foreach(string line in raw.Split('\n')){string x=line.Trim();if(x.Length==0)continue;if(!Uri.TryCreate(x,UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo.Length>0||u.Fragment.Length>0||!IPAddress.TryParse(u.Host.Trim('[',']'),out var ip)||ip.AddressFamily!=item.Item5)throw new InvalidOperationException("DNS 上游格式不安全。");if(IsBindable(ip))result.Add(x);}
  } return result;
 }
 private static (bool Has4,bool Has6) AvailableFamilies(IEnumerable<IPAddress> addresses){var eligible=addresses.Where(IsBindable).ToArray();return (eligible.Any(x=>x.AddressFamily==AddressFamily.InterNetwork||x.IsIPv4MappedToIPv6),eligible.Any(x=>x.AddressFamily==AddressFamily.InterNetworkV6&&!x.IsIPv4MappedToIPv6));}
 // Mirrors netip.Addr.IsGlobalUnicast: private unicast remains eligible.
 private static bool IsBindable(IPAddress ip){if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();byte[] b=ip.GetAddressBytes();if(ip.AddressFamily==AddressFamily.InterNetwork)return b[0]!=0&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==255&&b[1]==255&&b[2]==255&&b[3]==255);return ip.AddressFamily==AddressFamily.InterNetworkV6&&!ip.Equals(IPAddress.IPv6Any)&&!ip.Equals(IPAddress.IPv6Loopback)&&!ip.IsIPv6LinkLocal&&!ip.IsIPv6Multicast;}
 private static int ReservePort(){using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();return ((IPEndPoint)listener.LocalEndpoint).Port;}
 private static async Task WaitReady(Process child,int port,CancellationToken token){using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(Timeout);while(true){deadline.Token.ThrowIfCancellationRequested();if(child.HasExited)throw new IOException("私人核心無法啟動。");try{using var c=new TcpClient();await c.ConnectAsync(IPAddress.Loopback,port,deadline.Token);return;}catch(SocketException){await Task.Delay(100,deadline.Token);}}}
 private static async Task<T> WithTimeout<T>(Func<CancellationToken,Task<T>> action,CancellationToken token){using var c=CancellationTokenSource.CreateLinkedTokenSource(token);c.CancelAfter(Timeout);try{return await action(c.Token);}catch(OperationCanceledException) when(!token.IsCancellationRequested){throw new TimeoutException("逾時");}}
 private static async Task WithTimeout(Func<CancellationToken,Task> action,CancellationToken token){await WithTimeout(async ct=>{await action(ct);return true;},token);}
 private static async Task<byte[]> ReadExact(Stream s,int n,CancellationToken ct){byte[] b=new byte[n];await s.ReadExactlyAsync(b,ct);return b;}
 private static async Task<TcpClient> Socks(int port,byte command,IPAddress destination,int targetPort,CancellationToken ct){var c=new TcpClient();try{await c.ConnectAsync(IPAddress.Loopback,port,ct);var s=c.GetStream();await s.WriteAsync(new byte[]{5,1,0},ct);var hello=await ReadExact(s,2,ct);if(hello[0]!=5||hello[1]!=0)throw new IOException("SOCKS 認證失敗");byte[] addr=destination.GetAddressBytes();var req=new byte[4+addr.Length+2];req[0]=5;req[1]=command;req[3]=(byte)(addr.Length==4?1:4);addr.CopyTo(req,4);req[^2]=(byte)(targetPort>>8);req[^1]=(byte)targetPort;await s.WriteAsync(req,ct);var head=await ReadExact(s,4,ct);if(head[0]!=5||head[1]!=0||head[2]!=0)throw new IOException("SOCKS 連線失敗 "+head[1]);int len=head[3] switch{1=>4,4=>16,3=>(await ReadExact(s,1,ct))[0],_=>throw new IOException("SOCKS 回應格式錯誤")};var tail=await ReadExact(s,len+2,ct);c.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.KeepAlive,true);c.TagRelay(tail,head[3]);return c;}catch{c.Dispose();throw;}}
 private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TcpClient,IPEndPoint> Relays=new();
 private static void TagRelay(this TcpClient c,byte[] tail,byte type){if(type!=1&&type!=4)return;var address=new IPAddress(tail.AsSpan(0,tail.Length-2));if(IPAddress.Any.Equals(address)||IPAddress.IPv6Any.Equals(address))address=IPAddress.Loopback;Relays.Add(c,new IPEndPoint(address,(tail[^2]<<8)|tail[^1]));}
 private static async Task<int> TestHttps(int socks,bool has4,CancellationToken ct){using var c=await Socks(socks,1,IPAddress.Parse(has4?"1.1.1.1":"2606:4700:4700::1111"),443,ct);using var tls=new SslStream(c.GetStream(),false);await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="cloudflare-dns.com"},ct);await tls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: cloudflare-dns.com\r\nConnection: close\r\n\r\n"),ct);return await ReadHttpStatus(tls,ct);}
 private static async Task<int> ReadHttpStatus(Stream stream,CancellationToken ct){var b=new byte[1024];int n=0;while(n<b.Length){int read=await stream.ReadAsync(b.AsMemory(n,1),ct);if(read==0)break;n+=read;if(n>=2&&b[n-2]==13&&b[n-1]==10){string line=Encoding.ASCII.GetString(b,0,n-2);var parts=line.Split(' ',3);if(parts.Length<2||(parts[0]!="HTTP/1.1"&&parts[0]!="HTTP/1.0")||parts[1].Length!=3||!int.TryParse(parts[1],out int status)||status<100||status>599)throw new IOException("HTTP 狀態列無效");return status;}}throw new IOException("HTTP 狀態列不完整或過長");}
 private static async Task<IPAddress> ResolveDns(int port,string host,bool has4,bool has6,CancellationToken ct){Exception? last=null;foreach(int type in new[]{has4?1:0,has6?28:0}){if(type==0)continue;try{return await ResolveDnsFamily(port,host,type,ct);}catch(OperationCanceledException){throw;}catch(Exception ex){last=ex;}}throw new IOException("沒有可用的 DNS 答案",last);}
 private static async Task<IPAddress> ResolveDnsFamily(int port,string host,int queryType,CancellationToken ct){using var udp=new UdpClient(AddressFamily.InterNetwork);udp.Connect(IPAddress.Loopback,port);byte[] id=RandomNumberGenerator.GetBytes(2);using var m=new MemoryStream();m.Write(id);m.Write(new byte[]{1,0,0,1,0,0,0,0,0,0});foreach(string part in host.Split('.')){m.WriteByte((byte)part.Length);m.Write(Encoding.ASCII.GetBytes(part));}m.Write(new byte[]{0,0,(byte)queryType,0,1});await udp.SendAsync(m.ToArray(),ct);byte[] b=(await udp.ReceiveAsync(ct)).Buffer;if(b.Length<12||b[0]!=id[0]||b[1]!=id[1]||(b[2]&0x80)==0||(b[2]&0x7a)!=0||(b[3]&0x4f)!=0||b[4]!=0||b[5]!=1)throw new IOException("DNS 標頭無效或截斷");int p=12;int start=p;SkipName(b,ref p);byte[] expected=m.ToArray();if(p+4>b.Length||p-start!=expected.Length-16||!b.AsSpan(start,p-start).SequenceEqual(expected.AsSpan(12,p-start))||b[p]!=0||b[p+1]!=queryType||b[p+2]!=0||b[p+3]!=1)throw new IOException("DNS 問題不符");p+=4;int count=(b[6]<<8)|b[7];for(int i=0;i<count;i++){SkipName(b,ref p);if(p+10>b.Length)throw new IOException("DNS 回應截斷");int type=(b[p]<<8)|b[p+1],klass=(b[p+2]<<8)|b[p+3],len=(b[p+8]<<8)|b[p+9];p+=10;if(p+len>b.Length)throw new IOException("DNS 回應截斷");if(type==queryType&&klass==1&&len==(type==1?4:16))return new IPAddress(b.AsSpan(p,len));p+=len;}throw new IOException("DNS 未回傳所需位址答案");}
 private static void SkipName(byte[] b,ref int p){while(true){if(p>=b.Length)throw new IOException("DNS 名稱截斷");int n=b[p++];if(n==0)return;if((n&0xc0)==0xc0){if(p>=b.Length)throw new IOException("DNS 名稱截斷");int target=((n&63)<<8)|b[p++];if(target>=b.Length)throw new IOException("DNS 名稱指標無效");return;}if(n>63||p+n>b.Length)throw new IOException("DNS 名稱無效");p+=n;}}
 private static async Task TestStun(int socks,IPAddress destination,CancellationToken ct){using var c=await Socks(socks,3,IPAddress.Any,0,ct);if(!Relays.TryGetValue(c,out var relay)||!IPAddress.IsLoopback(relay.Address))throw new IOException("SOCKS 未提供本機 UDP relay");using var udp=new UdpClient(relay.AddressFamily);udp.Connect(relay);byte[] tx=RandomNumberGenerator.GetBytes(12);byte[] address=destination.GetAddressBytes();int h=4+address.Length+2;byte[] b=new byte[h+20];b[3]=(byte)(address.Length==4?1:4);address.CopyTo(b,4);b[h-2]=0x0d;b[h-1]=0x96;b[h]=0;b[h+1]=1;b[h+4]=0x21;b[h+5]=0x12;b[h+6]=0xa4;b[h+7]=0x42;tx.CopyTo(b,h+8);await udp.SendAsync(b,ct);byte[] r=(await udp.ReceiveAsync(ct)).Buffer;if(r.Length<h+20||r[0]!=0||r[1]!=0||r[2]!=0||r[3]!=b[3]||!r.AsSpan(4,address.Length).SequenceEqual(address)||r[h-2]!=0x0d||r[h-1]!=0x96||r[h]!=1||r[h+1]!=1||r[h+4]!=0x21||r[h+5]!=0x12||r[h+6]!=0xa4||r[h+7]!=0x42||!r.AsSpan(h+8,12).SequenceEqual(tx))throw new IOException("STUN 回應無效或交易不符");int length=(r[h+2]<<8)|r[h+3];if((length&3)!=0||r.Length!=h+20+length)throw new IOException("STUN 長度無效");}
 private static void SafeDelete(string path){if(!Directory.Exists(path))return;string full=Path.GetFullPath(path),basePath=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("router-diagnostics-",StringComparison.Ordinal)||Path.GetFileName(full).Length!=51)throw new IOException("拒絕清理非診斷暫存目錄。");if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0||Directory.EnumerateFileSystemEntries(full,"*",SearchOption.AllDirectories).Any(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0))throw new IOException("拒絕清理含重解析點的診斷目錄。");Directory.Delete(full,true);}
}
