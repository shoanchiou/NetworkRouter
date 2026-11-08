#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <winsock2.h>
#include <ws2tcpip.h>
#include <objbase.h>
#include <windows.h>
#include <initguid.h>
#include <fwpmu.h>
#include <iphlpapi.h>
#include <rpc.h>
#include <string>
#include <vector>
#include <iostream>
#include <stdexcept>
#pragma comment(lib,"fwpuclnt.lib")
#pragma comment(lib,"iphlpapi.lib")
#pragma comment(lib,"rpcrt4.lib")
#pragma comment(lib,"ws2_32.lib")
#pragma comment(lib,"ole32.lib")
const GUID provider={0x7af2d7d1,0x7f44,0x4e8c,{0xa7,0x23,0xec,0x55,0xb8,0x42,0x3e,0x91}};
const GUID sublayer={0x793fbd02,0x47ce,0x4fb4,{0xa1,0x76,0x9c,0x18,0x71,0x3c,0x72,0xbe}};
HANDLE engine=nullptr;
void check(DWORD code,const char* operation){if(code){char message[180];sprintf_s(message,"%s failed 0x%08lX",operation,code);throw std::runtime_error(message);}}
std::vector<UINT64> ownFilters(){
 HANDLE cursor=nullptr;check(FwpmFilterCreateEnumHandle0(engine,nullptr,&cursor),"Enumerate own filters");std::vector<UINT64> ids;
 try{for(;;){FWPM_FILTER0** batch=nullptr;UINT32 count=0;check(FwpmFilterEnum0(engine,cursor,256,&batch,&count),"Read own filters");
  for(UINT32 n=0;n<count;++n)if(batch[n]->providerKey&&IsEqualGUID(*batch[n]->providerKey,provider))ids.push_back(batch[n]->filterId);
  if(batch)FwpmFreeMemory0(reinterpret_cast<void**>(&batch));if(count<256)break;
 }}catch(...){FwpmFilterDestroyEnumHandle0(engine,cursor);throw;}FwpmFilterDestroyEnumHandle0(engine,cursor);return ids;
}
void ensureProvider(){
 FWPM_PROVIDER0 p={};p.providerKey=provider;p.flags=FWPM_PROVIDER_FLAG_PERSISTENT;p.displayData.name=const_cast<wchar_t*>(L"Unified Network Router");DWORD code=FwpmProviderAdd0(engine,&p,nullptr);if(code!=FWP_E_ALREADY_EXISTS)check(code,"Create own provider");
 FWPM_SUBLAYER0 s={};s.subLayerKey=sublayer;s.flags=FWPM_SUBLAYER_FLAG_PERSISTENT;s.providerKey=const_cast<GUID*>(&provider);s.weight=65535;s.displayData.name=p.displayData.name;code=FwpmSubLayerAdd0(engine,&s,nullptr);if(code!=FWP_E_ALREADY_EXISTS)check(code,"Create own sublayer");
}
void add(const GUID& layer,FWP_ACTION_TYPE action,UINT64 weight,std::vector<FWPM_FILTER_CONDITION0> conditions,const std::wstring& description){
 FWPM_FILTER_CONDITION0 flags={};flags.fieldKey=FWPM_CONDITION_FLAGS;flags.matchType=FWP_MATCH_FLAGS_NONE_SET;flags.conditionValue.type=FWP_UINT32;flags.conditionValue.uint32=FWP_CONDITION_FLAG_IS_LOOPBACK;conditions.push_back(flags);
 FWPM_FILTER0 f={};check(UuidCreate(&f.filterKey),"Create filter ID");f.flags=FWPM_FILTER_FLAG_PERSISTENT;f.providerKey=const_cast<GUID*>(&provider);f.subLayerKey=sublayer;f.layerKey=layer;f.displayData.name=const_cast<wchar_t*>(L"Unified router bypass protection");f.displayData.description=const_cast<wchar_t*>(description.c_str());f.action.type=action;f.weight.type=FWP_UINT64;f.weight.uint64=&weight;f.numFilterConditions=static_cast<UINT32>(conditions.size());f.filterCondition=conditions.data();UINT64 id=0;check(FwpmFilterAdd0(engine,&f,nullptr,&id),"Add bypass protection");
}
void installGuard(UINT64 luid,const wchar_t* core){
 FWP_BYTE_BLOB* app=nullptr;check(FwpmGetAppIdFromFileName0(core,&app),"Resolve routing core");FWPM_FILTER_CONDITION0 identity={};identity.fieldKey=FWPM_CONDITION_ALE_APP_ID;identity.matchType=FWP_MATCH_EQUAL;identity.conditionValue.type=FWP_BYTE_BLOB_TYPE;identity.conditionValue.byteBlob=app;
 const GUID layers[]={FWPM_LAYER_ALE_AUTH_CONNECT_V4,FWPM_LAYER_ALE_AUTH_CONNECT_V6,FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6};auto description=L"RouterGuard|V=3|L="+std::to_wstring(luid);
 try{for(const auto& layer:layers){
  // All traffic enters the router. Persistent block remains after a crash.
  add(layer,FWP_ACTION_BLOCK,1,{},description);
  // Trusted core binds its own TCP and UDP sockets to the selected adapter.
  add(layer,FWP_ACTION_PERMIT,800,{identity},description);
  if(luid){FWPM_FILTER_CONDITION0 next={};next.fieldKey=FWPM_CONDITION_IP_NEXTHOP_INTERFACE;next.matchType=FWP_MATCH_EQUAL;next.conditionValue.type=FWP_UINT64;next.conditionValue.uint64=&luid;auto arrival=next;arrival.fieldKey=FWPM_CONDITION_IP_ARRIVAL_INTERFACE;
   // Authorization can occur again in either direction. Wildcard-bound sockets
   // must not require IP_LOCAL_INTERFACE. Zero LUID never creates a permit.
   add(layer,FWP_ACTION_PERMIT,500,{next},description);add(layer,FWP_ACTION_PERMIT,500,{arrival},description);
  }
  // Keep DHCP address acquisition alive. DNS is handled by the TUN resolver.
  FWPM_FILTER_CONDITION0 udp={};udp.fieldKey=FWPM_CONDITION_IP_PROTOCOL;udp.matchType=FWP_MATCH_EQUAL;udp.conditionValue.type=FWP_UINT8;udp.conditionValue.uint8=IPPROTO_UDP;
  FWPM_FILTER_CONDITION0 local={};local.fieldKey=FWPM_CONDITION_IP_LOCAL_PORT;local.matchType=FWP_MATCH_EQUAL;local.conditionValue.type=FWP_UINT16;auto remote=local;remote.fieldKey=FWPM_CONDITION_IP_REMOTE_PORT;
  bool v6=IsEqualGUID(layer,FWPM_LAYER_ALE_AUTH_CONNECT_V6)||IsEqualGUID(layer,FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6);local.conditionValue.uint16=v6?546:68;remote.conditionValue.uint16=v6?547:67;add(layer,FWP_ACTION_PERMIT,700,{udp,local,remote},description);
  // Preserve PMTU/error delivery and IPv6 neighbor/router discovery. These are
  // link/network control messages, not bypasses for application TCP/UDP data.
  auto icmp=udp;icmp.conditionValue.uint8=static_cast<UINT8>(v6?IPPROTO_ICMPV6:IPPROTO_ICMP);
  auto type=local;type.fieldKey=FWPM_CONDITION_ICMP_TYPE;
  const std::vector<UINT16> types=v6?std::vector<UINT16>{1,2,3,4,130,131,132,133,134,135,136,143}:std::vector<UINT16>{3,11,12};
  for(auto value:types){type.conditionValue.uint16=value;add(layer,FWP_ACTION_PERMIT,700,{icmp,type},description);}
 }}catch(...){FwpmFreeMemory0(reinterpret_cast<void**>(&app));throw;}FwpmFreeMemory0(reinterpret_cast<void**>(&app));
}
int wmain(int argc,wchar_t** argv){
 bool transaction=false;
 try{if(argc<2)throw std::runtime_error("Use lock interfaceGUID corePath or stop or status");std::wstring operation=argv[1];if(operation!=L"lock"&&operation!=L"stop"&&operation!=L"status")throw std::runtime_error("Unknown operation");if(operation==L"lock"&&argc!=4)throw std::runtime_error("Lock requires interface GUID or zero and core path");UINT64 luid=0;
  if(operation==L"lock"&&std::wstring(argv[2])!=L"0"){std::wstring guidText=argv[2];if(guidText.empty())throw std::runtime_error("Missing TUN GUID");if(guidText.front()!=L'{')guidText=L"{"+guidText+L"}";GUID guid={};check(CLSIDFromString(guidText.c_str(),&guid),"Parse TUN GUID");NET_LUID net={};check(ConvertInterfaceGuidToLuid(&guid,&net),"Find TUN interface");luid=net.Value;MIB_IF_ROW2 row={};row.InterfaceLuid=net;check(GetIfEntry2(&row),"Check TUN identity");if(std::wstring(row.Alias)!=L"NetworkRouter")throw std::runtime_error("TUN identity does not match NetworkRouter");}
  check(FwpmEngineOpen0(nullptr,RPC_C_AUTHN_WINNT,nullptr,nullptr,&engine),"Open WFP requires administrator");auto old=ownFilters();
  if(operation==L"status"){bool guarded=false;UINT64 saved=0;for(auto id:old){FWPM_FILTER0* f=nullptr;check(FwpmFilterGetById0(engine,id,&f),"Read guard");std::wstring d=f->displayData.description?f->displayData.description:L"";if(d.rfind(L"RouterGuard|V=3|L=",0)==0){guarded=true;saved=std::stoull(d.substr(18));}FwpmFreeMemory0(reinterpret_cast<void**>(&f));}std::cout<<"guard="<<(guarded?1:0)<<";luid="<<saved<<";count="<<old.size()<<"\n";FwpmEngineClose0(engine);return 0;}
  check(FwpmTransactionBegin0(engine,0),"Start atomic change");transaction=true;for(auto id:old)check(FwpmFilterDeleteById0(engine,id),"Remove own prior rule");
  if(operation==L"lock"){ensureProvider();installGuard(luid,argv[3]);}else{DWORD c=FwpmSubLayerDeleteByKey0(engine,&sublayer);if(c!=FWP_E_SUBLAYER_NOT_FOUND)check(c,"Remove own empty sublayer");c=FwpmProviderDeleteByKey0(engine,&provider);if(c!=FWP_E_PROVIDER_NOT_FOUND)check(c,"Remove own empty provider");}
  check(FwpmTransactionCommit0(engine),"Commit atomic change");transaction=false;FwpmEngineClose0(engine);std::cout<<"SUCCESS\n";return 0;
 }catch(const std::exception& e){if(transaction)FwpmTransactionAbort0(engine);if(engine)FwpmEngineClose0(engine);std::cerr<<"FAILED "<<e.what()<<"\n";return 1;}
}