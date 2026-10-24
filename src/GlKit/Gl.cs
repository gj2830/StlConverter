using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.OpenGL;

namespace GlKit;

/// <summary>
/// The OpenGL calls the 3D views (<see cref="GlView"/>: PushPullMe's model, the converter's mesh) draw with, and no
/// more. On the desktop they go through Silk.NET (<see cref="SilkGl"/>, function pointers from the window's context); in
/// the browser straight to the WebGL functions linked into the app (<see cref="WebGl"/>): the browser's runtime cannot
/// call through Silk.NET's function pointers. A GL call a view needs anew goes into all three.
/// </summary>
public unsafe interface IGl
{
    string? GetStringS(StringName name);
    uint CreateShader(ShaderType type);
    void ShaderSource(uint shader, string source);
    void CompileShader(uint shader);
    void GetShader(uint shader, ShaderParameterName name, out int value);
    string GetShaderInfoLog(uint shader);
    void DeleteShader(uint shader);
    uint CreateProgram();
    void AttachShader(uint program, uint shader);
    void LinkProgram(uint program);
    void GetProgram(uint program, ProgramPropertyARB name, out int value);
    string GetProgramInfoLog(uint program);
    void UseProgram(uint program);
    void DeleteProgram(uint program);
    int GetUniformLocation(uint program, string name);
    void Uniform1(int at, float x);
    void Uniform1(int at, int x);
    void Uniform1(int at, uint count, int* values);
    void Uniform2(int at, float x, float y);
    void Uniform2(int at, uint count, float* values);
    void Uniform3(int at, float x, float y, float z);
    void Uniform4(int at, float x, float y, float z, float w);
    void UniformMatrix4(int at, uint count, bool transpose, float* values);
    uint GenVertexArray();
    void BindVertexArray(uint array);
    void DeleteVertexArray(uint array);
    uint GenBuffer();
    void BindBuffer(BufferTargetARB target, uint buffer);
    void BufferData(BufferTargetARB target, nuint size, void* data, BufferUsageARB usage);
    void DeleteBuffer(uint buffer);
    void EnableVertexAttribArray(uint index);
    void DisableVertexAttribArray(uint index);
    void VertexAttribPointer(uint index, int size, VertexAttribPointerType type, bool normalized, uint stride, void* offset);
    void VertexAttribDivisor(uint index, uint divisor);
    uint GenTexture();
    void BindTexture(TextureTarget target, uint texture);
    void TexStorage2D(TextureTarget target, uint levels, SizedInternalFormat format, uint width, uint height);
    void TexParameter(TextureTarget target, TextureParameterName name, int value);
    void DeleteTexture(uint texture);
    uint GenRenderbuffer();
    void BindRenderbuffer(RenderbufferTarget target, uint buffer);
    void RenderbufferStorage(RenderbufferTarget target, InternalFormat format, uint width, uint height);
    void DeleteRenderbuffer(uint buffer);
    uint GenFramebuffer();
    void BindFramebuffer(FramebufferTarget target, uint buffer);
    void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textureTarget, uint texture, int level);
    void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget bufferTarget, uint buffer);
    void DeleteFramebuffer(uint buffer);
    void Viewport(int x, int y, uint width, uint height);
    void ClearColor(float r, float g, float b, float a);
    void ClearDepth(float depth);
    void Clear(ClearBufferMask mask);
    void Enable(EnableCap cap);
    void Disable(EnableCap cap);
    void DepthFunc(DepthFunction func);
    void DepthMask(bool on);
    void ColorMask(bool r, bool g, bool b, bool a);
    void BlendEquation(BlendEquationModeEXT mode);
    void PolygonOffset(float factor, float units);
    void BlendFuncSeparate(BlendingFactor srcRgb, BlendingFactor dstRgb, BlendingFactor srcAlpha, BlendingFactor dstAlpha);
    void DrawArrays(PrimitiveType mode, int first, uint count);
    void DrawArraysInstanced(PrimitiveType mode, int first, uint count, uint instances);
    void ReadPixels(int x, int y, uint width, uint height, PixelFormat format, PixelType type, void* pixels);
}

/// <summary>OpenGL through Silk.NET (the desktop).</summary>
public sealed unsafe class SilkGl(GL gl) : IGl
{
    public string? GetStringS(StringName name) => gl.GetStringS(name);
    public uint CreateShader(ShaderType type) => gl.CreateShader(type);
    public void ShaderSource(uint shader, string source) => gl.ShaderSource(shader, source);
    public void CompileShader(uint shader) => gl.CompileShader(shader);
    public void GetShader(uint shader, ShaderParameterName name, out int value) => gl.GetShader(shader, name, out value);
    public string GetShaderInfoLog(uint shader) => gl.GetShaderInfoLog(shader);
    public void DeleteShader(uint shader) => gl.DeleteShader(shader);
    public uint CreateProgram() => gl.CreateProgram();
    public void AttachShader(uint program, uint shader) => gl.AttachShader(program, shader);
    public void LinkProgram(uint program) => gl.LinkProgram(program);
    public void GetProgram(uint program, ProgramPropertyARB name, out int value) => gl.GetProgram(program, name, out value);
    public string GetProgramInfoLog(uint program) => gl.GetProgramInfoLog(program);
    public void UseProgram(uint program) => gl.UseProgram(program);
    public void DeleteProgram(uint program) => gl.DeleteProgram(program);
    public int GetUniformLocation(uint program, string name) => gl.GetUniformLocation(program, name);
    public void Uniform1(int at, float x) => gl.Uniform1(at, x);
    public void Uniform1(int at, int x) => gl.Uniform1(at, x);
    public void Uniform1(int at, uint count, int* values) => gl.Uniform1(at, count, values);
    public void Uniform2(int at, float x, float y) => gl.Uniform2(at, x, y);
    public void Uniform2(int at, uint count, float* values) => gl.Uniform2(at, count, values);
    public void Uniform3(int at, float x, float y, float z) => gl.Uniform3(at, x, y, z);
    public void Uniform4(int at, float x, float y, float z, float w) => gl.Uniform4(at, x, y, z, w);
    public void UniformMatrix4(int at, uint count, bool transpose, float* values) => gl.UniformMatrix4(at, count, transpose, values);
    public uint GenVertexArray() => gl.GenVertexArray();
    public void BindVertexArray(uint array) => gl.BindVertexArray(array);
    public void DeleteVertexArray(uint array) => gl.DeleteVertexArray(array);
    public uint GenBuffer() => gl.GenBuffer();
    public void BindBuffer(BufferTargetARB target, uint buffer) => gl.BindBuffer(target, buffer);
    public void BufferData(BufferTargetARB target, nuint size, void* data, BufferUsageARB usage) => gl.BufferData(target, size, data, usage);
    public void DeleteBuffer(uint buffer) => gl.DeleteBuffer(buffer);
    public void EnableVertexAttribArray(uint index) => gl.EnableVertexAttribArray(index);
    public void DisableVertexAttribArray(uint index) => gl.DisableVertexAttribArray(index);
    public void VertexAttribPointer(uint index, int size, VertexAttribPointerType type, bool normalized, uint stride, void* offset) => gl.VertexAttribPointer(index, size, type, normalized, stride, offset);
    public void VertexAttribDivisor(uint index, uint divisor) => gl.VertexAttribDivisor(index, divisor);
    public uint GenTexture() => gl.GenTexture();
    public void BindTexture(TextureTarget target, uint texture) => gl.BindTexture(target, texture);
    public void TexStorage2D(TextureTarget target, uint levels, SizedInternalFormat format, uint width, uint height) => gl.TexStorage2D(target, levels, format, width, height);
    public void TexParameter(TextureTarget target, TextureParameterName name, int value) => gl.TexParameter(target, name, value);
    public void DeleteTexture(uint texture) => gl.DeleteTexture(texture);
    public uint GenRenderbuffer() => gl.GenRenderbuffer();
    public void BindRenderbuffer(RenderbufferTarget target, uint buffer) => gl.BindRenderbuffer(target, buffer);
    public void RenderbufferStorage(RenderbufferTarget target, InternalFormat format, uint width, uint height) => gl.RenderbufferStorage(target, format, width, height);
    public void DeleteRenderbuffer(uint buffer) => gl.DeleteRenderbuffer(buffer);
    public uint GenFramebuffer() => gl.GenFramebuffer();
    public void BindFramebuffer(FramebufferTarget target, uint buffer) => gl.BindFramebuffer(target, buffer);
    public void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textureTarget, uint texture, int level) => gl.FramebufferTexture2D(target, attachment, textureTarget, texture, level);
    public void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget bufferTarget, uint buffer) => gl.FramebufferRenderbuffer(target, attachment, bufferTarget, buffer);
    public void DeleteFramebuffer(uint buffer) => gl.DeleteFramebuffer(buffer);
    public void Viewport(int x, int y, uint width, uint height) => gl.Viewport(x, y, width, height);
    public void ClearColor(float r, float g, float b, float a) => gl.ClearColor(r, g, b, a);
    public void ClearDepth(float depth) => gl.ClearDepth(depth);
    public void Clear(ClearBufferMask mask) => gl.Clear(mask);
    public void Enable(EnableCap cap) => gl.Enable(cap);
    public void Disable(EnableCap cap) => gl.Disable(cap);
    public void DepthFunc(DepthFunction func) => gl.DepthFunc(func);
    public void DepthMask(bool on) => gl.DepthMask(on);
    public void ColorMask(bool r, bool g, bool b, bool a) => gl.ColorMask(r, g, b, a);
    public void BlendEquation(BlendEquationModeEXT mode) => gl.BlendEquation(mode);
    public void PolygonOffset(float factor, float units) => gl.PolygonOffset(factor, units);
    public void BlendFuncSeparate(BlendingFactor srcRgb, BlendingFactor dstRgb, BlendingFactor srcAlpha, BlendingFactor dstAlpha) => gl.BlendFuncSeparate(srcRgb, dstRgb, srcAlpha, dstAlpha);
    public void DrawArrays(PrimitiveType mode, int first, uint count) => gl.DrawArrays(mode, first, count);
    public void DrawArraysInstanced(PrimitiveType mode, int first, uint count, uint instances) => gl.DrawArraysInstanced(mode, first, count, instances);
    public void ReadPixels(int x, int y, uint width, uint height, PixelFormat format, PixelType type, void* pixels) => gl.ReadPixels(x, y, width, height, format, type, pixels);
}

/// <summary>
/// WebGL 2 in the browser: the GL functions linked into the app (they come with Skia, which draws Avalonia there),
/// called straight (DllImport), on the context current while Avalonia draws.
/// </summary>
public sealed unsafe class WebGl : IGl
{
    private const string Lib = "*"; // (linked into the app itself)

    [DllImport(Lib)] private static extern byte* glGetString(int name);
    [DllImport(Lib)] private static extern uint glCreateShader(int type);
    [DllImport(Lib)] private static extern void glShaderSource(uint shader, int count, byte** sources, int* lengths);
    [DllImport(Lib)] private static extern void glCompileShader(uint shader);
    [DllImport(Lib)] private static extern void glGetShaderiv(uint shader, int name, int* value);
    [DllImport(Lib)] private static extern void glGetShaderInfoLog(uint shader, int size, int* length, byte* log);
    [DllImport(Lib)] private static extern void glDeleteShader(uint shader);
    [DllImport(Lib)] private static extern uint glCreateProgram();
    [DllImport(Lib)] private static extern void glAttachShader(uint program, uint shader);
    [DllImport(Lib)] private static extern void glLinkProgram(uint program);
    [DllImport(Lib)] private static extern void glGetProgramiv(uint program, int name, int* value);
    [DllImport(Lib)] private static extern void glGetProgramInfoLog(uint program, int size, int* length, byte* log);
    [DllImport(Lib)] private static extern void glUseProgram(uint program);
    [DllImport(Lib)] private static extern void glDeleteProgram(uint program);
    [DllImport(Lib)] private static extern int glGetUniformLocation(uint program, byte* name);
    [DllImport(Lib)] private static extern void glUniform1f(int at, float x);
    [DllImport(Lib)] private static extern void glUniform1i(int at, int x);
    [DllImport(Lib)] private static extern void glUniform1iv(int at, int count, int* values);
    [DllImport(Lib)] private static extern void glUniform2f(int at, float x, float y);
    [DllImport(Lib)] private static extern void glUniform2fv(int at, int count, float* values);
    [DllImport(Lib)] private static extern void glUniform3f(int at, float x, float y, float z);
    [DllImport(Lib)] private static extern void glUniform4f(int at, float x, float y, float z, float w);
    [DllImport(Lib)] private static extern void glUniformMatrix4fv(int at, int count, byte transpose, float* values);
    [DllImport(Lib)] private static extern void glGenVertexArrays(int n, uint* arrays);
    [DllImport(Lib)] private static extern void glBindVertexArray(uint array);
    [DllImport(Lib)] private static extern void glDeleteVertexArrays(int n, uint* arrays);
    [DllImport(Lib)] private static extern void glGenBuffers(int n, uint* buffers);
    [DllImport(Lib)] private static extern void glBindBuffer(int target, uint buffer);
    [DllImport(Lib)] private static extern void glBufferData(int target, nint size, void* data, int usage);
    [DllImport(Lib)] private static extern void glDeleteBuffers(int n, uint* buffers);
    [DllImport(Lib)] private static extern void glEnableVertexAttribArray(uint index);
    [DllImport(Lib)] private static extern void glDisableVertexAttribArray(uint index);
    [DllImport(Lib)] private static extern void glVertexAttribPointer(uint index, int size, int type, byte normalized, int stride, void* offset);
    [DllImport(Lib)] private static extern void glVertexAttribDivisor(uint index, uint divisor);
    [DllImport(Lib)] private static extern void glGenTextures(int n, uint* textures);
    [DllImport(Lib)] private static extern void glBindTexture(int target, uint texture);
    [DllImport(Lib)] private static extern void glTexStorage2D(int target, int levels, int format, int width, int height);
    [DllImport(Lib)] private static extern void glTexParameteri(int target, int name, int value);
    [DllImport(Lib)] private static extern void glDeleteTextures(int n, uint* textures);
    [DllImport(Lib)] private static extern void glGenRenderbuffers(int n, uint* buffers);
    [DllImport(Lib)] private static extern void glBindRenderbuffer(int target, uint buffer);
    [DllImport(Lib)] private static extern void glRenderbufferStorage(int target, int format, int width, int height);
    [DllImport(Lib)] private static extern void glDeleteRenderbuffers(int n, uint* buffers);
    [DllImport(Lib)] private static extern void glGenFramebuffers(int n, uint* buffers);
    [DllImport(Lib)] private static extern void glBindFramebuffer(int target, uint buffer);
    [DllImport(Lib)] private static extern void glFramebufferTexture2D(int target, int attachment, int textureTarget, uint texture, int level);
    [DllImport(Lib)] private static extern void glFramebufferRenderbuffer(int target, int attachment, int bufferTarget, uint buffer);
    [DllImport(Lib)] private static extern void glDeleteFramebuffers(int n, uint* buffers);
    [DllImport(Lib)] private static extern void glViewport(int x, int y, int width, int height);
    [DllImport(Lib)] private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport(Lib)] private static extern void glClearDepthf(float depth);
    [DllImport(Lib)] private static extern void glClear(int mask);
    [DllImport(Lib)] private static extern void glEnable(int cap);
    [DllImport(Lib)] private static extern void glDisable(int cap);
    [DllImport(Lib)] private static extern void glDepthFunc(int func);
    [DllImport(Lib)] private static extern void glDepthMask(byte on);
    [DllImport(Lib)] private static extern void glColorMask(byte r, byte g, byte b, byte a);
    [DllImport(Lib)] private static extern void glBlendEquation(int mode);
    [DllImport(Lib)] private static extern void glPolygonOffset(float factor, float units);
    [DllImport(Lib)] private static extern void glBlendFuncSeparate(int srcRgb, int dstRgb, int srcAlpha, int dstAlpha);
    [DllImport(Lib)] private static extern void glDrawArrays(int mode, int first, int count);
    [DllImport(Lib)] private static extern void glDrawArraysInstanced(int mode, int first, int count, int instances);
    [DllImport(Lib)] private static extern void glReadPixels(int x, int y, int width, int height, int format, int type, void* pixels);

    private const int InfoLogLength = 0x8B84;

    /// <summary>A text as a zero-ended UTF-8 string for the call it is handed to.</summary>
    private static T WithUtf8<T>(string text, Func<nint, T> use)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes) return use((nint)p);
    }

    private static uint One(delegate*<int, uint*, void> gen) { uint id; gen(1, &id); return id; }
    private static void Free(delegate*<int, uint*, void> delete, uint id) => delete(1, &id);

    private static string Log(uint id, delegate*<uint, int, int*, void> param, delegate*<uint, int, int*, byte*, void> log)
    {
        int length;
        param(id, InfoLogLength, &length);
        if (length <= 0) return "";
        var bytes = new byte[length];
        int written;
        fixed (byte* p = bytes) log(id, length, &written, p);
        return Encoding.UTF8.GetString(bytes, 0, Math.Max(0, written));
    }

    public string? GetStringS(StringName name) => Marshal.PtrToStringUTF8((nint)glGetString((int)name));
    public uint CreateShader(ShaderType type) => glCreateShader((int)type);

    public void ShaderSource(uint shader, string source) => WithUtf8(source, p =>
    {
        byte* text = (byte*)p;
        glShaderSource(shader, 1, &text, null);
        return 0;
    });

    public void CompileShader(uint shader) => glCompileShader(shader);
    public void GetShader(uint shader, ShaderParameterName name, out int value) { int v; glGetShaderiv(shader, (int)name, &v); value = v; }
    public string GetShaderInfoLog(uint shader) => Log(shader, &glGetShaderiv, &glGetShaderInfoLog);
    public void DeleteShader(uint shader) => glDeleteShader(shader);
    public uint CreateProgram() => glCreateProgram();
    public void AttachShader(uint program, uint shader) => glAttachShader(program, shader);
    public void LinkProgram(uint program) => glLinkProgram(program);
    public void GetProgram(uint program, ProgramPropertyARB name, out int value) { int v; glGetProgramiv(program, (int)name, &v); value = v; }
    public string GetProgramInfoLog(uint program) => Log(program, &glGetProgramiv, &glGetProgramInfoLog);
    public void UseProgram(uint program) => glUseProgram(program);
    public void DeleteProgram(uint program) => glDeleteProgram(program);
    public int GetUniformLocation(uint program, string name) => WithUtf8(name, p => glGetUniformLocation(program, (byte*)p));
    public void Uniform1(int at, float x) => glUniform1f(at, x);
    public void Uniform1(int at, int x) => glUniform1i(at, x);
    public void Uniform1(int at, uint count, int* values) => glUniform1iv(at, (int)count, values);
    public void Uniform2(int at, float x, float y) => glUniform2f(at, x, y);
    public void Uniform2(int at, uint count, float* values) => glUniform2fv(at, (int)count, values);
    public void Uniform3(int at, float x, float y, float z) => glUniform3f(at, x, y, z);
    public void Uniform4(int at, float x, float y, float z, float w) => glUniform4f(at, x, y, z, w);
    public void UniformMatrix4(int at, uint count, bool transpose, float* values) => glUniformMatrix4fv(at, (int)count, (byte)(transpose ? 1 : 0), values);
    public uint GenVertexArray() => One(&glGenVertexArrays);
    public void BindVertexArray(uint array) => glBindVertexArray(array);
    public void DeleteVertexArray(uint array) => Free(&glDeleteVertexArrays, array);
    public uint GenBuffer() => One(&glGenBuffers);
    public void BindBuffer(BufferTargetARB target, uint buffer) => glBindBuffer((int)target, buffer);
    public void BufferData(BufferTargetARB target, nuint size, void* data, BufferUsageARB usage) => glBufferData((int)target, (nint)size, data, (int)usage);
    public void DeleteBuffer(uint buffer) => Free(&glDeleteBuffers, buffer);
    public void EnableVertexAttribArray(uint index) => glEnableVertexAttribArray(index);
    public void DisableVertexAttribArray(uint index) => glDisableVertexAttribArray(index);
    public void VertexAttribPointer(uint index, int size, VertexAttribPointerType type, bool normalized, uint stride, void* offset) => glVertexAttribPointer(index, size, (int)type, (byte)(normalized ? 1 : 0), (int)stride, offset);
    public void VertexAttribDivisor(uint index, uint divisor) => glVertexAttribDivisor(index, divisor);
    public uint GenTexture() => One(&glGenTextures);
    public void BindTexture(TextureTarget target, uint texture) => glBindTexture((int)target, texture);
    public void TexStorage2D(TextureTarget target, uint levels, SizedInternalFormat format, uint width, uint height) => glTexStorage2D((int)target, (int)levels, (int)format, (int)width, (int)height);
    public void TexParameter(TextureTarget target, TextureParameterName name, int value) => glTexParameteri((int)target, (int)name, value);
    public void DeleteTexture(uint texture) => Free(&glDeleteTextures, texture);
    public uint GenRenderbuffer() => One(&glGenRenderbuffers);
    public void BindRenderbuffer(RenderbufferTarget target, uint buffer) => glBindRenderbuffer((int)target, buffer);
    public void RenderbufferStorage(RenderbufferTarget target, InternalFormat format, uint width, uint height) => glRenderbufferStorage((int)target, (int)format, (int)width, (int)height);
    public void DeleteRenderbuffer(uint buffer) => Free(&glDeleteRenderbuffers, buffer);
    public uint GenFramebuffer() => One(&glGenFramebuffers);
    public void BindFramebuffer(FramebufferTarget target, uint buffer) => glBindFramebuffer((int)target, buffer);
    public void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textureTarget, uint texture, int level) => glFramebufferTexture2D((int)target, (int)attachment, (int)textureTarget, texture, level);
    public void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget bufferTarget, uint buffer) => glFramebufferRenderbuffer((int)target, (int)attachment, (int)bufferTarget, buffer);
    public void DeleteFramebuffer(uint buffer) => Free(&glDeleteFramebuffers, buffer);
    public void Viewport(int x, int y, uint width, uint height) => glViewport(x, y, (int)width, (int)height);
    public void ClearColor(float r, float g, float b, float a) => glClearColor(r, g, b, a);
    public void ClearDepth(float depth) => glClearDepthf(depth);
    public void Clear(ClearBufferMask mask) => glClear((int)mask);
    public void Enable(EnableCap cap) => glEnable((int)cap);
    public void Disable(EnableCap cap) => glDisable((int)cap);
    public void DepthFunc(DepthFunction func) => glDepthFunc((int)func);
    public void DepthMask(bool on) => glDepthMask((byte)(on ? 1 : 0));
    public void ColorMask(bool r, bool g, bool b, bool a) => glColorMask((byte)(r ? 1 : 0), (byte)(g ? 1 : 0), (byte)(b ? 1 : 0), (byte)(a ? 1 : 0));
    public void BlendEquation(BlendEquationModeEXT mode) => glBlendEquation((int)mode);
    public void PolygonOffset(float factor, float units) => glPolygonOffset(factor, units);
    public void BlendFuncSeparate(BlendingFactor srcRgb, BlendingFactor dstRgb, BlendingFactor srcAlpha, BlendingFactor dstAlpha) => glBlendFuncSeparate((int)srcRgb, (int)dstRgb, (int)srcAlpha, (int)dstAlpha);
    public void DrawArrays(PrimitiveType mode, int first, uint count) => glDrawArrays((int)mode, first, (int)count);
    public void DrawArraysInstanced(PrimitiveType mode, int first, uint count, uint instances) => glDrawArraysInstanced((int)mode, first, (int)count, (int)instances);
    public void ReadPixels(int x, int y, uint width, uint height, PixelFormat format, PixelType type, void* pixels) => glReadPixels(x, y, (int)width, (int)height, (int)format, (int)type, pixels);
}
