#!/usr/bin/env python3
"""Compile un shader HLSL avec le DXC Linux de Vortice.Dxc.Native (vérification hors Windows).

Vortice.Dxc ne fonctionne pas sous Linux (chaînes UTF-16 alors que wchar_t y fait 4 octets) :
ce script appelle directement libdxcompiler.so par ctypes, avec les bons wchar_t.
Utilisation : dxc-linux.py libdxcompiler.so fichier.hlsl point_entree profil [sortie.dxil]
Code de sortie : 0 si la compilation réussit, 1 sinon (erreurs sur la sortie d'erreur).
"""
import ctypes, sys, uuid

def guid(text):
    return (ctypes.c_ubyte * 16).from_buffer_copy(uuid.UUID(text).bytes_le)

CLSID_DxcCompiler = guid("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0")
IID_IDxcCompiler3 = guid("228B4687-5A6A-4730-900C-9702B2203F54")
IID_IDxcResult = guid("58346CDA-DDE7-4497-9461-6F87AF5E0659")

class DxcBuffer(ctypes.Structure):
    _fields_ = [("Ptr", ctypes.c_void_p), ("Size", ctypes.c_size_t), ("Encoding", ctypes.c_uint)]

def method(obj, index, restype, *argtypes):
    table = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    return ctypes.CFUNCTYPE(restype, ctypes.c_void_p, *argtypes)(table[index])

def blob_bytes(blob):
    if not blob:
        return b""
    pointer = method(blob, 3, ctypes.c_void_p)(blob)
    size = method(blob, 4, ctypes.c_size_t)(blob)
    return ctypes.string_at(pointer, size) if pointer and size else b""

def main():
    lib_path, source_path, entry, profile = sys.argv[1:5]
    output = sys.argv[5] if len(sys.argv) > 5 else None
    lib = ctypes.CDLL(lib_path)
    compiler = ctypes.c_void_p()
    if lib.DxcCreateInstance(ctypes.byref(CLSID_DxcCompiler), ctypes.byref(IID_IDxcCompiler3), ctypes.byref(compiler)) != 0:
        print("DxcCreateInstance a échoué", file=sys.stderr)
        return 1

    data = open(source_path, "rb").read()
    source = DxcBuffer(ctypes.cast(ctypes.c_char_p(data), ctypes.c_void_p), len(data), 65001)
    args = [source_path, "-T", profile, "-E", entry]
    array = (ctypes.c_wchar_p * len(args))(*args)
    result = ctypes.c_void_p()
    compile_ = method(compiler, 3, ctypes.c_int32, ctypes.POINTER(DxcBuffer), ctypes.POINTER(ctypes.c_wchar_p),
                      ctypes.c_uint32, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p))
    if compile_(compiler, ctypes.byref(source), array, len(args), None,
                ctypes.cast(ctypes.byref(IID_IDxcResult), ctypes.c_void_p), ctypes.byref(result)) != 0:
        print("Compile a échoué", file=sys.stderr)
        return 1

    status = ctypes.c_int32()
    method(result, 3, ctypes.c_int32, ctypes.POINTER(ctypes.c_int32))(result, ctypes.byref(status))
    errors = ctypes.c_void_p()
    method(result, 5, ctypes.c_int32, ctypes.POINTER(ctypes.c_void_p))(result, ctypes.byref(errors))
    message = blob_bytes(errors).decode("utf-8", "replace").strip("\x00\n ")
    if status.value != 0:
        print(message or f"échec 0x{status.value & 0xffffffff:08X}", file=sys.stderr)
        return 1
    if message:
        print(message, file=sys.stderr)
    bytecode = ctypes.c_void_p()
    method(result, 4, ctypes.c_int32, ctypes.POINTER(ctypes.c_void_p))(result, ctypes.byref(bytecode))
    code = blob_bytes(bytecode)
    if output:
        open(output, "wb").write(code)
    print(f"{entry} ({profile}) : {len(code)} octets")
    return 0

if __name__ == "__main__":
    sys.exit(main())
