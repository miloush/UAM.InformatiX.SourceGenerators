//
// © 2022-2023 miloush.net. All rights reserved.
//

#define GENERATE_FULL_TYPES

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UAM.InformatiX.SourceGenerators.ComInheritanceGenerator
{
    [Generator]
    public class ComInheritanceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<InterfaceDeclarationSyntax> interfaceDeclarations = context.SyntaxProvider.CreateSyntaxProvider(IsInterfaceWithAttributesAndBase, IsComImportIUnknownAttribute).Where(syntax => syntax is not null)!;

            var compilationAndDeclarations = context.CompilationProvider.Combine(interfaceDeclarations.Collect());

            context.RegisterSourceOutput(compilationAndDeclarations, (spc, source) => Execute(spc, source.Left, source.Right));
        }

        private void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<InterfaceDeclarationSyntax> interfaces)
        {
            if (interfaces.IsDefaultOrEmpty)
                return;

            foreach (InterfaceDeclarationSyntax interfaceDeclaration in interfaces.Distinct())
            {
#if GENERATE_FULL_TYPES
                // create public interface declaration
                TypeDeclarationSyntax generatedDeclaration = interfaceDeclaration
                    .WithIdentifier(SyntaxFactory.Identifier(interfaceDeclaration.Identifier.Text.TrimStart('_')))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));

                // convert base list from _I to I
                if (generatedDeclaration.BaseList?.Types.Count > 0)
                    generatedDeclaration = generatedDeclaration.WithBaseList(
                        generatedDeclaration.BaseList.WithTypes(
                            SyntaxFactory.SeparatedList(
                                generatedDeclaration.BaseList.Types.Select(t => t.WithType(ToFullTypeName(t.Type)))
                            )));
#else
                // create partial interface declaration
                TypeDeclarationSyntax generatedDeclaration = SyntaxFactory.InterfaceDeclaration(interfaceDeclaration.Identifier.Text).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PartialKeyword)));
#endif
                // get model to resolve inherited interface types
                SemanticModel model = compilation.GetSemanticModel(interfaceDeclaration.SyntaxTree);
                if (model.GetDeclaredSymbol(interfaceDeclaration) is not INamedTypeSymbol interfaceSymbol)
                    continue;

                // traverse inherited interfaces and collect new members
                List<UsingDirectiveSyntax> usings = new List<UsingDirectiveSyntax>();
                List<SyntaxNode> content = new List<SyntaxNode>();
                CollectUsings(interfaceDeclaration, usings);
                CollectNewMembers(interfaceSymbol, usings, content);

                // convert region nodes into trivia of member nodes
                RegionsToTrivia(content);

#if GENERATE_FULL_TYPES
                // add members to the interface declaration
                generatedDeclaration = generatedDeclaration.WithMembers(SyntaxFactory.List(content.OfType<MemberDeclarationSyntax>().Concat(generatedDeclaration.Members)));
#else
                // add members to the interface declaration
                generatedDeclaration = generatedDeclaration.AddMembers(content.OfType<MemberDeclarationSyntax>().ToArray());
#endif
                // ensure any nested type hierarychy is preserved
                var rootDeclaration = WithAncestorDeclarations(interfaceDeclaration, generatedDeclaration);

                // add namespace and usings
                var namespaceDeclaration = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(interfaceSymbol.ContainingNamespace.ToString()), default, default, new SyntaxList<MemberDeclarationSyntax>(rootDeclaration));
                var compulationUnit = SyntaxFactory.CompilationUnit(default, new SyntaxList<UsingDirectiveSyntax>(usings.Distinct(new FullStringComparer<UsingDirectiveSyntax>())), default, new SyntaxList<MemberDeclarationSyntax>(namespaceDeclaration));

                // generate code
                var code = compulationUnit.NormalizeWhitespace().ToFullString();
                context.AddSource(generatedDeclaration.Identifier.Text + ".g", code);
            }
        }

        private static void CollectUsings(InterfaceDeclarationSyntax declarationSyntax, List<UsingDirectiveSyntax> usings)
        {
            if (declarationSyntax.SyntaxTree.GetCompilationUnitRoot() is CompilationUnitSyntax unit)
                usings.AddRange(unit.Usings);
        }

        private void CollectNewMembers(INamedTypeSymbol interfaceSymbol, List<UsingDirectiveSyntax> usings, List<SyntaxNode> content)
        {
            foreach (INamedTypeSymbol baseType in interfaceSymbol.Interfaces)
            {
                // #region baseType

                content.Add(SyntaxFactory.RegionDirectiveTrivia(
                                        SyntaxFactory.Token(SyntaxKind.HashToken),
                                        SyntaxFactory.Token(SyntaxKind.RegionKeyword),
                                        SyntaxFactory.Token(SyntaxTriviaList.Create(SyntaxFactory.PreprocessingMessage(baseType.Name)), SyntaxKind.EndOfDirectiveToken, default), isActive: true));

                CollectNewMembers(baseType, usings, content);

                // new methods

                foreach (SyntaxReference baseReference in baseType.DeclaringSyntaxReferences)
                {
                    if (baseReference.GetSyntax() is not InterfaceDeclarationSyntax baseDeclaration)
                        continue;

                    CollectUsings(baseDeclaration, usings);

                    IEnumerable<MemberDeclarationSyntax> newMembers = baseDeclaration.Members.Select(m => m.AddModifiers(SyntaxFactory.Token(SyntaxKind.NewKeyword)));
                    content.AddRange(newMembers);
                }

                // #endregion baseType

                content.Add(SyntaxFactory.EndRegionDirectiveTrivia(isActive: true));
            }
        }

        private void RegionsToTrivia(List<SyntaxNode> nodes)
        {
            // the list contains mix of methods, #region and #endregion nodes
            // this method adds the #region and #endregion as trivia to the following or preceding non-trivia node, respectively
            // no need to remove trivia nodes from list, it can be filtered

            SyntaxTriviaList leadingTrivia = new SyntaxTriviaList();
            int lastNonTriviaIndex = -1;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is RegionDirectiveTriviaSyntax regionNode)
                {
                    leadingTrivia = leadingTrivia.Add(SyntaxFactory.Trivia(regionNode));
                }
                else if (nodes[i] is EndRegionDirectiveTriviaSyntax regionEndNode)
                {
                    if (lastNonTriviaIndex >= 0)
                    {
                        SyntaxNode lastNode = nodes[lastNonTriviaIndex];
                        nodes[lastNonTriviaIndex] = lastNode.WithTrailingTrivia(lastNode.GetTrailingTrivia().Concat(new[] { SyntaxFactory.Trivia(regionEndNode) }));
                    }
                }
                else
                {
                    lastNonTriviaIndex = i;

                    if (leadingTrivia.Count > 0)
                    {
                        nodes[i] = nodes[i].WithLeadingTrivia(leadingTrivia);
                        leadingTrivia = new SyntaxTriviaList();
                    }
                }
            }
        }

        private TypeDeclarationSyntax WithAncestorDeclarations(TypeDeclarationSyntax tree, TypeDeclarationSyntax declaration)
        {
            while (tree.Parent is TypeDeclarationSyntax parent)
            {
                // make a clean partial declaration
                TypeDeclarationSyntax generatedParent = SyntaxFactory.TypeDeclaration(parent.Kind(), parent.Identifier.Text).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PartialKeyword)));
                declaration = generatedParent.AddMembers(declaration);

                tree = parent;
            }

            return declaration;
        }

        private TypeSyntax ToFullTypeName(TypeSyntax syntax)
        {
            switch (syntax)
            {
                case SimpleNameSyntax simpleName:
                    if (simpleName.Identifier.Text.StartsWith("_"))
                        return simpleName.WithIdentifier(SyntaxFactory.Identifier(simpleName.Identifier.Text.TrimStart('_')));
                    break;
                
                case QualifiedNameSyntax qualifiedName:
                    if (qualifiedName.Right.Identifier.Text.StartsWith("_"))
                        return qualifiedName.WithRight((SimpleNameSyntax)ToFullTypeName(qualifiedName.Right));
                    break;
            }

            return syntax;
        }


        private static bool IsInterfaceWithAttributesAndBase(SyntaxNode node, CancellationToken cancellationToken)
        {
            return node is InterfaceDeclarationSyntax ids && ids.AttributeLists.Count > 0 &&
#if GENERATE_FULL_TYPES
                ids.Identifier.Text.StartsWith("_");
#else
                ids.BaseList?.Types.Count > 0;
#endif
        }

        private const string ComImportTypeName = "System.Runtime.InteropServices.ComImportAttribute";
        private const string InterfaceTypeTypeName = "System.Runtime.InteropServices.InterfaceTypeAttribute";
        private InterfaceDeclarationSyntax? IsComImportIUnknownAttribute(GeneratorSyntaxContext context, CancellationToken cancellationToken)
        {
            InterfaceDeclarationSyntax interfaceDeclaration = (InterfaceDeclarationSyntax)context.Node;

            bool isComImport = false;
            bool isIUnknown = false;

            foreach (AttributeListSyntax attributeList in interfaceDeclaration.AttributeLists)
                foreach (AttributeSyntax attribute in attributeList.Attributes)
                {
                    if (context.SemanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol is not IMethodSymbol attributeSymbol)
                        continue;

                    INamedTypeSymbol namedTypeSymbol = attributeSymbol.ContainingType;
                    string fullName = namedTypeSymbol.ToDisplayString();

                    if (fullName == ComImportTypeName)
                    {
                        isComImport = true;
                    }
                    else if (fullName == InterfaceTypeTypeName)
                    {
                        if (attribute.ArgumentList?.Arguments.Count is not > 0)
                            return null;

                        isIUnknown = attribute.ArgumentList.Arguments[0].ToString().Contains("InterfaceIsIUnknown");
                        if (!isIUnknown)
                            return null;
                    }

                    if (isComImport && isIUnknown)
                        return interfaceDeclaration;
                }

            return null;
        }
    }

    internal class FullStringComparer<TNode> : IEqualityComparer<TNode> where TNode: SyntaxNode
    {
        public bool Equals(TNode x, TNode y)
        {
            return string.Equals(x.ToFullString(), y.ToFullString(), StringComparison.Ordinal);
        }

        public int GetHashCode(TNode obj)
        {
            return obj.ToFullString().GetHashCode();
        }
    }
}