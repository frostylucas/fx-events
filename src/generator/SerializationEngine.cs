﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Moonlight.Generators.Models;
using Moonlight.Generators.Problems;
using Moonlight.Generators.Serialization;
using Moonlight.Generators.Syntax;

namespace Moonlight.Generators
{
    public class SerializationEngine : ISyntaxContextReceiver
    {
        private static string Notice => "// Auto-generated by the Serialization Generator.";
        private const string EnumerableQualifiedName = "System.Collections.Generic.IEnumerable`1";
        private const string PackingMethod = "PackSerializedBytes";
        private const string UnpackingMethod = "UnpackSerializedBytes";

        private static readonly Dictionary<string, string[]> DeconstructionTypes = new()
        {
            ["System.Collections.Generic.KeyValuePair`2"] = new[] { "Key", "Value" },
            ["System.Tuple`2"] = new[] { "Item1", "Item2" }
        };

        private static readonly Dictionary<string, IDefaultSerialization> DefaultSerialization = new()
        {
            ["System.Collections.Generic.KeyValuePair`2"] = new KeyValuePairSerialization(),
            ["System.DateTime"] = new DateTimeSerialization(),
            ["System.TimeSpan"] = new TimeSpanSerialization(),
            ["System.Tuple`1"] = new TupleSingleSerialization(),
            ["System.Tuple`2"] = new TupleDoubleSerialization(),
            ["System.Tuple`3"] = new TupleTripleSerialization(),
            ["System.Tuple`4"] = new TupleQuadrupleSerialization(),
            ["System.Tuple`5"] = new TupleQuintupleSerialization(),
            ["System.Tuple`6"] = new TupleSextupleSerialization(),
            ["System.Tuple`7"] = new TupleSeptupleSerialization()
        };

        private static readonly Dictionary<string, string> PredefinedTypes = new()
        {
            { "bool", "Bool" },
            { "byte", "Byte" },
            { "byte[]", "Bytes" },
            { "char", "Char" },
            { "char[]", "Chars" },
            { "decimal", "Decimal" },
            { "double", "Double" },
            { "short", "Int16" },
            { "int", "Int32" },
            { "long", "Int64" },
            { "float", "Single" },
            { "string", "String" },
            { "sbyte", "SByte" },
            { "ushort", "UInt16" },
            { "uint", "UInt32" },
            { "ulong", "UInt64" }
        };

        public readonly List<WorkItem> WorkItems = new();
        public readonly List<SerializationProblem> Problems = new();
        public readonly List<string> Logs = new();

        public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
        {
            if (context.Node is not ClassDeclarationSyntax classDecl) return;

            var symbol = (INamedTypeSymbol) context.SemanticModel.GetDeclaredSymbol(context.Node);

            if (symbol == null) return;
            if (!HasMarkedAsSerializable(symbol)) return;

            var hasPartial = classDecl.Modifiers.Any(self => self.ToString() == "partial");

            if (!hasPartial)
            {
                var problem = new SerializationProblem
                {
                    Descriptor = new DiagnosticDescriptor(ProblemId.SerializationMarking, "Serialization Marking",
                        "Serialization marked type {0} is missing the partial keyword.", "serialization",
                        DiagnosticSeverity.Error, true),
                    Locations = new[] { symbol.Locations.FirstOrDefault() },
                    Format = new object[] { symbol.Name }
                };

                Problems.Add(problem);

                return;
            }

            CompilationUnitSyntax unit = null;
            NamespaceDeclarationSyntax namespaceDecl = null;
            SyntaxNode parent = classDecl;

            while ((parent = parent.Parent) != null)
            {
                switch (parent)
                {
                    case CompilationUnitSyntax syntax:
                        unit = syntax;

                        break;
                    case NamespaceDeclarationSyntax syntax:
                        namespaceDecl = syntax;

                        break;
                }
            }

            if (unit == null || namespaceDecl == null) return;

            WorkItems.Add(new WorkItem
            {
                TypeSymbol = symbol, SemanticModel = context.SemanticModel, ClassDeclaration = classDecl, Unit = unit,
                NamespaceDeclaration = namespaceDecl
            });
        }

        public CodeWriter Compile(WorkItem item)
        {
            var symbol = item.TypeSymbol;
            var code = new CodeWriter();
            var imports = new Dictionary<string, bool>
            {
                ["System"] = true, ["System.IO"] = true, ["System.Linq"] = true,
            };

            foreach (var usingDecl in item.Unit.Usings)
            {
                imports[usingDecl.Name.ToString()] = true;
            }

            foreach (var import in imports.Where(import => import.Value))
            {
                code.AppendLine($"using {import.Key};");
            }

            code.AppendLine();

            var properties = new List<Tuple<IPropertySymbol, bool>>();
            var shouldOverride =
                symbol.BaseType != null && symbol.BaseType.GetAttributes()
                    .Any(self => self.AttributeClass is { Name: "SerializationAttribute" });

            foreach (var member in GetAllMembers(symbol))
            {
                if (member is not IPropertySymbol propertySymbol) continue;

                var attributes = propertySymbol.GetAttributes();

                if (attributes.Any(self => self.AttributeClass is { Name: "IgnoreAttribute" })) continue;
                
                var forced = attributes.Any(self => self.AttributeClass is { Name: "ForceAttribute" });
                
                if (!forced &&
                    (propertySymbol.DeclaredAccessibility != Accessibility.Public ||
                     propertySymbol.IsIndexer || propertySymbol.IsReadOnly ||
                     propertySymbol.IsWriteOnly)) continue;

                properties.Add(Tuple.Create(propertySymbol, forced));
            }

            using (code.BeginScope($"namespace {item.NamespaceDeclaration.Name}"))
            {
                using (code.BeginScope(
                    $"public partial class {item.ClassDeclaration.Identifier}{item.ClassDeclaration.TypeParameterList} {item.ClassDeclaration.ConstraintClauses}"))
                {
                    if (!item.ClassDeclaration.DescendantNodes().Any(self =>
                        self is ConstructorDeclarationSyntax constructorDecl &&
                        constructorDecl.ParameterList.Parameters.Count == 0))
                    {
                        using (code.BeginScope($"public {symbol.Name}()"))
                        {
                        }
                    }

                    using (code.BeginScope($"public {symbol.Name}(BinaryReader reader)"))
                    {
                        code.AppendLine($"{UnpackingMethod}(reader);");
                    }

                    if (!HasImplementation(symbol, PackingMethod))
                    {
                        using (code.BeginScope(
                            $"public {(shouldOverride ? "new " : string.Empty)}void {PackingMethod}(BinaryWriter writer)"))
                        {
                            code.AppendLine(Notice);

                            foreach (var (property, _) in properties)
                            {
                                code.AppendLine();
                                code.AppendLine($"// Property: {property.Name} ({property.Type.MetadataName})");

                                using (code.BeginScope())
                                {
                                    AppendWriteLogic(property, property.Type, code, property.Name,
                                        symbol.Locations.FirstOrDefault());
                                }
                            }
                        }
                    }

                    if (!HasImplementation(symbol, UnpackingMethod))
                    {
                        using (code.BeginScope(
                            $"public {(shouldOverride ? "new " : string.Empty)}void {UnpackingMethod}(BinaryReader reader)"))
                        {
                            code.AppendLine(Notice);

                            foreach (var (property, forced) in properties)
                            {
                                if (forced && property.IsReadOnly) continue;
                                
                                code.AppendLine();
                                code.AppendLine($"// Property: {property.Name} ({property.Type.MetadataName})");

                                using (code.BeginScope())
                                {
                                    AppendReadLogic(property, property.Type, code, property.Name,
                                        symbol.Locations.FirstOrDefault());
                                }
                            }
                        }
                    }
                }
            }

            return code;
        }

        public void AppendWriteLogic(IPropertySymbol property, ITypeSymbol type, CodeWriter code, string name,
            Location location, ScopeTracker scope = null)
        {
            using (scope = scope == null ? code.Encapsulate() : scope.Reference())
            {
                var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;

                if (nullable)
                {
                    type = ((INamedTypeSymbol) type).TypeArguments.First();

                    code.AppendLine($"writer.Write({name}.HasValue);");
                    code.AppendLine($"if ({name}.HasValue)");
                    code.Open();
                }

                name = nullable ? $"{name}.Value" : name;

                if (DefaultSerialization.TryGetValue(GetQualifiedName(type), out var serialization))
                {
                    serialization.Serialize(this, property, type, code, name, GetIdentifierWithArguments(type),
                        location);

                    return;
                }

                if (IsPrimitive(type))
                {
                    if (!type.IsValueType)
                    {
                        using (code.BeginScope($"if ({name} is default({GetIdentifierWithArguments(type)}))"))
                        {
                            code.AppendLine(
                                $"throw new Exception(\"Member '{name}' is a primitive and has no value (null). If this is not an issue, please declare it as nullable.\");");
                        }
                    }

                    code.AppendLine($"writer.Write({name});");
                }
                else
                {
                    if (type.TypeKind != TypeKind.Struct && type.TypeKind != TypeKind.Enum && !nullable)
                    {
                        code.AppendLine($"writer.Write({name} != null);");
                        code.AppendLine($"if ({name} != null)");
                        code.Open();
                    }

                    switch (type.TypeKind)
                    {
                        case TypeKind.Enum:
                            code.AppendLine($"writer.Write((int) {name});");

                            break;
                        case TypeKind.Interface:
                        case TypeKind.Struct:
                        case TypeKind.Class:
                            var enumerable = GetQualifiedName(type) == EnumerableQualifiedName
                                ? (INamedTypeSymbol) type
                                : type.AllInterfaces.FirstOrDefault(self =>
                                    GetQualifiedName(self) == EnumerableQualifiedName);

                            if (enumerable != null)
                            {
                                var elementType = enumerable.TypeArguments.First();

                                using (code.BeginScope())
                                {
                                    var countTechnique = GetAllMembers(type)
                                        .Where(member => member is IPropertySymbol)
                                        .Aggregate("Count()", (current, symbol) => symbol.Name switch
                                        {
                                            "Count" => "Count",
                                            "Length" => "Length",
                                            _ => current
                                        });

                                    var prefix = SerializationEngine.GetVariablePrefix(name);
                                    
                                    code.AppendLine($"var {prefix}Count = {name}.{countTechnique};");
                                    code.AppendLine($"writer.Write({prefix}Count);");

                                    using (code.BeginScope($"foreach (var {prefix}Entry in {name})"))
                                    {
                                        AppendWriteLogic(property, elementType, code, $"{prefix}Entry", location, scope);
                                    }
                                }
                            }
                            else
                            {
                                if (type.TypeKind == TypeKind.Interface)
                                {
                                    var problem = new SerializationProblem
                                    {
                                        Descriptor = new DiagnosticDescriptor(ProblemId.InterfaceProperties,
                                            "Interface Properties",
                                            "Could not serialize property '{0}' of type {1} because Interface types are not supported",
                                            "serialization",
                                            DiagnosticSeverity.Error, true),
                                        Locations = new[] { property.Locations.FirstOrDefault(), location },
                                        Format = new object[] { property.Name, type.Name }
                                    };

                                    Problems.Add(problem);

                                    code.AppendLine(
                                        $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                    return;
                                }

                                if (HasImplementation(type, PackingMethod) || HasMarkedAsSerializable(type))
                                {
                                    code.AppendLine($"{name}.{PackingMethod}(writer);");
                                }
                                else
                                {
                                    var problem = new SerializationProblem
                                    {
                                        Descriptor = new DiagnosticDescriptor(ProblemId.MissingPackingMethod,
                                            "Packing Method",
                                            "Could not serialize property '{0}' because {1} is missing method {2}",
                                            "serialization",
                                            DiagnosticSeverity.Error, true),
                                        Locations = new[] { property.Locations.FirstOrDefault(), location },
                                        Format = new object[] { property.Name, type.Name, PackingMethod }
                                    };

                                    Problems.Add(problem);

                                    code.AppendLine(
                                        $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");
                                }
                            }

                            break;
                        case TypeKind.Array:
                            var array = (IArrayTypeSymbol) type;

                            code.AppendLine($"writer.Write({name}.Length);");

                            if (GetQualifiedName(array.ElementType) == "System.Byte")
                            {
                                code.AppendLine($"writer.Write({name});");
                            }
                            else
                            {
                                var prefix = SerializationEngine.GetVariablePrefix(name);
                                var indexName = $"{prefix}Idx";
                                
                                using (code.BeginScope($"for (var {indexName} = 0; {indexName} < {name}.Length; {indexName}++)"))
                                {
                                    AppendWriteLogic(property, array.ElementType, code, $"{name}[{indexName}]", location,
                                        scope);
                                }
                            }

                            break;
                    }
                }
            }
        }

        public void AppendReadLogic(IPropertySymbol property, ITypeSymbol type, CodeWriter code, string name,
            Location location, ScopeTracker scope = null)
        {
            using (scope = scope == null ? code.Encapsulate() : scope.Reference())
            {
                var nullable = type.NullableAnnotation == NullableAnnotation.Annotated;

                if (nullable)
                {
                    type = ((INamedTypeSymbol) type).TypeArguments.First();
                    code.AppendLine("if (reader.ReadBoolean())");
                    code.Open();
                }

                if (DefaultSerialization.TryGetValue(GetQualifiedName(type), out var serialization))
                {
                    serialization.Deserialize(this, property, type, code, name,
                        GetIdentifierWithArguments(type), location);

                    return;
                }

                if (IsPrimitive(type))
                {
                    code.AppendLine(
                        $"{name} = reader.Read{(PredefinedTypes.TryGetValue(type.Name, out var result) ? result : type.Name)}();");
                }
                else
                {
                    if (type.TypeKind != TypeKind.Struct && type.TypeKind != TypeKind.Enum && !nullable)
                    {
                        code.AppendLine("if (reader.ReadBoolean())");
                        code.Open();
                    }

                    switch (type.TypeKind)
                    {
                        case TypeKind.Enum:
                            code.AppendLine($"{name} = ({GetIdentifierWithArguments(type)}) reader.ReadInt32();");

                            break;
                        case TypeKind.Interface:
                        case TypeKind.Struct:
                        case TypeKind.Class:
                            var enumerable = GetQualifiedName(type) == EnumerableQualifiedName
                                ? (INamedTypeSymbol) type
                                : type.AllInterfaces.FirstOrDefault(self =>
                                    GetQualifiedName(self) == EnumerableQualifiedName);

                            if (enumerable != null)
                            {
                                var elementType = (INamedTypeSymbol) enumerable.TypeArguments.First();

                                if (type.TypeKind == TypeKind.Interface &&
                                    GetQualifiedName(type) != EnumerableQualifiedName)
                                {
                                    var problem = new SerializationProblem
                                    {
                                        Descriptor = new DiagnosticDescriptor(ProblemId.InterfaceProperties,
                                            "Interface Properties",
                                            "Could not deserialize property '{0}' of type {1} because Interface types are not supported",
                                            "serialization",
                                            DiagnosticSeverity.Error, true),
                                        Locations = new[] { property.Locations.FirstOrDefault(), location },
                                        Format = new object[] { property.Name, type.Name }
                                    };

                                    Problems.Add(problem);

                                    code.AppendLine(
                                        $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                    return;
                                }

                                using (code.BeginScope())
                                {
                                    var prefix = SerializationEngine.GetVariablePrefix(name);
                                    
                                    code.AppendLine($"var {prefix}Count = reader.ReadInt32();");

                                    var constructor =
                                        ((INamedTypeSymbol) type).Constructors.FirstOrDefault(
                                            self => GetQualifiedName(self.Parameters.FirstOrDefault()?.Type) ==
                                                    EnumerableQualifiedName);

                                    var method = HasImplementation(type, "Add", GetQualifiedName(elementType));
                                    var deconstructed = false;

                                    if (DeconstructionTypes.ContainsKey(GetQualifiedName(elementType)))
                                    {
                                        deconstructed = HasImplementation(type, "Add",
                                            elementType.TypeArguments.Cast<INamedTypeSymbol>().Select(GetQualifiedName)
                                                .ToArray());
                                    }

                                    if (method || deconstructed)
                                    {
                                        code.AppendLine(
                                            $"{name} = new {GetIdentifierWithArguments(type)}();");
                                    }
                                    else
                                    {
                                        code.AppendLine(
                                            $"var {prefix}Temp = new {GetIdentifierWithArguments(elementType)}[{prefix}Count];");
                                    }

                                    var indexName = $"{prefix}Idx";
                                    
                                    using (code.BeginScope($"for (var {indexName} = 0; {indexName} < {prefix}Count; {indexName}++)"))
                                    {
                                        AppendReadLogic(property, elementType, code,
                                            method || deconstructed ? $"var {prefix}Transient" : $"{prefix}Temp[{indexName}", location, scope);

                                        if (method)
                                        {
                                            code.AppendLine($"{name}.Add({prefix}Transient);");
                                        }
                                        else if (deconstructed)
                                        {
                                            var arguments = DeconstructionTypes[GetQualifiedName(elementType)]
                                                .Select(self => $"{prefix}Transient.{self}");

                                            code.AppendLine($"{name}.Add({string.Join(",", arguments)});");
                                        }
                                    }

                                    if (method || deconstructed)
                                    {
                                        return;
                                    }

                                    if (constructor != null)
                                    {
                                        code.AppendLine(
                                            $"{name} = new {GetIdentifierWithArguments(enumerable)}({prefix}Temp);");

                                        return;
                                    }

                                    if (GetQualifiedName(type) != EnumerableQualifiedName)
                                    {
                                        var problem = new SerializationProblem
                                        {
                                            Descriptor = new DiagnosticDescriptor(ProblemId.EnumerableProperties,
                                                "Enumerable Properties",
                                                "Could not deserialize property '{0}' because enumerable type {1} did not contain a suitable way of adding items",
                                                "serialization",
                                                DiagnosticSeverity.Error, true),
                                            Locations = new[] { property.Locations.FirstOrDefault(), location },
                                            Format = new object[] { property.Name, type.Name, elementType.Name }
                                        };

                                        Problems.Add(problem);

                                        code.AppendLine(
                                            $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                        return;
                                    }

                                    code.AppendLine($"{name} = {prefix}Temp;");
                                }
                            }
                            else
                            {
                                if (type.TypeKind == TypeKind.Interface)
                                {
                                    var problem = new SerializationProblem
                                    {
                                        Descriptor = new DiagnosticDescriptor(ProblemId.InterfaceProperties,
                                            "Interface Properties",
                                            "Could not deserialize property '{0}' of type {1} because Interface types are not supported",
                                            "serialization",
                                            DiagnosticSeverity.Error, true),
                                        Locations = new[] { property.Locations.FirstOrDefault(), location },
                                        Format = new object[] { property.Name, type.Name }
                                    };

                                    Problems.Add(problem);

                                    code.AppendLine(
                                        $"throw new Exception(\"{string.Format(problem.Descriptor.MessageFormat.ToString(), problem.Format)}\");");

                                    return;
                                }

                                code.AppendLine(
                                    $"{name} = new {GetIdentifierWithArguments(type)}(reader);");
                            }

                            break;
                        case TypeKind.Array:
                            var array = (IArrayTypeSymbol) type;

                            using (code.BeginScope())
                            {
                                var prefix = SerializationEngine.GetVariablePrefix(name);
                                
                                code.AppendLine($"var {prefix}Length = reader.ReadInt32();");
                                code.AppendLine(
                                    $"{name} = new {GetIdentifierWithArguments(array.ElementType)}[{prefix}Length];");

                                var indexName = $"{prefix}Idx";
                                
                                using (code.BeginScope($"for (var {indexName} = 0; {indexName} < {prefix}Length; {indexName}++)"))
                                {
                                    AppendReadLogic(property, array.ElementType, code, $"{name}[{indexName}]", location, scope);
                                }
                            }

                            break;
                    }
                }
            }
        }

        private static bool IsPrimitive(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_Int32:
                case SpecialType.System_Int64:
                case SpecialType.System_Byte:
                case SpecialType.System_UInt16:
                case SpecialType.System_UInt32:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Char:
                case SpecialType.System_String:
                case SpecialType.System_Object:
                    return true;
                default:
                    return false;
            }
        }

        public static string GetVariablePrefix(string value)
        {
            if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
                return value;

            return char.ToLower(value[0]) + value.Substring(1);
        }

        private static string GetIdentifierWithArguments(ISymbol symbol)
        {
            var builder = new StringBuilder();

            builder.Append(GetFullName(symbol));

            if (symbol is not INamedTypeSymbol named || named.TypeArguments == null ||
                named.TypeArguments.IsDefaultOrEmpty) return builder.ToString();

            builder.Append("<");
            builder.Append(string.Join(",",
                named.TypeArguments.Cast<INamedTypeSymbol>().Select(GetIdentifierWithArguments)));
            builder.Append(">");

            return builder.ToString();
        }

        public static string GetQualifiedName(ISymbol symbol)
        {
            var name = symbol != null ? GetFullName(symbol) : null;

            if (symbol is not INamedTypeSymbol { TypeArguments: { Length: > 0 } } named) return name;

            name += "`";
            name += named.TypeArguments.Length;

            return name;
        }

        private static string GetFullName(ISymbol symbol)
        {
            var builder = new StringBuilder();
            var containing = symbol;

            builder.Append(symbol.ContainingNamespace);
            builder.Append(".");

            var idx = builder.Length;

            while ((containing = containing.ContainingType) != null)
            {
                builder.Insert(idx, containing.Name + ".");
            }

            builder.Append(symbol.Name);

            return builder.ToString();
        }

        private static bool HasMarkedAsSerializable(ISymbol symbol)
        {
            var attribute = symbol?.GetAttributes()
                .FirstOrDefault(self => self.AttributeClass is { Name: "SerializationAttribute" });

            return attribute != null;
        }

        private bool HasImplementation(ITypeSymbol symbol, string methodName,
            params string[] parameters)
        {
            foreach (var member in GetAllMembers(symbol))
            {
                if (member is not IMethodSymbol methodSymbol || methodSymbol.Name != methodName) continue;
                if (parameters == null || parameters.Length == 0) return true;

                var failed = false;

                for (var index = 0; index < parameters.Length; index++)
                {
                    var parameter = parameters[index];

                    if (methodSymbol.Parameters.Length == index)
                    {
                        failed = true;
                        break;
                    }

                    if (GetQualifiedName(methodSymbol.Parameters[index].Type) == parameter) continue;

                    failed = true;

                    break;
                }

                if (failed) continue;

                return true;
            }

            return false;
        }

        private static IEnumerable<ISymbol> GetAllMembers(ITypeSymbol symbol)
        {
            var members = new List<ISymbol>();

            members.AddRange(symbol.GetMembers());

            if (symbol.BaseType != null)
                members.AddRange(
                    symbol.BaseType.GetMembers().Where(self => members.All(deep => self.Name != deep.Name)));

            foreach (var type in symbol.AllInterfaces)
            {
                members.AddRange(type.GetMembers().Where(self => members.All(deep => self.Name != deep.Name)));
            }

            return members.Where(self => !self.IsStatic);
        }
    }
}