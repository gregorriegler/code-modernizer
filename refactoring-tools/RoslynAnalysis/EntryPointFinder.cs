using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynAnalysis;

public class EntryPointFinder
{
    private readonly IWorkspaceLoader _workspaceLoader;
    
    public EntryPointFinder(IWorkspaceLoader workspaceLoader)
    {
        _workspaceLoader = workspaceLoader;
    }
    
    public async Task<List<EntryPoint>> FindEntryPointsAsync(string projectPath)
    {
        var projects = await _workspaceLoader.LoadProjectsAsync(projectPath);
        
        if (!projects.Any())
            return new List<EntryPoint>();
            
        var documents = projects.SelectMany(p => p.Documents).ToList();
        
        var allPublicMethods = new List<(EntryPoint entryPoint, Document document, MethodDeclarationSyntax methodDeclaration, SemanticModel semanticModel)>();
        var allMethods = new List<(Document document, MethodDeclarationSyntax methodDeclaration, SemanticModel semanticModel, string fullyQualifiedName)>();
        
        foreach (var document in documents)
        {
            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (syntaxTree == null) continue;
                
            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null) continue;
            
            var root = await syntaxTree.GetRootAsync();
            var methodDeclarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            
            foreach (var methodDeclaration in methodDeclarations)
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration);
                if (methodSymbol != null)
                {
                    var fullyQualifiedName = $"{methodSymbol.ContainingType.ContainingNamespace.ToDisplayString()}.{methodSymbol.ContainingType.Name}.{methodSymbol.Name}";
                    allMethods.Add((document, methodDeclaration, semanticModel, fullyQualifiedName));
                    
                    var entryPoint = TryCreateEntryPoint(document, methodDeclaration, semanticModel);
                    if (entryPoint != null)
                    {
                        allPublicMethods.Add((entryPoint, document, methodDeclaration, semanticModel));
                    }
                }
            }
        }
        
        var calledMethods = new HashSet<string>();
        
        foreach (var (_, document, methodDeclaration, semanticModel) in allPublicMethods)
        {
            if (methodDeclaration.Body != null)
            {
                var invocationExpressions = methodDeclaration.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var invocation in invocationExpressions)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is IMethodSymbol calledMethodSymbol)
                    {
                        var calledMethodFullName = $"{calledMethodSymbol.ContainingType.ContainingNamespace.ToDisplayString()}.{calledMethodSymbol.ContainingType.Name}.{calledMethodSymbol.Name}";
                        calledMethods.Add(calledMethodFullName);
                    }
                }
            }
        }
        
        var entryPoints = new List<EntryPoint>();
        
        foreach (var (entryPoint, document, methodDeclaration, semanticModel) in allPublicMethods)
        {
            if (calledMethods.Contains(entryPoint.FullyQualifiedName))
                continue;
                
            var reachableCount = CalculateReachableMethodsCount(methodDeclaration, semanticModel, allMethods);
            
            var updatedEntryPoint = new EntryPoint(
                entryPoint.FullyQualifiedName,
                entryPoint.FilePath,
                entryPoint.LineNumber,
                entryPoint.MethodSignature,
                reachableCount
            );
            
            entryPoints.Add(updatedEntryPoint);
        }
        
        return entryPoints.OrderByDescending(ep => ep.ReachableMethodsCount).ToList();
    }
    
    
    private EntryPoint? TryCreateEntryPoint(
        Document document, 
        MethodDeclarationSyntax methodDeclaration, 
        SemanticModel semanticModel
    )
    {
        if (!methodDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            return null;
        
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration);
        if (methodSymbol == null)
            return null;
            
        var containingType = methodSymbol.ContainingType;
        if (containingType == null)
            return null;
        
        if (IsTestMethod(methodSymbol))
            return null;
        
        var fullyQualifiedName = $"{containingType.ContainingNamespace.ToDisplayString()}.{containingType.Name}.{methodSymbol.Name}";
        
        var filePath = document.FilePath ?? string.Empty;
        
        var lineSpan = methodDeclaration.GetLocation().GetLineSpan();
        var lineNumber = lineSpan.StartLinePosition.Line + 1;
        
        var methodSignature = GetMethodSignature(methodSymbol);
        
        return new EntryPoint(
            fullyQualifiedName,
            filePath,
            lineNumber,
            methodSignature,
            1
        );
    }
    
    private int CalculateReachableMethodsCount(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        List<(Document document, MethodDeclarationSyntax methodDeclaration, SemanticModel semanticModel, string fullyQualifiedName)> allMethods)
    {
        var reachableMethods = new HashSet<string>();
        var methodsToProcess = new Queue<(MethodDeclarationSyntax method, SemanticModel model)>();
        var processedMethods = new HashSet<string>();
        
        methodsToProcess.Enqueue((methodDeclaration, semanticModel));
        
        while (methodsToProcess.Count > 0)
        {
            var (currentMethod, currentSemanticModel) = methodsToProcess.Dequeue();
            var currentMethodSymbol = currentSemanticModel.GetDeclaredSymbol(currentMethod);
            if (currentMethodSymbol == null) continue;
            
            var currentMethodFullName = $"{currentMethodSymbol.ContainingType.ContainingNamespace.ToDisplayString()}.{currentMethodSymbol.ContainingType.Name}.{currentMethodSymbol.Name}";
            
            if (processedMethods.Contains(currentMethodFullName))
                continue;
                
            processedMethods.Add(currentMethodFullName);
            reachableMethods.Add(currentMethodFullName);
            
            if (currentMethod.Body != null)
            {
                var invocationExpressions = currentMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>();
                
                foreach (var invocation in invocationExpressions)
                {
                    var symbolInfo = currentSemanticModel.GetSymbolInfo(invocation);
                    string? calledMethodFullName = null;
                    
                    if (symbolInfo.Symbol is IMethodSymbol calledMethodSymbol)
                    {
                        calledMethodFullName = $"{calledMethodSymbol.ContainingType.ContainingNamespace.ToDisplayString()}.{calledMethodSymbol.ContainingType.Name}.{calledMethodSymbol.Name}";
                    }
                    else
                    {
                        if (invocation.Expression is IdentifierNameSyntax identifierName)
                        {
                            var methodName = identifierName.Identifier.ValueText;
                            var fallbackMethodSymbol = currentSemanticModel.GetDeclaredSymbol(currentMethod);
                            if (fallbackMethodSymbol != null)
                            {
                                var currentNamespace = fallbackMethodSymbol.ContainingType.ContainingNamespace.ToDisplayString();
                                var currentTypeName = fallbackMethodSymbol.ContainingType.Name;
                                calledMethodFullName = $"{currentNamespace}.{currentTypeName}.{methodName}";
                            }
                        }
                    }
                    
                    if (calledMethodFullName != null)
                    {
                        var calledMethodInfo = allMethods
                            .FirstOrDefault(m => m.fullyQualifiedName == calledMethodFullName);
                            
                        if (calledMethodInfo.methodDeclaration != null)
                        {
                            if (!processedMethods.Contains(calledMethodFullName))
                            {
                                methodsToProcess.Enqueue((calledMethodInfo.methodDeclaration, calledMethodInfo.semanticModel));
                            }
                        }
                    }
                }
            }
        }
        
        return reachableMethods.Count;
    }
    
    private string GetMethodSignature(IMethodSymbol methodSymbol)
    {
        var returnType = GetSimplifiedTypeName(methodSymbol.ReturnType);
        var parameters = string.Join(", ", methodSymbol.Parameters.Select(p => $"{GetSimplifiedTypeName(p.Type)} {p.Name}"));
        return $"{returnType} {methodSymbol.Name}({parameters})";
    }
    
    private string GetSimplifiedTypeName(ITypeSymbol typeSymbol)
    {
        return typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }
    
    private bool IsTestMethod(IMethodSymbol methodSymbol)
    {
        var testAttributes = new[]
        {
            "Test",
            "TestMethod", 
            "Fact",
            "Theory",
            "TestCase"
        };
        
        return methodSymbol.GetAttributes().Any(attr => 
            testAttributes.Any(testAttr => 
                attr.AttributeClass?.Name.Contains(testAttr) == true));
    }
}
