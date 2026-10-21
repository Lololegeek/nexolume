# C ABI contract

Library: `office_ffi.dll`, Cdecl calling convention, Windows x64. No C# pointer to a Rust document is exposed. A document is referenced by an opaque identifier validated in the instance's registry.

```c
typedef struct { const uint8_t* ptr; size_t len; } OfficeBuffer;
uint64_t office_create(void);
void office_destroy(uint64_t engine);
int32_t office_execute(uint64_t engine, const uint8_t* input, size_t length, OfficeBuffer* output);
void office_free(const uint8_t* buffer);
```

| Resource | Allocation | Release |
|---|---|---|
| Engine instance | Rust, office_create | C# SafeHandle, office_destroy |
| Documents | Rust, CreateDocument / OpenDocument | CloseDocument or destruction of the instance |
| UTF-8 input | C# byte[] array, valid during the call | .NET after the call |
| Response buffer | Rust Box<[u8]> kept in a registry | C# calls office_free in a finally block |

`office_create` returns 0 if the instance cannot be created. `office_execute` returns 0 on successful transport, 1 for null arguments / excessive size, 2 for an unknown handle, invalid JSON or an unavailable registry, and 3 for a caught panic. A valid business error remains a JSON response with transport 0. The output buffer is initialised to empty before processing. No buffer needs to be released when transport fails.

The caller must provide input and output memory ranges that are actually valid. A null test cannot validate an arbitrary pointer. Unknown instance handles are never dereferenced; releases of unknown or repeated buffers do not dereference the pointer provided. The ownership contract must still be respected, and an old address must not be reused after release.

Commands use UTF-8 buffers and explicit lengths, with no null terminator required. Registries are protected by a Mutex. `catch_unwind` prevents recoverable panics from crossing office_execute and office_create. It does not protect against invalid memory accesses, memory exhaustion or all native faults. A poisoned registry produces an error instead of continuing with potentially inconsistent mutations.

C# calls hold the SafeHandle with DangerousAddRef / DangerousRelease during the exchange. The OfficeBridge layer copies and deserialises the response, then always releases the Rust buffer. Snapshot, opening and serialisation go through office_execute, with no additional export.
