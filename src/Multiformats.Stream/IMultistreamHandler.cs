namespace Multiformats.Stream;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public interface IMultistreamHandler
{
    string Protocol { get; }
    bool Handle(string protocol, Stream stream);
    Task<bool> HandleAsync(string protocol, Stream stream, CancellationToken cancellationToken);
}

public delegate bool StreamHandlerFunc(string protocol, Stream stream);
public delegate Task<bool> AsyncStreamHandlerFunc(string protocol, Stream stream, CancellationToken cancellationToken);

internal class FuncStreamHandler : IMultistreamHandler
{
    private readonly StreamHandlerFunc _handle;
    private readonly AsyncStreamHandlerFunc _asyncHandle;
    public string Protocol { get; }

    public FuncStreamHandler(string protocol, StreamHandlerFunc handle = null, AsyncStreamHandlerFunc asyncHandle = null)
    {
        _handle = handle;
        _asyncHandle = asyncHandle;

        Protocol = protocol;
    }

    public bool Handle(string protocol, Stream stream)
    {
        if (_handle != null)
            return _handle.Invoke(protocol, stream);

        if (_asyncHandle != null)
            return
                _asyncHandle.Invoke(protocol, stream, CancellationToken.None)
                    .ConfigureAwait(true)
                    .GetAwaiter()
                    .GetResult();

        return false;
    }

    public Task<bool> HandleAsync(string protocol, Stream stream, CancellationToken cancellationToken)
    {
        if (_asyncHandle != null)
            return _asyncHandle(protocol, stream, cancellationToken);

        if (_handle != null)
            return
                Task.Factory.FromAsync(
                    (p, s, cb, o) => ((Func<string, Stream, bool>) o).BeginInvoke(p, s, cb, o),
                    (ar) => ((Func<string, Stream, bool>) ar.AsyncState).EndInvoke(ar),
                    protocol, stream, state: _handle);

        return Task.FromResult(false);
    }
}
