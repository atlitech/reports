using System.Net;
using Atli.Reports.Blazor.Tailwind.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Atli.Reports.Blazor.Tailwind.Analysis;

/// <summary>Conservatively extracts component-owned literals and statically bound rendering edges.</summary>
public static class ComponentExtractor
{
  private const string ComponentInterface = "Microsoft.AspNetCore.Components.IComponent";
  private const string BuilderType = "Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder";
  private const string DynamicComponentType = "Microsoft.AspNetCore.Components.DynamicComponent";

  public static ComponentManifest Extract(CSharpCompilation compilation, DiscoveryRequest request)
  {
    var componentInterface =
      compilation.GetTypeByMetadataName(ComponentInterface)
      ?? throw new InvalidDataException(
        "The compilation does not reference Blazor's IComponent contract."
      );
    var identity = compilation.Assembly.Identity;
    var manifest = new ComponentManifest
    {
      Artifact = new ManifestArtifact
      {
        AssemblyName = identity.Name,
        AssemblyVersion = identity.Version.ToString(),
        PublicKeyToken = Convert.ToHexString(identity.PublicKeyToken.AsSpan()).ToLowerInvariant(),
        TargetFramework = request.TargetFramework,
        RuntimeIdentifier = request.RuntimeIdentifier,
        ImplementationSha256 = ManifestIO.HashFile(request.AssemblyPath),
      },
    };
    foreach (
      var type in AllTypes(compilation.Assembly.GlobalNamespace)
        .Where(type =>
          type.AllInterfaces.Contains(componentInterface, SymbolEqualityComparer.Default)
        )
        .OrderBy(MetadataName, StringComparer.Ordinal)
    )
    {
      var entry = new ComponentEntry { TypeName = MetadataName(type) };
      var walker = new ComponentWalker(compilation, type, entry, request.ProjectDirectory);
      foreach (var syntax in type.DeclaringSyntaxReferences)
        walker.VisitOwned(syntax.GetSyntax());
      if (
        type.BaseType is { } baseType
        && baseType.AllInterfaces.Contains(componentInterface, SymbolEqualityComparer.Default)
        && baseType.ContainingAssembly.Name != "Microsoft.AspNetCore.Components"
      )
        walker.AddDependency(baseType);
      walker.Complete();
      manifest.Components.Add(entry);
    }
    return manifest;
  }

  private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol container)
  {
    foreach (var member in container.GetMembers())
    {
      if (member is INamedTypeSymbol type)
      {
        yield return type;
        foreach (var nested in AllTypes(type))
          yield return nested;
      }
      else if (member is INamespaceSymbol space)
        foreach (var nested in AllTypes(space))
          yield return nested;
    }
  }

  public static string MetadataName(INamedTypeSymbol type)
  {
    var names = new Stack<string>();
    for (var current = type; current is not null; current = current.ContainingType)
      names.Push(current.MetadataName);
    var prefix = type.ContainingNamespace.IsGlobalNamespace
      ? ""
      : type.ContainingNamespace.ToDisplayString() + ".";
    return prefix + string.Join("+", names);
  }

  private sealed class ComponentWalker(
    CSharpCompilation compilation,
    INamedTypeSymbol owner,
    ComponentEntry entry,
    string projectDirectory
  )
  {
    private readonly HashSet<string> fragments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComponentReference> dependencies = new(
      StringComparer.Ordinal
    );
    private readonly HashSet<ISymbol> visitedHelpers = new(SymbolEqualityComparer.Default);
    private readonly HashSet<string> diagnosticKeys = new(StringComparer.Ordinal);
    private readonly HashSet<ISymbol> visitedValues = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> visitedAttributes = new(SymbolEqualityComparer.Default);

    public void VisitOwned(
      SyntaxNode root,
      Dictionary<ITypeParameterSymbol, ITypeSymbol>? substitutions = null
    )
    {
      var model = compilation.GetSemanticModel(root.SyntaxTree);
      var componentScopes = new Stack<(SyntaxNode Node, bool Dynamic, bool Resolved)>();
      // Own nested expressions and lambdas, but not unrelated nested component declarations.
      foreach (
        var node in root.DescendantNodesAndSelf(descendIntoChildren: node =>
          node == root || node is not TypeDeclarationSyntax
        )
      )
      {
        if (
          node is not InvocationExpressionSyntax invocation
          || model.GetOperation(invocation) is not IInvocationOperation operation
        )
          continue;
        var method = operation.TargetMethod;
        if (method.ContainingType.ToDisplayString() == BuilderType)
        {
          if (
            method.Name == "AddMarkupContent"
            && operation.Arguments.Length == 2
            && operation.Arguments[1].Value.ConstantValue
              is { HasValue: true, Value: string markup }
          )
          {
            foreach (var attribute in HtmlClassAttributes(markup))
              fragments.Add(WebUtility.HtmlDecode(attribute));
          }
          else if (method.Name == "AddMultipleAttributes" && operation.Arguments.Length == 2)
            CollectAttributeValues(operation.Arguments[1].Value);
          else if (
            operation.Arguments.Length >= 3
            && (
              method.Name == "AddComponentParameter"
              || method.Name == "AddAttribute"
                && operation.Arguments[1].Value.ConstantValue
                  is { HasValue: true, Value: string attributeName }
                && attributeName.Equals("class", StringComparison.OrdinalIgnoreCase)
            )
          )
            CollectValue(operation.Arguments[2].Value);
        }
        if (
          method.ContainingType.ToDisplayString() == BuilderType
          && method.Name == "OpenComponent"
        )
        {
          ITypeSymbol? child = method.IsGenericMethod
            ? method.TypeArguments[0]
            : GetStaticType(operation.Arguments.Last().Value);
          if (
            child is ITypeParameterSymbol parameter
            && substitutions is not null
            && substitutions.TryGetValue(parameter, out var concrete)
          )
            child = concrete;
          if (child is INamedTypeSymbol named)
          {
            var dynamic = named.ToDisplayString() == DynamicComponentType;
            componentScopes.Push((invocation, dynamic, false));
            if (!dynamic)
              AddDependency(named);
          }
          else
          {
            componentScopes.Push((invocation, false, false));
            AddDiagnostic(
              "generic-component",
              "The rendered component type cannot be resolved statically. Declare its possible component types.",
              invocation
            );
          }
        }
        else if (
          method.ContainingType.ToDisplayString() == BuilderType
          && method.Name == "CloseComponent"
          && componentScopes.Count > 0
        )
        {
          var scope = componentScopes.Pop();
          if (scope.Dynamic && !scope.Resolved)
            AddDiagnostic(
              "dynamic-component",
              "DynamicComponent selects its type at runtime. Declare all possible components for this owner or report.",
              scope.Node
            );
        }
        else if (
          method.ContainingType.ToDisplayString() == BuilderType
          && method.Name is "AddComponentParameter" or "AddAttribute"
          && componentScopes.TryPeek(out var currentScope)
          && currentScope.Dynamic
          && operation.Arguments.Length >= 3
          && operation.Arguments[1].Value.ConstantValue is { HasValue: true, Value: "Type" }
        )
        {
          if (TryStaticTypes(operation.Arguments[2].Value, out var dynamicTypes))
          {
            foreach (var dynamicType in dynamicTypes)
              AddDependency(dynamicType);
            var scope = componentScopes.Pop();
            componentScopes.Push((scope.Node, scope.Dynamic, true));
          }
        }
        else if (
          method.ContainingType.Name == "TypeInference"
          && method.DeclaringSyntaxReferences.Length > 0
        )
        {
          foreach (var argument in operation.Arguments)
            if (argument.Value.Type?.SpecialType == SpecialType.System_String)
              CollectValue(argument.Value);
          var definition = method.OriginalDefinition;
          if (visitedHelpers.Add(method))
          {
            var inferred = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(
              SymbolEqualityComparer.Default
            );
            foreach (var pair in definition.TypeParameters.Zip(method.TypeArguments))
              inferred.Add(pair.First, pair.Second);
            foreach (var syntax in definition.DeclaringSyntaxReferences)
              VisitOwned(syntax.GetSyntax(), inferred);
          }
        }
      }
    }

    private void CollectValue(IOperation operation)
    {
      if (operation.ConstantValue is { HasValue: true, Value: string text })
      {
        if (text.Length > 0)
          fragments.Add(text);
        return;
      }
      foreach (var value in OwnedValues(operation, visitedValues))
        CollectValue(value);
      // Follow conditional alternatives, local initializers, concatenations, interpolations and
      // literal collection entries. This never executes a helper or walks unrelated members.
      foreach (var child in operation.ChildOperations)
        CollectValue(child);
    }

    private void CollectAttributeValues(IOperation operation)
    {
      // Only class values from bounded dictionary/KeyValuePair initializers are candidates;
      // unrelated attribute values can contain private application payloads.
      if (
        operation
          is ISimpleAssignmentOperation
          {
            Target: IPropertyReferenceOperation { Arguments.Length: 1 } property
          } assignment
        && IsClassKey(property.Arguments[0].Value)
      )
        CollectValue(assignment.Value);
      else if (
        operation is IInvocationOperation { TargetMethod.Name: "Add", Arguments.Length: 2 } addition
        && IsClassKey(addition.Arguments[0].Value)
      )
        CollectValue(addition.Arguments[1].Value);
      else if (
        operation is IObjectCreationOperation { Arguments.Length: 2 } creation
        && creation.Constructor?.ContainingType.OriginalDefinition.ToDisplayString()
          == "System.Collections.Generic.KeyValuePair<TKey, TValue>"
        && IsClassKey(creation.Arguments[0].Value)
      )
        CollectValue(creation.Arguments[1].Value);
      foreach (var value in OwnedValues(operation, visitedAttributes))
        CollectAttributeValues(value);
      foreach (var child in operation.ChildOperations)
        CollectAttributeValues(child);
    }

    private static bool IsClassKey(IOperation operation) =>
      operation.ConstantValue is { HasValue: true, Value: string key }
      && key.Equals("class", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<IOperation> OwnedValues(IOperation operation, HashSet<ISymbol> visited)
    {
      ISymbol? symbol = operation switch
      {
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        ILocalReferenceOperation local => local.Local,
        IInvocationOperation invocation => invocation.TargetMethod,
        _ => null,
      };
      if (symbol is null || !IsOwnedType(symbol.ContainingType) || !visited.Add(symbol))
        yield break;
      foreach (var declaration in symbol.DeclaringSyntaxReferences)
      {
        var syntax = declaration.GetSyntax();
        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        IEnumerable<ExpressionSyntax> values = syntax switch
        {
          VariableDeclaratorSyntax variable when variable.Initializer is not null =>
          [
            variable.Initializer.Value,
          ],
          PropertyDeclarationSyntax property => property.ExpressionBody is { } body
            ? [body.Expression]
          : property.Initializer is { } initializer ? [initializer.Value]
          : property
            .DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Select(statement => statement.Expression)
            .OfType<ExpressionSyntax>(),
          MethodDeclarationSyntax method => method.ExpressionBody is { } body
            ? [body.Expression]
            : method
              .DescendantNodes(descendIntoChildren: child =>
                child is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax
              )
              .OfType<ReturnStatementSyntax>()
              .Select(statement => statement.Expression)
              .OfType<ExpressionSyntax>(),
          _ => [],
        };
        foreach (var expression in values)
          if (model.GetOperation(expression) is { } value)
            yield return value;
      }
    }

    private bool IsOwnedType(INamedTypeSymbol? type)
    {
      for (var current = owner; current is not null; current = current.BaseType)
        if (
          SymbolEqualityComparer.Default.Equals(
            current.OriginalDefinition,
            type?.OriginalDefinition
          )
        )
          return true;
      return false;
    }

    public void AddDependency(INamedTypeSymbol type)
    {
      var reference = new ComponentReference
      {
        AssemblyName = type.ContainingAssembly.Name,
        TypeName = MetadataName(type.OriginalDefinition),
      };
      dependencies[reference.ToString()] = reference;
    }

    private static ITypeSymbol? GetStaticType(IOperation value)
    {
      while (value is IConversionOperation conversion)
        value = conversion.Operand;
      if (
        value is IInvocationOperation invocation
        && invocation.TargetMethod.ContainingType.ToDisplayString()
          == "Microsoft.AspNetCore.Components.CompilerServices.RuntimeHelpers"
        && invocation.TargetMethod.Name == "TypeCheck"
        && invocation.Arguments.Length == 1
      )
        return GetStaticType(invocation.Arguments[0].Value);
      return value is ITypeOfOperation typeOf ? typeOf.TypeOperand : null;
    }

    private static bool TryStaticTypes(IOperation value, out List<INamedTypeSymbol> types)
    {
      types = [];
      while (value is IConversionOperation conversion)
        value = conversion.Operand;
      if (
        value is IInvocationOperation invocation
        && invocation.TargetMethod.ContainingType.ToDisplayString()
          == "Microsoft.AspNetCore.Components.CompilerServices.RuntimeHelpers"
        && invocation.TargetMethod.Name == "TypeCheck"
        && invocation.Arguments.Length == 1
      )
        return TryStaticTypes(invocation.Arguments[0].Value, out types);
      if (value is ITypeOfOperation { TypeOperand: INamedTypeSymbol named })
      {
        types.Add(named);
        return true;
      }
      IEnumerable<IOperation> alternatives = value switch
      {
        IConditionalOperation { WhenFalse: { } whenFalse } conditional =>
        [
          conditional.WhenTrue,
          whenFalse,
        ],
        ISwitchExpressionOperation expression => expression.Arms.Select(arm => arm.Value),
        _ => [],
      };
      foreach (var alternative in alternatives)
      {
        if (!TryStaticTypes(alternative, out var branchTypes))
          return false;
        types.AddRange(branchTypes);
      }
      return types.Count > 0;
    }

    private void AddDiagnostic(string kind, string message, SyntaxNode syntax)
    {
      var location = syntax.GetLocation().GetMappedLineSpan();
      var path = location.Path;
      if (Path.IsPathFullyQualified(path))
      {
        var relative = Path.GetRelativePath(projectDirectory, path);
        path = relative.StartsWith("..", StringComparison.Ordinal)
          ? Path.GetFileName(path)
          : relative;
      }
      var formatted = path.Replace('\\', '/') + ":" + (location.StartLinePosition.Line + 1);
      if (diagnosticKeys.Add(kind + ":" + formatted))
        entry.Unresolved.Add(
          new DiscoveryDiagnostic
          {
            Code = "ATLI1001",
            Kind = kind,
            Message = message,
            Location = formatted,
          }
        );
    }

    public void Complete()
    {
      entry.CandidateFragments = fragments.Order(StringComparer.Ordinal).ToList();
      entry.Dependencies = dependencies
        .Values.OrderBy(reference => reference.ToString(), StringComparer.Ordinal)
        .ToList();
    }
  }

  // Read actual attributes, respecting quoted values. A substring such as data-note="class=..."
  // or visible text is not an attribute. Tailwind alone interprets the resulting class values.
  private static IEnumerable<string> HtmlClassAttributes(string markup)
  {
    for (var index = 0; index < markup.Length; index++)
    {
      if (markup[index] != '<' || index + 1 >= markup.Length || !char.IsLetter(markup[index + 1]))
        continue;
      index++;
      while (
        index < markup.Length
        && !char.IsWhiteSpace(markup[index])
        && markup[index] is not '>' and not '/'
      )
        index++;
      while (index < markup.Length && markup[index] != '>')
      {
        while (index < markup.Length && (char.IsWhiteSpace(markup[index]) || markup[index] == '/'))
          index++;
        var start = index;
        while (
          index < markup.Length
          && !char.IsWhiteSpace(markup[index])
          && markup[index] is not '=' and not '>' and not '/'
        )
          index++;
        if (index == start)
          break;
        var name = markup[start..index];
        while (index < markup.Length && char.IsWhiteSpace(markup[index]))
          index++;
        if (index >= markup.Length || markup[index] != '=')
          continue;
        index++;
        while (index < markup.Length && char.IsWhiteSpace(markup[index]))
          index++;
        if (index >= markup.Length)
          break;
        var quote = markup[index] is '\'' or '"' ? markup[index++] : '\0';
        start = index;
        while (
          index < markup.Length
          && (
            quote == '\0'
              ? !char.IsWhiteSpace(markup[index]) && markup[index] != '>'
              : markup[index] != quote
          )
        )
          index++;
        var value = markup[start..index];
        if (quote != '\0' && index < markup.Length)
          index++;
        if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
          yield return value;
      }
    }
  }
}
