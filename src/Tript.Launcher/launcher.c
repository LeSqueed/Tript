// -municode (Makefile's launcher-windows target) already predefines UNICODE/_UNICODE on the
// compiler command line; redefining them here trips -Werror on newer mingw-w64 as a hard error.
#include <windows.h>
#include <wchar.h>
#include <string.h>

#define PATH_CAPACITY 32768
#define MARKER_CAPACITY 4096
#define MARKER_TOKEN_CAPACITY 256

// Mirrored in src/Tript.Shell/ShellExitCodes.cs - keep the two in step. Tript.Shell.exe returns
// this instead of 0 when the user clicked "Restart & update"; seeing it here is what tells this
// launcher to check for (and apply) a staged update before relaunching the shell.
#define TRIPT_EXIT_CODE_RESTART_FOR_UPDATE 90

// A safety valve, not an expected case: stops the launcher from looping forever if the shell
// somehow kept exiting with the restart-for-update code.
#define TRIPT_MAX_UPDATE_RESTARTS 3

static int fail(const wchar_t *message)
{
    MessageBoxW(NULL, message, L"Tript", MB_OK | MB_ICONERROR);
    return 1;
}

// ASCII-only widen: the marker's own format guarantees every byte is a digit, ASCII letter, '.'
// or '-' (see src/Tript.App/Updater/UpdateMarker.cs) - callers only widen a token that has already
// passed IsSafeMarkerToken, so a 1:1 byte->UTF-16 widen is exact and avoids pulling in
// MultiByteToWideChar for a launcher this small.
static void WidenAscii(const char *source, wchar_t *destination, size_t capacity)
{
    size_t index = 0;
    for (; source[index] != '\0' && index + 1 < capacity; index++)
        destination[index] = (wchar_t)(unsigned char)source[index];
    destination[index] = L'\0';
}

// Accepts only the characters the marker's staged-folder-name field can legitimately contain and
// rejects "..". Defense in depth, not the only guard - GetFileAttributesW on the staged shell
// executable (below) is what actually gates whether a swap is attempted at all.
static BOOL IsSafeMarkerToken(const char *token)
{
    if (token[0] == '\0' || strstr(token, "..") != NULL)
        return FALSE;
    for (size_t index = 0; token[index] != '\0'; index++)
    {
        char c = token[index];
        BOOL allowed = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
            || c == '.' || c == '-';
        if (!allowed)
            return FALSE;
    }
    return TRUE;
}

// Finds the given 0-based line in a '\n'-delimited buffer, trims a trailing '\r', and writes it
// (NUL-terminated) into `line`. Returns FALSE if that many lines don't exist or don't fit.
static BOOL ExtractLine(const char *buffer, int lineIndex, char *line, size_t lineCapacity)
{
    const char *cursor = buffer;
    for (int currentLine = 0; ; currentLine++)
    {
        const char *lineEnd = strchr(cursor, '\n');
        size_t lineLength = lineEnd != NULL ? (size_t)(lineEnd - cursor) : strlen(cursor);
        if (lineLength > 0 && cursor[lineLength - 1] == '\r')
            lineLength--;

        if (currentLine == lineIndex)
        {
            if (lineLength >= lineCapacity)
                return FALSE;
            memcpy(line, cursor, lineLength);
            line[lineLength] = '\0';
            return TRUE;
        }

        if (lineEnd == NULL)
            return FALSE;
        cursor = lineEnd + 1;
    }
}

// Looks for a staged update under <launcherDirectory>\.tript-update\ready.marker (written by
// Tript.App.Updater.UpdateManager - see src/Tript.App/Updater/UpdateMarker.cs, which this must
// stay in step with) and, if one is validly staged, swaps it into place before App\Tript.Shell.exe
// is launched. Never deletes the marker, the old-App backup, or any staged folder itself -
// UpdateManager.SweepLeftovers() on the next .NET process's startup owns that cleanup, keeping
// this native surface to string handling and a couple of MoveFileExW calls.
//
// Returns TRUE to continue starting the app normally (whether or not a swap happened -
// appDirectory/shellPath are always rebuilt fresh by the caller afterwards). Returns FALSE only
// for the one truly unrecoverable case - a swap left App\ missing and rolling back failed too - in
// which case *fatalExitCode is set and wWinMain must return it immediately.
static BOOL TryApplyStagedUpdate(const wchar_t *launcherDirectory, int *fatalExitCode)
{
    wchar_t markerPath[PATH_CAPACITY];
    wchar_t appDirectory[PATH_CAPACITY];
    wchar_t oldAppBackupPath[PATH_CAPACITY];
    wchar_t stagedDirectory[PATH_CAPACITY];
    wchar_t stagedShellPath[PATH_CAPACITY];
    wchar_t stagedFolderNameWide[MARKER_TOKEN_CAPACITY];
    char markerContent[MARKER_CAPACITY];
    char formatVersion[16];
    char stagedFolderName[MARKER_TOKEN_CAPACITY];

    if (swprintf_s(markerPath, ARRAYSIZE(markerPath), L"%ls\\.tript-update\\ready.marker",
            launcherDirectory) < 0 ||
        swprintf_s(appDirectory, ARRAYSIZE(appDirectory), L"%ls\\App", launcherDirectory) < 0 ||
        swprintf_s(oldAppBackupPath, ARRAYSIZE(oldAppBackupPath), L"%ls\\.tript-update\\old-App",
            launcherDirectory) < 0)
        return TRUE; // Path too long to even ask - behave as if no update were staged.

    HANDLE markerFile = CreateFileW(markerPath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, NULL);
    if (markerFile == INVALID_HANDLE_VALUE)
        return TRUE; // No update staged.

    DWORD bytesRead = 0;
    BOOL readOk = ReadFile(markerFile, markerContent, sizeof(markerContent) - 1, &bytesRead, NULL);
    CloseHandle(markerFile);
    if (!readOk)
        return TRUE;
    markerContent[bytesRead] = '\0';

    if (!ExtractLine(markerContent, 0, formatVersion, ARRAYSIZE(formatVersion)) ||
        strcmp(formatVersion, "1") != 0 ||
        !ExtractLine(markerContent, 2, stagedFolderName, ARRAYSIZE(stagedFolderName)) ||
        !IsSafeMarkerToken(stagedFolderName))
        return TRUE; // Malformed, or a future format this launcher predates - ignore silently.

    WidenAscii(stagedFolderName, stagedFolderNameWide, ARRAYSIZE(stagedFolderNameWide));

    if (swprintf_s(stagedDirectory, ARRAYSIZE(stagedDirectory), L"%ls\\.tript-update\\%ls",
            launcherDirectory, stagedFolderNameWide) < 0 ||
        swprintf_s(stagedShellPath, ARRAYSIZE(stagedShellPath), L"%ls\\Tript.Shell.exe",
            stagedDirectory) < 0)
        return TRUE;

    DWORD stagedAttributes = GetFileAttributesW(stagedShellPath);
    if (stagedAttributes == INVALID_FILE_ATTRIBUTES || (stagedAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return TRUE; // The staged package looks incomplete - leave the current App\ alone.

    // Plain MoveFileW, not MoveFileExW(..., MOVEFILE_REPLACE_EXISTING): that flag is documented to
    // fail whenever either path names a directory, which both of these always are. A same-volume
    // directory rename only succeeds when the destination doesn't already exist yet, which is the
    // case here as long as UpdateManager.SweepLeftovers() cleared old-App on the previous startup.
    if (!MoveFileW(appDirectory, oldAppBackupPath))
        return TRUE; // Could not move the current App\ aside (e.g. a file still in use, or a
                      // leftover old-App from an interrupted swap). The marker is untouched, so
                      // this is retried on the next full launch.

    if (!MoveFileW(stagedDirectory, appDirectory) && !MoveFileW(oldAppBackupPath, appDirectory))
    {
        *fatalExitCode = fail(L"Tript's update could not be completed and the previous "
            L"installation could not be restored. Reinstall Tript from a fresh download.");
        return FALSE;
    }

    return TRUE;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int showCommand)
{
    wchar_t launcherPath[PATH_CAPACITY];
    wchar_t appDirectory[PATH_CAPACITY];
    wchar_t shellPath[PATH_CAPACITY];
    wchar_t commandLine[PATH_CAPACITY];
    int attemptsLeft = TRIPT_MAX_UPDATE_RESTARTS;

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

    for (;;)
    {
        int fatalExitCode = 0;
        if (!TryApplyStagedUpdate(launcherPath, &fatalExitCode))
            return fatalExitCode;

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

        STARTUPINFOW startup = { 0 };
        PROCESS_INFORMATION process = { 0 };
        startup.cb = sizeof(startup);
        if (!CreateProcessW(shellPath, commandLine, NULL, NULL, FALSE, 0, NULL, appDirectory, &startup, &process))
            return fail(L"Tript could not start App\\Tript.Shell.exe.");

        DWORD exitCode = 1;
        DWORD waitResult = WaitForSingleObject(process.hProcess, INFINITE);
        if (waitResult == WAIT_OBJECT_0)
            GetExitCodeProcess(process.hProcess, &exitCode);

        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);

        if (waitResult != WAIT_OBJECT_0)
            return fail(L"Tript could not wait for the application to close.");

        if (exitCode != TRIPT_EXIT_CODE_RESTART_FOR_UPDATE)
            return (int)exitCode;

        attemptsLeft--;
        if (attemptsLeft <= 0)
            return (int)exitCode;
    }
}
