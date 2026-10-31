#!/usr/bin/env python3
"""Précompile les shaders d'Aria-Bench en DXIL signé (src/Aria.Bench/Shaders/Compiled/*.cso).

Aria-Bench n'embarque pas de compilateur : il charge ce code compilé, intégré à l'exe. Après toute modification
d'un .hlsl, relancer ce script (Linux ou WSL) avec la bibliothèque DXC du paquet NuGet Vortice.Dxc.Native :
    python3 outils/compiler-shaders.py ~/.nuget/packages/vortice.dxc.native/1.0.5/runtimes/linux-x64/native/libdxcompiler.so
Les tests vérifient que les fichiers compilés correspondent aux sources.
"""
import os, subprocess, sys

ICI = os.path.dirname(os.path.abspath(__file__))
SHADERS = os.path.join(ICI, "..", "src", "Aria.Bench", "Shaders")
# (fichier, point d'entrée, profil) : même liste que ShaderLibrary.Entries
ENTREES = [
    ("Scene", "VSSky", "vs_6_0"), ("Scene", "PSSky", "ps_6_0"),
    ("Scene", "VSTerrain", "vs_6_0"), ("Scene", "PSTerrain", "ps_6_0"),
    ("Scene", "VSObjects", "vs_6_0"), ("Scene", "PSObjects", "ps_6_0"),
    ("Post", "VSFull", "vs_6_0"), ("Post", "PSSsao", "ps_6_0"),
    ("Post", "PSBloomDown", "ps_6_0"), ("Post", "PSBloomUp", "ps_6_0"),
    ("Post", "PSComposite", "ps_6_0"),
]

def main():
    bibliotheque = sys.argv[1]
    sortie = sys.argv[2] if len(sys.argv) > 2 else os.path.join(SHADERS, "Compiled")
    os.makedirs(sortie, exist_ok=True)
    echecs = 0
    for fichier, entree, profil in ENTREES:
        source = os.path.join(SHADERS, fichier + ".hlsl")
        cible = os.path.join(sortie, entree + ".cso")
        r = subprocess.run([sys.executable, os.path.join(ICI, "dxc-linux.py"), bibliotheque, source, entree, profil, cible],
                           capture_output=True, text=True)
        if r.returncode != 0 or r.stderr.strip():
            echecs += 1
            print(f"ÉCHEC {fichier}.{entree} : {r.stderr.strip()}", file=sys.stderr)
        else:
            print(r.stdout.strip())
    return 1 if echecs else 0

if __name__ == "__main__":
    sys.exit(main())
