using System;
using System.Reflection;

namespace BLWarsailsInhabitableIslands
{
    internal sealed class OptionalBooleanPropertyAccessor
    {
        private readonly Type _declaringType;
        private readonly string _propertyName;
        private readonly MethodInfo _getter;

        internal OptionalBooleanPropertyAccessor(
            Type declaringType,
            string propertyName)
        {
            _declaringType = declaringType;
            _propertyName = propertyName;

            if (declaringType == null)
            {
                Availability = "declaring type is unavailable";
                return;
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                Availability = "property name is unavailable";
                return;
            }

            PropertyInfo property = declaringType.GetProperty(
                propertyName,
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static);
            if (property == null)
            {
                Availability = declaringType.FullName + "." + propertyName +
                               " is unavailable";
                return;
            }

            if (property.PropertyType != typeof(bool))
            {
                Availability = declaringType.FullName + "." + propertyName +
                               " does not return Boolean";
                return;
            }

            if (property.GetIndexParameters().Length != 0)
            {
                Availability = declaringType.FullName + "." + propertyName +
                               " is indexed";
                return;
            }

            MethodInfo getter = property.GetGetMethod(true);
            if (getter == null || getter.IsStatic ||
                getter.GetParameters().Length != 0)
            {
                Availability = declaringType.FullName + "." + propertyName +
                               " has no compatible instance getter";
                return;
            }

            _getter = getter;
            Availability = "available";
        }

        internal bool IsAvailable => _getter != null;

        internal string Availability { get; }

        internal bool TryGetValue(
            object instance,
            out bool value,
            out string failureReason)
        {
            value = false;
            if (_getter == null)
            {
                failureReason = Availability;
                return false;
            }

            if (instance == null || !_declaringType.IsInstanceOfType(instance))
            {
                failureReason = _declaringType.FullName + "." + _propertyName +
                                " received an incompatible instance";
                return false;
            }

            try
            {
                object result = _getter.Invoke(instance, null);
                if (result is bool boolValue)
                {
                    value = boolValue;
                    failureReason = null;
                    return true;
                }

                failureReason = _declaringType.FullName + "." + _propertyName +
                                " returned a non-Boolean value";
                return false;
            }
            catch (Exception ex)
            {
                Exception source = ex is TargetInvocationException invocation &&
                                   invocation.InnerException != null
                    ? invocation.InnerException
                    : ex;
                failureReason = _declaringType.FullName + "." + _propertyName +
                                " getter failed: " + source.GetType().Name +
                                ": " + source.Message;
                return false;
            }
        }
    }
}
