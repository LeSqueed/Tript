#define UNICODE
#define _UNICODE

#include <windows.h>
#include <wchar.h>

#define PATH_CAPACITY 32768

static int fail(const wchar_t *message)
{
    MessageBoxW(NULL, message, L"Tript", MB_OK | MB_ICONERROR);
    return 1;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int showCommand)
{
    wchar_t launcherPath[PATH_CAPACITY];
    wchar_t appDirectory[PATH_CAPACITY];
    wchar_t shellPath[PATH_CAPACITY];
    wchar_t commandLine[PATH_CAPACITY];
    STARTUPINFOW startup = { 0 };
    PROCESS_INFORMATION process = { 0 };
    DWORD exitCode = 1;

    UNREFERENCED_PARAMETER(instance);
    UNREFERENCED_PARAMETER(previous);
    UNREFERENCED_PARAMETER(showCommand);

    DWORD pathLength = GetModuleFileNameW(NULL, launcherPath, ARRAYSIZE(launcherPath));
    if (pathLength == 0 || pathLength == ARRAYSIZE(launcherPath))
        return fail(L"Tript could not determine its installation directory.");

    wchar_t *separator = wcsrchr(launcherPath, L'\\');
    if (separator == NULL)
        return fail(L"Tript could not determine its installation directory.");
    *separator = L'\0';

    if (swprintf_s(appDirectory, ARRAYSIZE(appDirectory), L"%ls\\App", launcherPath) < 0 ||
        swprintf_s(shellPath, ARRAYSIZE(shellPath), L"%ls\\Tript.Shell.exe", appDirectory) < 0)
        return fail(L"The Tript installation path is too long.");

    DWORD attributes = GetFileAttributesW(shellPath);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return fail(L"Tript is incomplete. App\\Tript.Shell.exe is missing.");

    int commandLength = arguments != NULL && arguments[0] != L'\0'
        ? swprintf_s(commandLine, ARRAYSIZE(commandLine), L"\"%ls\" %ls", shellPath, arguments)
        : swprintf_s(commandLine, ARRAYSIZE(commandLine), L"\"%ls\"", shellPath);
    if (commandLength < 0)
        return fail(L"The Tript command line is too long.");

    startup.cb = sizeof(startup);
    if (!CreateProcessW(shellPath, commandLine, NULL, NULL, FALSE, 0, NULL, appDirectory, &startup, &process))
        return fail(L"Tript could not start App\\Tript.Shell.exe.");

    DWORD waitResult = WaitForSingleObject(process.hProcess, INFINITE);
    if (waitResult == WAIT_OBJECT_0)
        GetExitCodeProcess(process.hProcess, &exitCode);

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return waitResult == WAIT_OBJECT_0 ? (int)exitCode : fail(L"Tript could not wait for the application to close.");
}
