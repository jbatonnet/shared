using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;

using static Python.Python;

namespace Python
{
    public static class AssemblyManager
    {
    }

    public static class TypeManager
    {
        public static Type FromPython(IntPtr pointer, Type baseType = null)
        {
            if (pointer == IntPtr.Zero)
                throw new ArgumentNullException("The specified pointer is null");
            if (ObjectManager.PythonToClr.ContainsKey(pointer))
                return (Type)ObjectManager.PythonToClr[pointer];

            // Get Python type name
            PythonObject pythonObject = pointer;
            string typeName = null;

            if (pythonObject is PythonType)
                typeName = (pythonObject as PythonType).Name;
            else if (pythonObject is PythonClass)
                typeName = (pythonObject as PythonClass).Name;
            else if (pythonObject is PythonModule)
                typeName = (pythonObject as PythonModule).Name;
            else
                throw new ArgumentNullException("The specified pointer cannot be converted to .NET type");

            // Convert base type
            if (baseType == null)
            {
                if (pythonObject is PythonClass)
                {
                    PythonTuple bases = (pythonObject as PythonClass).Bases;
                    int basesCount = bases.Size;

                    if (basesCount > 1)
                        throw new NotSupportedException("Cannot convert python type with multiple base classes");
                    else if (basesCount == 1)
                        baseType = FromPython(bases[0]);
                }
            }

            // Setup builders
            AssemblyName assemblyName = new AssemblyName(typeName + "_Assembly");
            AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndSave);
            ModuleBuilder module = assemblyBuilder.DefineDynamicModule(typeName + "_Module"
#if DEBUG
                , typeName + ".dll"
#endif
                );

            // Find class specs
            if (baseType == null)
                baseType = typeof(object);

            // Proxy methods
            MethodInfo constructorProxy = typeof(FromPythonHelper).GetMethod(nameof(FromPythonHelper.ConstructorProxy));
            MethodInfo methodProxy = typeof(FromPythonHelper).GetMethod(nameof(FromPythonHelper.MethodProxy));

            // Build type
            TypeBuilder typeBuilder = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, baseType);
            FieldInfo pointerField = typeBuilder.DefineField("pointer", typeof(IntPtr), FieldAttributes.Private);
            List<ConstructorBuilder> constructorBuilders = new List<ConstructorBuilder>();
            MethodBuilder strMethodBuilder = null;
            MethodBuilder hashMethodBuilder = null;

            foreach (var member in pythonObject)
            {
                PythonType memberType = member.Value.Type;

                switch (memberType.Name)
                {
                    // Properties
                    case "property":
                    {
                        PythonObject pythonGetMethod = member.Value.GetAttribute("fget");
                        PythonObject pythonSetMethod = member.Value.GetAttribute("fset");

                        PropertyInfo clrProperty = baseType.GetProperty(member.Key);
                        MethodInfo clrGetMethod = clrProperty?.GetGetMethod(true);
                        MethodInfo clrSetMethod = clrProperty?.GetSetMethod(true);

                        MethodBuilder getMethodBuilder = null, setMethodBuilder = null;
                        Type propertyType = clrProperty?.PropertyType ?? typeof(object);

                        if (pythonGetMethod != Py_None)
                            getMethodBuilder = FromPythonHelper.AddMethodProxy(typeBuilder, "get_" + member.Key, pythonGetMethod.Pointer, pointerField, clrGetMethod, propertyType, Type.EmptyTypes, true);
                        if (pythonSetMethod != Py_None)
                            setMethodBuilder = FromPythonHelper.AddMethodProxy(typeBuilder, "set_" + member.Key, pythonSetMethod.Pointer, pointerField, clrSetMethod, typeof(void), new Type[] { propertyType }, true);

                        if (clrProperty == null)
                        {
                            PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(member.Key, PropertyAttributes.None, typeof(object), Type.EmptyTypes);

                            if (getMethodBuilder != null)
                                propertyBuilder.SetGetMethod(getMethodBuilder);
                            if (setMethodBuilder != null)
                                propertyBuilder.SetSetMethod(setMethodBuilder);
                        }

                        break;
                    }

                    // Methods
                    case "instancemethod":
                    {
                        if (member.Key == typeName) // Constructor
                        {

                        }
                        else switch (member.Key)
                        {
                            // Object
                            case "__str__": break;
                            case "__hash__": break;

                            // IDisposable
                            case "__enter__": break;
                            case "__exit__": break;

                            // Methods
                            default:
                            {
                                MethodInfo method = baseType.GetMethod(member.Key);
                                if (method?.IsFinal == true)
                                    continue;

                                if (method == null)
                                    FromPythonHelper.AddGenericMethodProxy(typeBuilder, member.Key, member.Value.Pointer, pointerField);
                                else
                                    FromPythonHelper.AddMethodProxy(typeBuilder, member.Key, member.Value.Pointer, pointerField, method, method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray());

                                break;
                            }
                        }

                        break;
                    }
                }
            }

            // Build a default constructor if needed
            if (constructorBuilders.Count == 0)
            {
                ConstructorBuilder constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);

                ILGenerator ilGenerator = constructorBuilder.GetILGenerator();
                ilGenerator.Emit(OpCodes.Ldarg_0);
                ilGenerator.Emit(OpCodes.Dup); // instance

                if (IntPtr.Size == 4)
                    ilGenerator.Emit(OpCodes.Ldc_I4, pointer.ToInt32()); // type
                else if (IntPtr.Size == 8)
                    ilGenerator.Emit(OpCodes.Ldc_I8, pointer.ToInt64()); // type

                ilGenerator.Emit(OpCodes.Ldnull); // null
                ilGenerator.EmitCall(OpCodes.Call, constructorProxy, Type.EmptyTypes); // CallProxy
                ilGenerator.Emit(OpCodes.Stfld, pointerField);

                ilGenerator.Emit(OpCodes.Ret);
            }

            // Build type and check for abstract methods
            Type type = typeBuilder.CreateType();

#if DEBUG
            assemblyBuilder.Save(typeName + ".dll");
#endif

            // Register type and return it
            FromPythonHelper.BuiltTypes.Add(type);
            ObjectManager.Register(type, pointer);
            return type;
        }
        public static PythonClass ToPython(Type type)
        {
            if (ObjectManager.ClrToPython.ContainsKey(type))
                return (PythonClass)ObjectManager.ClrToPython[type];

            ClrTypeObject typeObject = new ClrTypeObject(type);
            ObjectManager.Register(type, typeObject.Pointer);

            return typeObject;
        }

        public static class FromPythonHelper
        {
            public static List<Type> BuiltTypes { get; } = new List<Type>();

            public static MethodBuilder AddMethodProxy(TypeBuilder type, string name, IntPtr pythonMethod, FieldInfo pointerField, MethodInfo baseMethod = null, Type returnType = null, Type[] parameterTypes = null, bool hidden = false)
            {
                if (returnType == null)
                    returnType = baseMethod == null ? typeof(object) : baseMethod.ReturnType;
                if (parameterTypes == null)
                    parameterTypes = baseMethod == null ? new Type[] { typeof(object[]) } : baseMethod.GetParameters().Select(p => p.ParameterType).ToArray();

                MethodAttributes methodAttributes = baseMethod?.IsVirtual == true ? (MethodAttributes.Public | MethodAttributes.Virtual) : MethodAttributes.Public;
                if (hidden)
                    methodAttributes |= MethodAttributes.HideBySig;

                MethodBuilder methodBuilder = type.DefineMethod(name, methodAttributes, returnType, parameterTypes);
                MethodInfo methodProxy = typeof(FromPythonHelper).GetMethod(nameof(MethodProxy));

                ILGenerator ilGenerator = methodBuilder.GetILGenerator();
                ilGenerator.Emit(OpCodes.Ldarg_0);

                ilGenerator.Emit(OpCodes.Ldfld, pointerField); // instance

                if (IntPtr.Size == 4)
                    ilGenerator.Emit(OpCodes.Ldc_I4, pythonMethod.ToInt32()); // method
                else if (IntPtr.Size == 8)
                    ilGenerator.Emit(OpCodes.Ldc_I8, pythonMethod.ToInt64()); // method

                if (parameterTypes.Length == 0)
                    ilGenerator.Emit(OpCodes.Ldnull); // args
                else
                {
                    ilGenerator.Emit(OpCodes.Ldc_I4, parameterTypes.Length);
                    ilGenerator.Emit(OpCodes.Newarr, typeof(object));

                    for (int i = 0; i < parameterTypes.Length; i++)
                    {
                        ilGenerator.Emit(OpCodes.Dup);
                        ilGenerator.Emit(OpCodes.Ldc_I4, i);
                        ilGenerator.Emit(OpCodes.Ldarg, i + 1);

                        if (parameterTypes[i].IsValueType)
                            ilGenerator.Emit(OpCodes.Box, parameterTypes[i]);

                        ilGenerator.Emit(OpCodes.Stelem_Ref);
                    }
                }

                ilGenerator.EmitCall(OpCodes.Call, methodProxy, null); // CallProxy

                if (returnType == typeof(void))
                    ilGenerator.Emit(OpCodes.Pop);
                else if (returnType.IsValueType)
                    ilGenerator.Emit(OpCodes.Unbox_Any, returnType);

                ilGenerator.Emit(OpCodes.Ret);

                if (baseMethod?.IsVirtual == true)
                    type.DefineMethodOverride(methodBuilder, baseMethod);

                return methodBuilder;
            }
            public static MethodBuilder AddGenericMethodProxy(TypeBuilder type, string name, IntPtr pythonMethod, FieldInfo pointerField)
            {
                Type methodReturnType = typeof(object);
                Type[] methodParameterTypes = new[] { typeof(object[]) };
                MethodAttributes methodAttributes = MethodAttributes.Public;
                MethodBuilder methodBuilder = type.DefineMethod(name, methodAttributes, methodReturnType, methodParameterTypes);
                MethodInfo methodProxy = typeof(FromPythonHelper).GetMethod(nameof(MethodProxy));

                ILGenerator ilGenerator = methodBuilder.GetILGenerator();
                ilGenerator.Emit(OpCodes.Ldarg_0);

                ilGenerator.Emit(OpCodes.Ldfld, pointerField); // instance

                if (IntPtr.Size == 4)
                    ilGenerator.Emit(OpCodes.Ldc_I4, pythonMethod.ToInt32()); // method
                else if (IntPtr.Size == 8)
                    ilGenerator.Emit(OpCodes.Ldc_I8, pythonMethod.ToInt64()); // method

                ilGenerator.Emit(OpCodes.Ldarg_1); // args
                ilGenerator.EmitCall(OpCodes.Call, methodProxy, null); // CallProxy
                ilGenerator.Emit(OpCodes.Ret);

                return methodBuilder;
            }

            public static IntPtr ConstructorProxy(object instance, IntPtr type, object[] args)
            {
                int length = args?.Length ?? 0;
                PythonTuple tuple = new PythonTuple(length);

                for (int i = 0; i < length; i++)
                    tuple[i] = PythonObject.From(args[i]);

                PythonObject result;
                using (PythonException.Checker)
                    result = PyObject_CallObject(type, tuple);

                ObjectManager.Register(instance, result);
                return result;
            }
            public static object MethodProxy(IntPtr instance, IntPtr method, object[] args)
            {
                int length = args?.Length ?? 0;

                PythonTuple parameters = new PythonTuple(length + 1);
                parameters[0] = instance;

                for (int i = 0; i < length; i++)
                    parameters[i + 1] = PythonObject.From(args[i]);

                PythonObject result;
                using (PythonException.Checker)
                    result = PyObject_CallObject(method, parameters);

                return PythonObject.Convert(result);
            }
        }
    }

    public class MyClass
    { 
        private IntPtr pointer;

        public object Test(params object[] args)
        {
            return PyObject_CallObject(pointer, PyTuple_New(0));
        }
        public virtual void Test2()
        {
        }
        public void Test3(object[] args)
        {
            pointer = PyObject_CallObject(pointer, IntPtr.Zero);
        }
    }
    public class MyClass2
    {
        public void Test2(int a, bool b, string c)
        {
            var toto = new object[3] { a, b, c };
        }
        public object Test3()
        {
            return Test4();
        }
        public object Test4()
        {
            return true;
        }
    }

    public static class ObjectManager
    {
        internal static Dictionary<object, IntPtr> ClrToPython = new Dictionary<object, IntPtr>();
        internal static Dictionary<IntPtr, object> PythonToClr = new Dictionary<IntPtr, object>();

        public static void Register(object value, IntPtr pointer)
        {
            if (PythonToClr.ContainsKey(pointer))
                return;

            PythonToClr.Add(pointer, value);
            ClrToPython.Add(value, pointer);
        }

        public static object FromPython(IntPtr pointer)
        {
            if (PythonToClr.ContainsKey(pointer))
                return PythonToClr[pointer];

            return null;
        }
        public static IntPtr ToPython(object value)
        {
            if (ClrToPython.ContainsKey(value))
                return (PythonObject)ClrToPython[value];

            return IntPtr.Zero;
        }
    }

    public class NamespaceObject : PythonObject
    {
        internal static PythonClass Class { get; private set; }
        static NamespaceObject()
        {
            Class = new PythonClass("NamespaceClass");
        }

        public NamespaceObject(object value)
        {
        }
    }

    public class ClrTypeObject : PythonClass
    {
        public Type ClrType { get; private set; }

        public ClrTypeObject(Type type) : base(type.FullName)
        {
            ClrType = type;

            AddMethod("__init__", __init__);
            AddMethod("__str__", __str__);
            //AddMethod("__hash__", __hash__);

            //AddMethod("__call__", __call__);
            //AddMethod("__getattr__", __getattr__);

            if (type.GetInterfaces().Contains(typeof(IDisposable)))
            {
                //AddMethod("__enter__", __enter__);
                //AddMethod("__exit__", __exit__);
            }

            if (type.IsSubclassOf(typeof(IEnumerable)))
            {
                //AddMethod("__iter__", __iter__);
                //AddMethod("__reversed__", __reversed__);
            }

            List<MethodInfo> propertyMethods = new List<MethodInfo>();

            // Add type properties
            foreach (PropertyInfo property in type.GetProperties())
            {
                MethodInfo getMethod = property.GetGetMethod(true);
                MethodInfo setMethod = property.GetSetMethod(true);

                propertyMethods.Add(getMethod);
                if (setMethod != null)
                    propertyMethods.Add(setMethod);

                AddProperty(property.Name, (a, b) => MethodProxy(getMethod, a, b as PythonTuple), setMethod == null ? (TwoArgsPythonObjectFunction)null : (a, b) => MethodProxy(setMethod, a, b as PythonTuple));
            }

            // Add type methods
            var methodGroups = type.GetMethods()
                                   .Except(propertyMethods)
                                   .Where(m => m.Name != "GetHashCode")
                                   .GroupBy(m => m.Name);
            foreach (var methodGroup in methodGroups)
                AddMethod(methodGroup.Key, (a, b) => MethodProxy(methodGroup.ToArray(), a, b as PythonTuple));
        }

        private PythonObject MethodProxy(MethodInfo method, PythonObject self, PythonTuple args)
        {
            object clrObject = ObjectManager.FromPython(self);

            object[] parameters = new object[args.Size];
            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = Convert(args[i]);

            object clrResult = method.Invoke(clrObject, parameters);
            return From(clrResult);
        }
        private PythonObject MethodProxy(MethodInfo[] methods, PythonObject self, PythonTuple args)
        {
            object clrObject = ObjectManager.FromPython(self);
            int argsCout = args.Size;

            foreach (MethodInfo method in methods)
            {
                ParameterInfo[] parametersInfo = method.GetParameters();
                if (argsCout > parametersInfo.Length)
                    continue;

                bool match = true;
                object[] parameters = new object[parametersInfo.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i >= argsCout && parametersInfo[i].IsOptional)
                    {
                        parameters[i] = parametersInfo[i].DefaultValue;
                        continue;
                    }

                    object parameter;
                    if (!TryConvert(args[i], parametersInfo[i].ParameterType, out parameter))
                    {
                        match = false;
                        break;
                    }

                    parameters[i] = parameter;
                }

                if (!match)
                    continue;

                object result = method.Invoke(clrObject, parameters);
                return From(result);
            }

            throw new ArgumentException("Could not find any overload matching the specified arguments");
        }

        private PythonObject __init__(PythonObject self, PythonObject args)
        {
            object clrObject;

            if (ClrType.IsAbstract)
                clrObject = new object();
            else
                clrObject = Activator.CreateInstance(ClrType);

            ClrObject pythonObject = new ClrObject(self, clrObject);
            ObjectManager.Register(clrObject, pythonObject.Pointer);

            return Py_None;
        }

        private static PythonObject __str__(PythonObject self, PythonObject args)
        {
            object value = ObjectManager.FromPython(self);
            if (value == null)
                return Py_None;

            return (PythonString)value.ToString();
        }
        private static PythonObject __hash__(PythonObject self, PythonObject args)
        {
            object value = ObjectManager.FromPython(self);
            if (value == null)
                return Py_None;

            return (PythonNumber)value.GetHashCode();
        }
        private static PythonObject __exit__(PythonObject self, PythonObject args)
        {
            object value = ObjectManager.FromPython(self);
            if (value == null)
                return Py_None;

            (value as IDisposable).Dispose();
            return Py_None;
        }

        /*
            Python system methods

            __del__
            __cmp__
            __eq__
            __ne__
            __lt__
            __gt__
            __le__
            __ge__
            __pos__
            __neg__
            __abs__
            __invert__
            __round__
            __floor__
            __ceil__
            __trunc__
            __add__
            __sub__
            __mul__
            __floordiv__
            __div__
            __truediv__
            __mod__
            __divmod__
            __pow__
            __lshift__
            __rshift__
            __and__
            __or__
            __xor__
            __radd__
            __rsub__
            __rmul__
            __rfloordiv__
            __rdiv__
            __rtruediv__
            __rmod__
            __rdivmod__
            __rpow__
            __rlshift__
            __rrshift__
            __rand__
            __ror__
            __rxor__
            __iadd__
            __isub__
            __imul__
            __ifloordiv__
            __idiv__
            __itruediv__
            __imod__
            __idivmod__
            __ipow__
            __ilshift__
            __irshift__
            __iand__
            __ior__
            __ixor__
            __int__
            __long__
            __float__
            __complex__
            __oct__
            __hex__
            __index__
            __coerce__
            __repr__
            __unicode__
            __format__
            __nonzero__
            __dir__
            __sizeof__
            __getattr__
            __setattr__
            __delattr__
            __len__
            __getitem__
            __setitem__
            __delitem__
            __iter__
            __reversed__
            __contains__
            __missing__
            __instancecheck__
            __subclasscheck__
            __call__
            __enter__
            __get__
            __set__
            __delete__
            __copy__
            __deepcopy__
            __getinitargs__
            __getnewargs__
            __getstate__
            __setstate__
            __reduce__
            __reduce_ex__
        */
    }

    public class ClrObject : PythonObject
    {
        public object Object { get; private set; }

        internal ClrObject(IntPtr pointer, object value)
        {
            Pointer = pointer;
            Object = value;
        }
    }
}