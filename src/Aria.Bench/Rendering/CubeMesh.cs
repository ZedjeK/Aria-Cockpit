using System.Numerics;
using System.Runtime.InteropServices;

namespace Aria.Bench.Rendering;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct Vertex(Vector3 Position, Vector3 Normal)
{
    public const int Size = 24;
}

/// <summary>Cube unité centré (de -0,5 à 0,5), 4 sommets par face pour des normales nettes.</summary>
public static class CubeMesh
{
    public static (Vertex[] Vertices, ushort[] Indices) Build()
    {
        var faces = new (Vector3 Normal, Vector3 Up)[]
        {
            (Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY)
        };
        var vertices = new List<Vertex>(24);
        var indices = new List<ushort>(36);
        foreach (var (normal, up) in faces)
        {
            var right = Vector3.Cross(up, normal);
            var center = normal * 0.5f;
            var start = (ushort)vertices.Count;
            vertices.Add(new(center - right * 0.5f - up * 0.5f, normal));
            vertices.Add(new(center - right * 0.5f + up * 0.5f, normal));
            vertices.Add(new(center + right * 0.5f + up * 0.5f, normal));
            vertices.Add(new(center + right * 0.5f - up * 0.5f, normal));
            indices.AddRange([start, (ushort)(start + 1), (ushort)(start + 2), start, (ushort)(start + 2), (ushort)(start + 3)]);
        }
        return ([.. vertices], [.. indices]);
    }
}
