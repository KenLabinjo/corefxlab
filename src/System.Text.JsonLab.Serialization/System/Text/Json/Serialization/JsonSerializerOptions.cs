﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace System.Text.Json.Serialization
{
    public class JsonSerializerOptions
    {
        internal const int BufferSizeUnspecified = -1;
        internal const int BufferSizeDefault = 16 * 1024;

        private volatile JsonMemberBasedClassMaterializer _classMaterializerStrategy;
        private JsonClassMaterializer _classMaterializer;
        private int _defaultBufferSize = BufferSizeUnspecified;
        private bool _hasRuntimeCustomAttributes;

        private static readonly GlobalAttributeInfo s_globalAttributeInfo = new GlobalAttributeInfo();

        private static readonly ConcurrentDictionary<ICustomAttributeProvider, object[]> s_reflectionAttributes = new ConcurrentDictionary<ICustomAttributeProvider, object[]>();
        private readonly Lazy<ConcurrentDictionary<ICustomAttributeProvider, List<Attribute>>> _runtimeAttributes = new Lazy<ConcurrentDictionary<ICustomAttributeProvider, List<Attribute>>>();

        private static readonly ConcurrentDictionary<Type, JsonClassInfo> s_classes = new ConcurrentDictionary<Type, JsonClassInfo>();
        private readonly ConcurrentDictionary<Type, JsonClassInfo> _local_classes = new ConcurrentDictionary<Type, JsonClassInfo>();

        public JsonSerializerOptions(bool ignoreDesignTimeAttributes = false)
        {
            IgnoreDesignTimeAttributes = ignoreDesignTimeAttributes;
        }

        public bool IgnoreDesignTimeAttributes {get; private set;}

        internal JsonClassInfo GetOrAddClass(Type classType)
        {
            JsonClassInfo result;

            // Once custom attributes have been used, cache classes locally.
            if (_hasRuntimeCustomAttributes)
            {
                if (!_local_classes.TryGetValue(classType, out result))
                {
                    result = _local_classes.GetOrAdd(classType, new JsonClassInfo(classType, this));
                }
            }
            else
            {
                if (!s_classes.TryGetValue(classType, out result))
                {
                    result = s_classes.GetOrAdd(classType, new JsonClassInfo(classType, this));
                }
            }

            return result;
        }

#if BUILDING_INBOX_LIBRARY
        public JsonReaderOptions ReaderOptions { get; set; }
        public JsonWriterOptions WriterOptions { get; set; }
#else
        internal JsonReaderOptions ReaderOptions { get; set; }
        internal JsonWriterOptions WriterOptions { get; set; }
#endif

        public JsonClassMaterializer ClassMaterializer
        {
            get
            {
                return _classMaterializer;
            }
            set
            {
                if (_classMaterializer != JsonClassMaterializer.Default && value != _classMaterializer)
                {
                    throw new InvalidOperationException("Can't change materializer");
                }

                _classMaterializer = value;
            }
        }

        public int DefaultBufferSize
        {
            get
            {
                return _defaultBufferSize;
            }
            set
            {
                if (value == 0 || value < BufferSizeUnspecified)
                {
                    throw new ArgumentException("todo: value must be greater than zero or equal to -1");
                }

                _defaultBufferSize = value;

                if (_defaultBufferSize == BufferSizeUnspecified)
                {
                    EffectiveBufferSize = BufferSizeDefault;
                }
                else
                {
                    EffectiveBufferSize = _defaultBufferSize;
                }
            }
        }

        public bool IgnoreNullPropertyValueOnWrite { get; set; }
        public bool IgnoreNullPropertyValueOnRead { get; set; }
        public bool CaseInsensitivePropertyName { get; set; }

        // Used internally for performance to avoid checking BufferSizeUnspecified.
        internal int EffectiveBufferSize { get; private set; } = BufferSizeDefault;

        //todo: throw exception if we try to add attributes or change the Ignore\Case properties above once (de)serialization occurred. That allows this instance to be shared across users.

        /// <summary>
        /// Add a global attribute.
        /// </summary>
        /// <param name="attribute"></param>
        public void AddAttribute(Attribute attribute)
        {
            if (attribute == null)
                throw new ArgumentNullException(nameof(attribute));

            AddAttributeInternal(GlobalAttributesProvider, attribute);
        }

        /// <summary>
        /// Adds an attribute to the specified type which may be a Property, Class or Assembly.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="attribute"></param>
        public void AddAttribute(ICustomAttributeProvider type, Attribute attribute)
        {
            if (type == null)
                throw new ArgumentException(nameof(type));

            if (attribute == null)
                throw new ArgumentNullException(nameof(attribute));

            AddAttributeInternal(type, attribute);
        }

        private void AddAttributeInternal(ICustomAttributeProvider type, Attribute attribute)
        {
            if (!_runtimeAttributes.Value.TryGetValue(type, out List<Attribute> attributes))
            {
                _runtimeAttributes.Value.TryAdd(type, attributes = new List<Attribute>());
                // Failure in TryAdd is OK since another thread just finished the same operation.
            }

            attributes.Add(attribute);
            _hasRuntimeCustomAttributes = true;
        }

        internal static object[] GetGlobalAttributes<TAttribute>(ICustomAttributeProvider type, bool inherit) where TAttribute : Attribute
        {
            if (!s_reflectionAttributes.TryGetValue(type, out object[] attributes))
            {
                attributes = type.GetCustomAttributes(inherit: inherit);
                s_reflectionAttributes.TryAdd(type, attributes);
                // Failure in TryAdd is OK since another thread just finished the same operation.
            }

            return attributes;
        }

        /// <summary>
        /// Returns the attributes for the provided type.
        /// </summary>
        /// <typeparam name="TAttribute"></typeparam>
        /// <param name="type"></param>
        /// <param name="inherit">Whether the base classes are searched.</param>
        /// <returns></returns>
        internal IEnumerable<TAttribute> GetAttributes<TAttribute>(ICustomAttributeProvider type, bool inherit = false) where TAttribute : Attribute
        {
            if (type == null)
                throw new ArgumentException(nameof(type));

            IEnumerable<TAttribute> attributes = Enumerable.Empty<TAttribute>();

            if (_runtimeAttributes.IsValueCreated)
            {
                ICustomAttributeProvider baseType = type;
                do
                {
                    _runtimeAttributes.Value.TryGetValue(baseType, out List<Attribute> allRuntimeAttributes);
                    if (allRuntimeAttributes != null)
                    {
                        attributes = attributes.Concat(allRuntimeAttributes.OfType<TAttribute>());
                    }

                    if (inherit)
                    {
                        baseType = (baseType as Type)?.BaseType;
                    }
                    else
                    {
                        baseType = null;
                    }
                }
                while (baseType != null && (Type)baseType != typeof(object));
            }

            if (IgnoreDesignTimeAttributes)
            {
                return attributes;
            }

            object[] globalAttributes = GetGlobalAttributes<TAttribute>(type, inherit);
            return attributes.Concat(globalAttributes.OfType<TAttribute>());
        }

        internal JsonMemberBasedClassMaterializer ClassMaterializerStrategy
        {
            get
            {
                if (_classMaterializerStrategy == null)
                {
                    if (ClassMaterializer == JsonClassMaterializer.Default)
                    {
#if BUILDING_INBOX_LIBRARY
                        _classMaterializerStrategy = new JsonReflectionEmitMaterializer();
#else
                        _classMaterializerStrategy = new JsonReflectionMaterializer();
#endif
                    }
                    else if (ClassMaterializer == JsonClassMaterializer.ReflectionEmit)
                    {
#if BUILDING_INBOX_LIBRARY
                        _classMaterializerStrategy = new JsonReflectionEmitMaterializer();
#else
                        throw new PlatformNotSupportedException("TODO: IL Emit is not supported on .NET Standard 2.0.");
#endif
                    }
                    else if (ClassMaterializer == JsonClassMaterializer.Reflection)
                    {
                        _classMaterializerStrategy = new JsonReflectionMaterializer();
                    }
                    else
                    {
                        throw new InvalidOperationException("todo: invalid option");
                    }
                }

                return _classMaterializerStrategy;
            }
            set
            {
                if (ClassMaterializer == JsonClassMaterializer.ReflectionEmit)
                {
#if !BUILDING_INBOX_LIBRARY
                        throw new PlatformNotSupportedException("TODO: IL Emit is not supported on .NET Standard 2.0.");
#endif
                }

                _classMaterializerStrategy = value;
            }
        }

        internal static ICustomAttributeProvider GlobalAttributesProvider => s_globalAttributeInfo;

        // Use this type to represent the type where global attributes are applied.
        internal class GlobalAttributeInfo : ICustomAttributeProvider
        {
            public object[] GetCustomAttributes(bool inherit) => Array.Empty<object>();
            public object[] GetCustomAttributes(Type attributeType, bool inherit) => Array.Empty<object>();
            public bool IsDefined(Type attributeType, bool inherit) => false;
        }
    }
}
