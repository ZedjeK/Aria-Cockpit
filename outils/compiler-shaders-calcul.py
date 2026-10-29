#!/usr/bin/env python3
"""Précompile les shaders de calcul d'Aria-Bench (src/Aria.Bench/Shaders/Vram.hlsl) en cs_5_1.

Sous Windows, avec le compilateur fourni par Windows lui-même (d3dcompiler_47.dll) : rien à installer, et rien n'est
livré avec l'exe, qui charge seulement le code compilé. Après toute modification de Vram.hlsl, relancer :
    python outils/compiler-shaders-calcul.py
Les shaders de rendu (Scene, Post) restent compilés en DXIL par outils/compiler-shaders.py.
"""
import ctypes, os, sys
from ctypes import wintypes

ICI = os.path.dirname(os.path.abspath(__file__))
SHADERS = os.path.join(ICI, "..", "src", "Aria.Bench", "Shaders")
# (point d'entrée, nom du fichier compilé) : même liste que ShaderLibrary.ComputeEntries
ENTREES = [("Fill", "CSFill"), ("Verify", "CSVerify"), ("Burn", "CSBurn")]
OPTIMISATION_NIVEAU_3 = 1 << 15


class Blob:
    """ID3DBlob : QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize."""

    def __init__(self, pointeur):
        self.p = pointeur

    def _methode(self, index, restype):
        vtable = ctypes.cast(ctypes.cast(self.p, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
        return ctypes.WINFUNCTYPE(restype, ctypes.c_void_p)(vtable[index])

    def octets(self):
        adresse = self._methode(3, ctypes.c_void_p)(self.p)
        taille = self._methode(4, ctypes.c_size_t)(self.p)
        return ctypes.string_at(adresse, taille)

    def liberer(self):
        if self.p:
            self._methode(2, wintypes.ULONG)(self.p)


def compiler(d3d, source, entree):
    code, erreurs = ctypes.c_void_p(), ctypes.c_void_p()
    hr = d3d.D3DCompile(source, len(source), b"Vram.hlsl", None, None, entree.encode(), b"cs_5_1",
                        OPTIMISATION_NIVEAU_3, 0, ctypes.byref(code), ctypes.byref(erreurs))
    try:
        if hr < 0 or not code.value:
            message = Blob(erreurs.value).octets().decode(errors="replace") if erreurs.value else "erreur inconnue"
            raise RuntimeError(f"{entree} : {message.strip()}")
        return Blob(code.value).octets()
    finally:
        for blob in (code, erreurs):
            if blob.value:
                Blob(blob.value).liberer()


def main():
    d3d = ctypes.WinDLL("d3dcompiler_47.dll")
    d3d.D3DCompile.restype = ctypes.c_long
    d3d.D3DCompile.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p,
                               ctypes.c_char_p, ctypes.c_char_p, wintypes.UINT, wintypes.UINT,
                               ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)]
    with open(os.path.join(SHADERS, "Vram.hlsl"), "rb") as f:
        source = f.read()
    sortie = os.path.join(SHADERS, "Compiled")
    os.makedirs(sortie, exist_ok=True)
    for entree, nom in ENTREES:
        octets = compiler(d3d, source, entree)
        with open(os.path.join(sortie, nom + ".cso"), "wb") as f:
            f.write(octets)
        print(f"{nom}.cso : {len(octets)} octets")
    return 0


if __name__ == "__main__":
    sys.exit(main())
