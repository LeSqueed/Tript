// -municode (Makefile's launcher-windows target) already predefines UNICODE/_UNICODE on the
// compiler command line; redefining them here trips -Werror on newer mingw-w64 as a hard error.
#include <windows.h>
#include <wchar.h>
#include <string.h>

#define PATH_CAPACITY 32768
#define MARKER_CAPACITY 4096
#define MARKER_TOKEN_CAPACITY 256

// Mirrored in src/Tript.Shell/ShellExitCodes.cs: the shell exits with this to apply a staged update.
#define TRIPT_EXIT_CODE_RESTART_FOR_UPDATE 90

// A safety valve, not an expected case: stops the launcher from looping forever if the shell
// somehow kept exiting with the restart-for-update code.
#define TRIPT_MAX_UPDATE_RESTARTS 3

// A swapped-in version failing within this window is rolled back. UpdateManager keeps old-App
// for longer than this, so the rollback target still exists.
#define TRIPT_UPDATE_PROBATION_MS 60000

// Explorer or exiting WebView2 helpers can briefly lock App: wait out a short lock, ask the user
// about a lasting one rather than silently restarting the old version.
#define TRIPT_SWAP_RETRY_MS 5000
#define TRIPT_SWAP_RETRY_STEP_MS 250

static int fail(const wchar_t *message)
{
    MessageBoxW(NULL, message, L"Tript", MB_OK | MB_ICONERROR);
    return 1;
}

// A leftover old-App would make MoveFileW skip the swap silently, so it is renamed aside;
// UpdateManager.SweepLeftovers removes it later, keeping a recursive delete out of this launcher.
static void DiscardStalePreviousInstall(const wchar_t *stagingDirectory, const wchar_t *oldAppBackupPath)
{
    wchar_t discardPath[PATH_CAPACITY];

    if (GetFileAttributesW(oldAppBackupPath) == INVALID_FILE_ATTRIBUTES)
        return;

    if (swprintf_s(discardPath, ARRAYSIZE(discardPath), L"%ls\\discard-%llu", stagingDirectory,
            (unsigned long long)GetTickCount64()) < 0)
        return;

    MoveFileW(oldAppBackupPath, discardPath);
}

static BOOL MoveAppAside(const wchar_t *appDirectory, const wchar_t *oldAppBackupPath)
{
    for (;;)
    {
        ULONGLONG deadline = GetTickCount64() + TRIPT_SWAP_RETRY_MS;
        for (;;)
        {
            if (MoveFileW(appDirectory, oldAppBackupPath))
                return TRUE;

            DWORD error = GetLastError();
            if (error != ERROR_ACCESS_DENIED && error != ERROR_SHARING_VIOLATION)
                return FALSE;
            if (GetTickCount64() >= deadline)
                break;
            Sleep(TRIPT_SWAP_RETRY_STEP_MS);
        }

        int choice = MessageBoxW(NULL,
            L"Tript could not install the update because a file in its folder is in use.\n\n"
            L"Close any Explorer window or program that is using the Tript folder, then choose Retry. "
            L"Choose Cancel to start the current version.",
            L"Tript", MB_RETRYCANCEL | MB_ICONWARNING);
        if (choice != IDRETRY)
            return FALSE;
    }
}

// ASCII-only widen: callers pass tokens that already passed IsSafeMarkerToken.
static void WidenAscii(const char *source, wchar_t *destination, size_t capacity)
{
    size_t index = 0;
    for (; source[index] != '\0' && index + 1 < capacity; index++)
        destination[index] = (wchar_t)(unsigned char)source[index];
    destination[index] = L'\0';
}

// Defense in depth only: the staged shell existing is what actually gates a swap.
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

// Recovers a swap interrupted between the two MoveFileW calls (App\ missing, old-App intact).
// Runs before the marker is read, since the marker may be absent or malformed.
static void RestoreInterruptedSwap(const wchar_t *appDirectory, const wchar_t *oldAppBackupPath)
{
    wchar_t backupShellPath[PATH_CAPACITY];

    if (GetFileAttributesW(appDirectory) != INVALID_FILE_ATTRIBUTES)
        return; // App\ is present, so no swap was left half-applied.

    if (swprintf_s(backupShellPath, ARRAYSIZE(backupShellPath), L"%ls\\Tript.Shell.exe",
            oldAppBackupPath) < 0)
        return;

    DWORD attributes = GetFileAttributesW(backupShellPath);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return; // No usable backup to restore - leave the caller to report the missing install.

// Best effort: on failure the caller still reports the missing install.
    MoveFileW(oldAppBackupPath, appDirectory);
}

// A new version that dies within probation is rolled back, and the failed version is recorded in
// .tript-update\rolled-back so UpdateManager does not re-apply the same release.
static BOOL RollBackFailedUpdate(const wchar_t *launcherDirectory, const char *failedVersion)
{
    wchar_t appDirectory[PATH_CAPACITY];
    wchar_t oldAppBackupPath[PATH_CAPACITY];
    wchar_t oldShellPath[PATH_CAPACITY];
    wchar_t failedAppPath[PATH_CAPACITY];
    wchar_t recordPath[PATH_CAPACITY];

    if (swprintf_s(appDirectory, ARRAYSIZE(appDirectory), L"%ls\\App", launcherDirectory) < 0 ||
        swprintf_s(oldAppBackupPath, ARRAYSIZE(oldAppBackupPath), L"%ls\\.tript-update\\old-App",
            launcherDirectory) < 0 ||
        swprintf_s(oldShellPath, ARRAYSIZE(oldShellPath), L"%ls\\Tript.Shell.exe", oldAppBackupPath) < 0 ||
        swprintf_s(failedAppPath, ARRAYSIZE(failedAppPath), L"%ls\\.tript-update\\failed-App",
            launcherDirectory) < 0 ||
        swprintf_s(recordPath, ARRAYSIZE(recordPath), L"%ls\\.tript-update\\rolled-back",
            launcherDirectory) < 0)
        return FALSE;

    DWORD attributes = GetFileAttributesW(oldShellPath);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        return FALSE; // Nothing to go back to.

// A leftover failed-App makes this fail; UpdateManager sweeps it on the next start.
    if (!MoveFileW(appDirectory, failedAppPath))
        return FALSE;

    if (!MoveFileW(oldAppBackupPath, appDirectory))
    {
        // Better the failing version than no install at all.
        MoveFileW(failedAppPath, appDirectory);
        return FALSE;
    }

    HANDLE record = CreateFileW(recordPath, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (record != INVALID_HANDLE_VALUE)
    {
        DWORD written = 0;
        WriteFile(record, failedVersion, (DWORD)strlen(failedVersion), &written, NULL);
        CloseHandle(record);
    }

    return TRUE;
}

// Swaps a validly staged update (see UpdateMarker.cs, keep in step) into place before the shell starts.
// Never deletes the marker or staged folders; UpdateManager.SweepLeftovers() owns cleanup.
// Returns FALSE only when a swap left App\ missing and the rollback failed too; *fatalExitCode is set.
static BOOL TryApplyStagedUpdate(const wchar_t *launcherDirectory, int *fatalExitCode,
    BOOL *swapped, char *swappedVersion, size_t versionCapacity)
{
    *swapped = FALSE;
    swappedVersion[0] = '\0';

    wchar_t markerPath[PATH_CAPACITY];
    wchar_t appDirectory[PATH_CAPACITY];
    wchar_t oldAppBackupPath[PATH_CAPACITY];
    wchar_t stagedDirectory[PATH_CAPACITY];
    wchar_t stagedShellPath[PATH_CAPACITY];
    wchar_t stagedFolderNameWide[MARKER_TOKEN_CAPACITY];
    char markerContent[MARKER_CAPACITY];
    char formatVersion[16];
    char targetVersion[MARKER_TOKEN_CAPACITY];
    char stagedFolderName[MARKER_TOKEN_CAPACITY];

    if (swprintf_s(markerPath, ARRAYSIZE(markerPath), L"%ls\\.tript-update\\ready.marker",
            launcherDirectory) < 0 ||
        swprintf_s(appDirectory, ARRAYSIZE(appDirectory), L"%ls\\App", launcherDirectory) < 0 ||
        swprintf_s(oldAppBackupPath, ARRAYSIZE(oldAppBackupPath), L"%ls\\.tript-update\\old-App",
            launcherDirectory) < 0)
        return TRUE; // Path too long to even ask - behave as if no update were staged.

    RestoreInterruptedSwap(appDirectory, oldAppBackupPath);

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

    wchar_t stagingDirectory[PATH_CAPACITY];
    if (swprintf_s(stagingDirectory, ARRAYSIZE(stagingDirectory), L"%ls\\.tript-update", launcherDirectory) < 0)
        return TRUE;

    DiscardStalePreviousInstall(stagingDirectory, oldAppBackupPath);

// Plain MoveFileW: MOVEFILE_REPLACE_EXISTING fails on directories, so this relies on
// UpdateManager.SweepLeftovers() having cleared old-App on the previous startup.
    if (!MoveAppAside(appDirectory, oldAppBackupPath))
        return TRUE; // Could not move the current App\ aside (a lock the user chose not to clear,
                      // or old-App still present from a previous swap). The marker is untouched, so
                      // this is retried on the next full launch.

    if (!MoveFileW(stagedDirectory, appDirectory))
    {
        if (!MoveFileW(oldAppBackupPath, appDirectory))
        {
            *fatalExitCode = fail(L"Tript's update could not be completed and the previous "
                L"installation could not be restored. Reinstall Tript from a fresh download.");
            return FALSE;
        }

        return TRUE; // The previous install is back in place; no swap happened.
    }

    // The version is only recorded for a rollback, so a marker whose version line is missing or
    // unsafe still swaps; it just cannot be named if it later has to be rolled back.
    *swapped = TRUE;
    if (ExtractLine(markerContent, 1, targetVersion, ARRAYSIZE(targetVersion)) && IsSafeMarkerToken(targetVersion))
        strcpy_s(swappedVersion, versionCapacity, targetVersion);

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

    BOOL rolledBack = FALSE;
    for (;;)
    {
        int fatalExitCode = 0;
        BOOL swapped = FALSE;
        char swappedVersion[MARKER_TOKEN_CAPACITY];
        if (!TryApplyStagedUpdate(launcherPath, &fatalExitCode, &swapped, swappedVersion, ARRAYSIZE(swappedVersion)))
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
        ULONGLONG started = GetTickCount64();
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

        // Only once per run, so a version that also fails after the rollback cannot loop.
        if (swapped && !rolledBack && exitCode != 0 && exitCode != TRIPT_EXIT_CODE_RESTART_FOR_UPDATE
            && GetTickCount64() - started < TRIPT_UPDATE_PROBATION_MS
            && RollBackFailedUpdate(launcherPath, swappedVersion))
        {
            rolledBack = TRUE;
            MessageBoxW(NULL, L"The Tript update could not start, so Tript went back to the version you had before.",
                L"Tript", MB_OK | MB_ICONWARNING);
            continue;
        }

        if (exitCode != TRIPT_EXIT_CODE_RESTART_FOR_UPDATE)
            return (int)exitCode;

        attemptsLeft--;
        if (attemptsLeft <= 0)
            return (int)exitCode;
    }
}
