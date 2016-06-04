using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;

public abstract class ObjectDatabase<T> : IEnumerable<T>
{
    public abstract ICollection<T> Objects { get; }

    public abstract void Load();
    public abstract void Save();

    public IEnumerator<T> GetEnumerator()
    {
        return Objects?.GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}