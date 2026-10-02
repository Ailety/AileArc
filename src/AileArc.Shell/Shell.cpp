// Native, dependency-free Explorer command handler. Never loads the archive engine.
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <atomic>
#include <new>
#include <string>
#include <vector>
#include "ShellStrings.h"

static HMODULE module;
static std::atomic<long> objects{0};
// {337C9674-42B4-4EA9-B3BC-4EE620FA8471}
static const CLSID commandId = {0x337c9674,0x42b4,0x4ea9,{0xb3,0xbc,0x4e,0xe6,0x20,0xfa,0x84,0x71}};
enum class Action { Root, Open, Smart, Extract, Create };

static bool English() {
    wchar_t language[16]{};
    DWORD size = sizeof(language);
    return RegGetValueW(HKEY_CURRENT_USER, L"Software\\Ailety\\AileArc", L"ShellLanguage",
        RRF_RT_REG_SZ, nullptr, language, &size) == ERROR_SUCCESS && wcscmp(language, L"en-US") == 0;
}
static HRESULT CopyText(const wchar_t* source, PWSTR* result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    size_t bytes = (wcslen(source) + 1) * sizeof(wchar_t);
    *result = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
    if (!*result) return E_OUTOFMEMORY;
    memcpy(*result, source, bytes);
    return S_OK;
}
static std::wstring AppPath() {
    wchar_t path[32768];
    DWORD count = GetModuleFileNameW(module, path, ARRAYSIZE(path));
    if (!count || count >= ARRAYSIZE(path)) return {};
    std::wstring directory(path, count);
    auto slash = directory.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return {};
    directory.resize(slash); // DLL in the Shell subdirectory of the payload.
    slash = directory.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return {};
    return directory.substr(0, slash + 1) + L"AileArc.exe";
}
static std::wstring Quote(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(c == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        result += c;
        slashes = 0;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}
static HRESULT Selection(IShellItemArray* items, std::vector<std::wstring>& paths, bool& archives) {
    archives = true;
    if (!items) return E_INVALIDARG;
    DWORD count = 0;
    HRESULT hr = items->GetCount(&count);
    if (FAILED(hr)) return hr;
    if (!count || count > 32) return E_INVALIDARG;
    for (DWORD i = 0; i < count; ++i) {
        IShellItem* item = nullptr;
        hr = items->GetItemAt(i, &item);
        if (FAILED(hr)) return hr;
        PWSTR path = nullptr;
        hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
        SFGAOF attributes = 0;
        HRESULT attrHr = item->GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, &attributes);
        item->Release();
        if (FAILED(hr)) return hr;
        // Bound allocation while the shell calls GetState synchronously.
        if (wcslen(path) > 30000) { CoTaskMemFree(path); return E_INVALIDARG; }
        try { paths.emplace_back(path); } catch (...) { CoTaskMemFree(path); throw; }
        CoTaskMemFree(path);
        const wchar_t* extension = PathFindExtensionW(paths.back().c_str());
        // Explorer exposes ZIP files as both a folder and a stream.
        archives = archives && SUCCEEDED(attrHr) && (!(attributes & SFGAO_FOLDER) || (attributes & SFGAO_STREAM)) &&
            (!_wcsicmp(extension, L".zip") || !_wcsicmp(extension, L".7z") || !_wcsicmp(extension, L".rar"));
    }
    return S_OK;
}

class Command final : public IExplorerCommand {
    std::atomic<ULONG> refs{1};
    Action action;
public:
    explicit Command(Action value) : action(value) { ++objects; }
    ~Command() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IExplorerCommand) { *result = static_cast<IExplorerCommand*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG left = --refs; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* result) override {
        return CopyText((English() ? shellEnglish : shellChinese)[static_cast<int>(action)], result);
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* result) override { if (!result) return E_POINTER; *result = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* result) override { if (!result) return E_POINTER; *result = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* result) override {
        if (!result) return E_POINTER;
        *result = commandId; result->Data1 += static_cast<unsigned>(action); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_HIDDEN;
        try {
            std::vector<std::wstring> paths;
            bool archives;
            if (SUCCEEDED(Selection(items, paths, archives)) && (archives || action == Action::Create || action == Action::Root)) *state = ECS_ENABLED;
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override {
        if (action == Action::Root) return E_NOTIMPL;
        try {
            std::vector<std::wstring> paths;
            bool archives;
            HRESULT hr = Selection(items, paths, archives);
            if (FAILED(hr)) return hr;
            if (!archives && action != Action::Create) return E_INVALIDARG;
            auto app = AppPath();
            if (app.empty()) return E_FAIL;
            const wchar_t* flags[] = {L"", L" --open --", L" --smart-extract --", L" --extract-to --", L" --create --"};
            auto command = Quote(app) + flags[static_cast<int>(action)];
            for (const auto& path : paths) {
                command += L" " + Quote(path);
                if (command.size() >= 30000) return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
            }
            STARTUPINFOW start{};
            start.cb = sizeof(start);
            PROCESS_INFORMATION process{};
            if (!CreateProcessW(app.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &start, &process)) return HRESULT_FROM_WIN32(GetLastError());
            CloseHandle(process.hThread); CloseHandle(process.hProcess);
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override {
        if (!flags) return E_POINTER;
        *flags = action == Action::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** result) override;
};

class Enumerator final : public IEnumExplorerCommand {
    std::atomic<ULONG> refs{1};
    ULONG position;
public:
    explicit Enumerator(ULONG pos = 0) : position(pos) { ++objects; }
    ~Enumerator() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IEnumExplorerCommand) { *result = static_cast<IEnumExplorerCommand*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG left = --refs; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, IExplorerCommand** result, ULONG* fetched) override {
        if (!result || (!fetched && count != 1)) return E_POINTER;
        if (fetched) *fetched = 0;
        ULONG total = 0;
        while (total < count && position < 4) {
            auto item = new(std::nothrow) Command(static_cast<Action>(position + 1));
            if (!item) {
                for (ULONG i = 0; i < total; ++i) { result[i]->Release(); result[i] = nullptr; }
                position -= total;
                return E_OUTOFMEMORY;
            }
            result[total++] = item; ++position;
        }
        if (fetched) *fetched = total;
        return total == count ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override { ULONG remaining = 4 - position; position += count < remaining ? count : remaining; return count <= remaining ? S_OK : S_FALSE; }
    HRESULT STDMETHODCALLTYPE Reset() override { position = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** result) override {
        if (!result) return E_POINTER;
        *result = new(std::nothrow) Enumerator(position); return *result ? S_OK : E_OUTOFMEMORY;
    }
};
HRESULT Command::EnumSubCommands(IEnumExplorerCommand** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (action != Action::Root) return E_NOTIMPL;
    *result = new(std::nothrow) Enumerator(); return *result ? S_OK : E_OUTOFMEMORY;
}
class Factory final : public IClassFactory {
    std::atomic<ULONG> refs{1};
public:
    Factory() { ++objects; }
    ~Factory() { --objects; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IClassFactory) { *result = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG left = --refs; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto command = new(std::nothrow) Command(Action::Root);
        if (!command) return E_OUTOFMEMORY;
        HRESULT hr = command->QueryInterface(iid, result); command->Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) ++objects; else --objects; return S_OK; }
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != commandId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(iid, result); factory->Release(); return hr;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
