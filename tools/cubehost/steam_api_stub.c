/* steam_api_stub.c: a steam_api64.dll for cubehost that runs no Steam code at all.
 *
 * Placed beside cubehost.exe, it is found before the game's own steam_api64.dll (the executable's folder comes first
 * in the DLL search), so the engine never loads Valve's steam_api or steamclient. Initialisation reports failure,
 * Steam is "not running", callbacks and crash-dump calls do nothing, and every interface lookup returns a dummy
 * object whose virtual functions all return 0 (client.dll calls some interfaces without checking them).
 * Built by build_stub.ps1 with MSVC (exports by __declspec(dllexport)).
 */
#include <windows.h>
#include <intrin.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* CUBEHOST_STEAMLOG=<file>: every interface lookup and what it got, one line each. */
static void note(const char *what, const char *version, const void *result)
{
    const char *path = getenv("CUBEHOST_STEAMLOG");
    if (!path)
        return;
    FILE *f = fopen(path, "a");
    if (!f)
        return;
    fprintf(f, "%s %s -> %p\n", what, version ? version : "(null)", result);
    fclose(f);
}

/* What a dummy method returns: a zeroed 64 KB block at a 4 GB-aligned address, so a caller reading an int or a
 * bool (eax, al) sees 0, and one reading a pointer (a string, a struct) gets valid, empty memory instead of NULL. */
static void *zero_block;

static void *zero(void)
{
    if (!zero_block)
        for (uintptr_t a = (uintptr_t)1 << 40; a < (uintptr_t)1 << 46 && !zero_block; a += (uintptr_t)1 << 32)
            zero_block = VirtualAlloc((void *)a, 0x10000, MEM_RESERVE | MEM_COMMIT, PAGE_READONLY);
    return zero_block;
}

static void *ret0(void) { return zero_block; }
static void *ret_null(void) { return NULL; }

/* A method returning a class by value (MSVC: hidden result pointer after this) that the caller reads: a zeroed
 * 8-byte result (CSteamID 0), returned as the pointer. */
static void *ret_zero_struct(void *self, uint64_t *result) { (void)self; *result = 0; return result; }

/* ISteamUser::GetAuthSessionTicket (engine2 180053550 calls slot 13 and needs a handle): an 8-byte zero ticket and
 * handle 1. The listen server's dummy BeginAuthSession answers 0 (OK); nothing leaves the process. */
static uint32_t auth_ticket(void *self, void *ticket, int max, uint32_t *size, const void *identity)
{
    (void)self; (void)identity;
    if (ticket && max >= 8)
        memset(ticket, 0, 8);
    if (size)
        *size = 8;
    return 1;
}

#define DUMMY_SLOTS 1024
typedef struct { void **vtable; char padding[256]; } Dummy;
typedef struct { const char *prefix; int slot; void *function; } Override;

/* Methods that cannot answer 0, by interface version prefix and vtable slot. */
static const Override overrides[] = {
    { "SteamUser", 2, (void *)ret_zero_struct },  /* ISteamUser::GetSteamID */
    { "SteamUser", 13, (void *)auth_ticket },     /* ISteamUser::GetAuthSessionTicket */
};

#define MAX_DUMMIES 64
static struct { char version[64]; void *vtable[DUMMY_SLOTS]; Dummy object; } dummies[MAX_DUMMIES];
static int dummy_count;
static uintptr_t context_counter = 1;

/* Every interface lookup gets a dummy, except from the networking modules, which must see no Steam to take their
 * non-Steam path (networksystem tests SteamNetworking, steamnetworkingsockets reads SteamUtils). */
static const char *const no_steam_modules[] = { "networksystem.dll", "steamnetworkingsockets.dll" };

/* Interfaces that answer NULL to everyone: with -nosteam, client.dll requires ISteamApps to be absent ("Application
 * running with '-nosteam' but Steam is present"). */
static const char *const absent[] = { "STEAMAPPS_INTERFACE_VERSION" };

/* Interfaces whose every method answers NULL. ISteamClient's methods hand out interfaces (or pipe and user handles,
 * 0 either way): the zero block would be taken for an object with no vtable (server.dll 10-15 991359, "Steamworks
 * Stats: Requesting session id", crashed on it after BeginMatch). */
static const char *const null_methods[] = { "SteamClient" };

static void module_of(void *address, char *name, size_t size)
{
    HMODULE m = NULL;
    name[0] = 0;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)address, &m))
    {
        char path[MAX_PATH];
        DWORD n = GetModuleFileNameA(m, path, sizeof path);
        const char *base = path;
        for (DWORD i = 0; i < n; i++)
            if (path[i] == '\\' || path[i] == '/')
                base = path + i + 1;
        strncpy(name, base, size - 1);
        name[size - 1] = 0;
    }
}

static void *dummy(const char *version, void *caller)
{
    if (!version)
        version = "";
    char module[MAX_PATH];
    module_of(caller, module, sizeof module);
    for (size_t p = 0; p < sizeof no_steam_modules / sizeof no_steam_modules[0]; p++)
        if (_stricmp(module, no_steam_modules[p]) == 0)
            return NULL;
    for (size_t p = 0; p < sizeof absent / sizeof absent[0]; p++)
        if (strncmp(version, absent[p], strlen(absent[p])) == 0)
            return NULL;
    for (int i = 0; i < dummy_count; i++)
        if (strcmp(dummies[i].version, version) == 0)
            return &dummies[i].object;
    if (dummy_count == MAX_DUMMIES)
        return &dummies[0].object;
    int d = dummy_count++;
    strncpy(dummies[d].version, version, sizeof dummies[d].version - 1);
    for (int i = 0; i < DUMMY_SLOTS; i++)
        dummies[d].vtable[i] = (void *)ret0;
    for (size_t p = 0; p < sizeof null_methods / sizeof null_methods[0]; p++)
        if (strncmp(version, null_methods[p], strlen(null_methods[p])) == 0)
            for (int i = 0; i < DUMMY_SLOTS; i++)
                dummies[d].vtable[i] = (void *)ret_null;
    for (size_t o = 0; o < sizeof overrides / sizeof overrides[0]; o++)
        if (strncmp(version, overrides[o].prefix, strlen(overrides[o].prefix)) == 0)
            dummies[d].vtable[overrides[o].slot] = overrides[o].function;
    zero();
    dummies[d].object.vtable = dummies[d].vtable;
    return &dummies[d].object;
}

/* Steamworks' context: { void (*init)(void *ctx); uintptr_t counter; <interface pointers> }; the init fills the
 * pointers through SteamInternal_FindOrCreateUserInterface (dummies here). */
typedef struct { void (*init)(void *); uintptr_t counter; char context[1]; } ContextInitData;

__declspec(dllexport) void *SteamInternal_ContextInit(void *data)
{
    ContextInitData *p = (ContextInitData *)data;
    if (p->counter != context_counter)
    {
        p->init(p->context);
        p->counter = context_counter;
    }
    return p->context;
}

__declspec(dllexport) void *SteamInternal_FindOrCreateUserInterface(int user, const char *version)
{ (void)user; void *r = dummy(version, _ReturnAddress()); note("user", version, r); return r; }
__declspec(dllexport) void *SteamInternal_FindOrCreateGameServerInterface(int user, const char *version)
{ (void)user; void *r = dummy(version, _ReturnAddress()); note("gameserver", version, r); return r; }
__declspec(dllexport) void *SteamInternal_CreateInterface(const char *version)
{ void *r = dummy(version, _ReturnAddress()); note("create", version, r); return r; }

/* ESteamAPIInitResult: 0 OK, 1 failed generic. */
__declspec(dllexport) int SteamInternal_SteamAPI_Init(const char *versions, char *error)
{
    (void)versions;
    if (error)
        strcpy(error, "cubehost: no Steam (stub steam_api64.dll)");
    return 1;
}
/* The engine's listen server requires the game server API (engine2 1800f8f40 fails fatally otherwise): it
 * "succeeds" here, with dummy interfaces and nothing behind them. */
__declspec(dllexport) int SteamInternal_GameServer_Init_V2(uint32_t ip, uint16_t gamePort, uint16_t queryPort, int mode, const char *version, const char *versions, char *error)
{
    (void)ip; (void)gamePort; (void)queryPort; (void)mode; (void)version; (void)versions;
    if (error)
        error[0] = 0;
    return 0;
}
__declspec(dllexport) int SteamAPI_InitAnonymousUser(void) { return 0; }
__declspec(dllexport) int SteamAPI_IsSteamRunning(void) { return 0; }
__declspec(dllexport) int SteamAPI_GetHSteamUser(void) { return 0; }
__declspec(dllexport) int SteamAPI_GetHSteamPipe(void) { return 0; }
__declspec(dllexport) int SteamGameServer_GetHSteamUser(void) { return 0; }
__declspec(dllexport) int SteamGameServer_GetHSteamPipe(void) { return 0; }
__declspec(dllexport) void SteamAPI_RunCallbacks(void) { }
__declspec(dllexport) void SteamGameServer_RunCallbacks(void) { }
__declspec(dllexport) void SteamAPI_Shutdown(void) { }
__declspec(dllexport) void SteamGameServer_Shutdown(void) { }
__declspec(dllexport) void SteamAPI_RegisterCallback(void *callback, int id) { (void)callback; (void)id; }
__declspec(dllexport) void SteamAPI_UnregisterCallback(void *callback) { (void)callback; }
__declspec(dllexport) void SteamAPI_RegisterCallResult(void *callback, uint64_t call) { (void)callback; (void)call; }
__declspec(dllexport) void SteamAPI_UnregisterCallResult(void *callback, uint64_t call) { (void)callback; (void)call; }
__declspec(dllexport) void SteamAPI_SetBreakpadAppID(uint32_t app) { (void)app; }
__declspec(dllexport) void SteamAPI_SetMiniDumpComment(const char *comment) { (void)comment; }
__declspec(dllexport) void SteamAPI_WriteMiniDump(uint32_t code, void *info, uint32_t build) { (void)code; (void)info; (void)build; }
__declspec(dllexport) void SteamAPI_UseBreakpadCrashHandler(const char *version, const char *date, const char *time, int fullMemory, void *context, void *callback)
{ (void)version; (void)date; (void)time; (void)fullMemory; (void)context; (void)callback; }
__declspec(dllexport) void SteamAPI_SetTryCatchCallbacks(int enable) { (void)enable; }
