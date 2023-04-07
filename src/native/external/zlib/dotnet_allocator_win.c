// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include <Windows.h>
#include <heapapi.h>
#include <intsafe.h>
#include <winnt.h>
#include "zutil.h"

// Gets the special heap we'll allocate from.
HANDLE GetZlibHeap()
{
#ifdef _WIN64
    static HANDLE s_hPublishedHeap = NULL;

    // If already initialized, return immediately.
    // We don't need a volatile read here since the publish is performed with release semantics.
    if (s_hPublishedHeap != NULL) { return s_hPublishedHeap; }

    // Attempt to create a new heap. If we can't, fall back to the standard process heap.
    // The heap will be dynamically sized.
    BOOL fDefaultedToProcessHeap = FALSE;
    HANDLE hNewHeap = HeapCreate(0, 0, 0);

    if (hNewHeap != NULL)
    {
        // Attempt to set the LFH flag on our new heap. Since it's just an optimization, ignore failures.
        // Ref: https://learn.microsoft.com/windows/win32/api/heapapi/nf-heapapi-heapsetinformation
        ULONG ulHeapInformation = 2; // LFH
        HeapSetInformation(hNewHeap, HeapCompatibilityInformation, &ulHeapInformation, sizeof(ulHeapInformation));
    }
    else
    {
        hNewHeap = GetProcessHeap();
        fDefaultedToProcessHeap = TRUE;
    }

    HANDLE hExistingPublishedHeap = InterlockedCompareExchangePointer(&s_hPublishedHeap, hNewHeap, NULL);
    if (hExistingPublishedHeap == NULL)
    {
        // We successfully published our heap handle - use it.
        return hNewHeap;
    }
    else
    {
        // Another thread already created the heap handle and published it.
        // We should destroy any custom heap we created and fall back to the existing published handle.
        if (!fDefaultedToProcessHeap) { HeapDestroy(hNewHeap); }
        return hExistingPublishedHeap;
    }
#else
    // We don't want to create a new heap in a 32-bit process because it could end up
    // reserving too much of the address space. Instead, fall back to the normal process heap.
    return GetProcessHeap();
#endif
}

typedef struct _DOTNET_ALLOC_COOKIE
{
    PVOID CookieValue;
    union _Size
    {
        SIZE_T RawValue;
        LPVOID EncodedValue;
    } Size;
} DOTNET_ALLOC_COOKIE;

// Historically, the Windows memory allocator always returns addresses aligned to some
// particular boundary. We'll make that same guarantee here just in case somebody
// depends on it.
const SIZE_T DOTNET_ALLOC_HEADER_COOKIE_SIZE_WITH_PADDING = (sizeof(DOTNET_ALLOC_COOKIE) + MEMORY_ALLOCATION_ALIGNMENT - 1) & ~((SIZE_T)MEMORY_ALLOCATION_ALIGNMENT  - 1);
const SIZE_T DOTNET_ALLOC_TRAILER_COOKIE_SIZE = sizeof(DOTNET_ALLOC_COOKIE);

voidpf ZLIB_INTERNAL zcalloc (opaque, items, size)
    voidpf opaque;
    unsigned items;
    unsigned size;
{
    (void)opaque; // suppress C4100 - unreferenced formal parameter

    // If initializing a fixed-size structure, zero the memory.
    DWORD dwFlags = (items == 1) ? HEAP_ZERO_MEMORY : 0;

    SIZE_T cbRequested;
    if (sizeof(items) + sizeof(size) <= sizeof(cbRequested))
    {
        // multiplication can't overflow; no need for safeint
        cbRequested = (SIZE_T)items * (SIZE_T)size;
    }
    else
    {
        // multiplication can overflow; go through safeint
        if (FAILED(SIZETMult(items, size, &cbRequested))) { return NULL; }
    }

    // Make sure the actual allocation has enough room for our frontside & backside cookies.
    SIZE_T cbActualAllocationSize;
    if (FAILED(SIZETAdd(cbRequested, DOTNET_ALLOC_HEADER_COOKIE_SIZE_WITH_PADDING + DOTNET_ALLOC_TRAILER_COOKIE_SIZE, &cbActualAllocationSize))) { return NULL; }

    LPVOID pAlloced = HeapAlloc(GetZlibHeap(), dwFlags, cbActualAllocationSize);
    if (pAlloced == NULL) { return NULL; } // OOM

    // Now set the header & trailer cookies
    DOTNET_ALLOC_COOKIE* pHeaderCookie = (DOTNET_ALLOC_COOKIE*)pAlloced;
    pHeaderCookie->CookieValue = EncodePointer(&pHeaderCookie->CookieValue);
    pHeaderCookie->Size.RawValue = cbRequested;

    LPBYTE pReturnToCaller = (LPBYTE)pHeaderCookie + DOTNET_ALLOC_HEADER_COOKIE_SIZE_WITH_PADDING;

    UNALIGNED DOTNET_ALLOC_COOKIE* pTrailerCookie = (UNALIGNED DOTNET_ALLOC_COOKIE*)(pReturnToCaller + cbRequested);
    pTrailerCookie->CookieValue = EncodePointer(&pTrailerCookie->CookieValue);
    pTrailerCookie->Size.EncodedValue = EncodePointer((PVOID)cbRequested);

    return pReturnToCaller;
}

FORCEINLINE
void zcfree_trash_cookie(UNALIGNED DOTNET_ALLOC_COOKIE* pCookie)
{
    memset(pCookie, 0, sizeof(*pCookie));
    pCookie->CookieValue = (PVOID)(SIZE_T)0xDEADBEEF;
}

// Marked noinline to keep it on the call stack during crash reports.
DECLSPEC_NOINLINE
DECLSPEC_NORETURN
void zcfree_cookie_check_failed()
{
    __fastfail(FAST_FAIL_HEAP_METADATA_CORRUPTION);
}

void ZLIB_INTERNAL zcfree (opaque, ptr)
    voidpf opaque;
    voidpf ptr;
{
    (void)opaque; // suppress C4100 - unreferenced formal parameter

    if (ptr == NULL) { return; } // ok to free nullptr

    // Check cookie at beginning and end

    DOTNET_ALLOC_COOKIE* pHeaderCookie = (DOTNET_ALLOC_COOKIE*)((LPBYTE)ptr - DOTNET_ALLOC_HEADER_COOKIE_SIZE_WITH_PADDING);
    if (DecodePointer(pHeaderCookie->CookieValue) != &pHeaderCookie->CookieValue) { goto Fail; }
    SIZE_T cbRequested = pHeaderCookie->Size.RawValue;

    UNALIGNED DOTNET_ALLOC_COOKIE* pTrailerCookie = (UNALIGNED DOTNET_ALLOC_COOKIE*)((LPBYTE)ptr + cbRequested);
    if (DecodePointer(pTrailerCookie->CookieValue) != &pTrailerCookie->CookieValue) { goto Fail; }
    if (DecodePointer(pTrailerCookie->Size.EncodedValue) != (LPVOID)cbRequested) { goto Fail; }

    // Checks passed - now trash the cookies and free memory

    zcfree_trash_cookie(pHeaderCookie);
    zcfree_trash_cookie(pTrailerCookie);

    if (!HeapFree(GetZlibHeap(), 0, pHeaderCookie)) { goto Fail; }
    return;

Fail:
    zcfree_cookie_check_failed();
}
