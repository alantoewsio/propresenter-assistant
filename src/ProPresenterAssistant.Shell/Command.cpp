#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <string>
#include <atomic>

// No managed runtime or media processing runs in Explorer's menu construction path.
static HMODULE module;
static std::atomic<long> objects{0};
static const CLSID CommandId = {0x9fbb5941,0x8c17,0x4978,{0xa4,0xd7,0x66,0x23,0x18,0xd4,0x93,0x71}};
static std::wstring AppPath() {
    wchar_t path[32768]; DWORD length = GetModuleFileNameW(module, path, ARRAYSIZE(path));
    if (!length || length == ARRAYSIZE(path)) return {};
    std::wstring result(path, length); return result.substr(0, result.find_last_of(L'\\')+1) + L"ProPresenterAssistant.exe";
}
class Command final : public IExplorerCommand {
    std::atomic<ULONG> refs{1};
public:
    Command() { ++objects; } ~Command() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override { if (!out) return E_POINTER; *out=nullptr; if (id==IID_IUnknown || id==__uuidof(IExplorerCommand)) { *out=static_cast<IExplorerCommand*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG count=--refs; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* title) override { return SHStrDupW(L"Import into ProPresenter",title); }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override { return SHStrDupW((AppPath()+L",0").c_str(),icon); }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* tip) override { return SHStrDupW(L"Choose media to add to a ProPresenter playlist",tip); }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* name) override { if (!name) return E_POINTER; *name=CommandId; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override { if (!state) return E_POINTER; DWORD count=0; *state=items && SUCCEEDED(items->GetCount(&count)) && count ? ECS_ENABLED : ECS_HIDDEN; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override { if (!flags) return E_POINTER; *flags=ECF_DEFAULT; return S_OK; }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** commands) override { if (!commands) return E_POINTER; *commands=nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override {
        if (!items) return E_INVALIDARG;
        DWORD count=0; HRESULT hr=items->GetCount(&count); if (FAILED(hr)) return hr;
        std::wstring paths;
        for (DWORD i=0;i<count;++i) {
            IShellItem* item=nullptr; if (FAILED(items->GetItemAt(i,&item))) continue;
            PWSTR path=nullptr; if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH,&path))) { paths+=path; paths+=L"\r\n"; CoTaskMemFree(path); }
            item->Release();
        }
        if (paths.empty()) return E_INVALIDARG;
        wchar_t temp[32768]; DWORD length=GetTempPathW(ARRAYSIZE(temp),temp); if (!length || length>=ARRAYSIZE(temp)) return E_FAIL;
        std::wstring folder=std::wstring(temp)+L"ProPresenterAssistant-Requests";
        if (!CreateDirectoryW(folder.c_str(),nullptr) && GetLastError()!=ERROR_ALREADY_EXISTS) return HRESULT_FROM_WIN32(GetLastError());
        GUID id; if (FAILED(CoCreateGuid(&id))) return E_FAIL; wchar_t guid[40]; StringFromGUID2(id,guid,40);
        std::wstring request=folder+L"\\"+guid+L".txt";
        HANDLE file=CreateFileW(request.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
        if (file==INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        DWORD written; wchar_t bom=0xfeff;
        bool ok=WriteFile(file,&bom,sizeof(bom),&written,nullptr) && written==sizeof(bom);
        DWORD bytes=static_cast<DWORD>(paths.size()*sizeof(wchar_t));
        ok=ok && WriteFile(file,paths.data(),bytes,&written,nullptr) && written==bytes; CloseHandle(file);
        if (!ok) { DeleteFileW(request.c_str()); return E_FAIL; }
        std::wstring app=AppPath(), command=L"\""+app+L"\" --import-list \""+request+L"\"";
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
        if (!CreateProcessW(app.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&startup,&process)) { DWORD error=GetLastError(); DeleteFileW(request.c_str()); return HRESULT_FROM_WIN32(error); }
        CloseHandle(process.hThread); CloseHandle(process.hProcess); return S_OK;
    }
};
class Factory final : public IClassFactory {
    std::atomic<ULONG> refs{1};
public:
    Factory() { ++objects; } ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override { if (!out) return E_POINTER; *out=nullptr; if (id==IID_IUnknown || id==IID_IClassFactory) { *out=static_cast<IClassFactory*>(this); AddRef(); return S_OK; } return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG count=--refs; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID id, void** out) override { if (outer) return CLASS_E_NOAGGREGATION; auto instance=new Command(); HRESULT hr=instance->QueryInterface(id,out); instance->Release(); return hr; }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++objects; else --objects; return S_OK; }
};
STDAPI DllGetClassObject(REFCLSID clsid, REFIID id, void** out) { if (clsid!=CommandId) return CLASS_E_CLASSNOTAVAILABLE; auto factory=new Factory(); HRESULT hr=factory->QueryInterface(id,out); factory->Release(); return hr; }
STDAPI DllCanUnloadNow() { return objects==0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) { if (reason==DLL_PROCESS_ATTACH) { module=instance; DisableThreadLibraryCalls(instance); } return TRUE; }
