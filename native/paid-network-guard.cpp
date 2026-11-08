#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <initguid.h>
#include <fwpmu.h>
#include <iphlpapi.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <sddl.h>
#include <aclapi.h>
#include <rpc.h>
#include <string>
#include <vector>
#include <sstream>
#include <iostream>
#include <stdexcept>
#include <algorithm>
#include <thread>
#pragma comment(lib,"fwpuclnt.lib")
#pragma comment(lib,"iphlpapi.lib")
#pragma comment(lib,"rpcrt4.lib")
#pragma comment(lib,"ole32.lib")
#pragma comment(lib,"shell32.lib")
#pragma comment(lib,"advapi32.lib")

// Independent of router-guard: restoring process routing must never erase this
// separate, machine-wide paid-adapter policy. No allow bit is ever persisted.
namespace {
const GUID kProvider={0xb86b06a2,0x129c,0x48bd,{0x98,0xb3,0xa6,0x72,0x38,0x5b,0x18,0x67}};
const GUID kSublayer={0x01f7489c,0x51e7,0x4933,{0xba,0xde,0x19,0xeb,0x42,0xda,0x0d,0x62}};
constexpr wchar_t kService[]=L"UnifiedPaidNetworkGuard";
constexpr wchar_t kReg[]=L"Software\\UnifiedNetworkRouter\\PaidNetworkGuard";
constexpr wchar_t kPipe[]=L"\\\\.\\pipe\\UnifiedPaidNetworkGuard-v1";
constexpr size_t kRuntimeCount=12;
constexpr size_t kBootCount=4;
SERVICE_STATUS_HANDLE gServiceHandle=nullptr;
SERVICE_STATUS gServiceStatus={};
HANDLE gStop=nullptr;
HANDLE gPermits=nullptr;
UINT64 gLuid=0;
std::wstring gGuid;
std::wstring gAdapter;
std::string gServiceError;

void Check(DWORD e,const char* what){if(e){std::ostringstream s;s<<what<<" failed 0x"<<std::hex<<e;throw std::runtime_error(s.str());}}
struct Handle { HANDLE h=nullptr; Handle()=default; explicit Handle(HANDLE v):h(v){} ~Handle(){if(h&&h!=INVALID_HANDLE_VALUE)CloseHandle(h);} Handle(const Handle&)=delete; Handle& operator=(const Handle&)=delete; };
struct ScHandle {SC_HANDLE h=nullptr; explicit ScHandle(SC_HANDLE v):h(v){} ~ScHandle(){if(h)CloseServiceHandle(h);} ScHandle(const ScHandle&)=delete;};
struct Engine {HANDLE h=nullptr;explicit Engine(bool dynamic=false){FWPM_SESSION0 s={};s.flags=dynamic?FWPM_SESSION_FLAG_DYNAMIC:0;Check(FwpmEngineOpen0(nullptr,RPC_C_AUTHN_WINNT,nullptr,&s,&h),"Open WFP (administrator required)");}~Engine(){if(h)FwpmEngineClose0(h);}Engine(const Engine&)=delete;};
struct Transaction {HANDLE h;bool done=false;explicit Transaction(HANDLE v):h(v){Check(FwpmTransactionBegin0(h,0),"Begin WFP transaction");}~Transaction(){if(!done)FwpmTransactionAbort0(h);}void Commit(){Check(FwpmTransactionCommit0(h),"Commit WFP transaction");done=true;}};
std::string Utf8(const std::wstring& s){if(s.empty())return{};int n=WideCharToMultiByte(CP_UTF8,0,s.data(),static_cast<int>(s.size()),nullptr,0,nullptr,nullptr);std::string r(static_cast<size_t>(n),'\0');WideCharToMultiByte(CP_UTF8,0,s.data(),static_cast<int>(s.size()),r.data(),n,nullptr,nullptr);return r;}
std::string Json(const std::string& s){std::string r="\"";const char* hex="0123456789abcdef";for(unsigned char c:s){if(c=='"'||c=='\\'){r+='\\';r+=static_cast<char>(c);}else if(c<32){r+="\\u00";r+=hex[c>>4];r+=hex[c&15];}else r+=static_cast<char>(c);}return r+'"';}
std::wstring CanonicalGuid(const std::wstring& text){if(text.empty())throw std::runtime_error("Missing adapter GUID");std::wstring value=text;if(value.front()!=L'{')value=L"{"+value+L"}";GUID id={};Check(static_cast<DWORD>(CLSIDFromString(value.c_str(),&id)),"Parse adapter GUID");wchar_t buffer[40]={};if(!StringFromGUID2(id,buffer,40))throw std::runtime_error("Format adapter GUID failed");return buffer;}
std::wstring ReadReg(const wchar_t* name){HKEY key=nullptr;LSTATUS e=RegOpenKeyExW(HKEY_LOCAL_MACHINE,kReg,0,KEY_READ|KEY_WOW64_64KEY,&key);if(e==ERROR_FILE_NOT_FOUND)return{};Check(e,"Read paid-adapter configuration");DWORD bytes=0,type=0;e=RegQueryValueExW(key,name,nullptr,&type,nullptr,&bytes);if(e==ERROR_FILE_NOT_FOUND){RegCloseKey(key);return{};}if(e||type!=REG_SZ||bytes<sizeof(wchar_t)||bytes>8192){RegCloseKey(key);throw std::runtime_error("Invalid paid-adapter registry value");}std::vector<wchar_t> value(bytes/sizeof(wchar_t)+1);e=RegQueryValueExW(key,name,nullptr,&type,reinterpret_cast<BYTE*>(value.data()),&bytes);RegCloseKey(key);Check(e,"Read paid-adapter value");return value.data();}
void WriteConfig(const std::wstring& guid,const std::wstring& adapter){HKEY key=nullptr;Check(RegCreateKeyExW(HKEY_LOCAL_MACHINE,kReg,0,nullptr,0,KEY_SET_VALUE|KEY_WOW64_64KEY,nullptr,&key,nullptr),"Create paid-adapter configuration");auto put=[&](const wchar_t* n,const std::wstring& v){return RegSetValueExW(key,n,0,REG_SZ,reinterpret_cast<const BYTE*>(v.c_str()),static_cast<DWORD>((v.size()+1)*sizeof(wchar_t)));};LSTATUS e=put(L"Guid",guid);if(!e)e=put(L"AdapterName",adapter);RegCloseKey(key);Check(e,"Save paid-adapter configuration");}
bool ResolveAdapter(const std::wstring& text,UINT64& luid,std::wstring& alias){GUID guid={};if(FAILED(CLSIDFromString(text.c_str(),&guid)))return false;NET_LUID net={};if(ConvertInterfaceGuidToLuid(&guid,&net))return false;MIB_IF_ROW2 row={};row.InterfaceLuid=net;if(GetIfEntry2(&row))return false;luid=net.Value;alias=row.Alias;return true;}

struct Spec {GUID layer;GUID condition;bool boot=false;};
std::vector<Spec> Specs(bool boot){std::vector<Spec> r;
 const GUID packets[]={FWPM_LAYER_INBOUND_IPPACKET_V4,FWPM_LAYER_OUTBOUND_IPPACKET_V4,FWPM_LAYER_INBOUND_IPPACKET_V6,FWPM_LAYER_OUTBOUND_IPPACKET_V6};
 for(const auto& layer:packets)r.push_back({layer,FWPM_CONDITION_IP_LOCAL_INTERFACE,boot});
 if(!boot){const GUID ale[]={FWPM_LAYER_ALE_AUTH_CONNECT_V4,FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,FWPM_LAYER_ALE_AUTH_CONNECT_V6,FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6};for(const auto& layer:ale){r.push_back({layer,FWPM_CONDITION_IP_NEXTHOP_INTERFACE,false});r.push_back({layer,FWPM_CONDITION_IP_ARRIVAL_INTERFACE,false});}}
 return r;
}
// Intentionally omit serviceName. The detailed FwpmProviderAdd0 remarks and
// Object Management loading rules say a provider without a service association
// loads persistent objects unconditionally. Binding the service would let its
// disabled startup type suppress the protection at the next BFE start.
void EnsureProvider(HANDLE engine){FWPM_PROVIDER0 p={};p.providerKey=kProvider;p.flags=FWPM_PROVIDER_FLAG_PERSISTENT;p.displayData.name=const_cast<wchar_t*>(L"Paid wired network safety switch");DWORD e=FwpmProviderAdd0(engine,&p,nullptr);if(e!=FWP_E_ALREADY_EXISTS)Check(e,"Create paid-adapter provider");FWPM_SUBLAYER0 s={};s.subLayerKey=kSublayer;s.flags=FWPM_SUBLAYER_FLAG_PERSISTENT;s.providerKey=const_cast<GUID*>(&kProvider);s.weight=65530;s.displayData.name=p.displayData.name;e=FwpmSubLayerAdd0(engine,&s,nullptr);if(e!=FWP_E_ALREADY_EXISTS)Check(e,"Create paid-adapter sublayer");}
void AddFilter(HANDLE engine,const Spec& spec,UINT64 luid,bool permit){if(!luid)throw std::runtime_error("Zero adapter LUID cannot be filtered");FWPM_FILTER_CONDITION0 c={};c.fieldKey=spec.condition;c.matchType=FWP_MATCH_EQUAL;c.conditionValue.type=FWP_UINT64;c.conditionValue.uint64=&luid;UINT64 weight=permit?200:100;FWPM_FILTER0 f={};f.layerKey=spec.layer;f.subLayerKey=kSublayer;f.providerKey=const_cast<GUID*>(&kProvider);f.flags=permit?0:(spec.boot?FWPM_FILTER_FLAG_BOOTTIME:FWPM_FILTER_FLAG_PERSISTENT);f.action.type=permit?FWP_ACTION_PERMIT:FWP_ACTION_BLOCK;f.weight.type=FWP_UINT64;f.weight.uint64=&weight;f.numFilterConditions=1;f.filterCondition=&c;f.displayData.name=const_cast<wchar_t*>(permit?L"Paid wired network allowed for this service session":L"Paid wired network locked by default");std::wstring description=L"PaidGuard|V=1|L="+std::to_wstring(luid)+(spec.boot?L"|Boot":permit?L"|Permit":L"|Persistent");f.displayData.description=const_cast<wchar_t*>(description.c_str());Check(FwpmFilterAdd0(engine,&f,nullptr,nullptr),"Add paid-adapter filter");}
struct Counts {size_t persistent=0,boot=0,permits=0,other=0;};
Counts Enumerate(HANDLE engine,UINT64 luid,bool remove=false){
 Counts r;
 std::vector<GUID> layers;
 for(const auto& s:Specs(false)){
  bool found=false;
  for(const auto& layer:layers)if(IsEqualGUID(layer,s.layer))found=true;
  if(!found)layers.push_back(s.layer);
 }
 for(const auto& layer:layers){
  HANDLE cursor=nullptr;
  FWPM_FILTER_ENUM_TEMPLATE0 t={};
  t.providerKey=const_cast<GUID*>(&kProvider);
  t.layerKey=layer;
  t.enumType=FWP_FILTER_ENUM_FULLY_CONTAINED;
  t.flags=FWP_FILTER_ENUM_FLAG_INCLUDE_BOOTTIME|FWP_FILTER_ENUM_FLAG_INCLUDE_DISABLED;
  t.actionMask=0xFFFFFFFF;
  Check(FwpmFilterCreateEnumHandle0(engine,&t,&cursor),"Enumerate paid-adapter filters");
  try{
   for(;;){
    FWPM_FILTER0** batch=nullptr;UINT32 count=0;
    Check(FwpmFilterEnum0(engine,cursor,128,&batch,&count),"Read paid-adapter filters");
    std::vector<UINT64> ids;
    for(UINT32 n=0;n<count;++n){
     auto f=batch[n];
     if(remove){ids.push_back(f->filterId);continue;}
     bool correct=IsEqualGUID(f->subLayerKey,kSublayer)&&f->numFilterConditions==1&&f->filterCondition&&f->filterCondition[0].matchType==FWP_MATCH_EQUAL&&f->filterCondition[0].conditionValue.type==FWP_UINT64&&f->filterCondition[0].conditionValue.uint64&&*f->filterCondition[0].conditionValue.uint64==luid;
     bool boot=(f->flags&FWPM_FILTER_FLAG_BOOTTIME)!=0;
     bool known=false;
     if(correct)for(const auto& s:Specs(boot))if(IsEqualGUID(s.layer,f->layerKey)&&IsEqualGUID(s.condition,f->filterCondition[0].fieldKey)){known=true;break;}
     if(!correct||!known||((f->flags&FWPM_FILTER_FLAG_DISABLED)&&!boot)){++r.other;continue;}
     if(f->action.type==FWP_ACTION_PERMIT&&!(f->flags&(FWPM_FILTER_FLAG_PERSISTENT|FWPM_FILTER_FLAG_BOOTTIME)))++r.permits;
     else if(f->action.type!=FWP_ACTION_BLOCK)++r.other;
     else if(boot)++r.boot;
     else if(f->flags&FWPM_FILTER_FLAG_PERSISTENT)++r.persistent;
     else ++r.other;
    }
    if(batch)FwpmFreeMemory0(reinterpret_cast<void**>(&batch));
    for(auto id:ids)Check(FwpmFilterDeleteById0(engine,id),"Remove paid-adapter filter");
    if(count<128)break;
   }
  }catch(...){FwpmFilterDestroyEnumHandle0(engine,cursor);throw;}
  FwpmFilterDestroyEnumHandle0(engine,cursor);
 }
 return r;
}
bool Complete(const Counts& c){return c.persistent==kRuntimeCount&&c.boot==kBootCount&&c.other==0;}
bool ActuallyLocked(const Counts& c){return Complete(c)&&c.permits==0;}
void InstallBase(UINT64 luid){Engine e;Transaction tx(e.h);Enumerate(e.h,luid,true);EnsureProvider(e.h);for(const auto& s:Specs(false))AddFilter(e.h,s,luid,false);for(const auto& s:Specs(true))AddFilter(e.h,s,luid,false);tx.Commit();}
void RemoveBase(){Engine e;Transaction tx(e.h);Enumerate(e.h,0,true);DWORD c=FwpmSubLayerDeleteByKey0(e.h,&kSublayer);if(c!=FWP_E_SUBLAYER_NOT_FOUND)Check(c,"Remove paid-adapter sublayer");c=FwpmProviderDeleteByKey0(e.h,&kProvider);if(c!=FWP_E_PROVIDER_NOT_FOUND)Check(c,"Remove paid-adapter provider");tx.Commit();}
void Lock(){if(gPermits){HANDLE e=gPermits;gPermits=nullptr;Check(FwpmEngineClose0(e),"Revoke paid-adapter permission");}}
void Allow(){UINT64 luid=0;std::wstring alias;std::wstring guid=CanonicalGuid(ReadReg(L"Guid"));if(!ResolveAdapter(guid,luid,alias))throw std::runtime_error("Configured paid adapter is not present");if(guid!=gGuid||luid!=gLuid)throw std::runtime_error("Paid-adapter identity changed; wait for refresh");Engine persistent;Counts counts=Enumerate(persistent.h,luid);if(!Complete(counts))throw std::runtime_error("Paid-adapter protection is incomplete; reinstall protection");if(gPermits&&counts.permits==kRuntimeCount)return;Lock();Engine dynamic(true);Transaction tx(dynamic.h);for(const auto& s:Specs(false))AddFilter(dynamic.h,s,luid,true);tx.Commit();gPermits=dynamic.h;dynamic.h=nullptr;gServiceError.clear();}
void Refresh(){std::wstring guid=CanonicalGuid(ReadReg(L"Guid"));UINT64 luid=0;std::wstring alias;if(!ResolveAdapter(guid,luid,alias)){Lock();gServiceError="Configured paid adapter is not present";return;}if(guid!=gGuid||luid!=gLuid){Lock();InstallBase(luid);gGuid=guid;gLuid=luid;gAdapter=alias;}else{Engine e;auto c=Enumerate(e.h,luid);if(!Complete(c)){Lock();InstallBase(luid);}}gAdapter=alias;gServiceError.clear();}

std::string State(bool running,bool installedOverride=false,const std::string& extraError={}){
 bool installed=installedOverride,locked=false,present=false;
 std::wstring guid,alias;
 std::string error=extraError;
 Counts counts;
 try{
  std::wstring configured=ReadReg(L"Guid");
  bool hasConfig=!configured.empty();
  if(hasConfig){guid=CanonicalGuid(configured);alias=ReadReg(L"AdapterName");}
  UINT64 luid=0;
  present=hasConfig&&ResolveAdapter(guid,luid,alias);
  if(!present&&gLuid&&guid==gGuid)luid=gLuid;
  if(hasConfig){
   Engine e;counts=Enumerate(e.h,luid);
   installed=Complete(counts);
   // Pipe responsiveness or SCM state is not evidence that kernel permits
   // have gone away. In particular, a timed-out allow reply may still have
   // installed permits successfully. Report what WFP actually contains.
   locked=ActuallyLocked(counts);
   if(!installed&&error.empty())error="Paid-adapter protection is incomplete";
   if(!present&&error.empty())error="Configured paid adapter is not present";
  }
 }catch(const std::exception& e){if(error.empty())error=e.what();}
 if(error.empty()&&running)error=gServiceError;
 return std::string("{\"Installed\":")+(installed?"true":"false")+",\"Locked\":"+(locked?"true":"false")+",\"ServiceRunning\":"+(running?"true":"false")+",\"AdapterName\":"+Json(Utf8(alias))+",\"Guid\":"+Json(Utf8(guid))+",\"Error\":"+Json(error)+",\"PersistentFilterCount\":"+std::to_string(counts.persistent)+",\"BootFilterCount\":"+std::to_string(counts.boot)+",\"PermitFilterCount\":"+std::to_string(counts.permits)+",\"AdapterPresent\":"+(present?"true":"false")+"}";
}
bool Io(HANDLE pipe,bool write,void* data,DWORD size,DWORD& done,DWORD timeout){Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));if(!event.h)return false;OVERLAPPED o={};o.hEvent=event.h;BOOL ok=write?WriteFile(pipe,data,size,&done,&o):ReadFile(pipe,data,size,&done,&o);if(ok)return true;if(GetLastError()!=ERROR_IO_PENDING)return false;DWORD wait=WaitForSingleObject(event.h,timeout);if(wait!=WAIT_OBJECT_0){CancelIoEx(pipe,&o);GetOverlappedResult(pipe,&o,&done,TRUE);SetLastError(ERROR_TIMEOUT);return false;}return GetOverlappedResult(pipe,&o,&done,FALSE)!=FALSE;}
std::string PipeRequest(const std::string& command,const wchar_t* pipeName=kPipe){
 // Another UI/status request may own the sole pipe while BFE tears down its
 // dynamic session. Wait and retry the actual open: WaitNamedPipe success
 // does not reserve an instance for this caller.
 Handle pipe;
 ULONGLONG connectUntil=GetTickCount64()+2500;
 for(;;){
  pipe.h=CreateFileW(pipeName,GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
  if(pipe.h!=INVALID_HANDLE_VALUE)break;
  DWORD error=GetLastError();
  if(error!=ERROR_PIPE_BUSY&&error!=ERROR_FILE_NOT_FOUND)Check(error,"Connect to paid-adapter service");
  ULONGLONG now=GetTickCount64();
  if(now>=connectUntil)throw std::runtime_error("Paid-adapter service pipe remained busy or unavailable for 2500 ms");
  DWORD remaining=static_cast<DWORD>(connectUntil-now);
  if(error==ERROR_FILE_NOT_FOUND)Sleep((std::min)(remaining,50UL));
  else if(!WaitNamedPipeW(pipeName,(std::min)(remaining,250UL))){
   error=GetLastError();
   if(error!=ERROR_SEM_TIMEOUT&&error!=ERROR_FILE_NOT_FOUND&&error!=ERROR_PIPE_BUSY)Check(error,"Wait for paid-adapter service pipe");
  }
 }
 DWORD mode=PIPE_READMODE_MESSAGE;
 Check(SetNamedPipeHandleState(pipe.h,&mode,nullptr,nullptr)?0:GetLastError(),"Set pipe mode");
 DWORD done=0;std::string request=command;
 if(!Io(pipe.h,true,request.data(),static_cast<DWORD>(request.size()),done,500))Check(GetLastError(),"Send paid-adapter command");
 char buffer[8192]={};
 DWORD responseTimeout=command=="allow"||command=="block"?15000:5000;
 if(!Io(pipe.h,false,buffer,8191,done,responseTimeout))Check(GetLastError(),"Read paid-adapter response");
 std::string response(buffer,done);
 // The server must not disconnect until this complete message is consumed.
 // Ack failure does not invalidate an already received operation result.
 char ack[]="ack";DWORD acknowledged=0;
 Io(pipe.h,true,ack,3,acknowledged,250);
 return response;
}
bool SendResponse(HANDLE pipe,std::string& response){
 DWORD sent=0;
 if(!Io(pipe,true,response.data(),static_cast<DWORD>(response.size()),sent,2000)||sent!=response.size())return false;
 char ack[8]={};DWORD count=0;
 return Io(pipe,false,ack,7,count,500)&&count==3&&std::string(ack,count)=="ack";
}
void ReportService(DWORD state,DWORD code=NO_ERROR){gServiceStatus.dwServiceType=SERVICE_WIN32_OWN_PROCESS;gServiceStatus.dwCurrentState=state;gServiceStatus.dwControlsAccepted=state==SERVICE_RUNNING?SERVICE_ACCEPT_STOP|SERVICE_ACCEPT_SHUTDOWN:0;gServiceStatus.dwWin32ExitCode=code;gServiceStatus.dwWaitHint=state==SERVICE_START_PENDING||state==SERVICE_STOP_PENDING?10000:0;SetServiceStatus(gServiceHandle,&gServiceStatus);}
void WINAPI ServiceControl(DWORD code){if(code==SERVICE_CONTROL_STOP||code==SERVICE_CONTROL_SHUTDOWN){ReportService(SERVICE_STOP_PENDING);if(gStop)SetEvent(gStop);}else if(code==SERVICE_CONTROL_INTERROGATE)SetServiceStatus(gServiceHandle,&gServiceStatus);}
void WINAPI ServiceMain(DWORD,wchar_t**){gServiceHandle=RegisterServiceCtrlHandlerW(kService,ServiceControl);if(!gServiceHandle)return;ReportService(SERVICE_START_PENDING);gStop=CreateEventW(nullptr,TRUE,FALSE,nullptr);PSECURITY_DESCRIPTOR sd=nullptr;try{if(!gStop)Check(GetLastError(),"Create service stop event");Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)",SDDL_REVISION_1,&sd,nullptr)?0:GetLastError(),"Create private pipe security");SECURITY_ATTRIBUTES sa={sizeof(sa),sd,FALSE};Handle pipe(CreateNamedPipeW(kPipe,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,PIPE_TYPE_MESSAGE|PIPE_READMODE_MESSAGE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,8192,1024,0,&sa));if(pipe.h==INVALID_HANDLE_VALUE)Check(GetLastError(),"Create administrator-only service pipe");LocalFree(sd);sd=nullptr;
  // Always begin locked. Previously granted permission never survives this
  // process, BFE session, service restart, shutdown, or reboot.
  Refresh();ReportService(SERVICE_RUNNING);
  while(WaitForSingleObject(gStop,0)!=WAIT_OBJECT_0){
   Handle connected(CreateEventW(nullptr,TRUE,FALSE,nullptr));
   if(!connected.h)Check(GetLastError(),"Create pipe event");
   OVERLAPPED connect={};connect.hEvent=connected.h;
   BOOL ok=ConnectNamedPipe(pipe.h,&connect);
   DWORD last=ok?ERROR_SUCCESS:GetLastError();
   if(last==ERROR_PIPE_CONNECTED)SetEvent(connected.h);
   else if(!ok&&last!=ERROR_IO_PENDING)Check(last,"Wait for paid-adapter client");
   HANDLE events[]={gStop,connected.h};
   DWORD wait=WaitForMultipleObjects(2,events,FALSE,2000);
   if(wait==WAIT_TIMEOUT){
    CancelIoEx(pipe.h,&connect);DWORD ignored=0;
    GetOverlappedResult(pipe.h,&connect,&ignored,TRUE);
    DisconnectNamedPipe(pipe.h);
    try{Refresh();}catch(const std::exception& e){try{Lock();}catch(...){}gServiceError=e.what();}
    continue;
   }
   if(wait!=WAIT_OBJECT_0+1){
    CancelIoEx(pipe.h,&connect);DWORD ignored=0;
    GetOverlappedResult(pipe.h,&connect,&ignored,TRUE);
    DisconnectNamedPipe(pipe.h);break;
   }
   char buffer[64]={};DWORD count=0;std::string response;
   try{
    if(!Io(pipe.h,false,buffer,63,count,2000))Check(GetLastError(),"Read service command");
    std::string command(buffer,count);
    if(command=="allow")Allow();
    else if(command=="block")Lock();
    else if(command!="status")throw std::runtime_error("Unknown service command");
    response=State(true);
   }catch(const std::exception& e){response=State(true,false,e.what());}
   // WriteFile completion alone only means buffered, so wait for a bounded
   // acknowledgement before DisconnectNamedPipe can discard unread bytes.
   SendResponse(pipe.h,response);
   DisconnectNamedPipe(pipe.h);
   try{Refresh();}catch(const std::exception& e){try{Lock();}catch(...){}gServiceError=e.what();}
  }
 }catch(const std::exception& e){gServiceError=e.what();}try{Lock();}catch(...){}if(sd)LocalFree(sd);if(gStop){CloseHandle(gStop);gStop=nullptr;}ReportService(SERVICE_STOPPED,gServiceError.empty()?NO_ERROR:ERROR_SERVICE_SPECIFIC_ERROR);
}

std::wstring ProgramDirectory(){PWSTR path=nullptr;Check(static_cast<DWORD>(SHGetKnownFolderPath(FOLDERID_ProgramFiles,KF_FLAG_DEFAULT,nullptr,&path)),"Find Program Files");std::wstring r=path;CoTaskMemFree(path);return r+L"\\UnifiedNetworkRouter";}
std::wstring InstalledExe(){return ProgramDirectory()+L"\\paid-network-guard.exe";}
std::wstring SelfExe(){std::vector<wchar_t> path(32768);DWORD n=GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size()));if(!n||n>=path.size())throw std::runtime_error("Cannot locate guard executable");return std::wstring(path.data(),n);}
void RequireAdmin(){SID_IDENTIFIER_AUTHORITY nt=SECURITY_NT_AUTHORITY;PSID admins=nullptr;Check(AllocateAndInitializeSid(&nt,2,SECURITY_BUILTIN_DOMAIN_RID,DOMAIN_ALIAS_RID_ADMINS,0,0,0,0,0,0,&admins)?0:GetLastError(),"Build administrator identity");BOOL member=FALSE;BOOL ok=CheckTokenMembership(nullptr,admins,&member);DWORD e=ok?0:GetLastError();FreeSid(admins);Check(e,"Check administrator permission");if(!member)throw std::runtime_error("Administrator permission is required");}
void NoReparse(const std::wstring& path,bool directory){Handle h(CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|(directory?FILE_FLAG_BACKUP_SEMANTICS:0),nullptr));if(h.h==INVALID_HANDLE_VALUE)Check(GetLastError(),"Inspect trusted installation path");FILE_ATTRIBUTE_TAG_INFO info={};Check(GetFileInformationByHandleEx(h.h,FileAttributeTagInfo,&info,sizeof(info))?0:GetLastError(),"Inspect installation attributes");if(info.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)throw std::runtime_error("Installation path contains a reparse point");if(directory&&!(info.FileAttributes&FILE_ATTRIBUTE_DIRECTORY))throw std::runtime_error("Installation directory is not a directory");}
void SetTrustedAcl(const std::wstring& path,bool directory){
 PSECURITY_DESCRIPTOR sd=nullptr;
 const wchar_t* sddl=directory?L"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)":L"O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)";
 Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,SDDL_REVISION_1,&sd,nullptr)?0:GetLastError(),"Build protected installation security");
 PACL dacl=nullptr;PSID owner=nullptr;BOOL present=FALSE,defaulted=FALSE;
 DWORD e=GetSecurityDescriptorDacl(sd,&present,&dacl,&defaulted)?0:GetLastError();
 if(!e&&!GetSecurityDescriptorOwner(sd,&owner,&defaulted))e=GetLastError();
 if(!e)e=SetNamedSecurityInfoW(const_cast<wchar_t*>(path.c_str()),SE_FILE_OBJECT,OWNER_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION|PROTECTED_DACL_SECURITY_INFORMATION,owner,nullptr,dacl,nullptr);
 LocalFree(sd);Check(e,"Protect installation owner and permissions");
}
void SecureDirectory(const std::wstring& path){for(size_t i=3;i<path.size();++i)if(path[i]==L'\\')NoReparse(path.substr(0,i),true);DWORD attrs=GetFileAttributesW(path.c_str());if(attrs==INVALID_FILE_ATTRIBUTES){Check(CreateDirectoryW(path.c_str(),nullptr)?0:GetLastError(),"Create protected installation directory");}NoReparse(path,true);SetTrustedAcl(path,true);}
void CopyWithRetry(const std::wstring& source,const std::wstring& destination){
 ULONGLONG until=GetTickCount64()+5000;
 for(;;){
  if(CopyFileW(source.c_str(),destination.c_str(),FALSE))return;
  DWORD e=GetLastError();
  if((e!=ERROR_SHARING_VIOLATION&&e!=ERROR_LOCK_VIOLATION)||GetTickCount64()>=until)Check(e,"Copy guard to protected Program Files");
  Sleep(100);
 }
}
void CopyTrustedBinary(){std::wstring folder=ProgramDirectory(),dest=InstalledExe(),src=SelfExe();SecureDirectory(folder);DWORD attrs=GetFileAttributesW(dest.c_str());if(attrs!=INVALID_FILE_ATTRIBUTES)NoReparse(dest,false);if(_wcsicmp(src.c_str(),dest.c_str())!=0)CopyWithRetry(src,dest);NoReparse(dest,false);SetTrustedAcl(dest,false);}
bool ServiceExists(){ScHandle scm(OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT));if(!scm.h)Check(GetLastError(),"Open service manager");ScHandle s(OpenServiceW(scm.h,kService,SERVICE_QUERY_STATUS));if(!s.h){DWORD e=GetLastError();if(e==ERROR_SERVICE_DOES_NOT_EXIST)return false;Check(e,"Read paid-adapter service");}return true;}
bool ServiceRunning(){ScHandle scm(OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT));if(!scm.h)return false;ScHandle s(OpenServiceW(scm.h,kService,SERVICE_QUERY_STATUS));if(!s.h)return false;SERVICE_STATUS_PROCESS status={};DWORD needed=0;return QueryServiceStatusEx(s.h,SC_STATUS_PROCESS_INFO,reinterpret_cast<BYTE*>(&status),sizeof(status),&needed)&&status.dwCurrentState==SERVICE_RUNNING;}
void ValidateServiceIdentity(){
 ScHandle scm(OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT));
 if(!scm.h)Check(GetLastError(),"Open service manager");
 ScHandle s(OpenServiceW(scm.h,kService,SERVICE_QUERY_CONFIG));
 if(!s.h){DWORD e=GetLastError();if(e==ERROR_SERVICE_DOES_NOT_EXIST)return;Check(e,"Check existing service identity");}
 DWORD needed=0;QueryServiceConfigW(s.h,nullptr,0,&needed);
 std::vector<BYTE> buffer(needed);
 auto config=reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buffer.data());
 Check(QueryServiceConfigW(s.h,config,needed,&needed)?0:GetLastError(),"Read existing service identity");
 std::wstring expected=L"\""+InstalledExe()+L"\" --service";
 if(_wcsicmp(config->lpBinaryPathName,expected.c_str())!=0)throw std::runtime_error("Existing service name belongs to a different executable; nothing was changed");
}
void StopService(){
 ScHandle scm(OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT));
 if(!scm.h)Check(GetLastError(),"Open service manager");
 ScHandle s(OpenServiceW(scm.h,kService,SERVICE_STOP|SERVICE_QUERY_STATUS));
 if(!s.h){DWORD e=GetLastError();if(e==ERROR_SERVICE_DOES_NOT_EXIST)return;Check(e,"Open paid-adapter service");}
 SERVICE_STATUS_PROCESS initial={};DWORD bytes=0;
 Check(QueryServiceStatusEx(s.h,SC_STATUS_PROCESS_INFO,reinterpret_cast<BYTE*>(&initial),sizeof(initial),&bytes)?0:GetLastError(),"Read paid-adapter service process");
 Handle process;
 if(initial.dwProcessId){
  process.h=OpenProcess(SYNCHRONIZE,FALSE,initial.dwProcessId);
  if(!process.h){DWORD e=GetLastError();if(e!=ERROR_INVALID_PARAMETER)Check(e,"Watch paid-adapter service process exit");}
 }
 SERVICE_STATUS status={};
 if(initial.dwCurrentState!=SERVICE_STOPPED&&initial.dwCurrentState!=SERVICE_STOP_PENDING&&!ControlService(s.h,SERVICE_CONTROL_STOP,&status)){
  DWORD e=GetLastError();if(e!=ERROR_SERVICE_NOT_ACTIVE)Check(e,"Stop paid-adapter service");
 }
 ULONGLONG until=GetTickCount64()+10000;
 for(;;){
  SERVICE_STATUS_PROCESS current={};
  Check(QueryServiceStatusEx(s.h,SC_STATUS_PROCESS_INFO,reinterpret_cast<BYTE*>(&current),sizeof(current),&bytes)?0:GetLastError(),"Wait for paid-adapter service");
  if(current.dwCurrentState==SERVICE_STOPPED)break;
  if(GetTickCount64()>=until)throw std::runtime_error("Paid-adapter service did not stop in time; protection remains installed");
  Sleep(100);
 }
 // SERVICE_STOPPED precedes ServiceMain returning, CRT teardown, and image
 // unmapping. Keep the original process handle, which cannot suffer PID reuse.
 if(process.h){
  DWORD wait=WaitForSingleObject(process.h,5000);
  if(wait==WAIT_FAILED)Check(GetLastError(),"Wait for paid-adapter process exit");
  if(wait!=WAIT_OBJECT_0)throw std::runtime_error("Paid-adapter service reported stopped but its process did not exit within 5000 ms");
 }
}
void EnsureService(){ScHandle scm(OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT|SC_MANAGER_CREATE_SERVICE));if(!scm.h)Check(GetLastError(),"Open service manager");std::wstring command=L"\""+InstalledExe()+L"\" --service";ScHandle s(OpenServiceW(scm.h,kService,SERVICE_CHANGE_CONFIG|SERVICE_QUERY_CONFIG));if(s.h){DWORD needed=0;QueryServiceConfigW(s.h,nullptr,0,&needed);std::vector<BYTE> buffer(needed);auto config=reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buffer.data());Check(QueryServiceConfigW(s.h,config,needed,&needed)?0:GetLastError(),"Read service identity");if(_wcsicmp(config->lpBinaryPathName,command.c_str())!=0)throw std::runtime_error("Existing service name belongs to a different executable");Check(ChangeServiceConfigW(s.h,SERVICE_WIN32_OWN_PROCESS,SERVICE_AUTO_START,SERVICE_ERROR_NORMAL,command.c_str(),nullptr,nullptr,L"BFE\0",L"LocalSystem",nullptr,L"Paid wired network safety switch")?0:GetLastError(),"Update paid-adapter service");}else{DWORD e=GetLastError();if(e!=ERROR_SERVICE_DOES_NOT_EXIST)Check(e,"Open paid-adapter service");ScHandle created(CreateServiceW(scm.h,kService,L"Paid wired network safety switch",SERVICE_QUERY_STATUS,SERVICE_WIN32_OWN_PROCESS,SERVICE_AUTO_START,SERVICE_ERROR_NORMAL,command.c_str(),nullptr,nullptr,L"BFE\0",L"LocalSystem",nullptr));if(!created.h)Check(GetLastError(),"Install paid-adapter service");}}
void StartService(){ScHandle scm(OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT));if(!scm.h)Check(GetLastError(),"Open service manager");ScHandle s(OpenServiceW(scm.h,kService,SERVICE_START|SERVICE_QUERY_STATUS));if(!s.h)Check(GetLastError(),"Open paid-adapter service");if(!StartServiceW(s.h,0,nullptr)){DWORD e=GetLastError();if(e!=ERROR_SERVICE_ALREADY_RUNNING)Check(e,"Start paid-adapter service");}ULONGLONG until=GetTickCount64()+10000;do{SERVICE_STATUS_PROCESS p={};DWORD bytes=0;Check(QueryServiceStatusEx(s.h,SC_STATUS_PROCESS_INFO,reinterpret_cast<BYTE*>(&p),sizeof(p),&bytes)?0:GetLastError(),"Wait for paid-adapter service startup");if(p.dwCurrentState==SERVICE_RUNNING)return;if(p.dwCurrentState==SERVICE_STOPPED)throw std::runtime_error("Paid-adapter service could not start");Sleep(100);}while(GetTickCount64()<until);throw std::runtime_error("Paid-adapter service startup timed out; adapter remains locked");}
void DeleteServiceEntry(){ScHandle scm(OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT));if(!scm.h)Check(GetLastError(),"Open service manager");ScHandle s(OpenServiceW(scm.h,kService,DELETE));if(!s.h){DWORD e=GetLastError();if(e==ERROR_SERVICE_DOES_NOT_EXIST)return;Check(e,"Open service for removal");}Check(DeleteService(s.h)?0:GetLastError(),"Remove paid-adapter service");}
void DeleteConfig(){LSTATUS e=RegDeleteKeyExW(HKEY_LOCAL_MACHINE,kReg,KEY_WOW64_64KEY,0);if(e!=ERROR_FILE_NOT_FOUND)Check(e,"Remove paid-adapter configuration");}
void Install(const std::wstring& input){
 RequireAdmin();std::wstring guid=CanonicalGuid(input),alias;UINT64 luid=0;
 if(!ResolveAdapter(guid,luid,alias))throw std::runtime_error("Selected paid adapter is not present");
 ValidateServiceIdentity();
 bool previous=ServiceExists()||!ReadReg(L"Guid").empty();
 try{
  StopService();CopyTrustedBinary();InstallBase(luid);WriteConfig(guid,alias);EnsureService();StartService();
  std::string result=PipeRequest("status");
  if(result.find("\"Locked\":true")==std::string::npos||result.find("\"Installed\":true")==std::string::npos)throw std::runtime_error("Installed protection did not report a locked state");
  std::cout<<result<<'\n';
 }catch(...){
  std::exception_ptr original=std::current_exception();
  if(!previous){
   try{StopService();}catch(...){}try{DeleteServiceEntry();}catch(...){}
   try{RemoveBase();}catch(...){}try{DeleteConfig();}catch(...){}
  }else{
   try{
    StartService();std::string recovered=PipeRequest("block");
    if(recovered.find("\"Locked\":true")==std::string::npos||recovered.find("\"Error\":\"\"")==std::string::npos)throw std::runtime_error("Prior service did not confirm a locked state");
   }
   catch(const std::exception& recovery){
    std::string originalMessage="Paid-adapter update failed";
    try{std::rethrow_exception(original);}catch(const std::exception& e){originalMessage=e.what();}catch(...){}
    throw std::runtime_error(originalMessage+"; restarting prior protection also failed: "+recovery.what());
   }
  }
  std::rethrow_exception(original);
 }
}
void Uninstall(){RequireAdmin();ValidateServiceIdentity();StopService();RemoveBase();DeleteServiceEntry();DeleteConfig();std::cout<<State(false)<<'\n';}
void CopyRetrySelfTest(){
 wchar_t temp[MAX_PATH]={},source[MAX_PATH]={},dest[MAX_PATH]={};
 if(!GetTempPathW(MAX_PATH,temp)||!GetTempFileNameW(temp,L"pgs",0,source))Check(GetLastError(),"Create self-test copy source");
 if(!GetTempFileNameW(temp,L"pgd",0,dest)){DWORD e=GetLastError();DeleteFileW(source);Check(e,"Create self-test copy destination");}
 try{
  const char payload[]="Paid guard isolated copy retry";
  {Handle writer(CreateFileW(source,GENERIC_WRITE,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr));
   if(writer.h==INVALID_HANDLE_VALUE)Check(GetLastError(),"Open self-test copy source");
   DWORD written=0;Check(WriteFile(writer.h,payload,sizeof(payload),&written,nullptr)?0:GetLastError(),"Write self-test copy source");}
  Handle occupied(CreateFileW(dest,GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr));
  if(occupied.h==INVALID_HANDLE_VALUE)Check(GetLastError(),"Occupy self-test copy destination");
  HANDLE releaseHandle=occupied.h;occupied.h=nullptr;
  std::thread release([releaseHandle]{Sleep(150);CloseHandle(releaseHandle);});
  std::exception_ptr copyError;
  try{CopyWithRetry(source,dest);}catch(...){copyError=std::current_exception();}
  release.join();
  if(copyError)std::rethrow_exception(copyError);
  {Handle reader(CreateFileW(dest,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr));
   if(reader.h==INVALID_HANDLE_VALUE)Check(GetLastError(),"Read self-test copied destination");
   char data[128]={};DWORD read=0;
   Check(ReadFile(reader.h,data,sizeof(data),&read,nullptr)?0:GetLastError(),"Verify self-test copied destination");
   if(read!=sizeof(payload)||memcmp(data,payload,sizeof(payload))!=0)throw std::runtime_error("Self-test copy retry changed the file contents");}
 }catch(...){DeleteFileW(source);DeleteFileW(dest);throw;}
 DeleteFileW(source);DeleteFileW(dest);
}
void PipeSelfTest(){
 // Unique local IPC only: this never opens the production service pipe,
 // installs a service, changes a WFP object, or sends network traffic.
 std::wstring name=L"\\\\.\\pipe\\UnifiedPaidNetworkGuard-selftest-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64());
 Handle server(CreateNamedPipeW(name.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,PIPE_TYPE_MESSAGE|PIPE_READMODE_MESSAGE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,8192,1024,0,nullptr));
 if(server.h==INVALID_HANDLE_VALUE)Check(GetLastError(),"Create self-test pipe");
 // Occupy the only pipe instance first, then release it. This exercises the
 // real client's retry path rather than assuming WaitNamedPipe reserves it.
 Handle occupied(CreateFileW(name.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr));
 if(occupied.h==INVALID_HANDLE_VALUE)Check(GetLastError(),"Occupy self-test pipe");
 bool acknowledged=false;std::string workerError;
 std::thread worker([&]{
  try{
   Sleep(150);
   DisconnectNamedPipe(server.h);
   Handle ready(CreateEventW(nullptr,TRUE,FALSE,nullptr));
   if(!ready.h)Check(GetLastError(),"Create self-test event");
   OVERLAPPED connect={};connect.hEvent=ready.h;
   if(!ConnectNamedPipe(server.h,&connect)){
    DWORD e=GetLastError();
    if(e==ERROR_IO_PENDING){
     if(WaitForSingleObject(ready.h,2000)!=WAIT_OBJECT_0){CancelIoEx(server.h,&connect);DWORD ignored=0;GetOverlappedResult(server.h,&connect,&ignored,TRUE);throw std::runtime_error("Self-test pipe connect timed out");}
    }else if(e!=ERROR_PIPE_CONNECTED)Check(e,"Connect self-test pipe");
   }
   char request[16]={};DWORD count=0;
   if(!Io(server.h,false,request,15,count,1000)||std::string(request,count)!="status")throw std::runtime_error("Self-test pipe command failed");
   // A response later than the former 1500-ms budget must still succeed.
   Sleep(1800);
   std::string response="{\"SelfTestPipe\":true}";
   acknowledged=SendResponse(server.h,response);
   DisconnectNamedPipe(server.h);
  }catch(const std::exception& e){workerError=e.what();}
 });
 std::string response;std::exception_ptr clientError;
 try{response=PipeRequest("status",name.c_str());}catch(...){clientError=std::current_exception();}
 worker.join();
 if(clientError)std::rethrow_exception(clientError);
 if(!workerError.empty())throw std::runtime_error(workerError);
 if(!acknowledged||response!="{\"SelfTestPipe\":true}")throw std::runtime_error("Self-test pipe acknowledgement failed");
}
void SelfTest(){
 auto live=Specs(false),boot=Specs(true);
 if(live.size()!=kRuntimeCount||boot.size()!=kBootCount)throw std::runtime_error("Self-test filter count mismatch");
 for(const auto& s:boot)if(!s.boot||!IsEqualGUID(s.condition,FWPM_CONDITION_IP_LOCAL_INTERFACE))throw std::runtime_error("Self-test boot filter mismatch");
 if(IsEqualGUID(kProvider,kSublayer))throw std::runtime_error("Self-test provider collision");
 if(CanonicalGuid(L"12345678-1234-1234-1234-123456789abc")!=L"{12345678-1234-1234-1234-123456789ABC}")throw std::runtime_error("Self-test unbraced GUID parsing failed");
 std::string escaped=Json("test\n\"\\");
 if(escaped!="\"test\\u000a\\\"\\\\\"")throw std::runtime_error("Self-test JSON escaping failed");
 if(!Complete({kRuntimeCount,kBootCount,0,0})||Complete({kRuntimeCount-1,kBootCount,0,0}))throw std::runtime_error("Self-test incomplete protection accepted");
 if(!ActuallyLocked({kRuntimeCount,kBootCount,0,0})||ActuallyLocked({kRuntimeCount,kBootCount,kRuntimeCount,0})||ActuallyLocked({kRuntimeCount,kBootCount,1,0})||ActuallyLocked({kRuntimeCount-1,kBootCount,0,0}))throw std::runtime_error("Self-test kernel permission status failed");
 PipeSelfTest();CopyRetrySelfTest();
 std::cout<<"{\"SelfTest\":true,\"NetworkModified\":false,\"PipeAcknowledgement\":true,\"KernelPermissionStatus\":true,\"CopyRetry\":true,\"RuntimeBlockSpecs\":"<<live.size()<<",\"BootBlockSpecs\":"<<boot.size()<<",\"SessionPermitSpecs\":"<<live.size()<<"}\n";
}
}

int wmain(int argc,wchar_t** argv){
 try{
  std::wstring op=argc>1?argv[1]:L"status";
  if(op==L"--service"){
   SERVICE_TABLE_ENTRYW table[]={{const_cast<wchar_t*>(kService),ServiceMain},{nullptr,nullptr}};
   Check(StartServiceCtrlDispatcherW(table)?0:GetLastError(),"Run paid-adapter Windows service");
   return 0;
  }
  if(op==L"--selftest"){SelfTest();return 0;}
  if(op==L"install"){
   if(argc!=3)throw std::runtime_error("install requires the paid adapter GUID");
   Install(argv[2]);return 0;
  }
  if(op==L"uninstall"){Uninstall();return 0;}
  if(op==L"status"){
   if(ServiceRunning()){
    try{std::cout<<PipeRequest("status")<<'\n';return 0;}
    catch(const std::exception& e){
     std::cout<<State(ServiceRunning(),false,e.what())<<'\n';return 0;
    }
   }
   std::cout<<State(false)<<'\n';return 0;
  }
  if(op==L"allow"||op==L"block"){
   RequireAdmin();
   if(!ServiceRunning()){
    if(op==L"allow")throw std::runtime_error("Paid-adapter service is stopped; permission cannot be granted");
    std::string response=State(false);
    std::cout<<response<<'\n';
    return response.find("\"Locked\":true")!=std::string::npos&&response.find("\"Error\":\"\"")!=std::string::npos?0:1;
   }
   std::string response=PipeRequest(op==L"allow"?"allow":"block");
   std::cout<<response<<'\n';
   bool expected=response.find(op==L"allow"?"\"Locked\":false":"\"Locked\":true")!=std::string::npos;
   bool noError=response.find("\"Error\":\"\"")!=std::string::npos;
   return expected&&noError?0:1;
  }
  throw std::runtime_error("Use install GUID, status, allow, block, uninstall, or --selftest");
 }catch(const std::exception& e){
  std::cout<<State(ServiceRunning(),false,e.what())<<'\n';return 1;
 }
}
