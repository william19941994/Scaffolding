using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.DotNet.MsIdentity.CodeReaderWriter
{
    internal class StartupModifier
    {
        public UsingDirectiveSyntax[] WebProjectUsings = new UsingDirectiveSyntax[]
        {
            UsingDirectives.MicrosoftAspNetCoreAuthentication,
            UsingDirectives.MicrosoftIdentityWeb,
            UsingDirectives.MicrosoftIdentityWebUI,
            UsingDirectives.MicrosoftAspNetCoreAuthenticationOpenIdConnect,
            UsingDirectives.MicrosoftAspNetCoreAuthorization,
            UsingDirectives.MicrosoftAspNetCoreMvcAuthorization
        };

        private string _startupFilePath;

        public StartupModifier(string startupFilePath)
        {
            _startupFilePath = startupFilePath ?? throw new ArgumentNullException(nameof(startupFilePath));
        }

        public bool ModifyStartup()
        {
            AddUsingDirectives();
            //ModifyServices()
            //AddAppCalls()
            return false;
        }

        private void AddUsingDirectives()
        {
            string startupText = File.ReadAllText(_startupFilePath);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(startupText);
            var root = (CompilationUnitSyntax)tree.GetRoot();
            UsingDirectiveSyntax[] items = CreateUsingDirectives(root, WebProjectUsings);
            //UsingDirectiveSyntax[] items = new UsingDirectiveSyntax[] { UsingDirectives.MicrosoftAspNetCoreAuthentication };
            var newroot = root.AddUsings(items);
            var startupcs = newroot.ToFullString();
            File.WriteAllText(_startupFilePath, startupcs);
        }
        private UsingDirectiveSyntax[] CreateUsingDirectives(CompilationUnitSyntax root, UsingDirectiveSyntax[] usingsToAdd)
        {
            IList<UsingDirectiveSyntax> items = new List<UsingDirectiveSyntax>();
            if (usingsToAdd.Any())
            {
                foreach (var usingItem in usingsToAdd)
                {
                    if (!root.Usings.Contains(usingItem))
                    {
                        items.Add(usingItem);
                    }
                }
            }
            return items.ToArray();
        }
    }


    public class UsingDirectives
    {
        public static UsingDirectiveSyntax MicrosoftAspNetCoreAuthentication = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(" Microsoft.AspNetCore.Authentication"));
        public static UsingDirectiveSyntax MicrosoftIdentityWeb = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName(" Microsoft.Identity.Web"));
        public static UsingDirectiveSyntax MicrosoftIdentityWebUI = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName(" Microsoft.Identity.Web.UI"));
        public static UsingDirectiveSyntax MicrosoftAspNetCoreAuthenticationOpenIdConnect = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName(" Microsoft.AspNetCore.Authentication.OpenIdConnect"));
        public static UsingDirectiveSyntax MicrosoftAspNetCoreAuthorization = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName(" Microsoft.AspNetCore.Authorization"));
        public static UsingDirectiveSyntax MicrosoftAspNetCoreMvcAuthorization = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName(" Microsoft.AspNetCore.Mvc.Authorization"));
/*        public UsingDirectiveSyntax MicrosoftAspNetCoreAuthentication = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("Microsoft.AspNetCore.Authentication"));
        public UsingDirectiveSyntax MicrosoftAspNetCoreAuthentication = SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("Microsoft.AspNetCore.Authentication"));*/

    }

}
