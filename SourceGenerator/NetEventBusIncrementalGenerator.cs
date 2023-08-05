using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SourceGenerator;

// how-to-debug-source-generator-vs2022
// https://github.com/dotnet/roslyn-sdk/issues/850#issuecomment-1041839050

[Generator(LanguageNames.CSharp)]
public class NetEventBusIncrementalGenerator : IIncrementalGenerator
{
    public static readonly string Namespace = "Any.NetEvent";
    public static readonly string AttributeName = $"NetEventAttribute";
    public static readonly string AttributeFullName = $"{Namespace}.{AttributeName}";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        {
            var attributes = $$"""
            namespace {{Namespace}};
            using global::System;

            [AttributeUsage(AttributeTargets.Method)]
            public class {{AttributeName}} : Attribute
            {
                public readonly ushort ReqId;
                public readonly ushort RespId;

                public NetEventAttribute(ushort reqId, ushort respId = 0)
                {
                    ReqId = reqId;
                    RespId = respId;
                }
            }
            """;
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource("NetEventAttribute.g.cs", SourceText.From(attributes, Encoding.UTF8)));
        }

        {
            var attributes = $$"""
            namespace {{Namespace}};
            using Arch.Core;

            public interface IPacket
            {
                ushort PacketId { get; }
                int Length { get; }
                T Decode<T>();
                int Encode(ArraySegment<byte> segment, int offset = 0);
            }

            public sealed partial class NetEventBus
            {
                private readonly Dictionary<int, Func<World, Entity, IPacket, (ushort, object)?>> _packetDict = new();

                public NetEventBus()
                {
                }

                public (ushort, object)? Dispatch(in World world, ref Entity entity, ref IPacket packet)
                {
                    if (_packetDict.TryGetValue(packet.PacketId, out var action))
                    {
                        return action.Invoke(world, entity, packet);
                    }
                    return null;
                }

                public delegate void EventDelegate<TRequest>(
                    in World world,
                    ref Entity entity,
                    ref TRequest request
                );

                public void Register<TRequest>(
                    ushort reqId,
                    EventDelegate<TRequest> @delegate
                )
                {
                    _packetDict[reqId] = (world, entity, packet) =>
                    {
                        var request = packet.Decode<TRequest>();
                        @delegate.Invoke(world, ref entity, ref request);
                        return null;
                    };
                }

                public delegate TResponse EventRespDelegate<TRequest, out TResponse>(
                    in World world,
                    ref Entity entity,
                    ref TRequest request
                );

                public void Register<TRequest, TResponse>(
                    ushort reqId,
                    ushort respId,
                    EventRespDelegate<TRequest, TResponse> @delegate
                )
                {
                    _packetDict[reqId] = (world, entity, packet) =>
                    {
                        var request = packet.Decode<TRequest>();
                        var response = @delegate.Invoke(world, ref entity, ref request);
                        if (response != null)
                        {
                            return new(respId, response);
                        }
                        return null;
                    };
                }
            }
            """;
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource("NetEventBus.g.cs", SourceText.From(attributes, Encoding.UTF8)));
        }

        var methodDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (s, _) => s is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, _) => GetMethodSymbolIfAttributeof(ctx, AttributeFullName))
            .Where(m => m is not null);

        var compilationAndMethods = context.CompilationProvider
            .Combine(methodDeclarations!.WithComparer(Comparer.Instance).Collect());

        // 加上这里只是为了让 compilationAndMethods 能够执行
        context.RegisterSourceOutput(compilationAndMethods,
            static (spc, source) => Generate(source.Item1, source.Item2, spc));
    }

    private static MethodDeclarationSyntax? GetMethodSymbolIfAttributeof(GeneratorSyntaxContext context, string name)
    {
        // we know the node is a EnumDeclarationSyntax thanks to IsSyntaxTargetForGeneration
        var methodDeclarationSyntax = (MethodDeclarationSyntax)context.Node;

        // loop through all the attributes on the method
        foreach (var attributeListSyntax in methodDeclarationSyntax.AttributeLists)
        {
            foreach (var attributeSyntax in attributeListSyntax.Attributes)
            {
                if (ModelExtensions.GetSymbolInfo(context.SemanticModel, attributeSyntax).Symbol is not IMethodSymbol
                    attributeSymbol) continue;

                var attributeContainingTypeSymbol = attributeSymbol.ContainingType;
                var fullName = attributeContainingTypeSymbol.ToDisplayString();

                // Is the attribute the [EnumExtensions] attribute?
                if (fullName != name) continue;
                return methodDeclarationSyntax;
            }
        }

        // we didn't find the attribute we were looking for
        return null;
    }

    private static void Generate(Compilation compilation, ImmutableArray<MethodDeclarationSyntax> methods,
        SourceProductionContext context)
    {
        if (methods.IsDefaultOrEmpty)
        {
            return;
        }

        var dict = new Dictionary<ITypeSymbol, List<IMethodSymbol>>();
        foreach (var methodSyntax in methods)
        {
            var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
            var methodSymbol = ModelExtensions.GetDeclaredSymbol(semanticModel, methodSyntax) as IMethodSymbol;
            var receiverType = methodSymbol.ReceiverType;
            if (!dict.TryGetValue(receiverType, out var list))
            {
                list = new();
                dict[receiverType] = list;
            }
            list.Add(methodSymbol);
        }

        var typeBuilder = new StringBuilder();
        foreach (var pair in dict)
        {
            var sb = new StringBuilder();
            foreach (var methodSymbol in pair.Value)
            {
                // var receiverType = methodSymbol.ReceiverType;
                var metadataName = methodSymbol.MetadataName;
                var returnType = methodSymbol.ReturnType;
                var parameters = methodSymbol.Parameters;

                var attributeData = methodSymbol.GetAttributes()
                    .First(data => data.AttributeClass.ToString() == AttributeFullName);

                var constructorArguments = attributeData.ConstructorArguments
                    .Select(constant => (ushort)constant.Value)
                    .ToArray();
                var reqId = constructorArguments[0];
                var respId = constructorArguments[1];

                var requestType = parameters[2].OriginalDefinition
                    .ToDisplayString()
                    .Split(' ')[1];
                if (respId > 0)
                {
                    sb.Append($$"""
                    eventBus.Register<{{requestType}}, {{returnType}}>({{reqId}}, {{respId}}, obj.{{metadataName}});
                    """);
                }
                else
                {
                    sb.Append($$"""
                    eventBus.Register<{{requestType}}>({{reqId}}, obj.{{metadataName}});
                    """);
                }
            }
            typeBuilder.Append($$"""
            public static void Register(this NetEventBus eventBus, {{pair.Key}} obj)
            {
                {{sb}}
            }
            """);

            var fileBuilder = new StringBuilder($$"""
            namespace {{Namespace}};
            public static class NetEventBusExtensions
            {
                {{typeBuilder}}
            }
            """);

            context.AddSource($"NetEventBusExtensions.g.cs", CSharpSyntaxTree
                .ParseText(fileBuilder.ToString())
                .GetRoot()
                .NormalizeWhitespace()
                .ToFullString()
            );
        }
    }

    class Comparer : IEqualityComparer<MethodDeclarationSyntax>
    {
        public static readonly Comparer Instance = new Comparer();

        public bool Equals(MethodDeclarationSyntax? x, MethodDeclarationSyntax? y)
        {
            return x?.Equals(y) ?? false;
        }

        public int GetHashCode(MethodDeclarationSyntax obj)
        {
            return obj.GetHashCode();
        }
    }
}