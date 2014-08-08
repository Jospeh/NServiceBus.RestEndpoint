﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace RestEndpoints.Core.Models
{
    internal class PropertyHelper
    {
        // Delegate type for a by-ref property getter
        private delegate TValue ByRefFunc<TDeclaringType, TValue>(ref TDeclaringType arg);

        private static readonly MethodInfo CallPropertyGetterOpenGenericMethod =
            typeof(PropertyHelper).GetTypeInfo().GetDeclaredMethod("CallPropertyGetter");

        private static readonly MethodInfo CallPropertyGetterByReferenceOpenGenericMethod =
            typeof(PropertyHelper).GetTypeInfo().GetDeclaredMethod("CallPropertyGetterByReference");

        private static readonly MethodInfo CallPropertySetterOpenGenericMethod =
            typeof(PropertyHelper).GetTypeInfo().GetDeclaredMethod("CallPropertySetter");

        private static readonly ConcurrentDictionary<Type, PropertyHelper[]> ReflectionCache =
            new ConcurrentDictionary<Type, PropertyHelper[]>();

        private readonly Func<object, object> _valueGetter;

        /// <summary>
        /// Initializes a fast property helper.
        ///
        /// This constructor does not cache the helper. For caching, use GetProperties.
        /// </summary>
        public PropertyHelper(PropertyInfo property, string propertyName)
        {
            Property = property;
            Name = propertyName;
            _valueGetter = MakeFastPropertyGetter(property);
        }

        public PropertyInfo Property { get; private set; }

        public virtual string Name { get; protected set; }

        public object GetValue(object instance)
        {
            return _valueGetter(instance);
        }

        /// <summary>
        /// Creates and caches fast property helpers that expose getters for every public get property on the
        /// underlying type.
        /// </summary>
        /// <param name="instance">the instance to extract property accessors for.</param>
        /// <returns>a cached array of all public property getters from the underlying type of target instance.
        /// </returns>
        public static PropertyHelper[] GetProperties(object instance)
        {
            return GetProperties(instance.GetType());
        }

        /// <summary>
        /// Creates and caches fast property helpers that expose getters for every public get property on the
        /// specified type.
        /// </summary>
        /// <param name="type">the type to extract property accessors for.</param>
        /// <returns>a cached array of all public property getters from the type of target instance.
        /// </returns>
        public static PropertyHelper[] GetProperties(Type type)
        {
            return GetProperties(type, CreateInstance, ReflectionCache);
        }

        /// <summary>
        /// Creates and caches fast property helpers that expose getters for every public get property on the
        /// specified type.
        /// </summary>
        /// <param name="type">the type to extract property accessors for.</param>
        /// <returns>a cached array of all public property getters from the type of target instance.
        /// </returns>
        private static PropertyHelper[] GetProperties(Type type, string propertyHierarchy)
        {
            return GetProperties(type, CreateInstance, ReflectionCache, propertyHierarchy);
        }

        /// <summary>
        /// Creates a single fast property getter. The result is not cached.
        /// </summary>
        /// <param name="propertyInfo">propertyInfo to extract the getter for.</param>
        /// <returns>a fast getter.</returns>
        /// <remarks>
        /// This method is more memory efficient than a dynamically compiled lambda, and about the
        /// same speed.
        /// </remarks>
        public static Func<object, object> MakeFastPropertyGetter(PropertyInfo propertyInfo)
        {

            var getMethod = propertyInfo.GetMethod;

            // Instance methods in the CLR can be turned into static methods where the first parameter
            // is open over "target". This parameter is always passed by reference, so we have a code
            // path for value types and a code path for reference types.
            var typeInput = getMethod.DeclaringType;
            var typeOutput = getMethod.ReturnType;

            Delegate callPropertyGetterDelegate;
            if (typeInput.IsValueType)
            {
                // Create a delegate (ref TDeclaringType) -> TValue
                var delegateType = typeof(ByRefFunc<,>).MakeGenericType(typeInput, typeOutput);
                var propertyGetterAsFunc = getMethod.CreateDelegate(delegateType);
                var callPropertyGetterClosedGenericMethod =
                    CallPropertyGetterByReferenceOpenGenericMethod.MakeGenericMethod(typeInput, typeOutput);
                callPropertyGetterDelegate =
                    callPropertyGetterClosedGenericMethod.CreateDelegate(
                        typeof(Func<object, object>), propertyGetterAsFunc);
            }
            else
            {
                // Create a delegate TDeclaringType -> TValue
                var propertyGetterAsFunc =
                    getMethod.CreateDelegate(typeof(Func<,>).MakeGenericType(typeInput, typeOutput));
                var callPropertyGetterClosedGenericMethod =
                    CallPropertyGetterOpenGenericMethod.MakeGenericMethod(typeInput, typeOutput);
                callPropertyGetterDelegate =
                    callPropertyGetterClosedGenericMethod.CreateDelegate(
                        typeof(Func<object, object>), propertyGetterAsFunc);
            }

            return (Func<object, object>)callPropertyGetterDelegate;
        }

        /// <summary>
        /// Creates a single fast property setter for reference types. The result is not cached.
        /// </summary>
        /// <param name="propertyInfo">propertyInfo to extract the setter for.</param>
        /// <returns>a fast getter.</returns>
        /// <remarks>
        /// This method is more memory efficient than a dynamically compiled lambda, and about the
        /// same speed. This only works for reference types.
        /// </remarks>
        public static Action<object, object> MakeFastPropertySetter(PropertyInfo propertyInfo)
        {
            var setMethod = propertyInfo.SetMethod;
            var parameters = setMethod.GetParameters();

            // Instance methods in the CLR can be turned into static methods where the first parameter
            // is open over "target". This parameter is always passed by reference, so we have a code
            // path for value types and a code path for reference types.
            var typeInput = setMethod.DeclaringType;
            var parameterType = parameters[0].ParameterType;

            // Create a delegate TDeclaringType -> { TDeclaringType.Property = TValue; }
            var propertySetterAsAction =
                setMethod.CreateDelegate(typeof(Action<,>).MakeGenericType(typeInput, parameterType));
            var callPropertySetterClosedGenericMethod =
                CallPropertySetterOpenGenericMethod.MakeGenericMethod(typeInput, parameterType);
            var callPropertySetterDelegate =
                callPropertySetterClosedGenericMethod.CreateDelegate(
                    typeof(Action<object, object>), propertySetterAsAction);

            return (Action<object, object>)callPropertySetterDelegate;
        }

        private static PropertyHelper CreateInstance(PropertyInfo property, string propertyHierarchy = null)
        {
            return new PropertyHelper(property, propertyHierarchy);
        }

        // Called via reflection
        private static object CallPropertyGetter<TDeclaringType, TValue>(
            Func<TDeclaringType, TValue> getter,
            object target)
        {
            return getter((TDeclaringType)target);
        }

        // Called via reflection
        private static object CallPropertyGetterByReference<TDeclaringType, TValue>(
            ByRefFunc<TDeclaringType, TValue> getter,
            object target)
        {
            var unboxed = (TDeclaringType)target;
            return getter(ref unboxed);
        }

        private static void CallPropertySetter<TDeclaringType, TValue>(
            Action<TDeclaringType, TValue> setter,
            object target,
            object value)
        {
            setter((TDeclaringType)target, (TValue)value);
        }

        protected static PropertyHelper[] GetProperties(
            Type type,
            Func<PropertyInfo, string, PropertyHelper> createPropertyHelper,
            ConcurrentDictionary<Type, PropertyHelper[]> cache,
            string propertyHierarchy = null)
        {
            // Unwrap nullable types. This means Nullable<T>.Value and Nullable<T>.HasValue will not be
            // part of the sequence of properties returned by this method.
            type = Nullable.GetUnderlyingType(type) ?? type;

            // Using an array rather than IEnumerable, as target will be called on the hot path numerous times.
            PropertyHelper[] helpers;

            if (!cache.TryGetValue(type, out helpers))
            {
                var getPropertyName =
                    new Func<PropertyInfo, string>(pi => (propertyHierarchy == null ? "" : propertyHierarchy+".") + pi.Name);

                // We avoid loading indexed properties using the where statement.
                // Indexed properties are not useful (or valid) for grabbing properties off an object.
                var properties = type.GetRuntimeProperties().Where(
                    prop => prop.GetIndexParameters().Length == 0 &&
                            prop.GetMethod != null &&
                            prop.GetMethod.IsPublic &&
                            !prop.GetMethod.IsStatic);

                var primitives = properties.Where(pi => IsPrimitiveType(pi.PropertyType))
                    .Select(p => createPropertyHelper(p, getPropertyName(p)));

                //recursive call for complex properties
                var complex = properties.Where(pi => !IsPrimitiveType(pi.PropertyType))
                    .SelectMany(pi => GetProperties(pi.PropertyType, getPropertyName(pi) ));
                
                helpers = primitives.Concat(complex).ToArray();
                cache.TryAdd(type, helpers);
            }

            return helpers;
        }

        public static bool IsPrimitiveType(Type fieldType)
        {
            return fieldType.IsPrimitive || fieldType.Namespace.Equals("System");
        }
    }
}