using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace Remoting.Tcp
{
    using RemoteObject = MarshalByRefObject;

    internal enum TcpRemotingCommand : byte
    {
        Get,
        Call,
        Result,
        Exception
    }

    internal enum TcpRemotingType : byte
    {
        Null,
        Value,
        RemoteObject,
        Exception,
        Buffer,
        Array,
        Delegate
    }

    internal abstract class TcpRemotingSerializer : RemotingSerializer
    {
        internal virtual void WriteObject(Stream stream, object value)
        {
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.Default, true))
            {
                if (value == null)
                {
                    writer.Write((byte)TcpRemotingType.Null);
                    return;
                }

                Type type = value.GetType();
                TypeConverter typeConverter = TypeDescriptor.GetConverter(type);

                if (type.IsValueType || type == typeof(string))
                {
                    writer.Write((byte)TcpRemotingType.Value);
                    WriteType(stream, type);
                    writer.Write(typeConverter.ConvertToString(value));
                }
                else if (type == typeof(byte[]))
                {
                    writer.Write((byte)TcpRemotingType.Buffer);
                    writer.Write((value as byte[]).Length);
                    writer.Write(value as byte[]);
                }
                else if (type.IsArray)
                {
                    Array array = value as Array;
                    int length = array.GetLength(0);

                    writer.Write((byte)TcpRemotingType.Array);
                    writer.Write(length);
                    WriteType(stream, type.GetElementType());

                    for (int i = 0; i < length; i++)
                        WriteObject(stream, array.GetValue(i));

                    return;
                }
                else
                    throw new NotSupportedException("Unable to wrap object of type " + type.FullName);
            }
        }
        internal virtual void WriteException(Stream stream, Exception exception)
        {
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.Default, true))
            {
                writer.Write((byte)TcpRemotingType.Exception);
                WriteType(stream, exception.GetType());
                writer.Write(exception.Message);
                writer.Write(exception.StackTrace);
            }
        }
        internal virtual void WriteType(Stream stream, Type type)
        {
            List<Type> typeHierarchy = new List<Type>();

            // Build type hierarchy
            while (type.BaseType != null && type.BaseType != typeof(object))
            {
                typeHierarchy.Add(type);
                type = type.BaseType;
            }

            // Send everything
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.Default, true))
            {
                writer.Write(typeHierarchy.Count);

                foreach (Type typeParent in typeHierarchy)
                    writer.Write(typeParent.FullName);
            }
        }

        internal object ReadObject(Stream stream)
        {
            TcpRemotingType remotingType = (TcpRemotingType)stream.ReadByte();
            return ReadObject(stream, remotingType);
        }
        internal virtual object ReadObject(Stream stream, TcpRemotingType remotingType)
        {
            switch (remotingType)
            {
                case TcpRemotingType.Null: return null;
                case TcpRemotingType.Exception: return ReadException(stream);
                case TcpRemotingType.RemoteObject: return ReadRemoteObject(stream);
            }

            using (BinaryReader reader = new BinaryReader(stream, Encoding.Default, true))
            {
                switch (remotingType)
                {
                    case TcpRemotingType.Value:
                    {
                        Type type = ReadType(stream);
                        string value = reader.ReadString();

                        TypeConverter typeConverter = TypeDescriptor.GetConverter(type);

                        return typeConverter.ConvertFromString(value);
                    }

                    case TcpRemotingType.Buffer:
                    {
                        int count = reader.ReadInt32();
                        return reader.ReadBytes(count);
                    }

                    case TcpRemotingType.Array:
                    {
                        int length = reader.ReadInt32();
                        Type type = ReadType(stream);

                        Array array = Array.CreateInstance(type, length);

                        for (int i = 0; i < length; i++)
                            array.SetValue(ReadObject(stream), i);

                        return array;
                    }
                }

                throw new NotSupportedException("Unable to unwrap object of type " + remotingType);
            }
        }
        internal abstract RemoteObject ReadRemoteObject(Stream stream);
        internal virtual Exception ReadException(Stream stream)
        {
            using (BinaryReader reader = new BinaryReader(stream, Encoding.Default, true))
            {
                Type exceptionType = ReadType(stream);
                string exceptionMessage = reader.ReadString();
                string exceptionStackTrace = reader.ReadString();

                return new Exception(exceptionMessage);
            }
        }
        internal virtual Type ReadType(Stream stream)
        {
            using (BinaryReader reader = new BinaryReader(stream, Encoding.Default, true))
            {
                int typeHierarchySize = reader.ReadInt32();
                List<string> typeHierarchy = new List<string>();

                for (int i = 0; i < typeHierarchySize; i++)
                    typeHierarchy.Add(reader.ReadString());

                foreach (string typeName in typeHierarchy)
                {
                    Type type = ResolveType(typeName);
                    if (type != null)
                        return type;
                }
            }

            return typeof(RemoteObject);
        }
    }
}