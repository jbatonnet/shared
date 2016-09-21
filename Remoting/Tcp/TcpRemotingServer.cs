using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Remoting.Tcp
{
    using System.Reflection;
    using RemoteId = Int32;
    using RemoteObject = MarshalByRefObject;

    public class TcpRemotingServer : RemotingServer
    {
        internal const ushort DefaultPort = 9090;

        private class Serializer : TcpRemotingSerializer
        {
            public TcpRemotingServer Server { get; }

            public Serializer(TcpRemotingServer server)
            {
                Server = server;
            }

            internal override void WriteObject(Stream stream, object value)
            {
                RemoteObject remoteObject = value as RemoteObject;
                if (remoteObject != null)
                {
                    RemoteId remoteId = Server.RegisterRemoteObject(remoteObject);
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

            internal override RemoteObject ReadRemoteObject(Stream stream)
            {
                throw new NotImplementedException();
            }
        }
        private class Client
        {
            public TcpClient TcpClient { get; }
            public NetworkStream NetworkStream { get; }
            public byte[] Buffer { get; } = new byte[1024];

            public List<RemoteObject> RemoteObjects { get; } = new List<RemoteObject>();

            public Client(TcpClient tcpClient)
            {
                TcpClient = tcpClient;
                NetworkStream = tcpClient.GetStream();
            }
        }

        public ushort Port { get; }

        private Dictionary<string, RemoteObject> baseObjects = new Dictionary<string, RemoteObject>();

        private List<RemoteId> remoteIds = new List<RemoteId>();
        private List<RemoteObject> remoteObjects = new List<RemoteObject>();
        private RemoteId currentRemoteId = 1;

        private Serializer serializer;
        private TcpListener tcpListener;
        private CancellationTokenSource cancellationToken = new CancellationTokenSource();

        public TcpRemotingServer() : this(DefaultPort) { }
        public TcpRemotingServer(ushort port)
        {
            Port = port;

            serializer = new Serializer(this);
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
            RegisterRemoteObject(value);
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
            Client client = new Client(tcpClient);

            // Trigger async read
            client.NetworkStream.BeginRead(client.Buffer, 0, 1, r => Client_ReadStream(client, r), null);
        }
        private void Client_ReadStream(Client client, IAsyncResult result)
        {
            int size = client.NetworkStream.EndRead(result);
            if (size <= 0)
                return;

            // Process current command
            TcpRemotingCommand command = (TcpRemotingCommand)client.Buffer[0];
            try
            {
                switch (command)
                {
                    case TcpRemotingCommand.Get: ProcessGet(client); break;
                    case TcpRemotingCommand.Call: ProcessCall(client); break;
                }
            }
            catch (Exception e)
            {
                client.NetworkStream.WriteByte((byte)TcpRemotingCommand.Exception);
                serializer.WriteException(client.NetworkStream, e);
            }

            // Trigger async read
            client.NetworkStream.BeginRead(client.Buffer, 0, 1, r => Client_ReadStream(client, r), null);
        }

        private void ProcessGet(Client client)
        {
            lock (client)
            using (BinaryReader reader = new BinaryReader(client.NetworkStream, Encoding.Default, true))
            {
                string name = reader.ReadString();
                RemoteObject remoteObject;

                if (!baseObjects.TryGetValue(name, out remoteObject))
                    throw new Exception("Could not find the specified object");

                int index = remoteObjects.IndexOf(remoteObject);
                RemoteId remoteId = remoteIds[index];

                serializer.WriteRemoteObject(client.NetworkStream, remoteId, remoteObject);
            }
        }
        private void ProcessCall(Client client)
        {
            string methodName;

            List<Type> methodSignature = new List<Type>();
            object[] methodArgs;

            Type type;
            MethodInfo typeMethod;
            RemoteObject remoteObject;

            using (BinaryReader reader = new BinaryReader(client.NetworkStream, Encoding.Default, true))
            {
                // Find remote object
                RemoteId remoteId = reader.ReadInt32();
                int index = remoteIds.IndexOf(remoteId);
                if (index == -1)
                    throw new Exception("Could not find specified remote object");

                remoteObject = remoteObjects[index];

                // Method info
                methodName = reader.ReadString();

                List<object> methodParameterValues = new List<object>();

                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    methodSignature.Add(serializer.ReadType(client.NetworkStream));
                    methodParameterValues.Add(serializer.ReadObject(client.NetworkStream));
                }

                methodArgs = methodParameterValues.ToArray();

                // Find the specified method
                type = remoteObject.GetType();
                typeMethod = type.GetMethod(methodName, methodSignature.ToArray());
            }

            using (BinaryWriter writer = new BinaryWriter(client.NetworkStream, Encoding.Default, true))
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
                        serializer.WriteObject(client.NetworkStream, pair.Value);
                    }

                    serializer.WriteObject(client.NetworkStream, result);
                }
                catch (Exception e)
                {
                    writer.Write((byte)TcpRemotingCommand.Exception);
                    serializer.WriteException(client.NetworkStream, e);
                }
            }
        }

        private RemoteId RegisterRemoteObject(RemoteObject remoteObject)
        {
            int index = remoteObjects.IndexOf(remoteObject);
            if (index >= 0)
                return remoteIds[index];

            RemoteId remoteId = currentRemoteId++;

            remoteIds.Add(remoteId);
            remoteObjects.Add(remoteObject);

            return remoteId;
        }
    }
}