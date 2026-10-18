using RsDocx.Core.Stl;

namespace StlConverter;

// The few 3D kinds the WPF window was written with (a point, a direction, a box), here without WPF, so the window's
// code reads as it did.

/// <summary>A point in the view (the file's units).</summary>
public record struct Point3D(double X, double Y, double Z)
{
    public static Vector3D operator -(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3D operator +(Point3D a, Vector3D v) => new(a.X + v.X, a.Y + v.Y, a.Z + v.Z);
    public static Point3D operator -(Point3D a, Vector3D v) => new(a.X - v.X, a.Y - v.Y, a.Z - v.Z);
}

/// <summary>A direction in the view.</summary>
public record struct Vector3D(double X, double Y, double Z)
{
    public readonly double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public void Normalize() { double l = Length; if (l > 0) { X /= l; Y /= l; Z /= l; } }
    public readonly Vector3D Normalized() { var v = this; v.Normalize(); return v; }
    public static Vector3D operator *(Vector3D v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3D operator -(Vector3D v) => new(-v.X, -v.Y, -v.Z);
    public static double DotProduct(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3D CrossProduct(Vector3D a, Vector3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vector3D Of(Vec3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>A box: its corner and its size.</summary>
public readonly record struct Rect3D(double X, double Y, double Z, double SizeX, double SizeY, double SizeZ);
