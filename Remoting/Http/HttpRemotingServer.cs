﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Remoting.Http
{
    using RemoteId = Int32;
    using RemoteObject = MarshalByRefObject;

    public class HttpRemotingServer : RemotingServer
    {
        internal const ushort DefaultPort = 8080;

        private class Serializer : HttpRemotingSerializer
        {
            public HttpRemotingServer Server { get; }

            public Serializer(HttpRemotingServer server)
            {
                Server = server;
            }

            internal override XElement WrapObject(object value)
            {
                RemoteObject target = value as RemoteObject;

                if (target != null)
                {
                    RemoteId id = Server.RegisterRemoteObject(target);
                    return WrapRemoteObject(id, target);
                }

                return base.WrapObject(value);
            }
            internal XElement WrapRemoteObject(RemoteId id, RemoteObject target)
            {
                XElement objectElement = new XElement("RemoteObject");

                // Send remote object id
                objectElement.Add(new XAttribute("Id", id));

                // Send remote object type hierarchy
                Type type = target.GetType();
                XElement typeElement = new XElement("Type", new XAttribute("FullName", type.FullName), new XAttribute("Assembly", type.Assembly.FullName));
                objectElement.Add(typeElement);

                while (type.BaseType != null && type.BaseType != typeof(object))
                {
                    type = type.BaseType;

                    XElement parentElement = new XElement("Parent", new XAttribute("FullName", type.FullName), new XAttribute("Assembly", type.Assembly.FullName));
                    typeElement.Add(parentElement);
                }

                return objectElement;
            }
        }

        private static Regex httpHeaderRegex = new Regex("^(?<Method>GET|POST) (?<Url>[^ ]+) HTTP/1.1$", RegexOptions.Compiled);
        private static Regex headerRegex = new Regex("^(?<Key>[^:]+): (?<Value>.+)", RegexOptions.Compiled);

        public ushort Port { get; }

        private Dictionary<string, RemoteObject> baseObjects = new Dictionary<string, RemoteObject>();
        private Dictionary<RemoteId, RemoteObject> remoteObjects = new Dictionary<RemoteId, RemoteObject>();
        private Dictionary<RemoteObject, RemoteId> remoteObjectsIndices = new Dictionary<RemoteObject, RemoteId>();
        private RemoteId currentRemoteIndex = 1;

        private Serializer serializer;
        private TcpListener tcpListener;
        private CancellationTokenSource cancellationToken = new CancellationTokenSource();

        public HttpRemotingServer() : this(DefaultPort) { }
        public HttpRemotingServer(ushort port)
        {
            Port = port;

            serializer = new Serializer(this);
            tcpListener = new TcpListener(IPAddress.Any, port);
        }

        public override void Start()
        {
            tcpListener.Start();

            Task.Run(() => ServerLoop());
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

        private void ServerLoop()
        {
            CancellationTokenSource token = cancellationToken;

            while (!token.IsCancellationRequested)
            {
                TcpClient client = tcpListener.AcceptTcpClient();
                Stream clientStream = client.GetStream();

                try
                {
                    string method;
                    string url;
                    XDocument requestDocument = null;

                    using (StreamReader requestReader = new StreamReader(clientStream, Encoding.UTF8, false, 128, true))
                    {
                        string line = requestReader.ReadLine();

                        // HTTP header
                        Match httpHeader = httpHeaderRegex.Match(line);
                        if (!httpHeader.Success)
                            throw new Exception("Not a valid HTTP request");

                        method = httpHeader.Groups["Method"].Value.ToUpper();
                        url = httpHeader.Groups["Url"].Value;

                        // Process headers
                        int? contentLength = null;

                        while (true)
                        {
                            line = requestReader.ReadLine();
                            if (string.IsNullOrEmpty(line))
                                break;

                            Match header = headerRegex.Match(line);
                            if (!header.Success)
                                throw new Exception("Could not parse request headers");

                            switch (header.Groups[1].Value)
                            {
                                case "Content-Length": contentLength = int.Parse(header.Groups[2].Value); break;
                            }
                        }

                        if (contentLength != null && contentLength > 0)
                        {
                            // Decode request document
                            char[] requestContentChars = new char[contentLength.Value];
                            requestReader.ReadBlock(requestContentChars, 0, contentLength.Value);
                            string requestContent = new string(requestContentChars);
                            requestDocument = XDocument.Parse(requestContent);
                        }
                    }

                    // Build a reponse
                    XDocument responseDocument = null;

                    // Process request
                    if (method == "GET")
                    {
                        // Find the specified object
                        string name = url.Substring(1);
                        RemoteObject target;
                        if (!baseObjects.TryGetValue(name, out target))
                            throw new Exception("Could not find the specified object");

                        // Find its ID
                        RemoteId id;
                        if (!remoteObjectsIndices.TryGetValue(target, out id))
                            throw new Exception("Could not find the specified object in remote objects");

                        responseDocument = ProcessGet(id, target);
                    }
                    else
                    {
                        string idString = url.Substring(1);
                        RemoteId id = RemoteId.Parse(idString);

                        RemoteObject target;
                        if (!remoteObjects.TryGetValue(id, out target))
                            throw new Exception("Could not find the specified object in remote objects");

                        switch (requestDocument.Root.Name.LocalName)
                        {
                            case "Call": responseDocument = ProcessCall(id, target, requestDocument); break;

                            default:
                                throw new Exception("Unhandled request type");
                        }
                    }

                    using (StreamWriter responseWriter = new StreamWriter(clientStream))
                    {
                        responseWriter.NewLine = "\r\n";

                        responseWriter.WriteLine("HTTP/1.1 200 OK");
                        responseWriter.WriteLine("Connection: Closed");
                        responseWriter.WriteLine();

                        responseWriter.WriteLine(responseDocument.ToString());
                    }
                }
                catch (Exception e)
                {
                    using (StreamWriter responseWriter = new StreamWriter(clientStream))
                    {
                        responseWriter.NewLine = "\r\n";

                        responseWriter.WriteLine("HTTP/1.1 400 Bad Request");
                        responseWriter.WriteLine("Connection: Closed");
                        responseWriter.WriteLine();

                        responseWriter.WriteLine(e.ToString());
                    }
                }
                finally
                {
                    clientStream.Close();
                }
            }
        }

        private XDocument ProcessGet(RemoteId id, RemoteObject target)
        {
            // Build a response
            XDocument responseDocument = new XDocument(new XElement("Result"));
            responseDocument.Root.Add(serializer.WrapRemoteObject(id, target));

            return responseDocument;
        }
        private XDocument ProcessCall(RemoteId id, RemoteObject target, XDocument requestDocument)
        {
            XDocument responseDocument = null;

            // Decode method info
            XAttribute methodNameElement = requestDocument.Root.Attribute("Method");
            Type[] methodSignature = requestDocument.Root.Elements("Parameter").Select(e => Type.GetType(e.Attribute("Type")?.Value)).ToArray();

            // Find the specified method
            Type type = target.GetType();
            string methodName = methodNameElement.Value;
            MethodInfo typeMethod = type.GetMethod(methodName, methodSignature);

            // Decode parameters
            object[] methodArgs = requestDocument.Root.Elements("Parameter").Select(e => serializer.UnwrapObject(e.Elements().Single())).ToArray();

            // Call the target method
            try
            {
                object result = typeMethod.Invoke(target, methodArgs);

                responseDocument = new XDocument(new XElement("Response"));
                responseDocument.Root.Add(new XElement("Result", serializer.WrapObject(result)));

                ParameterInfo[] methodParameters = typeMethod.GetParameters();
                for (int i = 0; i < methodParameters.Length; i++)
                {
                    if (methodParameters[i].ParameterType.IsValueType)
                        continue;

                    responseDocument.Root.Add(new XElement("Parameter", new XAttribute("Index", i), serializer.WrapObject(methodArgs[i])));
                }
            }
            catch (Exception e)
            {
                responseDocument = new XDocument(serializer.WrapException(e));
            }

            return responseDocument;
        }

        protected RemoteId RegisterRemoteObject(RemoteObject value)
        {
            RemoteId id;
            if (remoteObjectsIndices.TryGetValue(value, out id))
                return id;

            id = currentRemoteIndex++;

            remoteObjects.Add(id, value);
            remoteObjectsIndices.Add(value, id);

            return id;
        }
    }
}