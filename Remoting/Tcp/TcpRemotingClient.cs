using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;

namespace Remoting.Tcp
{
    using RemoteId = Int32;
    using RemoteObject = MarshalByRefObject;

    public class TcpRemotingClient : RemotingClient
    {
        private class Serializer : TcpRemotingSerializer
        {
            public TcpRemotingClient Client { get; }

            public Serializer(TcpRemotingClient client)
            {
                Client = client;
            }

            internal override RemoteObject ReadRemoteObject(Stream stream)
            {
                using (BinaryReader reader = new BinaryReader(stream, Encoding.Default, true))
                {
                    RemoteId remoteId = reader.ReadInt32();
                    Type type = ReadType(stream);

                    RemoteProxy remoteProxy = Client.remoteProxyIndex.GetObject(remoteId);
                    if (remoteProxy == null)
                    {
                        remoteProxy = new RemoteProxy(Client, remoteId, type);
                        Client.remoteProxyIndex.Register(remoteId, remoteProxy);
                    }

                    return remoteProxy.GetTransparentProxy() as RemoteObject;
                }
            }
        }

        public string Host { get; }
        public ushort Port { get; }

        private ObjectIndex<RemoteProxy> remoteProxyIndex = new ObjectIndex<RemoteProxy>();

        private Serializer serializer;
        private TcpClient tcpClient;
        private NetworkStream networkStream;

        public TcpRemotingClient() : this("127.0.0.1") { }
        public TcpRemotingClient(string host) : this(host, TcpRemotingServer.DefaultPort) { }
        public TcpRemotingClient(ushort port) : this("127.0.0.1", port) { }
        public TcpRemotingClient(string host, ushort port)
        {
            Host = host;
            Port = port;

            serializer = new Serializer(this);
            tcpClient = new TcpClient(host, port);
            networkStream = tcpClient.GetStream();
        }

        public override async Task<RemoteObject> GetObject(string name)
        {
            return await Task.Run(() =>
            {
                lock (networkStream)
                {
                    using (BinaryWriter writer = new BinaryWriter(networkStream, Encoding.Default, true))
                    {
                        writer.Write((byte)TcpRemotingCommand.Get);
                        writer.Write(name);
                    }

                    TcpRemotingCommand result = (TcpRemotingCommand)networkStream.ReadByte();
                    switch (result)
                    {
                        case TcpRemotingCommand.Exception: throw serializer.ReadException(networkStream);
                        case TcpRemotingCommand.Result: return serializer.ReadRemoteObject(networkStream);
                    }

                    throw new NotSupportedException("Could not understand server result " + result);
                }
            });
        }
        public override async Task<T> GetObject<T>(string name)
        {
            return await GetObject(name) as T;
        }

        protected override async Task<IMessage> ProcessMethod(RemoteId remoteId, IMethodCallMessage methodCallMessage)
        {
            return await Task.Run(() =>
            {
                lock (networkStream)
                {
                    Type[] methodSignature = (Type[])methodCallMessage.MethodSignature;

                    using (BinaryWriter writer = new BinaryWriter(networkStream, Encoding.Default, true))
                    {
                        writer.Write((byte)TcpRemotingCommand.Call);
                        writer.Write(remoteId);
                        writer.Write(methodCallMessage.MethodName);

                        // Write parameters
                        int count = methodSignature.Length;
                        writer.Write(count);

                        for (int i = 0; i < count; i++)
                        {
                            serializer.WriteType(networkStream, methodSignature[i]);
                            serializer.WriteObject(networkStream, methodCallMessage.Args[i]);
                        }
                    }

                    using (BinaryReader reader = new BinaryReader(networkStream, Encoding.Default, true))
                    { 
                        TcpRemotingCommand result = (TcpRemotingCommand)networkStream.ReadByte();
                        switch (result)
                        {
                            case TcpRemotingCommand.Exception: return new ReturnMessage(serializer.ReadException(networkStream), methodCallMessage);
                            case TcpRemotingCommand.Result:
                            {
                                // Read parameters
                                int count = reader.ReadInt32();

                                for (int i = 0; i < count; i++)
                                {
                                    int index = reader.ReadInt32();
                                    object value = serializer.ReadObject(networkStream);

                                    Copy(ref value, ref methodCallMessage.Args[index]);
                                }

                                return new ReturnMessage(serializer.ReadObject(networkStream), methodCallMessage.Args, methodCallMessage.ArgCount, methodCallMessage.LogicalCallContext, methodCallMessage);
                            }
                        }

                        throw new NotSupportedException("Could not understand server result " + result);
                    }
                }
            });
        }
    }
}