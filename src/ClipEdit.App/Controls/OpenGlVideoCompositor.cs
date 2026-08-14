using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace ClipEdit.App.Controls;

internal sealed class OpenGlVideoCompositor : IDisposable
{
    private const uint ArrayBuffer = 0x8892;
    private const uint Blend = 0x0BE2;
    private const uint ColorAttachment0 = 0x8CE0;
    private const uint ColorBufferBit = 0x00004000;
    private const uint CullFace = 0x0B44;
    private const uint DepthTest = 0x0B71;
    private const uint DynamicDraw = 0x88E8;
    private const uint Float = 0x1406;
    private const uint FragmentShader = 0x8B30;
    private const uint Framebuffer = 0x8D40;
    private const uint FramebufferComplete = 0x8CD5;
    private const uint Linear = 0x2601;
    private const uint Rgba = 0x1908;
    private const uint ScissorTest = 0x0C11;
    private const uint Texture0 = 0x84C0;
    private const uint Texture2D = 0x0DE1;
    private const uint TextureMagFilter = 0x2800;
    private const uint TextureMinFilter = 0x2801;
    private const uint TextureWrapS = 0x2802;
    private const uint TextureWrapT = 0x2803;
    private const uint TriangleStrip = 0x0005;
    private const uint UnsignedByte = 0x1401;
    private const uint VertexShader = 0x8B31;
    private const uint ClampToEdge = 0x812F;
    private const uint CompileStatus = 0x8B81;
    private const uint LinkStatus = 0x8B82;
    private const uint Version = 0x1F02;

    private readonly ActiveTextureDelegate _activeTexture;
    private readonly AttachShaderDelegate _attachShader;
    private readonly BindBufferDelegate _bindBuffer;
    private readonly BindFramebufferDelegate _bindFramebuffer;
    private readonly BindTextureDelegate _bindTexture;
    private readonly BindVertexArrayDelegate? _bindVertexArray;
    private readonly BufferDataDelegate _bufferData;
    private readonly CheckFramebufferStatusDelegate _checkFramebufferStatus;
    private readonly ClearDelegate _clear;
    private readonly ClearColorDelegate _clearColor;
    private readonly CompileShaderDelegate _compileShader;
    private readonly CreateProgramDelegate _createProgram;
    private readonly CreateShaderDelegate _createShader;
    private readonly DeleteBuffersDelegate _deleteBuffers;
    private readonly DeleteFramebuffersDelegate _deleteFramebuffers;
    private readonly DeleteProgramDelegate _deleteProgram;
    private readonly DeleteShaderDelegate _deleteShader;
    private readonly DeleteTexturesDelegate _deleteTextures;
    private readonly DeleteVertexArraysDelegate? _deleteVertexArrays;
    private readonly DisableDelegate _disable;
    private readonly DrawArraysDelegate _drawArrays;
    private readonly EnableVertexAttribArrayDelegate _enableVertexAttribArray;
    private readonly FramebufferTexture2DDelegate _framebufferTexture2D;
    private readonly GenBuffersDelegate _genBuffers;
    private readonly GenFramebuffersDelegate _genFramebuffers;
    private readonly GenTexturesDelegate _genTextures;
    private readonly GenVertexArraysDelegate? _genVertexArrays;
    private readonly GetAttribLocationDelegate _getAttribLocation;
    private readonly GetProgramivDelegate _getProgramiv;
    private readonly GetShaderivDelegate _getShaderiv;
    private readonly GetStringDelegate _getString;
    private readonly GetUniformLocationDelegate _getUniformLocation;
    private readonly LinkProgramDelegate _linkProgram;
    private readonly ShaderSourceDelegate _shaderSource;
    private readonly TexImage2DDelegate _texImage2D;
    private readonly TexParameteriDelegate _texParameteri;
    private readonly Uniform1iDelegate _uniform1i;
    private readonly UseProgramDelegate _useProgram;
    private readonly VertexAttribPointerDelegate _vertexAttribPointer;
    private readonly ViewportDelegate _viewport;
    private readonly uint _program;
    private readonly uint _vertexBuffer;
    private readonly uint _vertexArray;
    private readonly int _positionAttribute;
    private readonly int _textureAttribute;
    private readonly int _textureUniform;
    private uint _sourceFramebuffer;
    private uint _sourceTexture;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _disposed;

    public OpenGlVideoCompositor(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _activeTexture = Load<ActiveTextureDelegate>(gl, "glActiveTexture");
        _attachShader = Load<AttachShaderDelegate>(gl, "glAttachShader");
        _bindBuffer = Load<BindBufferDelegate>(gl, "glBindBuffer");
        _bindFramebuffer = Load<BindFramebufferDelegate>(gl, "glBindFramebuffer");
        _bindTexture = Load<BindTextureDelegate>(gl, "glBindTexture");
        _bufferData = Load<BufferDataDelegate>(gl, "glBufferData");
        _checkFramebufferStatus = Load<CheckFramebufferStatusDelegate>(gl, "glCheckFramebufferStatus");
        _clear = Load<ClearDelegate>(gl, "glClear");
        _clearColor = Load<ClearColorDelegate>(gl, "glClearColor");
        _compileShader = Load<CompileShaderDelegate>(gl, "glCompileShader");
        _createProgram = Load<CreateProgramDelegate>(gl, "glCreateProgram");
        _createShader = Load<CreateShaderDelegate>(gl, "glCreateShader");
        _deleteBuffers = Load<DeleteBuffersDelegate>(gl, "glDeleteBuffers");
        _deleteFramebuffers = Load<DeleteFramebuffersDelegate>(gl, "glDeleteFramebuffers");
        _deleteProgram = Load<DeleteProgramDelegate>(gl, "glDeleteProgram");
        _deleteShader = Load<DeleteShaderDelegate>(gl, "glDeleteShader");
        _deleteTextures = Load<DeleteTexturesDelegate>(gl, "glDeleteTextures");
        _disable = Load<DisableDelegate>(gl, "glDisable");
        _drawArrays = Load<DrawArraysDelegate>(gl, "glDrawArrays");
        _enableVertexAttribArray = Load<EnableVertexAttribArrayDelegate>(gl, "glEnableVertexAttribArray");
        _framebufferTexture2D = Load<FramebufferTexture2DDelegate>(gl, "glFramebufferTexture2D");
        _genBuffers = Load<GenBuffersDelegate>(gl, "glGenBuffers");
        _genFramebuffers = Load<GenFramebuffersDelegate>(gl, "glGenFramebuffers");
        _genTextures = Load<GenTexturesDelegate>(gl, "glGenTextures");
        _getAttribLocation = Load<GetAttribLocationDelegate>(gl, "glGetAttribLocation");
        _getProgramiv = Load<GetProgramivDelegate>(gl, "glGetProgramiv");
        _getShaderiv = Load<GetShaderivDelegate>(gl, "glGetShaderiv");
        _getString = Load<GetStringDelegate>(gl, "glGetString");
        _getUniformLocation = Load<GetUniformLocationDelegate>(gl, "glGetUniformLocation");
        _linkProgram = Load<LinkProgramDelegate>(gl, "glLinkProgram");
        _shaderSource = Load<ShaderSourceDelegate>(gl, "glShaderSource");
        _texImage2D = Load<TexImage2DDelegate>(gl, "glTexImage2D");
        _texParameteri = Load<TexParameteriDelegate>(gl, "glTexParameteri");
        _uniform1i = Load<Uniform1iDelegate>(gl, "glUniform1i");
        _useProgram = Load<UseProgramDelegate>(gl, "glUseProgram");
        _vertexAttribPointer = Load<VertexAttribPointerDelegate>(gl, "glVertexAttribPointer");
        _viewport = Load<ViewportDelegate>(gl, "glViewport");
        _genVertexArrays = LoadOptional<GenVertexArraysDelegate>(gl, "glGenVertexArrays");
        _bindVertexArray = LoadOptional<BindVertexArrayDelegate>(gl, "glBindVertexArray");
        _deleteVertexArrays = LoadOptional<DeleteVertexArraysDelegate>(gl, "glDeleteVertexArrays");

        var versionText = Marshal.PtrToStringAnsi(_getString(Version)) ?? string.Empty;
        var isOpenGlEs = versionText.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        var vertexShader = Compile(
            VertexShader,
            isOpenGlEs
                ? "#version 100\nattribute vec2 aPosition; attribute vec2 aTexCoord; varying vec2 vTexCoord; void main(){ gl_Position=vec4(aPosition,0.0,1.0); vTexCoord=aTexCoord; }"
                : "#version 130\nin vec2 aPosition; in vec2 aTexCoord; out vec2 vTexCoord; void main(){ gl_Position=vec4(aPosition,0.0,1.0); vTexCoord=aTexCoord; }");
        var fragmentShader = Compile(
            FragmentShader,
            isOpenGlEs
                ? "#version 100\nprecision mediump float; varying vec2 vTexCoord; uniform sampler2D uTexture; void main(){ gl_FragColor=texture2D(uTexture,vTexCoord); }"
                : "#version 130\nin vec2 vTexCoord; uniform sampler2D uTexture; out vec4 outputColor; void main(){ outputColor=texture(uTexture,vTexCoord); }");
        _program = _createProgram();
        _attachShader(_program, vertexShader);
        _attachShader(_program, fragmentShader);
        _linkProgram(_program);
        _getProgramiv(_program, LinkStatus, out var linked);
        _deleteShader(vertexShader);
        _deleteShader(fragmentShader);
        if (linked == 0)
        {
            throw new InvalidOperationException("Could not link the live-preview OpenGL compositor.");
        }

        _positionAttribute = _getAttribLocation(_program, "aPosition");
        _textureAttribute = _getAttribLocation(_program, "aTexCoord");
        _textureUniform = _getUniformLocation(_program, "uTexture");
        if (_positionAttribute < 0 || _textureAttribute < 0 || _textureUniform < 0)
        {
            throw new InvalidOperationException("The live-preview OpenGL compositor is missing a shader input.");
        }

        _genBuffers(1, out _vertexBuffer);
        if (_genVertexArrays is not null && _bindVertexArray is not null)
        {
            _genVertexArrays(1, out _vertexArray);
        }
    }

    public int SourceFramebuffer => checked((int)_sourceFramebuffer);

    public void EnsureSourceTarget(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sourceFramebuffer != 0 && width == _sourceWidth && height == _sourceHeight)
        {
            return;
        }

        DeleteSourceTarget();
        _genTextures(1, out _sourceTexture);
        _bindTexture(Texture2D, _sourceTexture);
        _texParameteri(Texture2D, TextureMinFilter, checked((int)Linear));
        _texParameteri(Texture2D, TextureMagFilter, checked((int)Linear));
        _texParameteri(Texture2D, TextureWrapS, checked((int)ClampToEdge));
        _texParameteri(Texture2D, TextureWrapT, checked((int)ClampToEdge));
        _texImage2D(Texture2D, 0, checked((int)Rgba), width, height, 0, Rgba, UnsignedByte, nint.Zero);

        _genFramebuffers(1, out _sourceFramebuffer);
        _bindFramebuffer(Framebuffer, _sourceFramebuffer);
        _framebufferTexture2D(Framebuffer, ColorAttachment0, Texture2D, _sourceTexture, 0);
        if (_checkFramebufferStatus(Framebuffer) != FramebufferComplete)
        {
            DeleteSourceTarget();
            throw new InvalidOperationException("Could not create the live-preview source framebuffer.");
        }

        _sourceWidth = width;
        _sourceHeight = height;
    }

    public void Composite(int targetFramebuffer, int width, int height, float[] vertices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Length != 16)
        {
            throw new ArgumentException("A transformed video quad requires four XY/UV vertices.", nameof(vertices));
        }

        _bindFramebuffer(Framebuffer, checked((uint)targetFramebuffer));
        _viewport(0, 0, width, height);
        _disable(Blend);
        _disable(CullFace);
        _disable(DepthTest);
        _disable(ScissorTest);
        _clearColor(0, 0, 0, 1);
        _clear(ColorBufferBit);
        _useProgram(_program);
        if (_vertexArray != 0)
        {
            _bindVertexArray!(_vertexArray);
        }

        _bindBuffer(ArrayBuffer, _vertexBuffer);
        var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
        try
        {
            _bufferData(
                ArrayBuffer,
                checked((nint)(vertices.Length * sizeof(float))),
                handle.AddrOfPinnedObject(),
                DynamicDraw);
        }
        finally
        {
            handle.Free();
        }

        const int stride = 4 * sizeof(float);
        _enableVertexAttribArray(checked((uint)_positionAttribute));
        _vertexAttribPointer(checked((uint)_positionAttribute), 2, Float, 0, stride, nint.Zero);
        _enableVertexAttribArray(checked((uint)_textureAttribute));
        _vertexAttribPointer(
            checked((uint)_textureAttribute),
            2,
            Float,
            0,
            stride,
            checked((nint)(2 * sizeof(float))));
        _activeTexture(Texture0);
        _bindTexture(Texture2D, _sourceTexture);
        _uniform1i(_textureUniform, 0);
        _drawArrays(TriangleStrip, 0, 4);
        _bindTexture(Texture2D, 0);
        _bindBuffer(ArrayBuffer, 0);
        if (_vertexArray != 0)
        {
            _bindVertexArray!(0);
        }

        _useProgram(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteSourceTarget();
        if (_vertexBuffer != 0)
        {
            var buffer = _vertexBuffer;
            _deleteBuffers(1, ref buffer);
        }

        if (_vertexArray != 0 && _deleteVertexArrays is not null)
        {
            var vertexArray = _vertexArray;
            _deleteVertexArrays(1, ref vertexArray);
        }

        _deleteProgram(_program);
    }

    private uint Compile(uint type, string source)
    {
        var shader = _createShader(type);
        var sourcePointer = Marshal.StringToCoTaskMemUTF8(source);
        var pointerArray = Marshal.AllocCoTaskMem(nint.Size);
        try
        {
            Marshal.WriteIntPtr(pointerArray, sourcePointer);
            _shaderSource(shader, 1, pointerArray, nint.Zero);
            _compileShader(shader);
            _getShaderiv(shader, CompileStatus, out var compiled);
            if (compiled == 0)
            {
                _deleteShader(shader);
                throw new InvalidOperationException("Could not compile the live-preview OpenGL compositor shader.");
            }

            return shader;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointerArray);
            Marshal.FreeCoTaskMem(sourcePointer);
        }
    }

    private void DeleteSourceTarget()
    {
        if (_sourceFramebuffer != 0)
        {
            var framebuffer = _sourceFramebuffer;
            _deleteFramebuffers(1, ref framebuffer);
            _sourceFramebuffer = 0;
        }

        if (_sourceTexture != 0)
        {
            var texture = _sourceTexture;
            _deleteTextures(1, ref texture);
            _sourceTexture = 0;
        }

        _sourceWidth = 0;
        _sourceHeight = 0;
    }

    private static T Load<T>(GlInterface gl, string name)
        where T : Delegate
    {
        var pointer = gl.GetProcAddress(name);
        if (pointer == nint.Zero)
        {
            throw new InvalidOperationException($"OpenGL function {name} is unavailable.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static T? LoadOptional<T>(GlInterface gl, string name)
        where T : Delegate
    {
        var pointer = gl.GetProcAddress(name);
        return pointer == nint.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ActiveTextureDelegate(uint texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AttachShaderDelegate(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindBufferDelegate(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindFramebufferDelegate(uint target, uint framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindTextureDelegate(uint target, uint texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BindVertexArrayDelegate(uint vertexArray);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BufferDataDelegate(uint target, nint size, nint data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CheckFramebufferStatusDelegate(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ClearDelegate(uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ClearColorDelegate(float red, float green, float blue, float alpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void CompileShaderDelegate(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CreateProgramDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint CreateShaderDelegate(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteBuffersDelegate(int count, ref uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteFramebuffersDelegate(int count, ref uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteProgramDelegate(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteShaderDelegate(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteTexturesDelegate(int count, ref uint textures);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteVertexArraysDelegate(int count, ref uint vertexArrays);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DisableDelegate(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DrawArraysDelegate(uint mode, int first, int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EnableVertexAttribArrayDelegate(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void FramebufferTexture2DDelegate(uint target, uint attachment, uint textureTarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenBuffersDelegate(int count, out uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenFramebuffersDelegate(int count, out uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenTexturesDelegate(int count, out uint textures);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GenVertexArraysDelegate(int count, out uint vertexArrays);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetAttribLocationDelegate(uint program, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetProgramivDelegate(uint program, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetShaderivDelegate(uint shader, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint GetStringDelegate(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetUniformLocationDelegate(uint program, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void LinkProgramDelegate(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ShaderSourceDelegate(uint shader, int count, nint strings, nint lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TexImage2DDelegate(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, nint pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TexParameteriDelegate(uint target, uint name, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Uniform1iDelegate(int location, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UseProgramDelegate(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void VertexAttribPointerDelegate(uint index, int size, uint type, int normalized, int stride, nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ViewportDelegate(int x, int y, int width, int height);
}
