namespace Multiformats.Stream.Tests;

using BinaryEncoding;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

public class MultistreamTests
{
    [Fact]
    public Task Async_TestInvalidProtocol()
    {
        return UsePipeWithMuxerAsync(async (a, b, mux) =>
        {
            _ = mux.NegotiateAsync(a, CancellationToken.None);

            Multistream ms = Multistream.Create(b, "/THIS_IS_WRONG");

            _ = await Assert.ThrowsAsync<Exception>(() => ms.WriteAsync(Array.Empty<byte>(), 0, 0));
        });
    }

    [Fact]
    public Task Async_TestLazyConns()
    {
        return UsePipeWithMuxerAsync(async (a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task<Multistream> la = Task.Factory.StartNew(() => Multistream.CreateSelect(a, "/c"));
            Task<Multistream> lb = Task.Factory.StartNew(() => Multistream.CreateSelect(b, "/c"));

            Multistream[] result = await Task.WhenAll(la, lb);
            await VerifyPipeAsync(result[0], result[1]);
        });
    }

    [Fact]
    public Task Async_TestProtocolNegotation()
    {
        return UsePipeWithMuxerAsync(async (a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task<NegotiationResult> negotiator = mux.NegotiateAsync(a, CancellationToken.None);

            await MultistreamMuxer.SelectProtoOrFailAsync("/a", b, CancellationToken.None);

            string protocol = (await negotiator).Protocol;
            Assert.Equal("/a", protocol);
        }, verify: true);
    }

    [Fact]
    public Task Async_TestSelectFails()
    {
        return UsePipeWithMuxerAsync(async (a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            _ = mux.NegotiateAsync(a, CancellationToken.None);

            _ = await Assert.ThrowsAsync<NotSupportedException>(() => MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e" }, b, CancellationToken.None));
        });
    }

    [Fact]
    public Task Async_TestSelectOne()
    {
        return UsePipeWithMuxerAsync(async (a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task<NegotiationResult> negotiator = mux.NegotiateAsync(a, CancellationToken.None);

            _ = await MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e", "/c" }, b, CancellationToken.None);

            Task result = await Task.WhenAny(negotiator, Task.Delay(500));
            Assert.Equal(negotiator, result);
            Assert.Equal("/c", negotiator.Result.Protocol);
        }, verify: true);
    }

    [Fact]
    public Task Async_TestSelectOneAndWrite()
    {
        return UsePipeWithMuxerAsync(async (a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task<Task> task = Task.Factory.StartNew(async () =>
            {
                NegotiationResult selected = await mux.NegotiateAsync(a, CancellationToken.None);

                Assert.Equal("/c", selected.Protocol);
            });

            string sel = await MultistreamMuxer.SelectOneOfAsync(new[] { "/d", "/e", "/c" }, b, CancellationToken.None);
            Assert.Equal("/c", sel);
            Assert.True(task.Wait(500));
        }, verify: true);
    }

    [Fact]
    public void TestAddHandlerOverride()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler("/foo", (p, s) => throw new XunitException("should not get executed"));
            _ = mux.AddHandler("/foo", (p, s) => true);

            Task task = Task.Factory.StartNew(() => MultistreamMuxer.SelectProtoOrFail("/foo", a));

            Assert.True(mux.Handle(b));
            Assert.True(task.Wait(500));
        }, verify: true);
    }

    [Fact]
    public Task TestAddSyncAndAsyncHandlers()
    {
        return UsePipeWithMuxerAsync(async (a, b, mux) =>
        {
            _ = mux.AddHandler("/foo", asyncHandle: (p, s, c) => Task.FromResult(true));

            Task selectTask = MultistreamMuxer.SelectProtoOrFailAsync("/foo", a, CancellationToken.None);

            bool result = await mux.HandleAsync(b, CancellationToken.None);

            await selectTask;

            Assert.True(result);
        }, verify: true);
    }

    [Fact]
    public void TestHandleFunc()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", (p, s) =>
            {
                Assert.Equal("/c", p);

                return true;
            }));

            Task task = Task.Factory.StartNew(() => MultistreamMuxer.SelectProtoOrFail("/c", a));

            Assert.True(mux.Handle(b));
            Assert.True(task.Wait(500));
        }, verify: true);
    }

    [Fact]
    public void TestInvalidProtocol()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = Task.Factory.StartNew(() => mux.Negotiate(a));

            Multistream ms = Multistream.Create(b, "/THIS_IS_WRONG");

            _ = Assert.Throws<AggregateException>(() => ms.Write(Array.Empty<byte>(), 0, 0));
        });
    }

    [Fact]
    public void TestLazyAndMux()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task task = Task.Factory.StartNew(() =>
            {
                NegotiationResult selected = mux.Negotiate(a);
                Assert.Equal("/c", selected.Protocol);

                byte[] msg = new byte[5];
                int bytesRead = a.Read(msg, 0, msg.Length);
                Assert.Equal(bytesRead, msg.Length);
            });

            Multistream lb = Multistream.CreateSelect(b, "/c");
            byte[] outmsg = Encoding.UTF8.GetBytes("hello");
            lb.Write(outmsg, 0, outmsg.Length);

            Assert.True(task.Wait(500));

            VerifyPipe(a, lb);
        });
    }

    [Fact]
    public void TestLazyAndMuxWrite()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler("/a", null);
            _ = mux.AddHandler("/b", null);
            _ = mux.AddHandler("/c", null);

            Task doneTask = Task.Factory.StartNew(() =>
            {
                NegotiationResult selected = mux.Negotiate(a);
                Assert.Equal("/c", selected.Protocol);

                byte[] msg = Encoding.UTF8.GetBytes("hello");
                a.Write(msg, 0, msg.Length);
            });

            Multistream lb = Multistream.CreateSelect(b, "/c");
            byte[] msgin = new byte[5];
            int received = lb.Read(msgin, 0, msgin.Length);
            Assert.Equal(received, msgin.Length);
            Assert.Equal("hello", Encoding.UTF8.GetString(msgin));

            Assert.True(doneTask.Wait(500));

            VerifyPipe(a, lb);
        });
    }

    [Fact]
    public void TestLazyConns()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task<Multistream> la = Task.Factory.StartNew(() => Multistream.CreateSelect(a, "/c"));
            Task<Multistream> lb = Task.Factory.StartNew(() => Multistream.CreateSelect(b, "/c"));

            _ = Task.WhenAll(la, lb).ContinueWith(t => VerifyPipe(t.Result[0], t.Result[1]));
        });
    }

    [Fact]
    public void TestLs()
    {
        SubTestLs();
        SubTestLs("a");
        SubTestLs("a", "b", "c", "d", "e");
        SubTestLs("", "a");
    }

    [Fact]
    public void TestProtocolNegotation()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task<NegotiationResult> negotiator = Task.Factory.StartNew(() => mux.Negotiate(a));

            MultistreamMuxer.SelectProtoOrFail("/a", b);

            Task result = Task.WhenAny(negotiator, Task.Delay(500)).Result;
            Assert.True(result == negotiator);
            Assert.Equal("/a", negotiator.Result.Protocol);
        }, verify: true);
    }

    [Fact]
    public void TestRemoveProtocol()
    {
        MultistreamMuxer mux = new();
        _ = mux.AddHandler(new TestHandler("/a", null));
        _ = mux.AddHandler(new TestHandler("/b", null));
        _ = mux.AddHandler(new TestHandler("/c", null));

        List<string> protos = mux.Protocols.ToList();
        protos.Sort();
        Assert.Equal(protos, new[] { "/a", "/b", "/c" });

        mux.RemoveHandler("/b");

        protos = mux.Protocols.ToList();
        protos.Sort();
        Assert.Equal(protos, new[] { "/a", "/c" });
    }

    [Fact]
    public void TestSelectFails()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            _ = Task.Factory.StartNew(() => mux.Negotiate(a));

            _ = Assert.Throws<NotSupportedException>(() => MultistreamMuxer.SelectOneOf(new[] { "/d", "/e" }, b));
        });
    }

    [Fact]
    public void TestSelectOne()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task<NegotiationResult> negotiator = Task.Factory.StartNew(() => mux.Negotiate(a));

            _ = MultistreamMuxer.SelectOneOf(new[] { "/d", "/e", "/c" }, b);

            Task result = Task.WhenAny(negotiator, Task.Delay(500)).Result;
            Assert.True(result == negotiator);
            string protocol = negotiator.Result.Protocol;
            Assert.Equal("/c", protocol);
        }, verify: true);
    }

    [Fact]
    public void TestSelectOneAndWrite()
    {
        UsePipeWithMuxer((a, b, mux) =>
        {
            _ = mux.AddHandler(new TestHandler("/a", null));
            _ = mux.AddHandler(new TestHandler("/b", null));
            _ = mux.AddHandler(new TestHandler("/c", null));

            Task task = Task.Factory.StartNew(() =>
            {
                NegotiationResult selected = mux.Negotiate(a);

                Assert.Equal("/c", selected.Protocol);
            });

            string sel = MultistreamMuxer.SelectOneOf(new[] { "/d", "/e", "/c" }, b);
            Assert.Equal("/c", sel);
            Assert.True(task.Wait(500));
        }, verify: true);
    }

    private static async Task RunWithConnectedNetworkStreamsAsync(Func<NetworkStream, NetworkStream, Task> func)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        try
        {
            listener.Start(1);
            IPEndPoint clientEndPoint = (IPEndPoint)listener.LocalEndpoint;

            using TcpClient client = new(clientEndPoint.AddressFamily);
            Task<TcpClient> remoteTask = listener.AcceptTcpClientAsync();
            Task clientConnectTask = client.ConnectAsync(clientEndPoint.Address, clientEndPoint.Port);

            await Task.WhenAll(remoteTask, clientConnectTask);

            using TcpClient remote = remoteTask.Result;
            using NetworkStream serverStream = new(remote.Client, true);
            using NetworkStream clientStream = new(client.Client, true);
            await func(serverStream, clientStream);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void SubTestLs(params string[] protos)
    {
        MultistreamMuxer mr = new();
        Dictionary<string, bool> mset = new();
        foreach (string proto in protos)
        {
            _ = mr.AddHandler(proto, null);
            mset.Add(proto, true);
        }

        using MemoryStream buf = new();
        mr.Ls(buf);
        _ = buf.Seek(0, SeekOrigin.Begin);

        int count = Binary.Varint.Read(buf, out ulong n);
        Assert.Equal((int)n, buf.Length - count);

        _ = Binary.Varint.Read(buf, out ulong nitems);
        Assert.Equal((int)nitems, protos.Length);

        for (int i = 0; i < (int)nitems; i++)
        {
            string token = MultistreamMuxer.ReadNextToken(buf);
            Assert.True(mset.ContainsKey(token));
        }

        Assert.Equal(buf.Position, buf.Length);
    }

    private static void UsePipe(Action<Stream, Stream> action, int timeout = 1000, bool verify = false)
    {
        RunWithConnectedNetworkStreamsAsync((a, b) =>
        {
            try
            {
                action(a, b);
                if (verify)
                {
                    VerifyPipe(a, b);
                }

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }).Wait();
    }

    private static async Task UsePipeAsync(Func<Stream, Stream, Task> action, int timeout = 1000, bool verify = false)
    {
        await RunWithConnectedNetworkStreamsAsync(async (a, b) =>
        {
            await action(a, b);
            if (verify)
            {
                await VerifyPipeAsync(a, b);
            }
        });
    }

    private static void UsePipeWithMuxer(Action<Stream, Stream, MultistreamMuxer> action,
        int timeout = 1000, bool verify = false)
    {
        UsePipe((a, b) => action(a, b, new MultistreamMuxer()), timeout, verify);
    }

    private static Task UsePipeWithMuxerAsync(Func<Stream, Stream, MultistreamMuxer, Task> action,
        int timeout = 1000, bool verify = false)
    {
        return UsePipeAsync((a, b) => action(a, b, new MultistreamMuxer()), timeout, verify);
    }

    private static void VerifyPipe(Stream a, Stream b)
    {
        byte[] mes = new byte[1024];
        new Random().NextBytes(mes);
        _ = Task.Factory.StartNew(() =>
        {
            a.Write(mes, 0, mes.Length);
            b.Write(mes, 0, mes.Length);
        }).Wait(500);

        byte[] buf = new byte[mes.Length];
        int n = a.Read(buf, 0, buf.Length);
        Assert.Equal(n, buf.Length);
        Assert.Equal(buf, mes);

        n = b.Read(buf, 0, buf.Length);
        Assert.Equal(n, buf.Length);
        Assert.Equal(buf, mes);
    }

    private static async Task VerifyPipeAsync(Stream a, Stream b)
    {
        byte[] mes = new byte[1024];
        new Random().NextBytes(mes);
        mes[0] = 0x01;

        Task aw = a.WriteAsync(mes, 0, mes.Length);
        Task bw = b.WriteAsync(mes, 0, mes.Length);

        await Task.WhenAll(aw, bw).ConfigureAwait(false);

        byte[] buf = new byte[mes.Length];
        int n = await a.ReadAsync(buf).ConfigureAwait(false);
        Assert.Equal(n, buf.Length);
        Assert.Equal(buf, mes);

        n = await b.ReadAsync(buf).ConfigureAwait(false);
        Assert.Equal(n, buf.Length);
        Assert.Equal(buf, mes);
    }

    private class TestHandler : IMultistreamHandler
    {
        private readonly Func<string, Stream, bool> _action;

        public TestHandler(string protocol, Func<string, Stream, bool> action)
        {
            _action = action;
            Protocol = protocol;
        }

        public string Protocol { get; }

        public bool Handle(string protocol, Stream stream)
        {
            return _action?.Invoke(protocol, stream) ?? false;
        }

        public Task<bool> HandleAsync(string protocol, Stream stream, CancellationToken cancellationToken)
        {
            return Task.Factory.FromAsync(
                (p, s, cb, o) => ((Func<string, Stream, bool>)o).BeginInvoke(p, s, cb, o),
                (ar) => ((Func<string, Stream, bool>)ar.AsyncState).EndInvoke(ar),
                protocol, stream, state: _action);
        }
    }
}
