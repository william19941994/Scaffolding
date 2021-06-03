using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.DotNet.MSIdentity.AuthenticationParameters;
using Microsoft.DotNet.MSIdentity.Properties;
using Microsoft.DotNet.MSIdentity.Tool;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.DotNet.MSIdentity.CodeReaderWriter
{
    internal class ProjectModifier
    {
        public List<CodeModifierConfig> CodeModifierConfigs { get; private set; } = new List<CodeModifierConfig>();
        private readonly ProvisioningToolOptions _toolOptions;
        private readonly ApplicationParameters _appParameters;

        public SyntaxTrivia SemiColonTrivia
        {
            get
            {
                return SyntaxFactory.Trivia(SyntaxFactory.SkippedTokensTrivia()
                    .WithTokens(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.SemicolonToken))));
            }
        }
        public ProjectModifier(ApplicationParameters applicationParameters, ProvisioningToolOptions toolOptions)
        {
            _toolOptions = toolOptions ?? throw new ArgumentNullException(nameof(toolOptions));
            _appParameters = applicationParameters ?? throw new ArgumentNullException(nameof(applicationParameters));
        }

        internal IDictionary<string, string>? VerfiyParameters(string[]? parametersToCheck, List<ParameterSyntax> foundParameters)
        {
            IDictionary<string, string> parametersWithNames = new Dictionary<string, string>();
            if (foundParameters.Any())
            {
                var pars = foundParameters.ToList();
                if (parametersToCheck != null)
                {
                    foreach (var parameter in parametersToCheck)
                    {
                        var verifiedParams = pars.Where(p => p.Type != null && p.Type.ToFullString().Trim().Equals(parameter.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (!verifiedParams.Any())
                        {
                            return null;
                        }
                        else
                        {
                            parametersWithNames.Add(parameter, verifiedParams.First().Identifier.ValueText);
                        }
                    }
                }
            }
            
            return parametersWithNames;
        }

        /// <summary>
        /// Added "Microsoft identity platform" auth to base or empty C# .NET Core 3.1, .NET 5 and above projects.
        /// Includes adding PackageReferences, modifying Startup.cs, and Layout.cshtml changes.
        /// </summary>
        /// <param name="projectType"></param>
        /// <returns></returns>
        public async Task AddAuth()
        {
            Debugger.Launch();
            string projectType = _toolOptions.ProjectTypeIdentifier;
            //Read the jsons in CodeModifierConfigs for CodeChanges
            ReadCodeModifierConfigs();
            var codeModifierConfig = CodeModifierConfigs.Where(x => x.Identifier != null &&
                                                                    x.Identifier.Equals(projectType, StringComparison.OrdinalIgnoreCase))
                                                                    .FirstOrDefault();
            var programFilePath = Directory.EnumerateFiles(_toolOptions.ProjectPath, "Program.cs").FirstOrDefault();

            // CodeModifierConfig, .csproj path, Program.cs path cannot be null
            if (codeModifierConfig != null &&
                codeModifierConfig.Files != null &&
                codeModifierConfig.Files.Any() && 
                !string.IsNullOrEmpty(_toolOptions.ProjectFilePath))
            {
                //Initialize CodeAnalysis.Project wrapper
                CodeAnaylsisHelper proj = new CodeAnaylsisHelper(_toolOptions.ProjectFilePath);

                //Get Startup class name from CreateHostBuilder in Program.cs. If Program.cs is not being used, method
                //will bail out.
                foreach(var file in codeModifierConfig.Files)
                {
                    var fileName = file.FileName;
                    string className = GetClassName(fileName);

                    if (!string.IsNullOrEmpty(file.FileName) && file.FileName.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(programFilePath))
                        {
                            var programDoc = proj.CodeAnalysisProject.Documents.Where(d => d.Name.Equals(programFilePath)).FirstOrDefault();
                            var startupClassName = await GetStartupClass(programDoc);
                            className = startupClassName;
                            var startupFilePath = string.Empty;
                            if (!string.IsNullOrEmpty(startupClassName))
                            {
                                var startupClass = string.Concat(startupClassName, ".cs");
                                fileName = startupClass;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(fileName))
                    {
                        string? filePath = Directory.EnumerateFiles(_toolOptions.ProjectPath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                        var classDoc = proj.CodeAnalysisProject.Documents.Where(d => d.Name.Equals(filePath)).FirstOrDefault();
                        if (classDoc != null && !string.IsNullOrEmpty(filePath))
                        {
                            DocumentEditor documentEditor = await DocumentEditor.CreateAsync(classDoc);
                            var docRoot = documentEditor.OriginalRoot;
                            if (docRoot != null && docRoot is CompilationUnitSyntax root)
                            {
                                var namespaceNode = root.Members.OfType<NamespaceDeclarationSyntax>()?.FirstOrDefault();
                                //get classNode
                                var classNode = namespaceNode?.DescendantNodes().Where(node => node is ClassDeclarationSyntax cds
                                    && cds.Identifier.ValueText.Contains(className)).First();

                                if (classNode is ClassDeclarationSyntax classDeclarationSyntax &&
                                    classNode is ClassDeclarationSyntax modifiedClassDeclarationSyntax)
                                {
                                    //adding usings
                                    var usingNodes = CreateUsings(file.Usings);
                                    foreach (var usingNode in usingNodes)
                                    {
                                        //if usings exist (very likely), add it after the last one.
                                        if (root.Usings.Count > 0)
                                        {
                                            var usingName = usingNode.Name.ToString();
                                            if (!root.Usings.Any(node => node.Name.ToString().Equals(usingName)))
                                            {
                                                documentEditor.InsertAfter(root.Usings.Last(), usingNode);
                                            }
                                        }
                                        //else, create a using block
                                        else
                                        {
                                            var newRoot = root.AddUsings(usingNode);
                                            documentEditor.ReplaceNode(root, newRoot);
                                        }
                                    }

                                    //add class properties
                                    if (file.ClassProperties != null && file.ClassProperties.Any())
                                    {
                                        var sampleMember = modifiedClassDeclarationSyntax.Members.FirstOrDefault();
                                        var memberLeadingTrivia = sampleMember?.GetLeadingTrivia() ?? new SyntaxTriviaList(SyntaxFactory.Tab);
                                        var memberTrailingTrivia = new SyntaxTriviaList(SemiColonTrivia, SyntaxFactory.CarriageReturnLineFeed);

                                        var classProperties = CreateClassProperties(file.ClassProperties, modifiedClassDeclarationSyntax.Members, memberLeadingTrivia, memberTrailingTrivia);

                                        if (classProperties.Length > 0)
                                        {
                                            modifiedClassDeclarationSyntax = modifiedClassDeclarationSyntax.AddMembers(classProperties);
                                        }
                                    }

                                    //add class attributes
                                    if (file.ClassAttributes != null && file.ClassAttributes.Any())
                                    {
                                        var classAttributes = CreateAttributeList(file.ClassAttributes, modifiedClassDeclarationSyntax.AttributeLists);
                                        if (classAttributes != null)
                                        {
                                            var leadingTrivia = classDeclarationSyntax.GetLeadingTrivia();
                                            modifiedClassDeclarationSyntax = modifiedClassDeclarationSyntax.WithAttributeLists(
                                                modifiedClassDeclarationSyntax.AttributeLists
                                                    .Insert(0, classAttributes
                                                        .WithAdditionalAnnotations(Formatter.Annotation)
                                                        .WithLeadingTrivia(leadingTrivia)
                                                        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed)));
                                        }
                                    }

                                    //add code snippets/changes.
                                    if (file.Methods != null && file.Methods.Any())
                                    {
                                        //code changes are chunked together by methods. Easier for document node modifications.
                                        foreach (var method in file.Methods)
                                        {
                                            var methodName = method.Key;
                                            var methodChanges = method.Value;

                                            if (!string.IsNullOrEmpty(methodName) &&
                                                methodChanges != null &&
                                                methodChanges.CodeChanges != null)
                                            {
                                                //get method
                                                IDictionary<string, string>? parameterValues = null;
                                                var nodes = modifiedClassDeclarationSyntax?.DescendantNodes();
                                                var methodNode = modifiedClassDeclarationSyntax?.DescendantNodes().Where(
                                                        node => node is MethodDeclarationSyntax mds &&
                                                        mds.Identifier.ValueText.Contains(methodName) &&
                                                        (parameterValues = VerfiyParameters(methodChanges.Parameters, mds.ParameterList.Parameters.ToList())) != null).FirstOrDefault();

                                                if (methodNode != null)
                                                {
                                                    //get method block
                                                    var blockSyntaxNode = methodNode.DescendantNodes().OfType<BlockSyntax>().FirstOrDefault();
                                                    var modifiedBlockSyntaxNode = methodNode.DescendantNodes().OfType<BlockSyntax>().FirstOrDefault();

                                                    foreach (var change in methodChanges.CodeChanges)
                                                    {
                                                        if (!string.IsNullOrEmpty(change.Block))
                                                        {
                                                            //using defaults for leading and trailing trivia
                                                            var trailingTrivia = new SyntaxTriviaList(SyntaxFactory.CarriageReturnLineFeed);
                                                            var leadingTrivia = new SyntaxTriviaList(SyntaxFactory.Tab);
                                                            //set leading and trailing trivia(spacing
                                                            if (modifiedBlockSyntaxNode != null)
                                                            {
                                                                if (modifiedBlockSyntaxNode.Statements.Any())
                                                                {
                                                                    trailingTrivia = modifiedBlockSyntaxNode.Statements[0].GetTrailingTrivia();
                                                                    leadingTrivia = modifiedBlockSyntaxNode.Statements[0].GetLeadingTrivia();
                                                                }
                                                                if (!trailingTrivia.Contains(SemiColonTrivia))
                                                                {
                                                                    trailingTrivia = trailingTrivia.Insert(0, SemiColonTrivia);
                                                                }

                                                                //CodeChange.Parent and CodeChange.Type go together.
                                                                if (!string.IsNullOrEmpty(change.Parent) && !string.IsNullOrEmpty(change.Type))
                                                                {
                                                                    string parentBlock = FormatCodeBlock(change.Parent, parameterValues);
                                                                    if (!string.IsNullOrEmpty(parentBlock))
                                                                    {
                                                                        var statement = modifiedBlockSyntaxNode.Statements.Where(st => st.ToString().Contains(parentBlock));
                                                                        if (statement.Any())
                                                                        {
                                                                            var statementToModify = statement.First();
                                                                            var node = modifiedBlockSyntaxNode.DescendantNodes().Where(n => n is ExpressionStatementSyntax && n.ToString().Contains(parentBlock)).FirstOrDefault();

                                                                            if (node != null && node is ExpressionStatementSyntax exprNode)
                                                                            {
                                                                                var dasdas = modifiedBlockSyntaxNode.DescendantNodes();
                                                                                if (change.Type.Equals(CodeChangeType.MemberAccess))
                                                                                {
                                                                                    if (!exprNode.ToString().Trim(' ', '\r', '\n').Contains(change.Block.Trim(' ', '\r', '\n')))
                                                                                    {

                                                                                        var modifiedExprNode = exprNode.WithExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, exprNode.Expression, SyntaxFactory.IdentifierName(change.Block)));
                                                                                        modifiedBlockSyntaxNode = modifiedBlockSyntaxNode.ReplaceNode(node, modifiedExprNode);
                                                                                    }
                                                                                }
                                                                                else if (change.Type.Equals(CodeChangeType.LambdaMember))
                                                                                {
                                                                                    throw new NotImplementedException();
                                                                                }
                                                                                else if (change.Type.Equals(CodeChangeType.InBlock))
                                                                                {
                                                                                    BlockSyntax? blockToEdit;
                                                                                    if (!string.IsNullOrEmpty(change.InsertAfter))
                                                                                    {
                                                                                        string insertAfterBlock = FormatCodeBlock(change.InsertAfter, parameterValues);
                                                                                        blockToEdit = modifiedBlockSyntaxNode.DescendantNodes().Where(node =>
                                                                                                        node is BlockSyntax &&
                                                                                                        !node.ToString().Contains(parentBlock) &&
                                                                                                        node.ToString().Contains(insertAfterBlock)).FirstOrDefault() as BlockSyntax;
                                                                                    }
                                                                                    else
                                                                                    {
                                                                                        blockToEdit = modifiedBlockSyntaxNode.DescendantNodes().Where(node =>
                                                                                                        node is BlockSyntax &&
                                                                                                        !node.ToString().Contains(parentBlock)).FirstOrDefault() as BlockSyntax;
                                                                                    }

                                                                                    if (blockToEdit != null)
                                                                                    {
                                                                                        var innerTrailingTrivia = blockToEdit.Statements.FirstOrDefault()?.GetTrailingTrivia() ?? trailingTrivia;
                                                                                        var innerLeadingTrivia = blockToEdit.Statements.FirstOrDefault()?.GetLeadingTrivia() ?? leadingTrivia;

                                                                                        if (!innerTrailingTrivia.Contains(SemiColonTrivia))
                                                                                        {
                                                                                            innerTrailingTrivia = innerTrailingTrivia.Insert(0, SemiColonTrivia);
                                                                                        }

                                                                                        StatementSyntax innerStatement = SyntaxFactory.ParseStatement(change.Block)
                                                                                            .WithAdditionalAnnotations(Formatter.Annotation)
                                                                                            .WithLeadingTrivia(innerLeadingTrivia)
                                                                                            .WithTrailingTrivia(innerTrailingTrivia);

                                                                                        if (!StatementExists(blockToEdit, innerStatement))
                                                                                        {
                                                                                            var newBlock = blockToEdit.WithStatements(blockToEdit.Statements.Add(innerStatement));
                                                                                            modifiedBlockSyntaxNode = modifiedBlockSyntaxNode.ReplaceNode(blockToEdit, newBlock);
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }

                                                                //if there is no CodeChange.Parent, check if to InsertAfter a statement.
                                                                else if (!string.IsNullOrEmpty(change.InsertAfter))
                                                                {
                                                                    string insertBlock = FormatCodeBlock(change.InsertAfter, parameterValues);
                                                                    if (!string.IsNullOrEmpty(insertBlock))
                                                                    {
                                                                        List<StatementSyntax> statementList = new List<StatementSyntax>();

                                                                        string formattedCodeBlock = FormatCodeBlock(change.Block, parameterValues);
                                                                        if (!string.IsNullOrEmpty(formattedCodeBlock))
                                                                        {
                                                                            StatementSyntax statement = SyntaxFactory.ParseStatement(formattedCodeBlock)
                                                                                .WithAdditionalAnnotations(Formatter.Annotation)
                                                                                .WithTrailingTrivia(trailingTrivia)
                                                                                .WithLeadingTrivia(leadingTrivia);
                                                                            //check if statement already exists.
                                                                            if (!StatementExists(modifiedBlockSyntaxNode, statement))
                                                                            {
                                                                                statementList.Add(statement);
                                                                            }
                                                                        }

                                                                        if (statementList.Any())
                                                                        {
                                                                            var insertAfterNode = modifiedBlockSyntaxNode.DescendantNodes().Where(node => node is ExpressionStatementSyntax && node.ToString().Contains(insertBlock)).FirstOrDefault();
                                                                            if (insertAfterNode != null)
                                                                            {
                                                                                modifiedBlockSyntaxNode = modifiedBlockSyntaxNode.InsertNodesAfter(insertAfterNode, statementList);
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                                //if there are no Parent or InsertAfter in CodeChange,
                                                                //just insert statement at the end of the block
                                                                else
                                                                {
                                                                    string formattedCodeBlock = FormatCodeBlock(change.Block, parameterValues);
                                                                    if (!string.IsNullOrEmpty(formattedCodeBlock))
                                                                    {
                                                                        StatementSyntax statement = SyntaxFactory.ParseStatement(formattedCodeBlock)
                                                                                                        .WithAdditionalAnnotations(Formatter.Annotation)
                                                                                                        .WithTrailingTrivia(trailingTrivia)
                                                                                                        .WithLeadingTrivia(leadingTrivia);
                                                                        //check if statement already exists.
                                                                        if (!StatementExists(modifiedBlockSyntaxNode, statement))
                                                                        {
                                                                            if (change.Append.GetValueOrDefault())
                                                                            {
                                                                                modifiedBlockSyntaxNode = modifiedBlockSyntaxNode.WithStatements(new SyntaxList<StatementSyntax>(modifiedBlockSyntaxNode.Statements.Insert(0, statement)));
                                                                            }
                                                                            else
                                                                            {
                                                                                modifiedBlockSyntaxNode = modifiedBlockSyntaxNode.WithStatements(modifiedBlockSyntaxNode.Statements.Add(statement));
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                    if (blockSyntaxNode != null && modifiedBlockSyntaxNode != null && modifiedClassDeclarationSyntax != null)
                                                    {
                                                        modifiedClassDeclarationSyntax = modifiedClassDeclarationSyntax.ReplaceNode(blockSyntaxNode, modifiedBlockSyntaxNode);
                                                    }
                                                }
                                            }
                                        }
                                        documentEditor.ReplaceNode(classDeclarationSyntax, modifiedClassDeclarationSyntax);
                                        //edit using documenteditor
                                        //documentEditor.
                                    }
                                }
                            }
                            var newdoc = documentEditor.GetChangedDocument();
                            var txt = await newdoc.GetTextAsync();
                            System.IO.File.WriteAllText(filePath, txt.ToString());
                        }
                    }
                    
                }
            }
        }

        internal bool StatementExists(BlockSyntax blockSyntaxNode, StatementSyntax statement)
        {
            if (blockSyntaxNode.Statements.Any(st => st.ToString().Contains(statement.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return false;
        }

        internal bool PropertyExists(string property, SyntaxList<MemberDeclarationSyntax> members)
        {
            if (members.Where(m => m.ToString().Trim(' ', '\r', '\n', ';').Equals(property, StringComparison.OrdinalIgnoreCase)).Any())
            {
                return true;
            }
            return false;
        }

        internal bool AttributeExists(string attribute, SyntaxList<AttributeListSyntax> attributeList)
        {
            if (attributeList.Any() && !string.IsNullOrEmpty(attribute))
            {
                return attributeList.Where(al => al.Attributes.Where(attr => attr.ToString().Equals(attribute, StringComparison.OrdinalIgnoreCase)).Any()).Any();
            }
            return false;
        }

        internal UsingDirectiveSyntax[] CreateUsings(string[]? usings)
        {
            var usingDirectiveList = new List<UsingDirectiveSyntax>();
            if (usings != null && usings.Any())
            {
                foreach (var usingDirectiveString in usings)
                {
                    var nameLeadingTrivia = new SyntaxTriviaList(SyntaxFactory.Space);
                    var additionalAnnotation = Formatter.Annotation;
                    var usingTrailingTrivia = new SyntaxTriviaList(SyntaxFactory.CarriageReturnLineFeed);
                    var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName(usingDirectiveString).WithLeadingTrivia(nameLeadingTrivia))
                        .WithAdditionalAnnotations(additionalAnnotation)
                        .WithTrailingTrivia(usingTrailingTrivia);
                    usingDirectiveList.Add(usingDirective);
                }
            }
            return usingDirectiveList.ToArray();
        }

        internal AttributeListSyntax? CreateAttributeList(string[]? attributes, SyntaxList<AttributeListSyntax> attributeLists)
        {
            var attributeList = new List<AttributeSyntax>();
            if (attributes != null && attributes.Any())
            {
                foreach(var attribute in attributes)
                {
                    if (!AttributeExists(attribute, attributeLists))
                    {
                        attributeList.Add(SyntaxFactory.Attribute(SyntaxFactory.ParseName(attribute)));
                    }
                }
            }
            
            if (attributeList.Any())
            {
                return SyntaxFactory.AttributeList(SyntaxFactory.SeparatedList(attributeList));
            }
            //return null if AttributeListSyntax does not have anything. No obvious to check for an emprt AttributeListSyntax.
            else
            {
                return null;
            }

        }
        internal MemberDeclarationSyntax[] CreateClassProperties(
            string[]? classProperties,
            SyntaxList<MemberDeclarationSyntax> members,
            SyntaxTriviaList leadingTrivia,
            SyntaxTriviaList trailingTrivia)
        {
            var propertyDeclarationList = new List<MemberDeclarationSyntax>();
            if (classProperties != null && classProperties.Any())
            {
                foreach (var classPropertyString in classProperties)
                {
                    if (!string.IsNullOrEmpty(classPropertyString) && !PropertyExists(classPropertyString, members))
                    {
                        var additionalAnnotation = Formatter.Annotation;
                        var classPropertyDeclaration = SyntaxFactory.ParseMemberDeclaration(classPropertyString)
                            ?.WithAdditionalAnnotations(additionalAnnotation)
                            ?.WithTrailingTrivia(trailingTrivia)
                            ?.WithLeadingTrivia(leadingTrivia);
                        if (classPropertyDeclaration != null)
                        {
                            propertyDeclarationList.Add(classPropertyDeclaration);
                        }
                    }
                }
            }
            return propertyDeclarationList.ToArray();
        }

        private async Task<string> GetStartupClass(Document? programDoc)
        {
            if (programDoc != null && await programDoc.GetSyntaxRootAsync() is CompilationUnitSyntax root)
            {
                var namespaceNode = root.Members.OfType<NamespaceDeclarationSyntax>()?.FirstOrDefault();
                var programClassNode = namespaceNode?.DescendantNodes().Where(node => node is ClassDeclarationSyntax cds
                && cds.Identifier.ValueText.Contains("Program")).First();
                var nodes = programClassNode?.DescendantNodes();
                var useStartupNode = programClassNode?.DescendantNodes().Where(node => node is MemberAccessExpressionSyntax maes
                && maes.ToString().Contains("webBuilder.UseStartup")).First();

                var useStartupTxt = useStartupNode?.ToString();
                if (!string.IsNullOrEmpty(useStartupTxt))
                {
                    int startIndex = useStartupTxt.IndexOf("<");
                    int endIndex = useStartupTxt.IndexOf(">");
                    if (startIndex > -1 && endIndex > startIndex)
                    return useStartupTxt.Substring(startIndex + 1, endIndex - startIndex - 1);
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Format a string of a SimpleMemberAccessExpression(eg., Type.Value)
        /// Replace Type with its value from the parameterDict.
        /// </summary>
        /// <param name="codeBlock">SimpleMemberAccessExpression string</param>
        /// <param name="parameterDict">IDictionary with parameter type keys and values</param>
        /// <returns></returns>
        internal string FormatCodeBlock(string codeBlock, IDictionary<string, string>? parameterDict)
        {
            string formattedCodeBlock = string.Empty;
            if (!string.IsNullOrEmpty(codeBlock) && parameterDict != null)
            {
                string value = Regex.Replace(codeBlock, "^([^.]*).", "");
                string param = Regex.Replace(codeBlock, "[*^.].*", "");
                if (parameterDict != null && parameterDict.TryGetValue(param, out string? parameter))
                {
                    formattedCodeBlock = $"{parameter}.{value}";
                }
                else
                {
                    formattedCodeBlock = codeBlock;
                }
            }
            return formattedCodeBlock;
        }

        internal string GetClassName(string? className)
        {
            string formattedClassName = string.Empty;
            if (!string.IsNullOrEmpty(className))
            {
                string[] blocks = className.Split(".cs");
                if (blocks.Length > 1)
                {
                    return blocks[0];
                }
            }
            return formattedClassName;
        }

        private void ReadCodeModifierConfigs()
        {
            if (!string.IsNullOrEmpty(_toolOptions.ProjectType))
            {
                var properties = typeof(Resources).GetProperties(BindingFlags.Static | BindingFlags.NonPublic)
               .Where(p => p.PropertyType == typeof(byte[]))
               .ToArray();

                foreach (PropertyInfo propertyInfo in properties)
                {
                    if (propertyInfo.Name.StartsWith("cm") && propertyInfo.Name.Contains(_toolOptions.ProjectType))
                    {
                        byte[] content = (propertyInfo.GetValue(null) as byte[])!;
                        CodeModifierConfig? projectDescription = ReadCodeModifierConfigFromFileContent(content);

                        if (projectDescription == null)
                        {
                            throw new FormatException($"Resource file { propertyInfo.Name } could not be parsed. ");
                        }
                        CodeModifierConfigs.Add(projectDescription);
                    }
                }
            }
        }

        private CodeModifierConfig? ReadCodeModifierConfigFromFileContent(byte[] fileContent)
        {
            string jsonText = Encoding.UTF8.GetString(fileContent);
            return JsonSerializer.Deserialize<CodeModifierConfig>(jsonText);
        }
    }
}
