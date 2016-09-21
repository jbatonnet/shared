using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Remoting.Http
{
    using RemoteId = Int32;
    using RemoteObject = MarshalByRefObject;

    public class HttpRemotingClient : RemotingClient
    {
        private class Serializer : HttpRemotingSerializer
        {
            public HttpRemotingClient Client { get; }

            public Serializer(HttpRemotingClient client)
            {
                Client = client;
            }

            internal override object UnwrapObject(XElement element)
            {
                if (element != null && element.Name.LocalName == "RemoteObject")
                {
                    XAttribute idAttribute = element.Attribute("Id");
                    RemoteId id = RemoteId.Parse(idAttribute.Value);

                    RemoteProxy proxy;

                    int index = Client.remoteIds.IndexOf(id);
                    if (index >= 0)
                        proxy = Client.remoteProxies[index];
                    else
                    {
                        XElement typeElement = element.Element("Type");
                        XAttribute typeFullNameAttribute = typeElement.Attribute("FullName");
                        XAttribute typeAssemblyAttribute = typeElement.Attribute("Assembly");

                        // Decode remote type
                        Type type = ResolveType(typeFullNameAttribute.Value);

                        if (type == null)
                        {
                            foreach (XElement parentElement in typeElement.Elements("Parent"))
                            {
                                if (type != null)
                                    break;

                                XAttribute parentFullNameAttribute = parentElement.Attribute("FullName");
                                XAttribute parentAssemblyAttribute = parentElement.Attribute("Assembly");

                                type = ResolveType(parentFullNameAttribute.Value);
                            }
                        }

                        type = type ?? typeof(RemoteObject);

                        // Create a proxy if needed
                        proxy = new RemoteProxy(Client, id, type);

                        Client.remoteIds.Add(id);
                        Client.remoteProxies.Add(proxy);
                    }

                    return proxy.GetTransparentProxy();
                }

                return base.UnwrapObject(element);
            }
        }

        public string Host { get; }
        public ushort Port { get; }

        private List<RemoteId> remoteIds = new List<RemoteId>();
        private List<RemoteProxy> remoteProxies = new List<RemoteProxy>();

        private Serializer serializer;

        public HttpRemotingClient() : this("127.0.0.1") { }
        public HttpRemotingClient(string host) : this(host, HttpRemotingServer.DefaultPort) { }
        public HttpRemotingClient(ushort port) : this("127.0.0.1", port) { }
        public HttpRemotingClient(string host, ushort port)
        {
            Host = host;
            Port = port;

            serializer = new Serializer(this);
        }

        public override async Task<RemoteObject> GetObject(string name)
        {
            string url = $"http://{Host}:{Port}/{name}";

            using (HttpClient httpClient = new HttpClient())
            {
                // Send request and get response
                HttpResponseMessage response = await httpClient.GetAsync(url);

                // Decode response
                string responseContent = await response.Content.ReadAsStringAsync();
                XDocument responseDocument = XDocument.Parse(responseContent);

                // Process response
                XElement resultElement = responseDocument.Root.Element("RemoteObject");
                return serializer.UnwrapObject(resultElement) as RemoteObject;
            }
        }
        public override async Task<T> GetObject<T>(string name)
        {
            return await GetObject(name) as T;
        }

        protected override async Task<IMessage> ProcessMethod(RemoteId id, IMethodCallMessage methodCallMessage)
        {
            string url = $"http://{Host}:{Port}/{id}";

            using (HttpClient httpClient = new HttpClient())
            {
                // Create request
                XDocument requestDocument = new XDocument(new XElement("Call"));

                requestDocument.Root.Add(new XAttribute("Method", methodCallMessage.MethodName));

                Type[] methodSignature = (Type[])methodCallMessage.MethodSignature;
                int count = methodSignature.Length;

                for (int i = 0; i < count; i++)
                {
                    XElement parameterElement = new XElement("Parameter");

                    parameterElement.Add(new XAttribute("Type", methodSignature[i].FullName));
                    parameterElement.Add(serializer.WrapObject(methodCallMessage.Args[i]));

                    requestDocument.Root.Add(parameterElement);
                }

                // Send request and get response
                HttpResponseMessage response = await httpClient.PostAsync(url, new StringContent(requestDocument.ToString()));

                // Decode response
                string responseContent = await response.Content.ReadAsStringAsync();
                XDocument responseDocument = XDocument.Parse(responseContent);

                // Decode exception if needed
                if (responseDocument.Root.Name.LocalName == "Exception")
                    return new ReturnMessage(serializer.UnwrapException(responseDocument.Root), methodCallMessage);

                // Unwrap result
                XElement resultElement = responseDocument.Root.Element("Result");
                object result = serializer.UnwrapObject(resultElement.Elements().Single());

                // Unwrap parameters
                foreach (XElement parameterElement in responseDocument.Root.Elements("Parameter"))
                {
                    XAttribute indexAttribute = parameterElement.Attribute("Index");

                    int index = int.Parse(indexAttribute.Value);
                    object value = serializer.UnwrapObject(parameterElement.Elements().Single());

                    Copy(ref value, ref methodCallMessage.Args[index]);
                }
                
                return new ReturnMessage(result, methodCallMessage.Args, methodCallMessage.ArgCount, methodCallMessage.LogicalCallContext, methodCallMessage);
            }
        }
    }
}