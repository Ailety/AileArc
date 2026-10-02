#include "../src/AileArc.Shell/Shell.cpp"
#include <shlobj.h>
#include <cstdio>
#include <fstream>
#include <filesystem>
#include <stdexcept>

static void Require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
int wmain(int argc, wchar_t** argv) {
    // The test executable doubles as the launched app, recording Windows' parsed argv.
    if (argc > 1 && !wcscmp(argv[1], L"--create")) {
        std::ofstream output(std::filesystem::path(argv[0]).parent_path() / "received.txt", std::ios::binary);
        for (int i = 1; i < argc; ++i) output.write(reinterpret_cast<char*>(argv[i]), (wcslen(argv[i]) + 1) * sizeof(wchar_t));
        return 0;
    }
    try {
        Require(argc == 2, "Usage: tests.exe payload-directory");
        Require(SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)), "COM init");
        std::filesystem::path root(argv[1]);
        auto library = root / "Shell/AileArc.Shell.dll";
        HMODULE dll = LoadLibraryW(library.c_str());
        Require(dll != nullptr, "Load native DLL");
        auto getClass = reinterpret_cast<HRESULT(__stdcall*)(REFCLSID, REFIID, void**)>(GetProcAddress(dll, "DllGetClassObject"));
        auto canUnload = reinterpret_cast<HRESULT(__stdcall*)()>(GetProcAddress(dll, "DllCanUnloadNow"));
        Require(getClass && canUnload && canUnload() == S_OK, "Exports and unloaded state");
        IClassFactory* factory = nullptr;
        Require(getClass(commandId, IID_IClassFactory, reinterpret_cast<void**>(&factory)) == S_OK, "Class factory");
        IExplorerCommand* command = nullptr;
        Require(factory->CreateInstance(nullptr, IID_IExplorerCommand, reinterpret_cast<void**>(&command)) == S_OK, "Root command");
        factory->Release();
        Require(canUnload() == S_FALSE, "Live object pins DLL");
        EXPCMDFLAGS flags{};
        Require(command->GetFlags(&flags) == S_OK && flags == ECF_HASSUBCOMMANDS, "Root is a flyout");
        IEnumExplorerCommand* items = nullptr;
        Require(command->EnumSubCommands(&items) == S_OK, "Subcommands");
        IExplorerCommand* children[4]{};
        ULONG fetched = 0;
        Require(items->Next(4, children, &fetched) == S_OK && fetched == 4, "Four actions");
        IEnumExplorerCommand* clone = nullptr;
        Require(items->Clone(&clone) == S_OK && clone->Skip(1) == S_FALSE && clone->Reset() == S_OK, "Enumerator semantics");
        clone->Release(); items->Release();
        auto zip = root / L"中文 space.zip";
        auto txt = root / L"--report.txt";
        std::ofstream(zip).put('x'); std::ofstream(txt).put('x');
        auto selection = [](const std::filesystem::path& path) {
            PIDLIST_ABSOLUTE pidl = nullptr;
            Require(SUCCEEDED(SHParseDisplayName(path.c_str(), nullptr, &pidl, 0, nullptr)), "Parse selection");
            IShellItemArray* result = nullptr;
            Require(SUCCEEDED(SHCreateShellItemArrayFromIDLists(1, const_cast<PCIDLIST_ABSOLUTE*>(&pidl), &result)), "Selection array");
            CoTaskMemFree(pidl); return result;
        };
        auto archive = selection(zip);
        auto document = selection(txt);
        for (int i = 0; i < 4; ++i) {
            EXPCMDSTATE state{};
            Require(children[i]->GetState(archive, FALSE, &state) == S_OK && state == ECS_ENABLED, "Archive actions visible");
            Require(children[i]->GetState(document, FALSE, &state) == S_OK && state == (i == 3 ? ECS_ENABLED : ECS_HIDDEN), "Document only offers create");
            Require(children[i]->GetState(nullptr, FALSE, &state) == S_OK && state == ECS_HIDDEN, "Empty selection hidden");
            PWSTR title = nullptr;
            Require(children[i]->GetTitle(archive, &title) == S_OK && wcslen(title) > 0, "Localized title");
            CoTaskMemFree(title);
        }
        // Exact round-trip of quoting (spaces, quotes and trailing backslashes).
        std::wstring expected = L"C:\\space dir\\a\"b\\";
        auto line = L"app " + Quote(expected);
        int parsedCount = 0;
        auto parsed = CommandLineToArgvW(line.c_str(), &parsedCount);
        Require(parsed && parsedCount == 2 && expected == parsed[1], "Command argument quoting");
        LocalFree(parsed);
        Require(CopyFileW(argv[0], (root / "AileArc.exe").c_str(), FALSE), "Launch probe copy");
        auto record = root / "received.txt";
        std::filesystem::remove(record);
        Require(children[3]->Invoke(document, nullptr) == S_OK, "Invoke launches app");
        bool matched = false;
        const std::wstring expectedArgs = std::wstring(L"--create\0--\0", 12) + txt.wstring() + L'\0';
        for (int i = 0; i < 100; ++i) {
            std::ifstream input(record, std::ios::binary);
            std::string bytes((std::istreambuf_iterator<char>(input)), {});
            if (bytes.size() == expectedArgs.size() * sizeof(wchar_t) && !memcmp(bytes.data(), expectedArgs.data(), bytes.size())) { matched = true; break; }
            Sleep(50);
        }
        Require(matched, "Launched application received exact action and path");
        archive->Release(); document->Release();
        for (auto child : children) child->Release();
        command->Release();
        Require(canUnload() == S_OK, "All COM references released");
        FreeLibrary(dll); CoUninitialize();
        puts("Native Shell contract tests passed."); return 0;
    } catch (const std::exception& error) { fprintf(stderr, "%s\n", error.what()); return 1; }
}
