using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Text;

namespace Il2CppDumper
{
    /// <summary>
    /// Reconstructs interface MethodImpl rows that are not represented explicitly
    /// in IL2CPP metadata. Only private virtual methods are considered, so normal
    /// public implicit interface implementations keep their original shape.
    /// </summary>
    public static class ExplicitInterfaceOverrideMapper
    {
        public static int Apply(TypeDefinition typeDefinition)
        {
            if (typeDefinition == null)
                throw new ArgumentNullException(nameof(typeDefinition));
            if (!typeDefinition.HasInterfaces || !typeDefinition.HasMethods)
                return 0;

            var interfaces = new List<InterfaceContract>();
            foreach (var implementation in typeDefinition.Interfaces)
            {
                TypeDefinition definition = TryResolve(implementation.InterfaceType);
                if (definition != null)
                {
                    interfaces.Add(new InterfaceContract(implementation.InterfaceType, definition));
                }
            }

            if (interfaces.Count == 0)
                return 0;

            int added = 0;
            foreach (var method in typeDefinition.Methods)
            {
                if (!method.IsPrivate || !method.IsVirtual)
                    continue;

                SplitMethodName(method.Name, out string interfacePrefix, out string contractName);
                var matches = FindMatches(method, interfacePrefix, contractName, interfaces);
                if (matches.Count != 1)
                    continue;

                InterfaceMethodMatch match = matches[0];
                if (HasOverride(method, match.InterfaceType, match.Method))
                    continue;

                method.Overrides.Add(CreateOverrideReference(
                    method.Module, match.InterfaceType, match.Method));
                added++;
            }

            return added;
        }

        private static List<InterfaceMethodMatch> FindMatches(
            MethodDefinition implementation,
            string interfacePrefix,
            string contractName,
            List<InterfaceContract> interfaces)
        {
            var matches = new List<InterfaceMethodMatch>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contract in interfaces)
            {
                if (interfacePrefix != null &&
                    !string.Equals(NormalizeInterfaceName(interfacePrefix),
                        NormalizeInterfaceName(contract.Reference.FullName),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var interfaceMethod in contract.Definition.Methods)
                {
                    if (!string.Equals(interfaceMethod.Name, contractName, StringComparison.Ordinal) ||
                        !SignatureMatches(implementation, interfaceMethod, contract.Reference))
                    {
                        continue;
                    }

                    string key = BuildMatchKey(contract.Reference, interfaceMethod);
                    if (seen.Add(key))
                    {
                        matches.Add(new InterfaceMethodMatch(contract.Reference, interfaceMethod));
                    }
                }
            }
            return matches;
        }

        private static bool SignatureMatches(
            MethodDefinition implementation,
            MethodDefinition contract,
            TypeReference interfaceType)
        {
            if (implementation.GenericParameters.Count != contract.GenericParameters.Count ||
                implementation.Parameters.Count != contract.Parameters.Count)
            {
                return false;
            }

            var genericInterface = interfaceType as GenericInstanceType;
            if (!string.Equals(GetTypeKey(implementation.ReturnType, null),
                GetTypeKey(contract.ReturnType, genericInterface), StringComparison.Ordinal))
            {
                return false;
            }

            for (int i = 0; i < implementation.Parameters.Count; i++)
            {
                if (!string.Equals(GetTypeKey(implementation.Parameters[i].ParameterType, null),
                    GetTypeKey(contract.Parameters[i].ParameterType, genericInterface),
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static string GetTypeKey(TypeReference type, GenericInstanceType genericInterface)
        {
            if (type is GenericParameter parameter)
            {
                if (parameter.Type == GenericParameterType.Type && genericInterface != null &&
                    parameter.Position >= 0 && parameter.Position < genericInterface.GenericArguments.Count)
                {
                    return GetTypeKey(genericInterface.GenericArguments[parameter.Position], null);
                }
                return (parameter.Type == GenericParameterType.Method ? "!!" : "!") +
                    parameter.Position;
            }
            if (type is GenericInstanceType generic)
            {
                var builder = new StringBuilder();
                builder.Append(NormalizeTypeName(generic.ElementType.FullName));
                builder.Append('<');
                for (int i = 0; i < generic.GenericArguments.Count; i++)
                {
                    if (i != 0) builder.Append(',');
                    builder.Append(GetTypeKey(generic.GenericArguments[i], genericInterface));
                }
                builder.Append('>');
                return builder.ToString();
            }
            if (type is ArrayType array)
                return "array" + array.Rank + "(" + GetTypeKey(array.ElementType, genericInterface) + ")";
            if (type is ByReferenceType byReference)
                return "byref(" + GetTypeKey(byReference.ElementType, genericInterface) + ")";
            if (type is PointerType pointer)
                return "ptr(" + GetTypeKey(pointer.ElementType, genericInterface) + ")";
            if (type is OptionalModifierType optionalModifier)
                return "modopt(" + GetTypeKey(optionalModifier.ModifierType, genericInterface) + "," +
                    GetTypeKey(optionalModifier.ElementType, genericInterface) + ")";
            if (type is RequiredModifierType requiredModifier)
                return "modreq(" + GetTypeKey(requiredModifier.ModifierType, genericInterface) + "," +
                    GetTypeKey(requiredModifier.ElementType, genericInterface) + ")";
            if (type is PinnedType pinned)
                return "pinned(" + GetTypeKey(pinned.ElementType, genericInterface) + ")";
            if (type is SentinelType sentinel)
                return "sentinel(" + GetTypeKey(sentinel.ElementType, genericInterface) + ")";
            return NormalizeTypeName(type.FullName);
        }

        private static MethodReference CreateOverrideReference(
            ModuleDefinition module,
            TypeReference interfaceType,
            MethodDefinition contract)
        {
            MethodReference imported = module.ImportReference(contract);
            var reference = new MethodReference(
                imported.Name,
                imported.ReturnType,
                module.ImportReference(interfaceType))
            {
                HasThis = imported.HasThis,
                ExplicitThis = imported.ExplicitThis,
                CallingConvention = imported.CallingConvention,
            };
            foreach (var genericParameter in imported.GenericParameters)
            {
                reference.GenericParameters.Add(
                    new GenericParameter(genericParameter.Name, reference)
                    {
                        Attributes = genericParameter.Attributes,
                    });
            }
            foreach (var parameter in imported.Parameters)
            {
                reference.Parameters.Add(new ParameterDefinition(
                    parameter.Name, parameter.Attributes, parameter.ParameterType));
            }
            return reference;
        }

        private static bool HasOverride(
            MethodDefinition implementation,
            TypeReference interfaceType,
            MethodDefinition contract)
        {
            string expectedInterface = NormalizeInterfaceName(interfaceType.FullName);
            foreach (var existing in implementation.Overrides)
            {
                if (string.Equals(existing.Name, contract.Name, StringComparison.Ordinal) &&
                    string.Equals(NormalizeInterfaceName(existing.DeclaringType.FullName),
                        expectedInterface, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildMatchKey(TypeReference interfaceType, MethodDefinition contract)
        {
            var builder = new StringBuilder();
            builder.Append(NormalizeInterfaceName(interfaceType.FullName));
            builder.Append("::");
            builder.Append(contract.Name);
            builder.Append('`');
            builder.Append(contract.GenericParameters.Count);
            builder.Append('(');
            var genericInterface = interfaceType as GenericInstanceType;
            for (int i = 0; i < contract.Parameters.Count; i++)
            {
                if (i != 0) builder.Append(',');
                builder.Append(GetTypeKey(contract.Parameters[i].ParameterType, genericInterface));
            }
            builder.Append(")->");
            builder.Append(GetTypeKey(contract.ReturnType, genericInterface));
            return builder.ToString();
        }

        private static void SplitMethodName(
            string methodName,
            out string interfacePrefix,
            out string contractName)
        {
            int separator = methodName.LastIndexOf('.');
            if (separator > 0)
            {
                interfacePrefix = methodName.Substring(0, separator);
                contractName = methodName.Substring(separator + 1);
            }
            else
            {
                interfacePrefix = null;
                contractName = methodName;
            }
        }

        private static string NormalizeInterfaceName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (current == '`')
                {
                    while (i + 1 < value.Length && char.IsDigit(value[i + 1])) i++;
                    continue;
                }
                if (current == '/' || current == '+') current = '.';
                if (!char.IsWhiteSpace(current)) builder.Append(current);
            }
            return builder.ToString();
        }

        private static string NormalizeTypeName(string value) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Replace('/', '.').Replace('+', '.');

        private static TypeDefinition TryResolve(TypeReference reference)
        {
            try
            {
                return reference.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
            catch (ResolutionException)
            {
                return null;
            }
        }

        private sealed class InterfaceContract
        {
            public InterfaceContract(TypeReference reference, TypeDefinition definition)
            {
                Reference = reference;
                Definition = definition;
            }

            public TypeReference Reference { get; }
            public TypeDefinition Definition { get; }
        }

        private sealed class InterfaceMethodMatch
        {
            public InterfaceMethodMatch(TypeReference interfaceType, MethodDefinition method)
            {
                InterfaceType = interfaceType;
                Method = method;
            }

            public TypeReference InterfaceType { get; }
            public MethodDefinition Method { get; }
        }
    }
}
