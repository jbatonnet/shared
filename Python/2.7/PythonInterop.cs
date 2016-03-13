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

            // Setup builders
            AssemblyName assemblyName = new AssemblyName(typeName + "_Assembly");
            AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndSave);
            ModuleBuilder module = assemblyBuilder.DefineDynamicModule(typeName + "_Module");

            // Find class specs
            if (baseType == null)
                baseType = typeof(object);

            // Proxy methods
            MethodInfo constructorProxy = typeof(TypeManager).GetMethod(nameof(ConstructorProxy));
            MethodInfo methodProxy = typeof(TypeManager).GetMethod(nameof(MethodProxy));

            // Build type
            TypeBuilder typeBuilder = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, baseType);
            FieldBuilder fieldBuilder = typeBuilder.DefineField("pointer", typeof(IntPtr), FieldAttributes.Private);
            List<ConstructorBuilder> constructorBuilders = new List<ConstructorBuilder>();
            MethodBuilder strMethodBuilder = null;
            MethodBuilder hashMethodBuilder = null;

            foreach (var member in pythonObject)
            {
                PythonType memberType = member.Value.Type;

                switch (memberType.Name)
                {
                    // TODO: Properties

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

                                MethodAttributes methodAttributes = method?.IsVirtual == true ? (MethodAttributes.Public | MethodAttributes.Virtual) : MethodAttributes.Public;
                                MethodBuilder methodBuilder = typeBuilder.DefineMethod(member.Key, methodAttributes, method == null ? typeof(object) : method.ReturnType, method == null ? new Type[] { typeof(object[]) } : method.GetParameters().Select(p => p.ParameterType).ToArray());

                                ILGenerator ilGenerator = methodBuilder.GetILGenerator();
                                ilGenerator.Emit(OpCodes.Ldarg_0);

                                ilGenerator.Emit(OpCodes.Ldfld, fieldBuilder); // instance

                                if (IntPtr.Size == 4)
                                    ilGenerator.Emit(OpCodes.Ldc_I4, member.Value.Pointer.ToInt32()); // method
                                else if (IntPtr.Size == 8)
                                    ilGenerator.Emit(OpCodes.Ldc_I8, member.Value.Pointer.ToInt64()); // method
                                
                                ilGenerator.Emit(OpCodes.Ldnull); // args
                                ilGenerator.EmitCall(OpCodes.Call, methodProxy, Type.EmptyTypes); // CallProxy
                        
                                if (method != null && method.ReturnType == typeof(void))
                                    ilGenerator.Emit(OpCodes.Pop);

                                ilGenerator.Emit(OpCodes.Ret);

                                if (method?.IsVirtual == true)
                                    typeBuilder.DefineMethodOverride(methodBuilder, method);

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
                ilGenerator.Emit(OpCodes.Ldarg_0); // instance

                if (IntPtr.Size == 4)
                    ilGenerator.Emit(OpCodes.Ldc_I4, pointer.ToInt32()); // type
                else if (IntPtr.Size == 8)
                    ilGenerator.Emit(OpCodes.Ldc_I8, pointer.ToInt64()); // type

                ilGenerator.Emit(OpCodes.Ldnull); // null
                ilGenerator.EmitCall(OpCodes.Call, constructorProxy, Type.EmptyTypes); // CallProxy
                ilGenerator.Emit(OpCodes.Stfld, fieldBuilder);

                ilGenerator.Emit(OpCodes.Ret);
            }

            // Build type and check for abstract methods
            Type type = typeBuilder.CreateType();

            // Debug: Save assembly
            //assemblyBuilder.Save(typeName + ".dll");

            // Register type and return it
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
    public class MyClass2 : MyClass
    {
        public override void Test2()
        {
            base.Test2();
        }
    }

    public static class ObjectManager
    {
        internal static Hashtable ClrToPython = new Hashtable();
        internal static Hashtable PythonToClr = new Hashtable();

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
            AddMethod("__hash__", __hash__);

            AddMethod("__call__", __call__);
            AddMethod("__getattr__", __getattr__);

            if (type.GetInterfaces().Contains(typeof(IDisposable)))
            {
                AddMethod("__enter__", __enter__);
                AddMethod("__exit__", __exit__);
            }

            if (type.IsSubclassOf(typeof(IEnumerable)))
            {
                AddMethod("__iter__", __iter__);
                AddMethod("__reversed__", __reversed__);
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

                AddProperty(property.Name, (a, b) => MethodProxy(getMethod, a, b), setMethod == null ? (TwoArgsPythonObjectFunction)null : (a, b) => MethodProxy(setMethod, a, b));
            }

            // Add type methods
            var methodGroups = type.GetMethods().Except(propertyMethods).GroupBy(m => m.Name);
            foreach (var methodGroup in methodGroups)
                AddMethod(methodGroup.Key, (a, b) => MethodProxy(methodGroup.ToArray(), a, b), PythonFunctionType.VarArgs);
        }

        private PythonObject MethodProxy(MethodInfo method, PythonObject self, PythonObject args)
        {
            PythonTuple tuple = (PythonTuple)args;
            PythonObject pythonObject = tuple[0];
            object clrObject = ObjectManager.FromPython(pythonObject);

            object[] parameters = new object[tuple.Size - 1];
            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = Convert(tuple[i + 1]);

            object clrResult = method.Invoke(clrObject, parameters);
            return From(clrResult);
        }
        private PythonObject MethodProxy(MethodInfo[] methods, PythonObject self, PythonObject args)
        {
            PythonTuple tuple = (PythonTuple)args;
            PythonObject pythonObject = tuple[0];
            object clrObject = ObjectManager.FromPython(pythonObject);

            int argsCout = tuple.Size - 1;

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
                    if (!TryConvert(tuple[i + 1], parametersInfo[i].ParameterType, out parameter))
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
            PythonTuple tuple = (PythonTuple)args;
            PythonObject pythonInstance = tuple[0];

            object clrObject;

            if (ClrType.IsAbstract)
                clrObject = new object();
            else
                clrObject = Activator.CreateInstance(ClrType);

            ClrObject pythonObject = new ClrObject(pythonInstance, clrObject);
            ObjectManager.Register(clrObject, pythonObject.Pointer);

            return Py_None;
        }

        private static PythonObject __del__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __cmp__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __eq__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ne__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __lt__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __gt__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __le__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ge__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __pos__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __neg__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __abs__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __invert__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __round__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __floor__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ceil__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __trunc__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __add__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __sub__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __mul__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __floordiv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __div__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __truediv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __mod__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __divmod__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __pow__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __lshift__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rshift__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __and__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __or__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __xor__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __radd__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rsub__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rmul__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rfloordiv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rdiv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rtruediv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rmod__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rdivmod__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rpow__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rlshift__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rrshift__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rand__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ror__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __rxor__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __iadd__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __isub__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __imul__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ifloordiv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __idiv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __itruediv__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __imod__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __idivmod__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ipow__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ilshift__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __irshift__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __iand__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ior__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __ixor__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __int__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __long__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __float__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __complex__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __oct__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __hex__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __index__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __coerce__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __str__(PythonObject self, PythonObject args)
        {
            PythonObject me = args;

            object value = ObjectManager.FromPython(me);
            if (value == null)
                return Py_None;

            return (PythonString)value.ToString();
        }
        private static PythonObject __repr__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __unicode__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __format__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __hash__(PythonObject self, PythonObject args)
        {
            PythonObject me = args;

            object value = ObjectManager.FromPython(me);
            if (value == null)
                return Py_None;

            return (PythonNumber)value.GetHashCode();
        }
        private static PythonObject __nonzero__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __dir__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __sizeof__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __getattr__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __setattr__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __delattr__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __len__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __getitem__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __setitem__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __delitem__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __iter__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __reversed__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __contains__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __missing__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __instancecheck__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __subclasscheck__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __call__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __enter__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __exit__(PythonObject self, PythonObject args)
        {
            PythonObject me = args;

            object value = ObjectManager.FromPython(me);
            if (value == null)
                return Py_None;

            (value as IDisposable).Dispose();
            return Py_None;
        }
        private static PythonObject __get__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __set__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __delete__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __copy__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __deepcopy__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __getinitargs__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __getnewargs__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __getstate__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __setstate__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __reduce__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
        private static PythonObject __reduce_ex__(PythonObject self, PythonObject args)
        {
            return Py_None;
        }
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