using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;

public class ObjectProxy<T> : RealProxy where T : class
{
    public T Object { get; }

    public ObjectProxy(T obj) : base(typeof(T))
    {
        Object = obj;
    }

    public override IMessage Invoke(IMessage message)
    {
        IMethodCallMessage methodCallMessage = message as IMethodCallMessage;
        if (methodCallMessage != null)
            OnMethodCall(methodCallMessage.MethodBase);

        return ChannelServices.SyncDispatchMessage(message);
    }

    protected virtual void OnMethodCall(MethodBase method) { }
    protected virtual void OnPropertyGet(PropertyInfo property) { }
    protected virtual void OnPropertySet(PropertyInfo property) { }

    protected virtual void OnCollectionChange(PropertyInfo property) { }
}