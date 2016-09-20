using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Remoting
{
    using RemoteObject = MarshalByRefObject;

    public class RemotingServerAttribute : Attribute
    {
        public string Name { get; }

        public RemotingServerAttribute(string name)
        {
            Name = name;
        }
    }

    public abstract class RemotingServer
    {
        public abstract void Start();
        public abstract void Stop();

        public abstract void AddObject(string name, RemoteObject value);
        public abstract void RemoveObject(string name);
    }
}