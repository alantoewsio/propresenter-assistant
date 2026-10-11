#include <windows.h>
#include <shellapi.h>
#include <fstream>
#include <filesystem>
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int) {
    int count=0; auto args=CommandLineToArgvW(GetCommandLineW(),&count);
    if (count!=3 || wcscmp(args[1],L"--import-list")) return 1;
    wchar_t path[32768]; GetModuleFileNameW(nullptr,path,32768);
    std::ifstream input(std::filesystem::path(args[2]),std::ios::binary);
    std::ofstream output(std::filesystem::path(path).parent_path()/L"captured-request.txt",std::ios::binary);
    output<<input.rdbuf(); input.close(); output.close(); DeleteFileW(args[2]); LocalFree(args); return 0;
}
