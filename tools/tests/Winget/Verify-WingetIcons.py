"""Inspect built PE icon resources without executing the service or loading DLL code."""

import argparse
import ctypes
from ctypes import wintypes
from pathlib import Path
import struct


def ico_frames(path):
    data = path.read_bytes()
    reserved, kind, count = struct.unpack_from("<HHH", data)
    if (reserved, kind) != (0, 1):
        raise ValueError("Expected an ICO file.")
    frames = {}
    for index in range(count):
        width, height, _, _, _, _, length, offset = struct.unpack_from("<BBBBHHII", data, 6 + index * 16)
        frames[(width or 256, height or 256)] = data[offset:offset + length]
    return frames


def verify_pe(path, expected):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
    kernel.LoadLibraryExW.restype = wintypes.HMODULE
    kernel.FreeLibrary.argtypes = [wintypes.HMODULE]
    kernel.FindResourceW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p]
    kernel.FindResourceW.restype = wintypes.HRSRC
    kernel.SizeofResource.argtypes = [wintypes.HMODULE, wintypes.HRSRC]
    kernel.SizeofResource.restype = wintypes.DWORD
    kernel.LoadResource.argtypes = [wintypes.HMODULE, wintypes.HRSRC]
    kernel.LoadResource.restype = wintypes.HGLOBAL
    kernel.LockResource.argtypes = [wintypes.HGLOBAL]
    kernel.LockResource.restype = ctypes.c_void_p
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMODULE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t)
    kernel.EnumResourceNamesW.argtypes = [wintypes.HMODULE, ctypes.c_void_p, callback_type, ctypes.c_ssize_t]
    kernel.EnumResourceNamesW.restype = wintypes.BOOL

    # LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE: never resolve imports
    # or run DllMain. Resource payloads are inspected exactly as stored in the PE.
    module = kernel.LoadLibraryExW(str(path), None, 0x2 | 0x20)
    if not module:
        raise ctypes.WinError(ctypes.get_last_error())

    def resource(name, kind):
        entry = kernel.FindResourceW(module, name, kind)
        if not entry:
            raise ctypes.WinError(ctypes.get_last_error())
        size = kernel.SizeofResource(module, entry)
        loaded = kernel.LoadResource(module, entry)
        pointer = kernel.LockResource(loaded)
        if not pointer:
            raise ctypes.WinError(ctypes.get_last_error())
        return ctypes.string_at(pointer, size)

    groups = []
    errors = []

    @callback_type
    def collect(_module, _kind, name, _parameter):
        try:
            groups.append(resource(name, 14))
            return True
        except Exception as error:
            errors.append(error)
            return False

    try:
        enumerated = kernel.EnumResourceNamesW(module, 14, collect, 0)
        if errors:
            raise errors[0]
        if not enumerated or not groups:
            raise ValueError(f"No embedded icon group: {path.name}")
        for group in groups:
            _, kind, count = struct.unpack_from("<HHH", group)
            if kind != 1 or count != len(expected):
                continue
            actual = {}
            for index in range(count):
                width, height, _, _, _, _, length, resource_id = struct.unpack_from("<BBBBHHIH", group, 6 + index * 14)
                payload = resource(resource_id, 3)
                if len(payload) != length:
                    raise ValueError("Embedded icon entry has an inconsistent size.")
                actual[(width or 256, height or 256)] = payload
            if actual == expected:
                print(f"PASS {path.name}: all {len(expected)} embedded icon frames match the Winget asset byte-for-byte.")
                return
        raise ValueError(f"Embedded icon differs from the Winget asset: {path.name}")
    finally:
        kernel.FreeLibrary(module)


def main():
    repo = Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", type=Path, default=repo / "x64/Debug")
    arguments = parser.parse_args()
    expected = ico_frames(repo / "src/modules/Winget/Assets/Winget.ico")
    for name in ("Kit.WingetService.exe", "Kit.WingetModuleInterface.dll"):
        binary = (arguments.runtime_root / name).resolve(strict=True)
        verify_pe(binary, expected)


if __name__ == "__main__":
    main()
