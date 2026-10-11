#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <vector>
static const CLSID Id={0x9fbb5941,0x8c17,0x4978,{0xa4,0xd7,0x66,0x23,0x18,0xd4,0x93,0x71}};
int wmain(int count,wchar_t** args) {
    if(count!=4) return 1;
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    HMODULE dll=LoadLibraryW(args[1]); if(!dll) return 2;
    auto get=reinterpret_cast<LPFNGETCLASSOBJECT>(GetProcAddress(dll,"DllGetClassObject"));
    auto unload=reinterpret_cast<LPFNCANUNLOADNOW>(GetProcAddress(dll,"DllCanUnloadNow"));
    if(!get || !unload || unload()!=S_OK) return 3;
    IClassFactory* factory=nullptr; if(FAILED(get(Id,IID_PPV_ARGS(&factory)))) return 4;
    IExplorerCommand* command=nullptr; if(FAILED(factory->CreateInstance(nullptr,IID_PPV_ARGS(&command)))) return 5;
    factory->Release();
    PWSTR title=nullptr; command->GetTitle(nullptr,&title);
    if(!title || wcscmp(title,L"Import into ProPresenter")) return 6; CoTaskMemFree(title);
    EXPCMDSTATE state; command->GetState(nullptr,FALSE,&state); if(state!=ECS_HIDDEN) return 7;
    EXPCMDFLAGS flags; command->GetFlags(&flags); if(flags!=ECF_DEFAULT) return 8;
    PIDLIST_ABSOLUTE ids[2]{};
    for(int i=0;i<2;++i) if(FAILED(SHParseDisplayName(args[i+2],nullptr,&ids[i],0,nullptr))) return 9;
    IShellItemArray* items=nullptr; if(FAILED(SHCreateShellItemArrayFromIDLists(2,(PCIDLIST_ABSOLUTE*)ids,&items))) return 10;
    command->GetState(items,FALSE,&state); if(state!=ECS_ENABLED) return 11;
    if(FAILED(command->Invoke(items,nullptr))) return 12;
    items->Release(); for(auto id:ids) CoTaskMemFree(id);
    auto capture=std::filesystem::path(args[1]).parent_path()/L"captured-request.txt";
    for(int i=0;i<100 && !std::filesystem::exists(capture);++i) Sleep(50);
    std::ifstream input(capture,std::ios::binary); std::vector<char> data((std::istreambuf_iterator<char>(input)),{});
    std::wstring expected=L"\ufeff"+std::wstring(args[2])+L"\r\n"+args[3]+L"\r\n";
    if(data.size()!=expected.size()*2 || memcmp(data.data(),expected.data(),data.size())) return 13;
    command->Release(); if(unload()!=S_OK) return 14; FreeLibrary(dll); CoUninitialize();
    std::cout<<"Passed: COM activation, exact title, top-level flags, state, multi-selection Unicode handoff, reference lifetime.\n"; return 0;
}
