using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace GlKit;

/// <summary>
/// A 3D view drawn with OpenGL on every system: on the desktop as Avalonia's OpenGL control, in the browser (where
/// Avalonia has none) in Skia's own WebGL 2 context while Avalonia draws. The view itself only says what to draw
/// (<see cref="OnGlInit"/>, <see cref="OnGlRender"/>) through <see cref="IGl"/>, and asks for a new frame with
/// <see cref="Redraw"/>.
/// </summary>
public abstract class GlView : OpenGlControlBase
{
    private IGl? _gl;

    protected GlView()
    {
        if (OperatingSystem.IsBrowser()) AttachedToVisualTree += (_, _) => AddSurface();
    }

    /// <summary>The GL objects made (programs, buffers); <paramref name="version"/> says which shading language.</summary>
    protected abstract void OnGlInit(IGl gl, GlVersion version);

    /// <summary>The GL objects given back (the desktop control is going).</summary>
    protected abstract void OnGlDeinit(IGl gl);

    /// <summary>A frame into framebuffer <paramref name="fb"/> of <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
    protected abstract void OnGlRender(int fb, int width, int height);

    /// <summary>A frame asked for: on the desktop from OpenGL, in the browser from Avalonia's own drawing pass.</summary>
    protected void Redraw()
    {
        if (OperatingSystem.IsBrowser()) _surface?.InvalidateVisual();
        else RequestNextFrameRendering();
    }

    private (int W, int H) PixelSize(Size size)
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return (Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
    }

    // ------------------------------------------------------------------ on the desktop

    protected sealed override void OnOpenGlInit(GlInterface gl)
    {
        _gl = new SilkGl(GL.GetApi(gl.GetProcAddress));
        OnGlInit(_gl, GlVersion);
    }

    protected sealed override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl != null) OnGlDeinit(_gl);
        _gl = null;
    }

    protected sealed override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var (w, h) = PixelSize(Bounds.Size);
        OnGlRender(fb, w, h);
    }

    // ------------------------------------------------------------------ in the browser

    // (an OpenGL control is not drawn again on InvalidateVisual: a plain control right behind this one draws instead,
    // made when this one is placed; this one stays on top for the mouse)
    private Control? _surface;
    private uint _fb, _color, _depth;
    private int _w, _h;

    private void AddSurface()
    {
        if (_surface != null || Parent is not Panel panel) return;
        _surface = new Surface(this) { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment, VerticalAlignment = VerticalAlignment, Width = Width, Height = Height, Margin = Margin };
        panel.Children.Insert(panel.Children.IndexOf(this), _surface);
    }

    private sealed class Surface(GlView view) : Control
    {
        public override void Render(DrawingContext context) => context.Custom(new SkiaDraw(view, new Rect(Bounds.Size)));
    }

    private sealed class SkiaDraw(GlView view, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context) => view.RenderInSkia(context, bounds);
    }

    /// <summary>The frame drawn into a framebuffer of its own in Skia's WebGL context, and the picture into Skia as a texture.</summary>
    private void RenderInSkia(ImmediateDrawingContext context, Rect bounds)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        if (lease.GrContext is not { } gr) return;
        using (var api = lease.TryLeasePlatformGraphicsApi())
        {
            if (api?.Context is not IGlContext ctx) return;
            if (_gl == null) OnGlInit(_gl = new WebGl(), ctx.Version);
            var g = _gl;
            var (w, h) = PixelSize(bounds.Size);
            if (_fb == 0 || w != _w || h != _h)
            {
                if (_fb != 0) { g.DeleteFramebuffer(_fb); g.DeleteTexture(_color); g.DeleteRenderbuffer(_depth); }
                _w = w; _h = h;
                _color = g.GenTexture();
                g.BindTexture(TextureTarget.Texture2D, _color);
                g.TexStorage2D(TextureTarget.Texture2D, 1, SizedInternalFormat.Rgba8, (uint)w, (uint)h);
                g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _depth = g.GenRenderbuffer();
                g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
                g.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
                _fb = g.GenFramebuffer();
                g.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
                g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _color, 0);
                g.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
            }
            // (what Skia leaves set in the context and the drawing counts on not being there: a clip as scissor box above all)
            g.Disable(EnableCap.ScissorTest); g.Disable(EnableCap.StencilTest);
            g.ColorMask(true, true, true, true);
            g.BlendEquation(BlendEquationModeEXT.FuncAdd);
            OnGlRender((int)_fb, w, h);
            g.BindVertexArray(0);
        }
        // (Skia's own state in the context was changed under it)
        gr.ResetContext();
        using var texture = new GRBackendTexture(_w, _h, false, new GRGlTextureInfo((uint)TextureTarget.Texture2D, _color, (uint)InternalFormat.Rgba8));
        using var image = SKImage.FromTexture(gr, texture, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        if (image != null) lease.SkCanvas.DrawImage(image, SKRect.Create(0, 0, (float)bounds.Width, (float)bounds.Height));
    }
}
