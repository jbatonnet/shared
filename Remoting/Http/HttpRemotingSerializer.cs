using System;
using System.ComponentModel;
using System.Linq;
using System.Xml.Linq;

namespace Remoting.Http
{
    internal abstract class HttpRemotingSerializer : RemotingSerializer
    {
        internal virtual XElement WrapException(Exception exception, string name = null)
        {
            XElement exceptionElement = new XElement(name ?? "Exception");
            Type exceptionType = exception.GetType();

            exceptionElement.Add(new XAttribute("Type", exceptionType.FullName));
            exceptionElement.Add(new XElement("Message", exception.Message));
            exceptionElement.Add(new XElement("StackTrace", exception.StackTrace));

            AggregateException aggregateException = exception as AggregateException;
            if (aggregateException != null)
            {
                foreach (Exception subException in aggregateException.InnerExceptions)
                    exceptionElement.Add(WrapException(subException, "AggregatedException"));
            }

            if (exception.InnerException != null)
                exceptionElement.Add(WrapException(exception.InnerException, "InnerException"));

            return exceptionElement;
        }
        internal virtual Exception UnwrapException(XElement element)
        {
            XAttribute typeAttribute = element.Attribute("Type");
            Type type = ResolveType(typeAttribute.Value);

            XElement messageElement = element.Element("Message");

            return new Exception(messageElement.Value);
        }

        internal virtual XElement WrapObject(object value)
        {
            if (value == null)
                return new XElement("Null");

            Type type = value.GetType();
            TypeConverter typeConverter = TypeDescriptor.GetConverter(type);

            if (type.IsValueType || type == typeof(string))
            {
                XElement valueElement = new XElement("Value");

                valueElement.Add(new XAttribute("Type", type.FullName));
                valueElement.Add(typeConverter.ConvertToString(value));

                return valueElement;
            }

            // Exception for byte array, to quickly transfer big buffers
            if (type == typeof(byte[]))
            {
                XElement bufferElement = new XElement("Buffer");

                bufferElement.Value = Convert.ToBase64String((byte[])value);

                return bufferElement;
            }

            if (type.IsArray)
            {
                XElement arrayElement = new XElement("Array");
                Array array = value as Array;

                arrayElement.Add(new XAttribute("Type", type.GetElementType().FullName));

                int length = array.GetLength(0);
                arrayElement.Add(new XAttribute("Length", length));

                for (int i = 0; i < length; i++)
                    arrayElement.Add(WrapObject(array.GetValue(i)));

                return arrayElement;
            }

            throw new NotSupportedException("Unable to wrap object of type " + type.FullName);
        }
        internal virtual object UnwrapObject(XElement element)
        {
            if (element == null)
                return null;

            switch (element.Name.LocalName)
            {
                case "Null": return null;

                case "Value":
                {
                    XAttribute typeAttribute = element.Attribute("Type");

                    Type type = ResolveType(typeAttribute.Value);
                    TypeConverter typeConverter = TypeDescriptor.GetConverter(type);

                    return typeConverter.ConvertFromString(element.Value);
                }

                case "Buffer":
                {
                    return Convert.FromBase64String(element.Value);
                }

                case "Array":
                {
                    XAttribute typeAttribute = element.Attribute("Type");
                    XAttribute lengthAttribute = element.Attribute("Length");

                    Type type = ResolveType(typeAttribute.Value);
                    int length = int.Parse(lengthAttribute.Value);

                    Array array = Array.CreateInstance(type, length);
                    for (int i = 0; i < length; i++)
                        array.SetValue(UnwrapObject(element.Elements().ElementAt(i)), i); // FIXME

                    return array;
                }
            }

            throw new NotSupportedException();
        }
    }
}