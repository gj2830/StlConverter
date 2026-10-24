using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.Rendering;
using GlKit;
using Silk.NET.OpenGL;

namespace StlConverter;

/// <summary>Triangles in one colour (each three positions one triangle, in the file's units); with, for picking, the
/// source triangle of each (null: not to be picked).</summary>
public sealed class MeshLayer
{
    public required float[] Positions { get; init; }
    public required Vector4 Color { get; init; }
    /// <summary>The inside's colour (null: the same as the outside, lit from behind as well).</summary>
    public Vector4? BackColor { get; init; }
    public IReadOnlyList<int>? Source { get; init; }
    public bool Transparent => Color.W < 0.999f;
}

/// <summary>Lines between pairs of points, a fixed width in pixels.</summary>
public sealed class LineLayer
{
    public required float[] Points { get; init; }
    public required Vector4 Color { get; init; }
    public double Width { get; init; } = 1;
}

/// <summary>
/// The mesh in 3D through OpenGL (on Windows through ANGLE, on Linux GLX/EGL, on macOS OpenGL), as the WPF window had
/// it with HelixToolkit: the faces lit by a light at the eye, the layers over them (selection, groups, the face shown),
/// the edges. The right button turns the view (round the point under the mouse), the middle one pans, the wheel zooms
/// toward the mouse; the left button is the window's (selecting).
/// </summary>
public sealed class MeshView : GlView, ICustomHitTest
{
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    // ------------------------------------------------------------------ what is shown

    private IReadOnlyList<MeshLayer> _base = [], _overlay = [], _coil = [];
    private MeshLayer? _highlight;
    private readonly Dictionary<string, LineLayer> _lines = new();
    private bool _dirty = true;

    /// <summary>The model, one layer per colour.</summary>
    public IReadOnlyList<MeshLayer> Base { get => _base; set { _base = value; Changed(); } }

    /// <summary>The selection and the groups waiting, a little above the model.</summary>
    public IReadOnlyList<MeshLayer> Overlay { get => _overlay; set { _overlay = value; Changed(); } }

    /// <summary>The face looked at (yellow).</summary>
    public MeshLayer? Highlight { get => _highlight; set { _highlight = value; Changed(); } }

    /// <summary>The thread coils (gold).</summary>
    public IReadOnlyList<MeshLayer> Coil { get => _coil; set { _coil = value; Changed(); } }

    /// <summary>A set of lines by name (null: away).</summary>
    public void SetLines(string name, LineLayer? lines)
    {
        if (lines == null) _lines.Remove(name); else _lines[name] = lines;
        Changed();
    }

    public bool HasLines(string name) => _lines.ContainsKey(name);

    private void Changed() { _dirty = true; Redraw(); }

    // ------------------------------------------------------------------ the camera

    public Vector3D Position { get; private set; } = new(0, -200, 100);
    public Vector3D Look { get; private set; } = new(0, 200, -100);
    public Vector3D Up { get; private set; } = new(0, 0, 1);

    /// <summary>The way the camera looks (its length: how far away the point turned round is).</summary>
    public Vector3D LookDirection => Look;

    private const double FieldOfView = 45;

    public void SetView(Vector3D position, Vector3D look, Vector3D up)
    {
        if (look.Length < 1e-12 || Vector3D.CrossProduct(look, up).Length < 1e-9 * look.Length * up.Length) return;
        if (!double.IsFinite(position.X + position.Y + position.Z + look.X + look.Y + look.Z)) return;
        Position = position; Look = look; Up = up;
        Redraw();
    }

    /// <summary>The camera frame: eye, right, up, forward (unit), and how far the view reaches across and up per unit forward.</summary>
    private (Vector3D Eye, Vector3D R, Vector3D U, Vector3D F, double Tx, double Ty)? Frame()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 1 || h < 1) return null;
        var f = Look.Normalized();
        var r = Vector3D.CrossProduct(f, Up);
        if (r.Length < 1e-12) return null;
        r.Normalize();
        var u = Vector3D.CrossProduct(r, f);
        double t = Math.Tan(FieldOfView * Math.PI / 360);
        return (Position, r, u, f, t, t * h / w);
    }

    /// <summary>The ray from the eye through a point of the view.</summary>
    public (Vector3D Origin, Vector3D Dir)? Ray(Point pos)
    {
        if (Frame() is not { } c) return null;
        var d = c.F + c.R * ((2 * pos.X / Bounds.Width - 1) * c.Tx) + c.U * ((1 - 2 * pos.Y / Bounds.Height) * c.Ty);
        return (c.Eye, d.Normalized());
    }

    /// <summary>Where a point is on the screen.</summary>
    public Point? Project(Vector3D p)
    {
        if (Frame() is not { } c) return null;
        var v = p - c.Eye; double z = Vector3D.DotProduct(v, c.F);
        if (Math.Abs(z) < 1e-12) return null;
        return new Point(Bounds.Width / 2 * (1 + Vector3D.DotProduct(v, c.R) / (z * c.Tx)), Bounds.Height / 2 * (1 - Vector3D.DotProduct(v, c.U) / (z * c.Ty)));
    }

    /// <summary>Everything (or the box) in view, looking the way the camera looks.</summary>
    public void ZoomExtents(Rect3D? box = null)
    {
        var b = box ?? Bounds3D();
        if (b is not { } r) return;
        var centre = new Vector3D(r.X + r.SizeX / 2, r.Y + r.SizeY / 2, r.Z + r.SizeZ / 2);
        double radius = Math.Max(1e-9, new Vector3D(r.SizeX, r.SizeY, r.SizeZ).Length / 2);
        double t = Math.Tan(FieldOfView * Math.PI / 360) * Math.Min(1, Bounds.Height > 0 ? Bounds.Height / Math.Max(1, Bounds.Width) : 1);
        double dist = radius / Math.Sin(Math.Atan(t)) * 1.05;
        var f = Look.Normalized();
        SetView(centre - f * dist, f * dist, Up);
    }

    private Rect3D? Bounds3D()
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        bool any = false;
        foreach (var l in _base.Concat(_coil))
            for (int i = 0; i + 2 < l.Positions.Length; i += 3)
            {
                any = true;
                x0 = Math.Min(x0, l.Positions[i]); y0 = Math.Min(y0, l.Positions[i + 1]); z0 = Math.Min(z0, l.Positions[i + 2]);
                x1 = Math.Max(x1, l.Positions[i]); y1 = Math.Max(y1, l.Positions[i + 1]); z1 = Math.Max(z1, l.Positions[i + 2]);
            }
        return any ? new Rect3D(x0, y0, z0, x1 - x0, y1 - y0, z1 - z0) : null;
    }

    // ------------------------------------------------------------------ picking

    /// <summary>The pickable triangles under a point of the view, nearest first: the layer, the triangle in it and where.</summary>
    public List<(MeshLayer Layer, int Triangle, Point3D At)> FindHits(Point pos)
    {
        var hits = new List<(double T, MeshLayer Layer, int Triangle)>();
        if (Ray(pos) is not { } ray) return [];
        foreach (var layer in _base.Concat(_overlay).Concat(_highlight is { } h ? [h] : []))
        {
            if (layer.Source == null) continue;
            var p = layer.Positions;
            for (int i = 0; i + 8 < p.Length; i += 9)
                if (RayTriangle(ray.Origin, ray.Dir, new(p[i], p[i + 1], p[i + 2]), new(p[i + 3], p[i + 4], p[i + 5]), new(p[i + 6], p[i + 7], p[i + 8])) is double t)
                    hits.Add((t, layer, i / 9));
        }
        return hits.OrderBy(x => x.T).Select(x =>
        {
            var at = ray.Origin + ray.Dir * x.T;
            return (x.Layer, x.Triangle, new Point3D(at.X, at.Y, at.Z));
        }).ToList();
    }

    private static double? RayTriangle(Vector3D o, Vector3D d, Vector3D a, Vector3D b, Vector3D c)
    {
        Vector3D e1 = b - a, e2 = c - a;
        var h = Vector3D.CrossProduct(d, e2); double det = Vector3D.DotProduct(e1, h);
        if (Math.Abs(det) < 1e-18) return null;
        var s = o - a; double u = Vector3D.DotProduct(s, h) / det;
        if (u < 0 || u > 1) return null;
        var q = Vector3D.CrossProduct(s, e1); double v = Vector3D.DotProduct(d, q) / det;
        if (v < 0 || u + v > 1) return null;
        double t = Vector3D.DotProduct(e2, q) / det;
        return t > 0 ? t : null;
    }

    // ------------------------------------------------------------------ the mouse: turning, panning, zooming

    private Point? _turnFrom, _panFrom;
    private Vector3D _pivot;
    private double _panDepth;

    public MeshView()
    {
        Focusable = true;
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Redraw(); };
    }

    private Vector3D PointUnder(Point pos) => FindHits(pos) is [var first, ..] ? new Vector3D(first.At.X, first.At.Y, first.At.Z) : Position + Look;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        var kind = pt.Properties.PointerUpdateKind;
        bool pan = kind == PointerUpdateKind.MiddleButtonPressed || (kind == PointerUpdateKind.RightButtonPressed && (e.KeyModifiers & KeyModifiers.Shift) != 0);
        if (pan)
        {
            var grab = PointUnder(pt.Position);
            _panDepth = Vector3D.DotProduct(grab - Position, Look.Normalized());
            _panFrom = pt.Position;
        }
        else if (kind == PointerUpdateKind.RightButtonPressed)
        {
            _pivot = PointUnder(pt.Position);
            _turnFrom = pt.Position;
        }
        else return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_turnFrom is { } from)
        {
            _turnFrom = pos;
            Orbit(pos.X - from.X, pos.Y - from.Y);
            e.Handled = true;
        }
        else if (_panFrom is { } pf && Frame() is { } c)
        {
            _panFrom = pos;
            double sx = 2 * _panDepth * c.Tx / Bounds.Width, sy = 2 * _panDepth * c.Ty / Bounds.Height;
            SetView(Position - c.R * ((pos.X - pf.X) * sx) + c.U * ((pos.Y - pf.Y) * sy), Look, Up);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_turnFrom == null && _panFrom == null) return;
        _turnFrom = _panFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        var pos = e.GetPosition(this);
        Vector3D target = FindHits(pos) is [var h, ..] ? new Vector3D(h.At.X, h.At.Y, h.At.Z)
            : Ray(pos) is { } r ? r.Origin + r.Dir * Look.Length : Position + Look;
        double s = Math.Pow(0.8, e.Delta.Y);
        SetView(target + (Position - target) * s, Look * s, Up);
    }

    /// <summary>Turned round the pivot as the mouse moved (0.4° a pixel): left/right round the screen's up, up/down round
    /// its sideways direction.</summary>
    private void Orbit(double dx, double dy)
    {
        var f = Look.Normalized();
        var up = Up - f * Vector3D.DotProduct(Up, f);
        if (up.Length < 1e-9) return;
        up.Normalize();
        var right = Vector3D.CrossProduct(f, up);
        double turn = -dx * 0.4, tilt = -dy * 0.4;
        Vector3D Dir(Vector3D v) => Rotate(Rotate(v, up, turn), right, tilt);
        SetView(_pivot + Dir(Position - _pivot), Dir(Look), Dir(up));
    }

    private static Vector3D Rotate(Vector3D v, Vector3D axis, double degrees)
    {
        var k = axis.Normalized();
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return v * c + Vector3D.CrossProduct(k, v) * s + k * (Vector3D.DotProduct(k, v) * (1 - c));
    }

    // ------------------------------------------------------------------ drawing

    private IGl? _gl;
    private uint _faceProgram, _lineProgram;
    private int _uMvp, _uLight, _uColor, _uBack, _uTwoSided, _uLineMvp, _uHalf;

    private sealed class Batch { public uint Vao, Vbo; public int Count; public Vector4 Color; public Vector4? Back; public float Width; }
    private readonly List<Batch> _opaque = new(), _seeThrough = new(), _lineBatches = new();
    private readonly Stack<Batch> _spare = new();

    /// <summary>A frame asked for: its pixels (width, height, RGBA rows top down) once drawn.</summary>
    private TaskCompletionSource<(int W, int H, byte[] Rgba)>? _capture;

    public Task<(int W, int H, byte[] Rgba)> CaptureAsync()
    {
        var tcs = _capture ??= new TaskCompletionSource<(int, int, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        Redraw();
        return tcs.Task;
    }

    protected override void OnGlInit(IGl gl, GlVersion version)
    {
        _gl = gl;
        string head = version.Type == GlProfileType.OpenGLES ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        _faceProgram = Program(head + """
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec3 aNormal;
            uniform mat4 uMvp;
            out vec3 vNormal;
            void main() { gl_Position = uMvp * vec4(aPos, 1.0); vNormal = aNormal; }
            """, head + """
            in vec3 vNormal;
            uniform vec3 uLight;
            uniform vec4 uColor;
            uniform vec4 uBack;
            uniform int uTwoSided;
            out vec4 fragColor;
            void main()
            {
                // a white light at the eye and the ambient light (#707078) added up, as WPF lights the HelixToolkit view
                bool front = gl_FrontFacing || uTwoSided == 1;
                vec3 n = normalize(gl_FrontFacing ? vNormal : -vNormal);
                vec4 c = front ? uColor : uBack;
                vec3 light = min(vec3(1.0), vec3(0.44, 0.44, 0.47) + max(dot(n, -uLight), 0.0));
                fragColor = vec4(c.rgb * light, c.a);
            }
            """);
        _lineProgram = Program(head + """
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec3 aOther;
            layout(location = 2) in float aSide;
            uniform mat4 uMvp;
            uniform vec2 uHalf;
            uniform float uWidth;
            void main()
            {
                vec4 p = uMvp * vec4(aPos, 1.0);
                vec4 o = uMvp * vec4(aOther, 1.0);
                vec2 ps = p.xy / max(p.w, 1e-6) * uHalf;
                vec2 os = o.xy / max(o.w, 1e-6) * uHalf;
                vec2 d = os - ps;
                d = length(d) < 1e-6 ? vec2(1.0, 0.0) : normalize(d);
                vec2 n = vec2(-d.y, d.x);
                p.xy += n * aSide * (uWidth * 0.5) / uHalf * p.w;
                gl_Position = p;
            }
            """, head + """
            uniform vec4 uColor;
            out vec4 fragColor;
            void main() { fragColor = uColor; }
            """);
        _uMvp = _gl.GetUniformLocation(_faceProgram, "uMvp");
        _uLight = _gl.GetUniformLocation(_faceProgram, "uLight");
        _uColor = _gl.GetUniformLocation(_faceProgram, "uColor");
        _uBack = _gl.GetUniformLocation(_faceProgram, "uBack");
        _uTwoSided = _gl.GetUniformLocation(_faceProgram, "uTwoSided");
        _uLineMvp = _gl.GetUniformLocation(_lineProgram, "uMvp");
        _uHalf = _gl.GetUniformLocation(_lineProgram, "uHalf");
        _dirty = true;
    }

    protected override void OnGlDeinit(IGl gl)
    {
        if (_gl == null) return;
        foreach (var b in _opaque.Concat(_seeThrough).Concat(_lineBatches).Concat(_spare)) { _gl.DeleteBuffer(b.Vbo); _gl.DeleteVertexArray(b.Vao); }
        _opaque.Clear(); _seeThrough.Clear(); _lineBatches.Clear(); _spare.Clear();
        _gl.DeleteProgram(_faceProgram); _gl.DeleteProgram(_lineProgram);
        _gl = null;
    }

    private uint Program(string vertex, string fragment)
    {
        var gl = _gl!;
        uint Shader(ShaderType type, string src)
        {
            uint s = gl.CreateShader(type);
            gl.ShaderSource(s, src);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type}: {gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint vs = Shader(ShaderType.VertexShader, vertex), fs = Shader(ShaderType.FragmentShader, fragment);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, vs); gl.AttachShader(p, fs);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException("link: " + gl.GetProgramInfoLog(p));
        gl.DeleteShader(vs); gl.DeleteShader(fs);
        return p;
    }

    /// <summary>A batch of 6 floats a vertex (faces: position, normal; lines: position, other end; side in the last).</summary>
    private unsafe Batch NewBatch(float[] data, bool lines)
    {
        var gl = _gl!;
        var b = _spare.Count > 0 ? _spare.Pop() : new Batch { Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer() };
        gl.BindVertexArray(b.Vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vbo);
        fixed (float* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        int stride = lines ? 7 : 6;
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)(stride * sizeof(float)), (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, (uint)(stride * sizeof(float)), (void*)(3 * sizeof(float)));
        if (lines) { gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, (uint)(stride * sizeof(float)), (void*)(6 * sizeof(float))); }
        else gl.DisableVertexAttribArray(2);
        gl.BindVertexArray(0);
        b.Count = data.Length / stride;
        return b;
    }

    /// <summary>Positions with a normal per triangle (flat, as WPF shades a mesh of separate triangles).</summary>
    private static float[] WithNormals(float[] p)
    {
        var d = new float[p.Length * 2];
        for (int i = 0; i + 8 < p.Length; i += 9)
        {
            var a = new Vector3(p[i], p[i + 1], p[i + 2]); var b = new Vector3(p[i + 3], p[i + 4], p[i + 5]); var c = new Vector3(p[i + 6], p[i + 7], p[i + 8]);
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
            for (int k = 0; k < 3; k++)
            {
                int o = (i + 3 * k) * 2;
                d[o] = p[i + 3 * k]; d[o + 1] = p[i + 3 * k + 1]; d[o + 2] = p[i + 3 * k + 2];
                d[o + 3] = n.X; d[o + 4] = n.Y; d[o + 5] = n.Z;
            }
        }
        return d;
    }

    private static float[] Quads(float[] pts)
    {
        var d = new List<float>(pts.Length / 6 * 42);
        for (int i = 0; i + 5 < pts.Length; i += 6)
        {
            void V(int at, int other, float side) { d.Add(pts[at]); d.Add(pts[at + 1]); d.Add(pts[at + 2]); d.Add(pts[other]); d.Add(pts[other + 1]); d.Add(pts[other + 2]); d.Add(side); }
            V(i, i + 3, 1); V(i, i + 3, -1); V(i + 3, i, -1);
            V(i, i + 3, -1); V(i + 3, i, 1); V(i + 3, i, -1);
        }
        return d.ToArray();
    }

    private void Upload()
    {
        foreach (var b in _opaque.Concat(_seeThrough).Concat(_lineBatches)) _spare.Push(b);
        _opaque.Clear(); _seeThrough.Clear(); _lineBatches.Clear();
        foreach (var l in _base.Concat(_coil).Concat(_overlay).Concat(_highlight is { } h ? [h] : []))
        {
            if (l.Positions.Length < 9) continue;
            var b = NewBatch(WithNormals(l.Positions), false);
            b.Color = l.Color; b.Back = l.BackColor;
            (l.Transparent ? _seeThrough : _opaque).Add(b);
        }
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        foreach (var l in _lines.Values)
        {
            if (l.Points.Length < 6) continue;
            var b = NewBatch(Quads(l.Points), true);
            b.Color = l.Color; b.Width = (float)(Math.Max(1, l.Width) * scale);
            _lineBatches.Add(b);
        }
    }

    private static Matrix4x4 ClipMatrix((Vector3D Eye, Vector3D R, Vector3D U, Vector3D F, double Tx, double Ty) f, double near, double far)
    {
        double a = (far + near) / (far - near), b = -2 * far * near / (far - near);
        var r = f.R * (1 / f.Tx); var u = f.U * (1 / f.Ty); var fw = f.F;
        double er = -Vector3D.DotProduct(r, f.Eye), eu = -Vector3D.DotProduct(u, f.Eye), ef = -Vector3D.DotProduct(fw, f.Eye);
        return new Matrix4x4(
            (float)r.X, (float)u.X, (float)(a * fw.X), (float)fw.X,
            (float)r.Y, (float)u.Y, (float)(a * fw.Y), (float)fw.Y,
            (float)r.Z, (float)u.Z, (float)(a * fw.Z), (float)fw.Z,
            (float)er, (float)eu, (float)(a * ef + b), (float)ef);
    }

    protected override unsafe void OnGlRender(int fb, int w, int h)
    {
        if (_gl == null) return;
        var g = _gl;
        if (_dirty) { Upload(); _dirty = false; }
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        g.Viewport(0, 0, (uint)w, (uint)h);
        // (transparent: the window's gradient shows through, as behind the WPF viewport)
        g.ClearColor(0, 0, 0, 0);
        g.ClearDepth(1);
        g.DepthMask(true);
        g.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (Frame() is { } frame)
        {
            double near = Math.Max(Look.Length / 1000, 1e-6);
            var mvp = ClipMatrix(frame, near, near * 1e5);
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Lequal);
            g.Disable(EnableCap.CullFace);
            g.Disable(EnableCap.Blend);

            g.UseProgram(_faceProgram);
            g.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
            g.Uniform3(_uLight, (float)frame.F.X, (float)frame.F.Y, (float)frame.F.Z);
            // the faces pushed back a little, so the edges on them lie in front
            g.Enable(EnableCap.PolygonOffsetFill);
            g.PolygonOffset(1, 1);
            foreach (var b in _opaque) DrawFaces(b);
            g.Disable(EnableCap.PolygonOffsetFill);

            g.UseProgram(_lineProgram);
            g.UniformMatrix4(_uLineMvp, 1, false, (float*)&mvp);
            g.Uniform2(_uHalf, w / 2f, h / 2f);
            int uWidth = g.GetUniformLocation(_lineProgram, "uWidth"), uLineColor = g.GetUniformLocation(_lineProgram, "uColor");
            foreach (var b in _lineBatches)
            {
                g.Uniform1(uWidth, b.Width);
                g.Uniform4(uLineColor, b.Color.X, b.Color.Y, b.Color.Z, b.Color.W);
                g.BindVertexArray(b.Vao);
                g.DrawArrays(PrimitiveType.Triangles, 0, (uint)b.Count);
            }

            g.Enable(EnableCap.Blend);
            g.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            g.DepthMask(false);
            g.UseProgram(_faceProgram);
            foreach (var b in _seeThrough) DrawFaces(b);
            g.DepthMask(true);
            g.Disable(EnableCap.Blend);
            g.BindVertexArray(0);
        }

        if (_capture is { } capture)
        {
            _capture = null;
            var pixels = new byte[w * h * 4];
            fixed (byte* p = pixels) g.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            var rows = new byte[pixels.Length];
            for (int y = 0; y < h; y++) Array.Copy(pixels, (h - 1 - y) * w * 4, rows, y * w * 4, w * 4);
            capture.SetResult((w, h, rows));
        }

        void DrawFaces(Batch b)
        {
            var back = b.Back ?? b.Color;
            g.Uniform4(_uColor, b.Color.X, b.Color.Y, b.Color.Z, b.Color.W);
            g.Uniform4(_uBack, back.X, back.Y, back.Z, b.Color.W);
            g.Uniform1(_uTwoSided, b.Back == null ? 1 : 0);
            g.BindVertexArray(b.Vao);
            g.DrawArrays(PrimitiveType.Triangles, 0, (uint)b.Count);
        }
    }
}
