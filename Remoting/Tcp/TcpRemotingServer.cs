using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Remoting.Tcp
{
    using RemoteId = Int32;
    using RemoteObject = MarshalByRefObject;

    public class TcpRemotingServer : RemotingServer
    {
        internal const ushort DefaultPort = 9090;

        private class Serializer : TcpRemotingSerializer
        {
            public TcpRemotingServer Server { get; }
            public Client Client { get; }

            public Serializer(TcpRemotingServer server, Client client)
            {
                Server = server;
                Client = client;
            }

            internal override void WriteObject(Stream stream, object value)
            {
                RemoteObject remoteObject = value as RemoteObject;
                if (remoteObject != null)
                {
                    RemoteId remoteId = Client.remoteObjectIndex.GetId(remoteObject);
                    WriteRemoteObject(stream, remoteId, remoteObject);
                    return;
                }

                base.WriteObject(stream, value);
            }
            internal void WriteRemoteObject(Stream stream, RemoteId remoteId, RemoteObject remoteObject)
            {
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.Default, true))
                {
                    writer.Write((byte)TcpRemotingType.RemoteObject);
                    writer.Write(remoteId);
                    WriteType(stream, remoteObject.GetType());
                }
            }

            internal override object ReadObject(Stream stream, TcpRemotingType remotingType)
            {
                switch (remotingType)
                {
                    case TcpRemotingType.Delegate: return ReadDelegate(stream);
                }

                return base.ReadObject(stream, remotingType);
            }
            internal override RemoteObject ReadRemoteObject(Stream stream)
            {
                throw new NotImplementedException();
            }
            internal Delegate ReadDelegate(Stream stream)
            {
                using (BinaryReader reader = new BinaryReader(stream, Encoding.Default, true))
                {
                    RemoteId delegateId = reader.ReadInt32();
                    Type delegateType = ReadType(stream);

                    Delegate delegateObject = Client.delegateIndex.GetObject(delegateId);
                    if (delegateObject == null)
                    {
                        delegateObject = CreateDelegate(delegateType, delegateId);
                        Client.delegateIndex.Register(delegateId, delegateObject);
                    }

                    return null;
                }
            }

            protected override object OnDelegateCall(int remoteId, object[] args)
            {
                throw new NotImplementedException();
            }
        }
        private class Client
        {
            public TcpRemotingServer Server { get; }
            public TcpClient TcpClient { get; }

            public NetworkStream NetworkStream { get; }
            public Serializer Serializer { get; }

            public ObjectIndex<RemoteObject> remoteObjectIndex = new ObjectIndex<RemoteObject>();
            public ObjectIndex<Delegate> delegateIndex = new ObjectIndex<Delegate>();

            private byte[] buffer = new byte[1];

            public Client(TcpRemotingServer server, TcpClient tcpClient)
            {
                Server = server;
                TcpClient = tcpClient;

                NetworkStream = tcpClient.GetStream();
                Serializer = new Serializer(server, this);

                // Trigger async read
                NetworkStream.BeginRead(buffer, 0, 1, NetworkStream_Read, null);
            }

            private void NetworkStream_Read(IAsyncResult result)
            {
                lock (NetworkStream)
                {
                    int size = NetworkStream.EndRead(result);
                    if (size <= 0)
                        return;

                    // Process current command
                    TcpRemotingCommand command = (TcpRemotingCommand)buffer[0];
                    try
                    {
                        switch (command)
                        {
                            case TcpRemotingCommand.Get: ProcessGet(); break;
                            case TcpRemotingCommand.Call: ProcessCall(); break;
                        }
                    }
                    catch (Exception e)
                    {
                        NetworkStream.WriteByte((byte)TcpRemotingCommand.Exception);
                        Serializer.WriteException(NetworkStream, e);
                    }
                }

                // Trigger async read
                NetworkStream.BeginRead(buffer, 0, 1, NetworkStream_Read, null);
            }

            private void ProcessGet()
            {
                using (BinaryReader reader = new BinaryReader(NetworkStream, Encoding.Default, true))
                {
                    string name = reader.ReadString();
                    RemoteObject remoteObject;

                    if (!Server.baseObjects.TryGetValue(name, out remoteObject))
                        throw new Exception("Could not find the specified object");

                    RemoteId remoteId = remoteObjectIndex.GetId(remoteObject);

                    Serializer.WriteRemoteObject(NetworkStream, remoteId, remoteObject);
                }
            }
            private void ProcessCall()
            {
                string methodName;

                List<Type> methodSignature = new List<Type>();
                object[] methodArgs;

                Type type;
                MethodInfo typeMethod;
                RemoteObject remoteObject;

                using (BinaryReader reader = new BinaryReader(NetworkStream, Encoding.Default, true))
                {
                    // Find remote object
                    RemoteId remoteId = reader.ReadInt32();

                    remoteObject = remoteObjectIndex.GetObject(remoteId);
                    if (remoteObject == null)
                        throw new Exception("Could not find specified remote object");

                    // Method info
                    methodName = reader.ReadString();

                    List<object> methodParameterValues = new List<object>();

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        methodSignature.Add(Serializer.ReadType(NetworkStream));
                        methodParameterValues.Add(Serializer.ReadObject(NetworkStream));
                    }

                    methodArgs = methodParameterValues.ToArray();

                    // Find the specified method
                    type = remoteObject.GetType();
                    typeMethod = type.GetMethod(methodName, methodSignature.ToArray());
                }

                using (BinaryWriter writer = new BinaryWriter(NetworkStream, Encoding.Default, true))
                {
                    // Invoke the method
                    try
                    {
                        object result = typeMethod.Invoke(remoteObject, methodArgs);

                        writer.Write((byte)TcpRemotingCommand.Result);

                        Dictionary<int, object> outParameters = new Dictionary<int, object>();
                        ParameterInfo[] methodParameters = typeMethod.GetParameters();
                        for (int i = 0; i < methodParameters.Length; i++)
                        {
                            if (methodParameters[i].ParameterType.IsValueType)
                                continue;

                            outParameters.Add(i, methodArgs[i]);
                        }

                        writer.Write(outParameters.Count);

                        foreach (var pair in outParameters)
                        {
                            writer.Write(pair.Key);
                            Serializer.WriteObject(NetworkStream, pair.Value);
                        }

                        Serializer.WriteObject(NetworkStream, result);
                    }
                    catch (Exception e)
                    {
                        writer.Write((byte)TcpRemotingCommand.Exception);
                        Serializer.WriteException(NetworkStream, e);
                    }
                }
            }
        }

        public ushort Port { get; }

        private Dictionary<string, RemoteObject> baseObjects = new Dictionary<string, RemoteObject>();

        private TcpListener tcpListener;
        private CancellationTokenSource cancellationToken = new CancellationTokenSource();

        public TcpRemotingServer() : this(DefaultPort) { }
        public TcpRemotingServer(ushort port)
        {
            Port = port;

            tcpListener = new TcpListener(IPAddress.Any, port);
        }

        public override void Start()
        {
            tcpListener.Start();
            tcpListener.BeginAcceptTcpClient(Server_AcceptClient, null);
        }
        public override void Stop()
        {
            cancellationToken?.Cancel();

            cancellationToken = new CancellationTokenSource();
        }

        public override void AddObject(string name, RemoteObject value)
        {
            string url = $"http://*:{Port}/{name}/";

            baseObjects.Add(name, value);
        }
        public override void RemoveObject(string name)
        {
            throw new NotImplementedException();
        }

        private void Server_AcceptClient(IAsyncResult result)
        {
            // Accept other clients
            tcpListener.BeginAcceptTcpClient(Server_AcceptClient, null);

            // Process current client
            TcpClient tcpClient = tcpListener.EndAcceptTcpClient(result);
            Client client = new Client(this, tcpClient);
        }
    }
}